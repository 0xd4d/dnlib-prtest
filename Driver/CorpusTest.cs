using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Driver {
	/// <summary>
	/// Runs both builds over the real-world corpus (corpus/files.txt):
	///
	///   1. "methods": max stack of every method body. Per file the worker prints a digest of all results,
	///      so equal digests mean identical results. For files with different digests, the worker writes
	///      every method's result and the driver lists the methods that differ.
	///   2. "save": load and save every file. Compares the SHA-1 of the output, errors, and save time.
	/// </summary>
	static class CorpusTest {
		public static IReadOnlyList<string> LoadCorpusFiles(TestContext context, int defaultFileCount) {
			string listPath = context.Paths.CorpusFileList;
			if (!File.Exists(listPath))
				throw new FatalException($"{listPath} doesn't exist. Run the 'download' command first.");
			var files = File.ReadAllLines(listPath).Where(line => line.Length > 0).ToList();
			int count = context.Options.GetInt("files", defaultFileCount);
			return files.Take(count).ToList();
		}

		public static void Run(TestContext context) {
			var report = context.Report;
			var files = LoadCorpusFiles(context, int.MaxValue);
			report.Heading(2, $"Corpus: {files.Count:N0} files");

			// Many files per process: starting a process costs ~100 ms.
			var settings = context.DefaultRunSettings(itemsPerProcess: 25, defaultTimeoutSeconds: 300);
			string workDir = Path.Combine(context.OutputDir, "corpus");

			var baselineMethods = RunAndSave(context.Baseline, "methods", files, settings, workDir);
			var candidateMethods = RunAndSave(context.Candidate, "methods", files, settings, workDir);
			ReportMethods(context, files, baselineMethods, candidateMethods, workDir);

			var baselineSaves = RunAndSave(context.Baseline, "save", files, settings, workDir);
			var candidateSaves = RunAndSave(context.Candidate, "save", files, settings, workDir);
			ReportSaves(context, files, baselineSaves, candidateSaves, workDir);
		}

		static string[] RunAndSave(WorkerBuild build, string command, IReadOnlyList<string> files, WorkerRunSettings settings, string workDir) {
			var results = WorkerRunner.Run(build, command, files, settings, workDir);
			File.WriteAllLines(Path.Combine(workDir, $"{build.Side}-{command}.tsv"), files.Select((file, i) => file + "\t" + results[i]));
			return results;
		}

		// ------------------------------------------------------------------------------------------------
		// Max stack of every method

		static void ReportMethods(TestContext context, IReadOnlyList<string> files, string[] baseline, string[] candidate, string workDir) {
			var report = context.Report;
			report.Heading(3, "Max stack of every method");

			// methods result: OK TAB methodCount TAB digest TAB milliseconds TAB allocatedBytes
			long methodCount = 0;
			double baselineMs = 0, candidateMs = 0;
			long baselineAllocated = 0, candidateAllocated = 0;
			int identicalFiles = 0;
			var differentFiles = new List<int>();
			var failures = new List<string[]>();
			var loadFailures = 0;

			for (int i = 0; i < files.Count; i++) {
				string[] b = baseline[i].Split('\t');
				string[] c = candidate[i].Split('\t');
				if (b[0] == "OK" && c[0] == "OK") {
					methodCount += long.Parse(b[1]);
					baselineMs += double.Parse(b[3]);
					candidateMs += double.Parse(c[3]);
					baselineAllocated += long.Parse(b[4]);
					candidateAllocated += long.Parse(c[4]);
					if (b[2] == c[2])
						identicalFiles++;
					else
						differentFiles.Add(i);
				}
				else if (b[0] == "LOADFAIL" && c[0] == "LOADFAIL")
					loadFailures++;
				else
					failures.Add(new[] { files[i], ShortResult(b), ShortResult(c) });
			}

			report.Line($"- Files where every method has the same result: {identicalFiles:N0}");
			report.Line($"- Files with at least one different method: **{differentFiles.Count:N0}**");
			report.Line($"- Files that dnlib can't load (same on both): {loadFailures:N0}");
			report.Line($"- Files that crashed, hung or failed (either side): **{failures.Count:N0}**");
			report.Line($"- Methods with a body: {methodCount:N0}");
			report.Line($"- Calculator time, summed over files: baseline {Format.Milliseconds(baselineMs)}, candidate {Format.Milliseconds(candidateMs)} ({Format.Ratio(candidateMs, baselineMs)}). " +
				"Workers run in parallel, so use the perf command for exact numbers.");
			report.Line($"- Calculator allocations: baseline {Format.Megabytes(baselineAllocated)}, candidate {Format.Megabytes(candidateAllocated)} ({Format.Ratio(candidateAllocated, baselineAllocated)})");
			report.Line();

			if (failures.Count > 0) {
				report.Line("Files that crashed, hung or failed (either side):");
				report.Line();
				report.Table(new[] { "File", "Baseline", "Candidate" }, failures.Take(50));
				if (failures.Count > 50)
					report.Line($"({failures.Count - 50} more in the .tsv files)");
			}

			if (differentFiles.Count > 0)
				ReportMethodDifferences(context, files, differentFiles, workDir);
		}

		static string ShortResult(string[] fields) => fields[0] == "OK" ? "OK" : string.Join(" ", fields.Take(2));

		/// <summary>
		/// For each file with a different digest, asks both workers for every method's result and diffs them.
		/// </summary>
		static void ReportMethodDifferences(TestContext context, IReadOnlyList<string> files, List<int> differentFiles, string workDir) {
			var report = context.Report;
			int maxFiles = context.Options.GetInt("detail-files", 200);
			var filesToExplain = differentFiles.Take(maxFiles).ToList();
			string detailsDir = Path.Combine(workDir, "method-details");
			Directory.CreateDirectory(detailsDir);
			Console.WriteLine($"  Comparing methods in {filesToExplain.Count} files...");

			var differences = new List<MethodDifference>();
			var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = context.Options.GetInt("jobs", Options.DefaultJobs) };
			var unavailable = new List<string[]>();
			Parallel.ForEach(filesToExplain, parallelOptions, fileIndex => {
				var baselineMethods = GetMethodDetails(context.Baseline, files[fileIndex], fileIndex, detailsDir, out string baselineError);
				var candidateMethods = GetMethodDetails(context.Candidate, files[fileIndex], fileIndex, detailsDir, out string candidateError);
				if (baselineMethods is null || candidateMethods is null) {
					lock (unavailable)
						unavailable.Add(new[] { files[fileIndex], baselineError ?? "OK", candidateError ?? "OK" });
					return;
				}
				var found = new List<MethodDifference>();
				foreach (var token in baselineMethods.Keys.Union(candidateMethods.Keys)) {
					baselineMethods.TryGetValue(token, out var baselineLine);
					candidateMethods.TryGetValue(token, out var candidateLine);
					if (baselineLine?.Result != candidateLine?.Result)
						found.Add(new MethodDifference { File = files[fileIndex], Baseline = baselineLine, Candidate = candidateLine });
				}
				lock (differences)
					differences.AddRange(found);
			});

			string differencesPath = Path.Combine(workDir, "method-differences.tsv");
			File.WriteAllLines(differencesPath, differences.Select(d =>
				$"{d.File}\t{d.Method.Token}\t{d.Baseline?.Result}\t{d.Candidate?.Result}\t{d.Method.Size}\t{d.Method.Name}"));

			report.Line($"Methods that differ, from {filesToExplain.Count} of {differentFiles.Count} files (all of them in `method-differences.tsv`):");
			report.Line();
			report.Table(new[] { "Kind of difference", "Methods" },
				differences.GroupBy(d => d.Kind).OrderByDescending(g => g.Count()).Select(g => new[] { g.Key, g.Count().ToString("N0") }));

			report.Line("Examples:");
			report.Line();
			var examples = differences.GroupBy(d => d.Kind).SelectMany(g => g.Take(5));
			report.Table(new[] { "Method", "File", "Baseline", "Candidate" },
				examples.Select(d => new[] { $"{d.Method.Name} ({d.Method.Size})", Path.GetFileName(d.File), d.Baseline?.Result ?? "missing", d.Candidate?.Result ?? "missing" }));

			if (unavailable.Count > 0) {
				report.Line("Files whose per-method results couldn't be produced (the method-details command failed):");
				report.Line();
				report.Table(new[] { "File", "Baseline", "Candidate" }, unavailable);
			}
		}

		sealed class MethodLine {
			public string Token;
			public string Result;   // "ok=True maxStack=3 writerEmits=3" or "exception X"
			public string Size;     // "instructions=N handlers=N header=N"
			public string Name;
		}

		sealed class MethodDifference {
			public string File;
			public MethodLine Baseline;
			public MethodLine Candidate;

			/// <summary>Token, size and name are the same on both sides.</summary>
			public MethodLine Method => Baseline ?? Candidate;

			public string Kind {
				get {
					if (Baseline is null)
						return "missing on baseline";
					if (Candidate is null)
						return "missing on candidate";
					var (baselineOk, baselineMax) = ParseResult(Baseline.Result);
					var (candidateOk, candidateMax) = ParseResult(Candidate.Result);
					if (baselineOk is null || candidateOk is null)
						return "exception on one side";
					if (baselineOk == true && candidateOk == false)
						return "ok on baseline, error on candidate";
					if (baselineOk == false && candidateOk == true)
						return "error on baseline, ok on candidate";
					string both = baselineOk == true ? "both ok" : "both error";
					return candidateMax < baselineMax ? $"{both}, candidate max stack lower" : $"{both}, candidate max stack higher";
				}
			}

			static (bool? ok, uint maxStack) ParseResult(string result) {
				if (!result.StartsWith("ok="))
					return (null, 0);
				var parts = result.Split(' ');
				return (parts[0] == "ok=True", uint.Parse(parts[1].Substring("maxStack=".Length)));
			}
		}

		/// <summary>Returns null and sets error if the worker failed (e.g. crashed or timed out).</summary>
		static Dictionary<string, MethodLine> GetMethodDetails(WorkerBuild build, string file, int fileIndex, string detailsDir, out string error) {
			string outputPath = Path.Combine(detailsDir, $"{fileIndex}-{build.Side}.txt");
			var result = build.RunCommand(TimeSpan.FromMinutes(5), "method-details", file, outputPath);
			if (!result.Succeeded || !File.Exists(outputPath)) {
				string lastErrorLine = result.Error.Trim().Split('\n').Last();
				error = result.TimedOut ? "TIMEOUT" : $"exit code {result.ExitCode}: {lastErrorLine}";
				return null;
			}
			error = null;
			var methods = new Dictionary<string, MethodLine>();
			foreach (var line in File.ReadLines(outputPath)) {
				string[] fields = line.Split('\t');
				if (fields.Length == 4)
					methods[fields[0]] = new MethodLine { Token = fields[0], Result = fields[1], Size = fields[2], Name = fields[3] };
			}
			return methods;
		}

		// ------------------------------------------------------------------------------------------------
		// Load and save

		static void ReportSaves(TestContext context, IReadOnlyList<string> files, string[] baseline, string[] candidate, string workDir) {
			var report = context.Report;
			report.Heading(3, "Load and save every file");

			// save result: OK|ERR TAB milliseconds TAB allocatedBytes TAB sha1|message
			int identical = 0, bothFail = 0, loadFailures = 0;
			var differentOutput = new List<string[]>();
			var newFailures = new List<string[]>();
			var fixedFiles = new List<string[]>();
			var crashes = new List<string[]>();
			double baselineMs = 0, candidateMs = 0;
			long baselineAllocated = 0, candidateAllocated = 0;
			var slowerFiles = new List<(string file, double baselineMs, double candidateMs)>();

			for (int i = 0; i < files.Count; i++) {
				string[] b = baseline[i].Split('\t');
				string[] c = candidate[i].Split('\t');
				bool bothRan = (b[0] == "OK" || b[0] == "ERR") && (c[0] == "OK" || c[0] == "ERR");
				if (bothRan) {
					double bMs = double.Parse(b[1]), cMs = double.Parse(c[1]);
					baselineMs += bMs;
					candidateMs += cMs;
					baselineAllocated += long.Parse(b[2]);
					candidateAllocated += long.Parse(c[2]);
					if (cMs > 2 * bMs && cMs - bMs > 50)
						slowerFiles.Add((files[i], bMs, cMs));

					if (b[0] == "OK" && c[0] == "OK") {
						if (b[3] == c[3])
							identical++;
						else
							differentOutput.Add(new[] { files[i] });
					}
					else if (b[0] == "OK")
						newFailures.Add(new[] { files[i], c[3] });
					else if (c[0] == "OK")
						fixedFiles.Add(new[] { files[i], b[3] });
					else
						bothFail++;
				}
				else if (b[0] == "LOADFAIL" && c[0] == "LOADFAIL")
					loadFailures++;
				else
					crashes.Add(new[] { files[i], ShortResult(b), ShortResult(c) });
			}

			report.Line($"- Byte-identical output: {identical:N0}");
			report.Line($"- Different output: **{differentOutput.Count:N0}**");
			report.Line($"- Saved by baseline, error on candidate: **{newFailures.Count:N0}**");
			report.Line($"- Error on baseline, saved by candidate: {fixedFiles.Count:N0}");
			report.Line($"- Error on both: {bothFail:N0}");
			report.Line($"- Files that dnlib can't load (same on both): {loadFailures:N0}");
			report.Line($"- Crashed, hung or failed (either side): **{crashes.Count:N0}**");
			report.Line($"- Save time, summed over files: baseline {Format.Milliseconds(baselineMs)}, candidate {Format.Milliseconds(candidateMs)} ({Format.Ratio(candidateMs, baselineMs)})");
			report.Line($"- Save allocations: baseline {Format.Megabytes(baselineAllocated)}, candidate {Format.Megabytes(candidateAllocated)} ({Format.Ratio(candidateAllocated, baselineAllocated)})");
			report.Line();

			void ListFiles(string title, string[] headers, List<string[]> rows) {
				if (rows.Count == 0)
					return;
				report.Line(title + ":");
				report.Line();
				report.Table(headers, rows.Take(30));
				if (rows.Count > 30)
					report.Line($"({rows.Count - 30} more in `save-differences.txt`)");
			}
			ListFiles("Saved by baseline, error on candidate", new[] { "File", "Candidate error" }, newFailures);
			ListFiles("Different output", new[] { "File" }, differentOutput);
			ListFiles("Error on baseline, saved by candidate", new[] { "File", "Baseline error" }, fixedFiles);
			ListFiles("Crashed, hung or failed (either side)", new[] { "File", "Baseline", "Candidate" }, crashes);
			ListFiles("Files that took more than twice as long to save (and 50 ms longer)", new[] { "File", "Baseline", "Candidate", "Ratio" },
				slowerFiles.OrderByDescending(s => s.candidateMs - s.baselineMs)
					.Select(s => new[] { s.file, Format.Milliseconds(s.baselineMs), Format.Milliseconds(s.candidateMs), Format.Ratio(s.candidateMs, s.baselineMs) }).ToList());

			File.WriteAllLines(Path.Combine(workDir, "save-differences.txt"),
				newFailures.Select(r => "NEW ERROR\t" + string.Join("\t", r))
				.Concat(differentOutput.Select(r => "DIFFERENT OUTPUT\t" + r[0]))
				.Concat(fixedFiles.Select(r => "FIXED\t" + string.Join("\t", r)))
				.Concat(crashes.Select(r => "CRASH/TIMEOUT\t" + string.Join("\t", r)))
				.Concat(slowerFiles.Select(s => $"SLOWER\t{s.file}\t{s.baselineMs:F1} ms\t{s.candidateMs:F1} ms")));
		}
	}
}
