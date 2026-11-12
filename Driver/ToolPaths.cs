using System;
using System.IO;

namespace Driver {
	/// <summary>
	/// Folders used by the test suite. Everything is relative to the suite's root folder (the folder
	/// with nuget-packages.txt), so it doesn't matter which folder you run it from.
	/// </summary>
	sealed class ToolPaths {
		public readonly string Root;

		public ToolPaths() {
			// The driver runs from Driver/bin/..., so walk up until we find the root folder.
			var dir = new DirectoryInfo(AppContext.BaseDirectory);
			while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "nuget-packages.txt")))
				dir = dir.Parent;
			Root = dir?.FullName ?? throw new FatalException("Could not find nuget-packages.txt above " + AppContext.BaseDirectory);
		}

		/// <summary>"id version" per line, most downloaded first.</summary>
		public string PackageList => Path.Combine(Root, "nuget-packages.txt");

		/// <summary>One folder per downloaded package with the .dll and .exe files it contains.</summary>
		public string PackagesDir => Path.Combine(Root, "corpus", "packages");

		/// <summary>The unique corpus files (full paths), in a fixed pseudo-random order.</summary>
		public string CorpusFileList => Path.Combine(Root, "corpus", "files.txt");

		public string WorkerProject => Path.Combine(Root, "Worker", "Worker.csproj");

		/// <summary>The two worker builds go to build/baseline and build/candidate.</summary>
		public string BuildDir(string side) => Path.Combine(Root, "build", side);

		public string Results => Path.Combine(Root, "results");
	}
}
