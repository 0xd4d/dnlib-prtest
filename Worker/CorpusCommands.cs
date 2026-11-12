using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace Worker {
	/// <summary>
	/// Commands that run on real assemblies from the NuGet corpus.
	/// </summary>
	static class CorpusCommands {
		/// <summary>
		/// Computes max stack for every method body in the file, the way the writer does.
		/// Result: OK TAB methodCount TAB digest TAB calculatorMilliseconds TAB calculatorAllocatedBytes
		///
		/// The digest is a SHA-1 of "token ok maxStack" for every method. If the baseline and the candidate
		/// print the same digest, they computed the same result for every method in the file. When the digests
		/// differ, the driver runs method-details to find out which methods differ.
		/// </summary>
		public static string CalculateMaxStackOfAllMethods(string path) {
			ModuleDefMD module = TryLoad(path, out string loadError);
			if (module is null)
				return "LOADFAIL\t" + loadError;
			using (module) {
				var calculator = new WriterStyleCalculator();
				var methodResults = new StringBuilder();
				int methodCount = 0;
				var stopwatch = new Stopwatch();
				long allocatedBytes = 0;

				foreach (var (method, body) in MethodsWithBodies(module, methodResults)) {
					methodCount++;
					long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
					stopwatch.Start();
					string result;
					try {
						bool ok = calculator.Calculate(body.Instructions, body.ExceptionHandlers, out uint maxStack);
						result = $"{ok} {maxStack}";
					}
					catch (Exception ex) {
						result = "exception " + ex.GetType().Name;
						calculator = new WriterStyleCalculator();
					}
					stopwatch.Stop();
					allocatedBytes += GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
					methodResults.Append($"{method.MDToken.Raw:X8} {result}\n");
				}

				string digest = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(methodResults.ToString())));
				return $"OK\t{methodCount}\t{digest}\t{stopwatch.Elapsed.TotalMilliseconds:F3}\t{allocatedBytes}";
			}
		}

		/// <summary>
		/// Writes one line per method: token, result, instruction count, handler count, full name.
		/// The driver diffs the baseline and candidate versions of this file.
		/// </summary>
		public static void WriteMethodDetails(string filePath, string outputPath) {
			using var output = new StreamWriter(outputPath);
			ModuleDefMD module = TryLoad(filePath, out string loadError);
			if (module is null) {
				output.WriteLine("LOADFAIL " + loadError);
				return;
			}
			using (module) {
				var calculator = new WriterStyleCalculator();
				var bodyErrors = new StringBuilder();
				foreach (var (method, body) in MethodsWithBodies(module, bodyErrors)) {
					string result;
					try {
						bool ok = calculator.Calculate(body.Instructions, body.ExceptionHandlers, out uint maxStack);
						uint written = WriterStyleCalculator.ValueWrittenByWriter(ok, maxStack);
						result = $"ok={ok} maxStack={maxStack} writerEmits={written}";
					}
					catch (Exception ex) {
						result = "exception " + ex.GetType().Name;
						calculator = new WriterStyleCalculator();
					}
					output.WriteLine($"{method.MDToken.Raw:X8}\t{result}\tinstructions={body.Instructions.Count} handlers={body.ExceptionHandlers.Count} header={body.MaxStack}\t{method.FullName}");
				}
				output.Write(bodyErrors);
			}
		}

		/// <summary>
		/// Loads the file and saves it to memory.
		/// Result: OK TAB saveMilliseconds TAB allocatedBytes TAB sha1OfOutput
		///     or: ERR TAB saveMilliseconds TAB allocatedBytes TAB errorMessage
		/// </summary>
		public static string LoadAndSave(string path) {
			ModuleDefMD module = TryLoad(path, out string loadError);
			if (module is null)
				return "LOADFAIL\t" + loadError;
			using (module) {
				var output = new MemoryStream();
				long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
				var stopwatch = Stopwatch.StartNew();
				string status, detail;
				try {
					if (module.IsILOnly)
						module.Write(output);
					else
						module.NativeWrite(output);
					status = "OK";
					detail = Convert.ToHexString(SHA1.HashData(output.ToArray()));
				}
				catch (Exception ex) {
					status = "ERR";
					detail = Text.OneLine(ex.GetType().Name + ": " + FirstLine(ex.Message));
				}
				stopwatch.Stop();
				long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
				return $"{status}\t{stopwatch.Elapsed.TotalMilliseconds:F1}\t{allocated}\t{detail}";
			}
		}

		/// <summary>
		/// Times the calculator on all method bodies of the file. Bodies are read first so that only the
		/// calculator is timed. Each iteration uses a new calculator, like the writer does once per module.
		/// Result: OK TAB methodCount TAB bestMilliseconds TAB allocatedBytesPerIteration
		/// </summary>
		public static string TimeCalculator(string path, int iterations) {
			ModuleDefMD module = TryLoad(path, out string loadError);
			if (module is null)
				return "LOADFAIL\t" + loadError;
			using (module) {
				var bodies = MethodsWithBodies(module, new StringBuilder()).Select(m => m.body).ToList();
				double bestMilliseconds = double.MaxValue;
				long allocatedBytes = 0;
				uint sumToKeepResultsAlive = 0;
				for (int iteration = 0; iteration < iterations; iteration++) {
					long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
					var stopwatch = Stopwatch.StartNew();
					var calculator = new WriterStyleCalculator();
					foreach (var body in bodies) {
						try {
							calculator.Calculate(body.Instructions, body.ExceptionHandlers, out uint maxStack);
							sumToKeepResultsAlive += maxStack;
						}
						catch {
							calculator = new WriterStyleCalculator();
						}
					}
					stopwatch.Stop();
					bestMilliseconds = Math.Min(bestMilliseconds, stopwatch.Elapsed.TotalMilliseconds);
					allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
				}
				GC.KeepAlive(sumToKeepResultsAlive);
				return $"OK\t{bodies.Count}\t{bestMilliseconds:F3}\t{allocatedBytes}";
			}
		}

		static ModuleDefMD TryLoad(string path, out string error) {
			try {
				error = null;
				return ModuleDefMD.Load(path);
			}
			catch (Exception ex) {
				error = Text.OneLine(ex.GetType().Name + ": " + FirstLine(ex.Message));
				return null;
			}
		}

		/// <summary>
		/// Every method with a non-empty CIL body. Methods whose body can't be read are skipped and
		/// reported in <paramref name="errors"/> so they still affect the digest.
		/// </summary>
		static IEnumerable<(MethodDef method, CilBody body)> MethodsWithBodies(ModuleDef module, StringBuilder errors) {
			List<MethodDef> methods;
			try {
				methods = module.GetTypes().SelectMany(type => type.Methods).ToList();
			}
			catch (Exception ex) {
				errors.Append($"could not enumerate types: {ex.GetType().Name}\n");
				yield break;
			}
			foreach (var method in methods) {
				CilBody body = null;
				try {
					if (method.HasBody)
						body = method.Body;
				}
				catch (Exception ex) {
					errors.Append($"{method.MDToken.Raw:X8} could not read body: {ex.GetType().Name}\n");
				}
				if (body is not null && body.Instructions.Count > 0)
					yield return (method, body);
			}
		}

		static string FirstLine(string text) {
			int newLine = text.IndexOfAny(new[] { '\r', '\n' });
			return newLine < 0 ? text : text.Substring(0, newLine);
		}
	}
}
