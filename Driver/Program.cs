using System;
using System.Globalization;
using System.IO;

namespace Driver {
	static class Program {
		const string Usage = @"Usage (run.sh / run.ps1 pass their arguments here):

  download [--top 3000] [--jobs N]
      Download the top N packages in nuget-packages.txt from nuget.org into corpus/ and
      write corpus/files.txt (unique .dll/.exe files). Run this once. The full corpus is ~11 GB.

  cases    --baseline DIR --candidate DIR    Hand-written test cases (small, invalid and huge method bodies)
  runtime  --baseline DIR --candidate DIR    Run generated methods on CoreCLR, Mono (if installed) and .NET Framework (Windows)
  corpus   --baseline DIR --candidate DIR    Compute max stack and save every corpus file with both builds and compare
  perf     --baseline DIR --candidate DIR    Time the max stack calculator on corpus files
  all      --baseline DIR --candidate DIR    cases + runtime + corpus + perf

  DIR is a dnlib checkout (the folder that contains src/dnlib.csproj). The baseline is usually master,
  the candidate is your branch.

Options:
  --output DIR          Where to write the report and raw results (default: results/<time>-<command>)
  --jobs N              Worker processes to run at the same time (default: CPU count, at most 1 per GB of RAM)
  --files N             Use only the first N corpus files (corpus default: all, perf default: 3000)
  --timeout SECONDS     Kill a worker that spends longer than this on one item (default: corpus 1800,
                        perf 300, cases 60)
  --max-memory-mb N     Kill a worker that uses more memory than this (default: 4096)
  --iterations N        perf: time each file N times in a row and keep the best (default: 3)
  --rounds N            perf: run baseline, candidate, baseline, candidate, ... N times each (default: 2)
  --detail-files N      corpus: explain method differences for at most N files (default: 200)
";

		static int Main(string[] args) {
			// Numbers are written by one program and parsed by the other, so always use "1.5", never "1,5".
			CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
			CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

			Options options;
			try {
				options = Options.Parse(args);
			}
			catch (UsageException ex) {
				Console.Error.WriteLine(ex.Message);
				Console.Error.WriteLine();
				Console.Error.WriteLine(Usage);
				return 1;
			}

			var paths = new ToolPaths();
			try {
				switch (options.Command) {
				case "download":
					CorpusDownloader.Run(paths, top: options.GetInt("top", 3000), jobs: options.GetInt("jobs", 16));
					return 0;

				case "cases":
				case "runtime":
				case "corpus":
				case "perf":
				case "all":
					return RunTests(options, paths);

				case "help":
				case "--help":
				case "-h":
					Console.WriteLine(Usage);
					return 0;

				default:
					throw new UsageException($"Unknown command '{options.Command}'");
				}
			}
			catch (UsageException ex) {
				Console.Error.WriteLine(ex.Message);
				Console.Error.WriteLine();
				Console.Error.WriteLine(Usage);
				return 1;
			}
			catch (FatalException ex) {
				Console.Error.WriteLine();
				Console.Error.WriteLine("ERROR: " + ex.Message);
				return 1;
			}
		}

		static int RunTests(Options options, ToolPaths paths) {
			string command = options.Command;
			string outputDir = options.GetString("output", Path.Combine(paths.Results, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + command));
			Directory.CreateDirectory(outputDir);

			string baselineDnlib = options.GetDnlibDir("baseline");
			string candidateDnlib = options.GetDnlibDir("candidate");
			var baseline = WorkerBuild.Build("baseline", baselineDnlib, paths);
			var candidate = WorkerBuild.Build("candidate", candidateDnlib, paths);

			using var report = new Report(Path.Combine(outputDir, "report.md"));
			report.Heading(1, "dnlib PR test report");
			report.Line($"- Baseline:  `{baselineDnlib}` ({baseline.Version})");
			report.Line($"- Candidate: `{candidateDnlib}` ({candidate.Version})");
			report.Line($"- Machine: {Environment.ProcessorCount} CPUs, {Environment.OSVersion}, .NET {Environment.Version}");
			report.Line($"- Started: {DateTime.Now:yyyy-MM-dd HH:mm}");
			report.Line();

			var context = new TestContext {
				Options = options,
				Paths = paths,
				Baseline = baseline,
				Candidate = candidate,
				OutputDir = outputDir,
				Report = report,
			};

			bool all = command == "all";
			if (all || command == "cases")
				CasesTest.Run(context);
			if (all || command == "runtime")
				RuntimeTest.Run(context);
			if (all || command == "corpus")
				CorpusTest.Run(context);
			if (all || command == "perf")
				PerfTest.Run(context);

			report.Line();
			report.Line($"Finished: {DateTime.Now:yyyy-MM-dd HH:mm}. Raw results are in `{outputDir}`.");
			Console.WriteLine();
			Console.WriteLine($"Report written to {Path.Combine(outputDir, "report.md")}");
			return 0;
		}
	}

	/// <summary>Everything a test needs.</summary>
	sealed class TestContext {
		public Options Options;
		public ToolPaths Paths;
		public WorkerBuild Baseline;
		public WorkerBuild Candidate;
		public string OutputDir;
		public Report Report;

		public WorkerBuild[] BothBuilds => new[] { Baseline, Candidate };

		/// <summary>
		/// A corpus file as "package/file", e.g. "newtonsoft.json.13.0.4/lib_net45_Newtonsoft.Json.dll".
		/// Shorter than the full path, and unlike the file name alone it tells versions apart.
		/// </summary>
		public string DisplayPath(string path) {
			string relative = Path.GetRelativePath(Paths.PackagesDir, path);
			return relative.StartsWith("..") ? path : relative.Replace('\\', '/');
		}

		public WorkerRunSettings DefaultRunSettings(int itemsPerProcess, int defaultTimeoutSeconds) => new WorkerRunSettings {
			Jobs = Options.GetInt("jobs", Options.DefaultJobs),
			ItemsPerProcess = itemsPerProcess,
			ItemTimeout = TimeSpan.FromSeconds(Options.GetInt("timeout", defaultTimeoutSeconds)),
			MaxMemoryBytes = Options.GetInt("max-memory-mb", 4096) * 1024L * 1024,
		};
	}

	/// <summary>An error that stops the program, printed without a stack trace.</summary>
	sealed class FatalException : Exception {
		public FatalException(string message) : base(message) { }
	}
}
