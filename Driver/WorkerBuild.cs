using System;
using System.IO;
using System.Linq;

namespace Driver {
	/// <summary>
	/// One build of the Worker project against one dnlib checkout.
	/// </summary>
	sealed class WorkerBuild {
		/// <summary>"baseline" or "candidate".</summary>
		public string Side;
		public string DnlibDir;
		public string WorkerDll;
		/// <summary>Git commit of the dnlib checkout, for the report.</summary>
		public string Version;

		public static WorkerBuild Build(string side, string dnlibDir, ToolPaths paths) {
			Console.WriteLine($"Building the worker against the {side} dnlib ({dnlibDir})...");
			string artifactsDir = paths.BuildDir(side);
			var result = ProcessRunner.Run("dotnet", new[] {
				"build", paths.WorkerProject,
				"-c", "Release",
				"-p:DnlibDir=" + dnlibDir,
				// Separate obj/ and bin/ folders for each side, so the two builds don't overwrite each other.
				"--artifacts-path", artifactsDir,
				"-nologo",
				"-v:q",
			}, TimeSpan.FromMinutes(10));
			if (!result.Succeeded)
				throw new FatalException($"Building the worker against {dnlibDir} failed:\n{result.Output}\n{result.Error}");

			string workerDll = Path.Combine(artifactsDir, "bin", "Worker", "release", "Worker.dll");
			if (!File.Exists(workerDll))
				throw new FatalException("The build succeeded but this file is missing: " + workerDll);

			return new WorkerBuild {
				Side = side,
				DnlibDir = dnlibDir,
				WorkerDll = workerDll,
				Version = DescribeGitVersion(dnlibDir),
			};
		}

		/// <summary>Runs a non-item worker command (e.g. list-cases) and returns its output.</summary>
		public ProcessResult RunCommand(TimeSpan timeout, params string[] arguments) =>
			ProcessRunner.Run("dotnet", new[] { WorkerDll }.Concat(arguments), timeout);

		static string DescribeGitVersion(string dir) {
			var commit = ProcessRunner.Run("git", new[] { "-C", dir, "log", "-1", "--format=%h %s" }, TimeSpan.FromSeconds(30));
			if (!commit.Succeeded)
				return "not a git checkout";
			var status = ProcessRunner.Run("git", new[] { "-C", dir, "status", "--porcelain", "--untracked-files=no" }, TimeSpan.FromSeconds(30));
			string dirty = status.Succeeded && status.Output.Trim().Length > 0 ? ", with uncommitted changes" : "";
			return "commit " + commit.Output.Trim() + dirty;
		}
	}
}
