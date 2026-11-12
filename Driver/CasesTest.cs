using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Driver {
	/// <summary>
	/// Runs every test case from Worker/TestCaseList.cs with both builds, each case in its own process,
	/// and reports the cases where the candidate fails an expectation, crashes, hangs, uses much more
	/// time or memory, or simply gives a different answer than the baseline.
	/// </summary>
	static class CasesTest {
		/// <summary>The parsed result of one case: "OK TAB text TAB check TAB ms TAB allocated TAB peak".</summary>
		sealed class CaseResult {
			public string Status;     // OK, or CRASH / TIMEOUT / MEMORY / EXCEPTION from the runner or worker
			public string Text;
			public string Check;      // "-", "PASS" or "FAIL: reason"
			public double Milliseconds;
			public long AllocatedBytes;
			public long PeakMemoryBytes;

			public bool Ok => Status == "OK";
			public bool CheckFailed => Check.StartsWith("FAIL");

			public static CaseResult Parse(string line) {
				string[] fields = line.Split('\t');
				if (fields[0] != "OK")
					return new CaseResult { Status = fields[0], Text = fields.Length > 1 ? fields[1] : "", Check = "-" };
				return new CaseResult {
					Status = "OK",
					Text = fields[1],
					Check = fields[2],
					Milliseconds = double.Parse(fields[3]),
					AllocatedBytes = long.Parse(fields[4]),
					PeakMemoryBytes = long.Parse(fields[5]),
				};
			}

			public string Summary => Ok ? Text : $"{Status}: {Text}";
			public string Measurements => Ok
				? $"{Format.Milliseconds(Milliseconds)}, alloc {Format.Megabytes(AllocatedBytes)}, peak {Format.Megabytes(PeakMemoryBytes)}"
				: Status;
		}

		sealed class CaseInfo {
			public string Name;
			public string Category;
			public string Description;
		}

		public static void Run(TestContext context) {
			var report = context.Report;
			report.Heading(2, "Test cases");

			var listOutput = context.Candidate.RunCommand(TimeSpan.FromMinutes(1), "list-cases");
			if (!listOutput.Succeeded)
				throw new FatalException("list-cases failed:\n" + listOutput.Error);
			var cases = listOutput.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
				.Select(line => line.TrimEnd('\r').Split('\t'))
				.Select(fields => new CaseInfo { Name = fields[0], Category = fields[1], Description = fields[2] })
				.ToList();
			var names = cases.Select(c => c.Name).ToList();

			// One process per case, so a crash or a memory blow-up only affects that case,
			// and the peak memory number belongs to that case alone.
			var settings = context.DefaultRunSettings(itemsPerProcess: 1, defaultTimeoutSeconds: 60);
			string workDir = Path.Combine(context.OutputDir, "cases");
			var baselineResults = WorkerRunner.Run(context.Baseline, "case", names, settings, workDir).Select(CaseResult.Parse).ToList();
			var candidateResults = WorkerRunner.Run(context.Candidate, "case", names, settings, workDir).Select(CaseResult.Parse).ToList();

			File.WriteAllLines(Path.Combine(workDir, "results.tsv"),
				cases.Select((c, i) => $"{c.Name}\tbaseline\t{baselineResults[i].Summary}\t{baselineResults[i].Check}\t{baselineResults[i].Measurements}\n" +
					$"{c.Name}\tcandidate\t{candidateResults[i].Summary}\t{candidateResults[i].Check}\t{candidateResults[i].Measurements}"));

			var verdicts = cases.Select((c, i) => Verdict(c, baselineResults[i], candidateResults[i])).ToList();
			int failures = verdicts.Count(v => v.StartsWith("**"));
			report.Line($"{cases.Count} cases. **{failures}** need attention (in bold). `changed` means the candidate gives a different answer than the baseline, which may be fine but should be looked at.");
			report.Line();

			foreach (var category in cases.Select(c => c.Category).Distinct()) {
				bool isPerformance = IsPerformanceCategory(category);
				report.Heading(3, $"Category: {category}");
				var rows = new List<string[]>();
				for (int i = 0; i < cases.Count; i++) {
					if (cases[i].Category != category)
						continue;
					var baseline = baselineResults[i];
					var candidate = candidateResults[i];
					if (isPerformance)
						rows.Add(new[] { cases[i].Name, baseline.Measurements, candidate.Measurements, verdicts[i] });
					else
						rows.Add(new[] { cases[i].Name, baseline.Summary, candidate.Summary, verdicts[i] });
				}
				report.Table(new[] { "Case", "Baseline", "Candidate", "Verdict" }, rows);
			}

			report.Heading(3, "What each case does");
			foreach (var c in cases)
				report.Line($"- `{c.Name}`: {c.Description}");
		}

		static bool IsPerformanceCategory(string category) => category.StartsWith("slow");

		/// <summary>
		/// Problems are in bold so they stand out. The first matching rule wins.
		/// </summary>
		static string Verdict(CaseInfo info, CaseResult baseline, CaseResult candidate) {
			bool isPerformance = IsPerformanceCategory(info.Category);

			// The candidate crashed, hung or ran out of memory
			if (!candidate.Ok) {
				return baseline.Status == candidate.Status
					? $"{candidate.Status} on both"
					: $"**{candidate.Status}** {candidate.Text} (baseline: {baseline.Status})";
			}

			// The candidate broke an expectation
			if (candidate.CheckFailed)
				return baseline.CheckFailed ? $"fails on both: {candidate.Check.Substring("FAIL: ".Length)}" : $"**{candidate.Check}**";

			// From here on the candidate ran fine
			if (!baseline.Ok)
				return $"fixed (baseline: {baseline.Status})";
			if (candidate.Check == "PASS" && baseline.CheckFailed)
				return "fixed";

			if (isPerformance) {
				// Small differences are noise. Flag only big ones.
				if (candidate.Milliseconds > 3 * baseline.Milliseconds + 100)
					return $"**SLOWER {Format.Ratio(candidate.Milliseconds, baseline.Milliseconds)}**";
				if (candidate.AllocatedBytes > 3 * baseline.AllocatedBytes + 10 * 1024 * 1024)
					return $"**ALLOCATES {Format.Ratio(candidate.AllocatedBytes, baseline.AllocatedBytes)}**";
				if (candidate.PeakMemoryBytes > 2 * baseline.PeakMemoryBytes + 200 * 1024 * 1024)
					return $"**PEAK MEMORY {Format.Ratio(candidate.PeakMemoryBytes, baseline.PeakMemoryBytes)}**";
			}

			string verdict = candidate.Check == "PASS" ? "pass" : "same";
			if (candidate.Text != baseline.Text)
				verdict = isPerformance ? $"changed: {baseline.Text} -> {candidate.Text}" : candidate.Check == "PASS" ? "pass, but changed" : "changed";
			if (isPerformance && 3 * candidate.Milliseconds + 100 < baseline.Milliseconds)
				verdict += $" (faster: {Format.Ratio(candidate.Milliseconds, baseline.Milliseconds)})";
			return verdict;
		}
	}
}
