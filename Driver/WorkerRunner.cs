using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace Driver {
	sealed class WorkerRunSettings {
		/// <summary>Worker processes running at the same time.</summary>
		public int Jobs;
		/// <summary>Items given to one worker process. 1 = a fresh process per item (used for test cases).</summary>
		public int ItemsPerProcess;
		/// <summary>A worker that spends longer than this on one item (or on starting up) is killed.</summary>
		public TimeSpan ItemTimeout;
		/// <summary>A worker whose memory use goes above this is killed.</summary>
		public long MaxMemoryBytes;
	}

	/// <summary>
	/// Runs a worker command over a list of items (file paths or test case names) in parallel processes.
	///
	/// The worker prints "BEGIN TAB i" before item i and "RESULT TAB i TAB ..." after it. When a worker
	/// crashes (e.g. stack overflow), runs too long or uses too much memory, the item it was working on gets
	/// a CRASH, TIMEOUT or MEMORY result and a new worker continues with the next item. So one bad input
	/// can't stop the whole run, and the report says exactly which input caused the problem.
	/// </summary>
	static class WorkerRunner {
		/// <summary>
		/// Returns one result per item: the fields after "RESULT TAB i TAB", or "CRASH/TIMEOUT/MEMORY TAB details".
		/// </summary>
		public static string[] Run(WorkerBuild worker, string command, IReadOnlyList<string> items, WorkerRunSettings settings,
			string workDir, params string[] extraArguments) {
			Directory.CreateDirectory(workDir);
			string itemsFile = Path.Combine(workDir, $"{worker.Side}-{command}-items.txt");
			File.WriteAllLines(itemsFile, items);
			string stderrLog = Path.Combine(workDir, $"{worker.Side}-{command}-stderr.txt");
			File.AppendAllText(stderrLog, $"=== {DateTime.Now:yyyy-MM-dd HH:mm:ss} {worker.Side} {command} ===\n");

			var job = new Job {
				Worker = worker,
				Command = command,
				ItemsFile = itemsFile,
				StderrLog = stderrLog,
				ExtraArguments = extraArguments,
				Settings = settings,
				Results = new string[items.Count],
			};

			var ranges = new ConcurrentQueue<(int first, int end)>();
			for (int first = 0; first < items.Count; first += settings.ItemsPerProcess)
				ranges.Enqueue((first, Math.Min(first + settings.ItemsPerProcess, items.Count)));

			var threads = new List<Thread>();
			for (int i = 0; i < Math.Min(settings.Jobs, ranges.Count); i++) {
				var thread = new Thread(() => {
					while (ranges.TryDequeue(out var range))
						RunRange(job, range.first, range.end);
				});
				thread.Start();
				threads.Add(thread);
			}

			var stopwatch = Stopwatch.StartNew();
			string label = $"  {worker.Side} {command}";
			while (threads.Any(t => t.IsAlive)) {
				Console.Write($"\r{label}: {job.FinishedCount}/{items.Count} ({stopwatch.Elapsed:hh\\:mm\\:ss}{EstimateTimeLeft(job.FinishedCount, items.Count, stopwatch.Elapsed)})  ");
				Thread.Sleep(1000);
			}
			Console.WriteLine($"\r{label}: {items.Count}/{items.Count} ({stopwatch.Elapsed:hh\\:mm\\:ss})  ");
			if (job.FatalError is not null)
				throw new FatalException(job.FatalError);
			return job.Results;
		}

		/// <summary>
		/// ", about 00:12:00 left", based on the speed so far. Corpus files are in a random-looking order, so big
		/// and small files are spread evenly and the speed so far is a fair guess for the rest.
		/// </summary>
		static string EstimateTimeLeft(int finished, int total, TimeSpan elapsed) {
			if (finished == 0 || finished >= total || elapsed < TimeSpan.FromSeconds(10))
				return "";
			var timeLeft = TimeSpan.FromSeconds(Math.Round(elapsed.TotalSeconds * (total - finished) / finished));
			return $", about {timeLeft:hh\\:mm\\:ss} left";
		}

		sealed class Job {
			public WorkerBuild Worker;
			public string Command;
			public string ItemsFile;
			public string StderrLog;
			public string[] ExtraArguments;
			public WorkerRunSettings Settings;
			public string[] Results;
			public int FinishedCount;
			public string FatalError;
		}

		/// <summary>Runs items first..end-1, starting a new worker whenever one dies.</summary>
		static void RunRange(Job job, int first, int end) {
			int next = first;
			while (next < end && job.FatalError is null)
				next = RunOneWorkerProcess(job, next, end);
		}

		/// <summary>
		/// Starts one worker for items first..end-1 and waits until it exits or is killed.
		/// Returns the index of the first item that still needs to run.
		/// </summary>
		static int RunOneWorkerProcess(Job job, int first, int end) {
			var startInfo = new ProcessStartInfo("dotnet") {
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
			};
			startInfo.ArgumentList.Add(job.Worker.WorkerDll);
			startInfo.ArgumentList.Add(job.Command);
			startInfo.ArgumentList.Add(job.ItemsFile);
			startInfo.ArgumentList.Add(first.ToString());
			startInfo.ArgumentList.Add(end.ToString());
			foreach (var argument in job.ExtraArguments)
				startInfo.ArgumentList.Add(argument);

			var gate = new object();
			int currentItem = -1;                 // the item between BEGIN and RESULT, or -1
			int nextItem = first;                 // the item after the last RESULT
			var sinceLastEvent = Stopwatch.StartNew(); // restarted on every BEGIN and RESULT
			var lastErrorLines = new Queue<string>();

			using var process = new Process { StartInfo = startInfo };
			process.OutputDataReceived += (_, e) => {
				if (e.Data is null)
					return;
				string[] parts = e.Data.Split('\t', 3);
				bool hasIndex = parts.Length >= 2 && int.TryParse(parts[1], out int index) && index >= first && index < end;
				if (parts[0] == "BEGIN" && hasIndex) {
					lock (gate) {
						currentItem = int.Parse(parts[1]);
						sinceLastEvent.Restart();
					}
				}
				else if (parts[0] == "RESULT" && hasIndex) {
					index = int.Parse(parts[1]);
					job.Results[index] = parts.Length > 2 ? parts[2] : "";
					Interlocked.Increment(ref job.FinishedCount);
					lock (gate) {
						currentItem = -1;
						nextItem = index + 1;
						sinceLastEvent.Restart();
					}
				}
				else {
					// Not part of the protocol, e.g. something dnlib or the runtime printed. Keep it for debugging.
					lock (job)
						File.AppendAllText(job.StderrLog, $"[{job.Worker.Side} items {first}..{end - 1}, stdout] {e.Data}\n");
				}
			};
			process.ErrorDataReceived += (_, e) => {
				if (e.Data is null)
					return;
				lock (gate) {
					lastErrorLines.Enqueue(e.Data);
					if (lastErrorLines.Count > 5)
						lastErrorLines.Dequeue();
				}
				lock (job)
					File.AppendAllText(job.StderrLog, $"[{job.Worker.Side} items {first}..{end - 1}] {e.Data}\n");
			};

			process.Start();
			process.BeginOutputReadLine();
			process.BeginErrorReadLine();

			// Watch the worker until it exits. Kill it if it hangs or uses too much memory.
			// killedItem is the item the worker was busy with when we decided to kill it (-1 = between items).
			string killReason = null;
			int killedItem = -1;
			while (!process.WaitForExit(200)) {
				long workingSet = 0;
				try {
					process.Refresh();
					workingSet = process.WorkingSet64;
				}
				catch (InvalidOperationException) {
					// The process exited between WaitForExit and Refresh.
				}
				lock (gate) {
					if (sinceLastEvent.Elapsed > job.Settings.ItemTimeout)
						killReason = $"TIMEOUT\tstill running after {job.Settings.ItemTimeout.TotalSeconds:F0} s";
					else if (workingSet > job.Settings.MaxMemoryBytes)
						killReason = $"MEMORY\tused more than {job.Settings.MaxMemoryBytes / (1024 * 1024)} MB";
					if (killReason is not null) {
						killedItem = currentItem;
						try { process.Kill(entireProcessTree: true); } catch { }
						break;
					}
				}
			}
			process.WaitForExit(); // also waits until all output events have been handled

			lock (gate) {
				if (currentItem >= 0) {
					if (killReason is not null && currentItem != killedItem) {
						// The worker finished the item we meant to kill it for and had just started the next one
						// when the kill landed. That next item isn't to blame, so run it again.
						return currentItem;
					}
					// The worker died while working on currentItem. Record why and continue after it.
					string crash = killReason ?? $"CRASH\texit code {process.ExitCode}: {string.Join(" | ", lastErrorLines)}";
					job.Results[currentItem] = crash.Replace('\r', ' ').Replace('\n', ' ');
					Interlocked.Increment(ref job.FinishedCount);
					return currentItem + 1;
				}
				if (nextItem < end) {
					// The worker stopped between two items (e.g. killed for memory a previous item used).
					// Continue with a new worker, unless this one didn't finish a single item: then
					// something is wrong with the worker itself and restarting would loop forever.
					if (nextItem > first)
						return nextItem;
					job.FatalError = $"The {job.Worker.Side} worker stopped without finishing item {nextItem} " +
						$"(exit code {process.ExitCode}{(killReason is null ? "" : ", " + killReason.Replace('\t', ' '))}):\n" +
						string.Join("\n", lastErrorLines);
				}
				return end;
			}
		}
	}
}
