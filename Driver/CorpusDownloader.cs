using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Driver {
	/// <summary>
	/// Downloads the packages listed in nuget-packages.txt from nuget.org and keeps their .dll and .exe files.
	/// Files are only read by dnlib, never executed.
	///
	/// corpus/packages/&lt;id&gt;.&lt;version&gt;/   the assemblies of one package. "lib/net45/A.dll" is stored as "lib_net45_A.dll".
	/// corpus/files.txt                     unique assemblies (by SHA-1), sorted by SHA-1 so big and small files are mixed.
	///
	/// Running it again only downloads packages that are missing, so an interrupted download can be resumed.
	/// </summary>
	static class CorpusDownloader {
		/// <summary>Skip anything bigger than this inside a package (protects against zip bombs).</summary>
		const long MaxAssemblySize = 256L * 1024 * 1024;

		public static void Run(ToolPaths paths, int top, int jobs) {
			var packages = File.ReadAllLines(paths.PackageList)
				.Where(line => line.Trim().Length > 0)
				.Select(line => line.Trim().Split(' '))
				.Select(fields => (id: fields[0], version: fields[1]))
				.Take(top)
				.ToList();
			Directory.CreateDirectory(paths.PackagesDir);
			Console.WriteLine($"Downloading {packages.Count} packages to {paths.PackagesDir} ...");

			using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
			int done = 0;
			var failed = new List<string>();
			Parallel.ForEach(packages, new ParallelOptions { MaxDegreeOfParallelism = jobs }, package => {
				string error = DownloadPackage(http, package.id, package.version, paths.PackagesDir);
				if (error is not null) {
					lock (failed)
						failed.Add($"{package.id} {package.version}: {error}");
				}
				int count = Interlocked.Increment(ref done);
				if (count % 50 == 0 || count == packages.Count)
					Console.WriteLine($"  {count}/{packages.Count}");
			});

			foreach (var failure in failed)
				Console.WriteLine("  failed: " + failure);
			if (failed.Count > 0)
				Console.WriteLine($"{failed.Count} packages failed. Run download again to retry them.");

			WriteFileList(paths, packages);
		}

		static string PackageDirName(string id, string version) => $"{id}.{version}".ToLowerInvariant();

		/// <summary>Returns null on success, or an error message.</summary>
		static string DownloadPackage(HttpClient http, string id, string version, string packagesDir) {
			string targetDir = Path.Combine(packagesDir, PackageDirName(id, version));
			if (Directory.Exists(targetDir))
				return null; // already downloaded

			string lowerId = id.ToLowerInvariant();
			string lowerVersion = version.ToLowerInvariant();
			string url = $"https://api.nuget.org/v3-flatcontainer/{lowerId}/{lowerVersion}/{lowerId}.{lowerVersion}.nupkg";

			byte[] nupkg = null;
			string lastError = null;
			for (int attempt = 1; attempt <= 3 && nupkg is null; attempt++) {
				try {
					nupkg = http.GetByteArrayAsync(url).GetAwaiter().GetResult();
				}
				catch (Exception ex) {
					lastError = ex.Message;
					Thread.Sleep(2000 * attempt);
				}
			}
			if (nupkg is null)
				return lastError;

			// Extract to a temporary folder first, so a half-extracted package never looks complete.
			string tempDir = targetDir + ".tmp";
			if (Directory.Exists(tempDir))
				Directory.Delete(tempDir, recursive: true);
			Directory.CreateDirectory(tempDir);
			try {
				using var zip = new ZipArchive(new MemoryStream(nupkg), ZipArchiveMode.Read);
				foreach (var entry in zip.Entries) {
					if (!IsAssembly(entry.FullName) || entry.Length > MaxAssemblySize)
						continue;
					// Flatten the path so nothing can be written outside tempDir.
					string flatName = entry.FullName.Replace('/', '_').Replace('\\', '_').Replace(':', '_');
					entry.ExtractToFile(Path.Combine(tempDir, flatName), overwrite: true);
				}
			}
			catch (Exception ex) {
				Directory.Delete(tempDir, recursive: true);
				return "bad package: " + ex.Message;
			}
			Directory.Move(tempDir, targetDir);
			return null;
		}

		static bool IsAssembly(string path) =>
			path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

		/// <summary>
		/// Writes corpus/files.txt: one file per unique SHA-1, ordered by SHA-1. The order looks random but is
		/// the same on every machine, so "--files 3000" picks the same files for everybody.
		/// </summary>
		static void WriteFileList(ToolPaths paths, List<(string id, string version)> packages) {
			Console.WriteLine("Hashing files to remove duplicates...");
			var allFiles = packages
				.Select(p => Path.Combine(paths.PackagesDir, PackageDirName(p.id, p.version)))
				.Where(Directory.Exists)
				.SelectMany(dir => Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
				.OrderBy(path => path, StringComparer.Ordinal)
				.ToList();

			var hashes = new string[allFiles.Count];
			Parallel.For(0, allFiles.Count, i => {
				using var stream = File.OpenRead(allFiles[i]);
				hashes[i] = Convert.ToHexString(SHA1.HashData(stream));
			});

			var uniqueFiles = allFiles
				.Select((path, i) => (path, hash: hashes[i]))
				.GroupBy(file => file.hash)
				.Select(group => group.First())
				.OrderBy(file => file.hash, StringComparer.Ordinal)
				.Select(file => file.path)
				.ToList();
			File.WriteAllLines(paths.CorpusFileList, uniqueFiles);
			Console.WriteLine($"{allFiles.Count:N0} files, {uniqueFiles.Count:N0} unique. List written to {paths.CorpusFileList}");
		}
	}
}
