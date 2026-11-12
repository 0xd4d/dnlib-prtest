using System;
using System.Globalization;

namespace Worker {
	/// <summary>
	/// Entry point of the worker. The driver starts the worker with one of these commands:
	///
	///   list-cases                                   print "name TAB category TAB description" for every test case
	///   case          ITEMS FIRST END                run test cases (each line of ITEMS is a case name)
	///   methods       ITEMS FIRST END                compute max stack for every method in each file (ITEMS = file paths)
	///   save          ITEMS FIRST END                load and save each file
	///   perf          ITEMS FIRST END ITERATIONS     time the max stack calculator on each file
	///   method-details FILE OUTPUT                   write one line per method of FILE (used to explain differences)
	///   make-runtime-test OUTPUT.exe                 write an exe whose methods are run on real runtimes
	///
	/// The item commands process lines FIRST..END-1 of ITEMS and print "BEGIN TAB index" before and
	/// "RESULT TAB index TAB ..." after each item. If the process crashes or hangs, the driver knows which
	/// item caused it and restarts the worker at the next item.
	/// </summary>
	static class Program {
		static int Main(string[] args) {
			// Numbers are written by one program and parsed by the other, so always use "1.5", never "1,5".
			CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
			CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

			if (args.Length == 0) {
				Console.Error.WriteLine("Missing command. See Worker/Program.cs for the list of commands.");
				return 1;
			}

			string command = args[0];
			switch (command) {
			case "list-cases":
				TestCaseList.PrintAll();
				return 0;

			case "case":
				return ItemLoop.Run(args, TestCaseList.RunByName);

			case "methods":
				return ItemLoop.Run(args, CorpusCommands.CalculateMaxStackOfAllMethods);

			case "save":
				return ItemLoop.Run(args, CorpusCommands.LoadAndSave);

			case "perf":
				int iterations = int.Parse(args[4]);
				return ItemLoop.Run(args, path => CorpusCommands.TimeCalculator(path, iterations));

			case "method-details":
				ItemLoop.RunOnWorkThread(() => CorpusCommands.WriteMethodDetails(filePath: args[1], outputPath: args[2]));
				return 0;

			case "make-runtime-test":
				RuntimeTestGenerator.Generate(outputPath: args[1]);
				return 0;

			default:
				Console.Error.WriteLine($"Unknown command: {command}");
				return 1;
			}
		}
	}
}
