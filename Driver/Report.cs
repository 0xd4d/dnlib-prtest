using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Driver {
	/// <summary>
	/// Writes the report as Markdown to report.md and to the console at the same time.
	/// </summary>
	sealed class Report : IDisposable {
		readonly StreamWriter file;

		public Report(string path) {
			file = new StreamWriter(path) { AutoFlush = true };
		}

		public void Line(string text = "") {
			file.WriteLine(text);
			Console.WriteLine(text);
		}

		public void Heading(int level, string title) {
			Line();
			Line(new string('#', level) + " " + title);
			Line();
		}

		public void Table(string[] headers, IEnumerable<string[]> rows) {
			Line("| " + string.Join(" | ", headers) + " |");
			Line("|" + string.Concat(headers.Select(_ => "---|")));
			foreach (var row in rows)
				Line("| " + string.Join(" | ", row.Select(EscapeCell)) + " |");
			Line();
		}

		static string EscapeCell(string text) => (text ?? "").Replace("|", "\\|");

		public void Dispose() => file.Dispose();
	}

	static class Format {
		public static string Milliseconds(double ms) => ms >= 10_000 ? $"{ms / 1000:F1} s" : $"{ms:F1} ms";

		public static string Megabytes(long bytes) => $"{bytes / (1024.0 * 1024):F1} MB";

		/// <summary>"2.50x" or "-" if the baseline is 0.</summary>
		public static string Ratio(double candidate, double baseline) => baseline > 0 ? $"{candidate / baseline:F2}x" : "-";
	}
}
