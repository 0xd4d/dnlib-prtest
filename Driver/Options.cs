using System;
using System.Collections.Generic;
using System.IO;

namespace Driver {
	/// <summary>
	/// Command line: the first argument is the command, the rest are "--name value" pairs.
	/// </summary>
	sealed class Options {
		public string Command;
		readonly Dictionary<string, string> values = new Dictionary<string, string>();

		static readonly string[] KnownOptions = {
			"top", "jobs", "output", "baseline", "candidate", "files", "timeout",
			"max-memory-mb", "iterations", "rounds", "detail-files",
		};

		public static Options Parse(string[] args) {
			if (args.Length == 0)
				throw new UsageException("Missing command");
			var options = new Options { Command = args[0] };
			for (int i = 1; i < args.Length; i++) {
				string arg = args[i];
				if (!arg.StartsWith("--"))
					throw new UsageException($"Expected an option starting with --, got '{arg}'");
				if (i + 1 >= args.Length)
					throw new UsageException($"Missing value for {arg}");
				string name = arg.Substring(2);
				if (Array.IndexOf(KnownOptions, name) < 0)
					throw new UsageException($"Unknown option {arg}");
				options.values[name] = args[++i];
			}
			return options;
		}

		public string GetString(string name, string defaultValue) =>
			values.TryGetValue(name, out var value) ? value : defaultValue;

		public int GetInt(string name, int defaultValue) {
			if (!values.TryGetValue(name, out var value))
				return defaultValue;
			if (!int.TryParse(value, out int number) || number <= 0)
				throw new UsageException($"--{name} must be a positive number, got '{value}'");
			return number;
		}

		/// <summary>Returns the full path of a required dnlib checkout option.</summary>
		public string GetDnlibDir(string name) {
			if (!values.TryGetValue(name, out var dir))
				throw new UsageException($"Missing --{name} <dnlib checkout>");
			dir = Path.GetFullPath(dir);
			if (!File.Exists(Path.Combine(dir, "src", "dnlib.csproj")))
				throw new UsageException($"--{name}: {dir} is not a dnlib checkout (src/dnlib.csproj is missing)");
			return dir;
		}

		/// <summary>
		/// One worker per CPU, but at most one per GB of RAM: a worker saving a big assembly can use
		/// about 1 GB, and too many at once makes the OS kill them.
		/// </summary>
		public static int DefaultJobs {
			get {
				long totalMemoryGB = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024L * 1024 * 1024);
				return (int)Math.Max(1, Math.Min(Environment.ProcessorCount, totalMemoryGB));
			}
		}
	}

	sealed class UsageException : Exception {
		public UsageException(string message) : base(message) { }
	}
}
