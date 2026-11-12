using System;
using System.IO;
using System.Threading;

namespace Worker {
	/// <summary>
	/// Runs one function per item (a file path or a test case name) and prints the results in the
	/// line format the driver reads:
	///
	///   BEGIN   TAB index
	///   RESULT  TAB index TAB fields...
	///
	/// A BEGIN without a RESULT tells the driver that this item crashed or hung the process.
	/// </summary>
	static class ItemLoop {
		/// <summary>
		/// The work runs on a thread with a 1 MB stack. That is the default main thread stack on
		/// Windows, so deep recursion fails here the same way it would for a typical Windows user,
		/// even on Linux and macOS where the main thread stack is 8 MB.
		/// </summary>
		const int WorkThreadStackSize = 1024 * 1024;

		public static int Run(string[] args, Func<string, string> processItem) {
			string itemsFile = args[1];
			int firstIndex = int.Parse(args[2]);
			int endIndex = int.Parse(args[3]);
			string[] items = File.ReadAllLines(itemsFile);

			RunOnWorkThread(() => {
				for (int index = firstIndex; index < endIndex && index < items.Length; index++) {
					Console.WriteLine($"BEGIN\t{index}");
					string result;
					try {
						result = processItem(items[index]);
					}
					catch (Exception ex) {
						result = "EXCEPTION\t" + Text.OneLine(ex.GetType().Name + ": " + ex.Message);
					}
					Console.WriteLine($"RESULT\t{index}\t{result}");
				}
			});
			return 0;
		}

		public static void RunOnWorkThread(Action work) {
			var thread = new Thread(() => work(), WorkThreadStackSize);
			thread.Start();
			thread.Join();
		}
	}

	static class Text {
		/// <summary>Makes a string safe to put in one tab-separated field.</summary>
		public static string OneLine(string text) {
			text ??= "";
			text = text.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
			const int MaxLength = 300;
			return text.Length <= MaxLength ? text : text.Substring(0, MaxLength) + "...";
		}
	}
}
