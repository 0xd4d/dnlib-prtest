using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Driver {
	/// <summary>
	/// Times the max stack calculator on real corpus files. Only the calculator is timed, not loading.
	///
	/// To reduce noise: each file is timed --iterations times in a row and the best time is kept, and
	/// the baseline and candidate runs alternate (B, C, B, C, ...) --rounds times, again keeping the best
	/// time per file. Allocations are exact, so they need no repeats.
	/// </summary>
	static class PerfTest {
		sealed class FileTiming {
			public int Methods;
			public double BestMilliseconds = double.MaxValue;
			public long AllocatedBytes;
			public bool Failed;
		}

		public static void Run(TestContext context) {
			var report = context.Report;
			var files = CorpusTest.LoadCorpusFiles(context, defaultFileCount: 3000);
			int iterations = context.Options.GetInt("iterations", 3);
			int rounds = context.Options.GetInt("rounds", 2);
			report.Heading(2, $"Calculator performance: {files.Count:N0} files, best of {iterations} iterations x {rounds} rounds");

			var settings = context.DefaultRunSettings(itemsPerProcess: 25, defaultTimeoutSeconds: 300);
			string workDir = Path.Combine(context.OutputDir, "perf");
			var timings = new Dictionary<string, FileTiming[]> {
				["baseline"] = files.Select(_ => new FileTiming()).ToArray(),
				["candidate"] = files.Select(_ => new FileTiming()).ToArray(),
			};

			for (int round = 1; round <= rounds; round++) {
				foreach (var build in context.BothBuilds) {
					Console.WriteLine($"  Round {round} of {rounds}");
					var results = WorkerRunner.Run(build, "perf", files, settings, workDir, iterations.ToString());
					File.WriteAllLines(Path.Combine(workDir, $"{build.Side}-round{round}.tsv"), files.Select((file, i) => file + "\t" + results[i]));
					// perf result: OK TAB methodCount TAB bestMilliseconds TAB allocatedBytes
					for (int i = 0; i < files.Count; i++) {
						string[] fields = results[i].Split('\t');
						var timing = timings[build.Side][i];
						if (fields[0] != "OK") {
							timing.Failed = true;
							continue;
						}
						timing.Methods = int.Parse(fields[1]);
						timing.BestMilliseconds = Math.Min(timing.BestMilliseconds, double.Parse(fields[2]));
						timing.AllocatedBytes = long.Parse(fields[3]);
					}
				}
			}

			var baseline = timings["baseline"];
			var candidate = timings["candidate"];
			var usable = Enumerable.Range(0, files.Count).Where(i => !baseline[i].Failed && !candidate[i].Failed).ToList();
			var failedOnCandidateOnly = Enumerable.Range(0, files.Count).Where(i => candidate[i].Failed && !baseline[i].Failed).ToList();

			double baselineMs = usable.Sum(i => baseline[i].BestMilliseconds);
			double candidateMs = usable.Sum(i => candidate[i].BestMilliseconds);
			long baselineAllocated = usable.Sum(i => baseline[i].AllocatedBytes);
			long candidateAllocated = usable.Sum(i => candidate[i].AllocatedBytes);
			long methods = usable.Sum(i => (long)baseline[i].Methods);

			report.Line($"- Methods: {methods:N0} in {usable.Count:N0} files");
			report.Line($"- Time: baseline {Format.Milliseconds(baselineMs)}, candidate {Format.Milliseconds(candidateMs)}, **{Format.Ratio(candidateMs, baselineMs)}**");
			report.Line($"- Allocated: baseline {Format.Megabytes(baselineAllocated)}, candidate {Format.Megabytes(candidateAllocated)}, **{Format.Ratio(candidateAllocated, baselineAllocated)}**");
			if (failedOnCandidateOnly.Count > 0)
				report.Line($"- Files that failed only on the candidate: **{failedOnCandidateOnly.Count}** (see the .tsv files)");
			report.Line();

			// Files under 10 ms are mostly noise, so only bigger ones are listed.
			var bigFiles = usable.Where(i => Math.Max(baseline[i].BestMilliseconds, candidate[i].BestMilliseconds) >= 10).ToList();
			report.Line($"Files taking 10 ms or more on either side: {bigFiles.Count}. " +
				$"Candidate more than 1.5x slower: {bigFiles.Count(i => candidate[i].BestMilliseconds > 1.5 * baseline[i].BestMilliseconds)}, " +
				$"more than 1.5x faster: {bigFiles.Count(i => baseline[i].BestMilliseconds > 1.5 * candidate[i].BestMilliseconds)}.");
			report.Line();

			string[] Row(int i) => new[] {
				context.DisplayPath(files[i]),
				baseline[i].Methods.ToString("N0"),
				Format.Milliseconds(baseline[i].BestMilliseconds),
				Format.Milliseconds(candidate[i].BestMilliseconds),
				Format.Ratio(candidate[i].BestMilliseconds, baseline[i].BestMilliseconds),
				Format.Ratio(candidate[i].AllocatedBytes, baseline[i].AllocatedBytes),
			};
			var headers = new[] { "File", "Methods", "Baseline", "Candidate", "Time ratio", "Alloc ratio" };

			report.Line("Biggest slowdowns:");
			report.Line();
			report.Table(headers, bigFiles.OrderByDescending(i => candidate[i].BestMilliseconds / baseline[i].BestMilliseconds).Take(10).Select(Row));
			report.Line("Biggest speedups:");
			report.Line();
			report.Table(headers, bigFiles.OrderBy(i => candidate[i].BestMilliseconds / baseline[i].BestMilliseconds).Take(10).Select(Row));
		}
	}
}
