using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Driver {
	/// <summary>
	/// Each build writes an exe (Worker/RuntimeTestGenerator.cs) whose methods get their max stack from that
	/// build's dnlib. The exe is then run on every runtime available here. A method that the baseline's exe
	/// runs fine but the candidate's exe doesn't is a regression.
	/// </summary>
	static class RuntimeTest {
		sealed class Runtime {
			public string Name;
			public string Program;
			public bool PassExePath;    // false: the exe itself is the program (.NET Framework on Windows)
		}

		public static void Run(TestContext context) {
			var report = context.Report;
			report.Heading(2, "Runtime test");

			var runtimes = FindRuntimes();
			report.Line("Runtimes: " + string.Join(", ", runtimes.Select(r => r.Name)) +
				(runtimes.Any(r => r.Name == "Mono") ? "" : ". (Mono not found in PATH. It checks more than CoreCLR, e.g. dead exception handlers, so install it if you can.)"));
			report.Line();

			// For each build: method name -> max stack written, and runtime name -> (method name -> result)
			var maxStacks = new Dictionary<string, Dictionary<string, string>>();
			var results = new Dictionary<string, Dictionary<string, Dictionary<string, string>>>();
			var methodNames = new List<string>();

			foreach (var build in context.BothBuilds) {
				string dir = Path.Combine(context.OutputDir, "runtime", build.Side);
				Directory.CreateDirectory(dir);
				string exePath = Path.Combine(dir, "RuntimeTest.exe");
				var generate = build.RunCommand(TimeSpan.FromMinutes(2), "make-runtime-test", exePath);
				if (!generate.Succeeded)
					throw new FatalException($"make-runtime-test failed with the {build.Side} build:\n{generate.Output}\n{generate.Error}");
				WriteCoreClrRuntimeConfig(exePath);

				maxStacks[build.Side] = ParseLines(generate.Output, '\t');
				if (methodNames.Count == 0)
					methodNames.AddRange(maxStacks[build.Side].Keys);

				results[build.Side] = new Dictionary<string, Dictionary<string, string>>();
				foreach (var runtime in runtimes) {
					var arguments = runtime.PassExePath ? new[] { exePath } : Array.Empty<string>();
					var run = ProcessRunner.Run(runtime.PassExePath ? runtime.Program : exePath, arguments, TimeSpan.FromMinutes(1));
					File.WriteAllText(Path.Combine(dir, runtime.Name.Replace(' ', '_') + "-output.txt"), run.Output + run.Error);
					var perMethod = ParseLines(run.Output, ':');
					// A method with no output line means the whole process died before reaching it.
					foreach (var name in methodNames) {
						if (!perMethod.ContainsKey(name))
							perMethod[name] = run.TimedOut ? "TIMEOUT" : $"no output (exit code {run.ExitCode})";
					}
					results[build.Side][runtime.Name] = perMethod;
				}
			}

			var headers = new List<string> { "Method", "maxstack baseline / candidate" };
			headers.AddRange(runtimes.Select(r => $"{r.Name} baseline / candidate"));
			headers.Add("Verdict");

			var rows = new List<string[]>();
			foreach (var name in methodNames) {
				var row = new List<string> { name, $"{maxStacks["baseline"][name]} / {maxStacks["candidate"].GetValueOrDefault(name, "?")}" };
				bool regression = false, failsOnBoth = false;
				foreach (var runtime in runtimes) {
					string baselineResult = results["baseline"][runtime.Name][name];
					string candidateResult = results["candidate"][runtime.Name].GetValueOrDefault(name, "?");
					row.Add($"{baselineResult} / {candidateResult}");
					if (baselineResult == "OK" && candidateResult != "OK")
						regression = true;
					if (baselineResult != "OK" && candidateResult != "OK")
						failsOnBoth = true;
				}
				row.Add(regression ? "**FAIL**" : failsOnBoth ? "fails on both (the runtime rejects this code)" : "ok");
				rows.Add(row.ToArray());
			}
			report.Table(headers.ToArray(), rows);
		}

		static List<Runtime> FindRuntimes() {
			var runtimes = new List<Runtime> { new Runtime { Name = "CoreCLR", Program = "dotnet", PassExePath = true } };
			string mono = ProcessRunner.FindInPath("mono");
			if (mono is not null)
				runtimes.Add(new Runtime { Name = "Mono", Program = mono, PassExePath = true });
			if (OperatingSystem.IsWindows())
				runtimes.Add(new Runtime { Name = ".NET Framework", PassExePath = false });
			return runtimes;
		}

		/// <summary>"dotnet RuntimeTest.exe" needs this file next to the exe to know which runtime to use.</summary>
		static void WriteCoreClrRuntimeConfig(string exePath) {
			string configPath = Path.ChangeExtension(exePath, ".runtimeconfig.json");
			File.WriteAllText(configPath,
				"{\"runtimeOptions\":{\"tfm\":\"net8.0\",\"rollForward\":\"LatestMajor\",\"framework\":{\"name\":\"Microsoft.NETCore.App\",\"version\":\"8.0.0\"}}}");
		}

		/// <summary>Parses "name SEPARATOR value" lines.</summary>
		static Dictionary<string, string> ParseLines(string output, char separator) {
			var values = new Dictionary<string, string>();
			foreach (var line in output.Split('\n')) {
				int index = line.IndexOf(separator);
				if (index > 0)
					values[line.Substring(0, index).Trim()] = line.Substring(index + 1).Trim();
			}
			return values;
		}
	}
}
