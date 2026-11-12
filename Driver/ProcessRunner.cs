using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Driver {
	sealed class ProcessResult {
		public int ExitCode;
		public bool TimedOut;
		public string Output;
		public string Error;
		public bool Succeeded => !TimedOut && ExitCode == 0;
	}

	/// <summary>
	/// Runs a program to completion and captures its output. Used for short jobs like builds and git.
	/// The long-running worker jobs use <see cref="WorkerRunner"/> instead.
	/// </summary>
	static class ProcessRunner {
		public static ProcessResult Run(string fileName, IEnumerable<string> arguments, TimeSpan timeout, string workingDirectory = null) {
			var startInfo = new ProcessStartInfo(fileName) {
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
			};
			foreach (var argument in arguments)
				startInfo.ArgumentList.Add(argument);

			var output = new StringBuilder();
			var error = new StringBuilder();
			using var process = new Process { StartInfo = startInfo };
			process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
			process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (error) error.AppendLine(e.Data); };
			try {
				process.Start();
			}
			catch (Exception ex) {
				return new ProcessResult { ExitCode = -1, Output = "", Error = $"Could not start {fileName}: {ex.Message}" };
			}
			process.BeginOutputReadLine();
			process.BeginErrorReadLine();

			bool timedOut = !process.WaitForExit((int)Math.Min(int.MaxValue, timeout.TotalMilliseconds));
			if (timedOut) {
				try { process.Kill(entireProcessTree: true); } catch { }
			}
			process.WaitForExit();
			return new ProcessResult {
				ExitCode = timedOut ? -1 : process.ExitCode,
				TimedOut = timedOut,
				Output = output.ToString(),
				Error = error.ToString(),
			};
		}

		/// <summary>Returns the full path of a program found in PATH, or null.</summary>
		public static string FindInPath(string programName) {
			var extensions = OperatingSystem.IsWindows() ? new[] { ".exe", ".cmd", ".bat" } : new[] { "" };
			foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)) {
				foreach (var extension in extensions) {
					if (dir.Length == 0)
						continue;
					string candidate = Path.Combine(dir, programName + extension);
					if (File.Exists(candidate))
						return candidate;
				}
			}
			return null;
		}
	}
}
