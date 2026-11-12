using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Writer;

namespace Worker {
	/// <summary>
	/// One test case. The driver runs each case in its own worker process, once with the baseline dnlib
	/// and once with the candidate dnlib, and compares the two results.
	/// </summary>
	sealed class TestCase {
		public string Name;
		public string Category;
		public string Description;
		public Func<Meter, CaseOutcome> Run;
	}

	/// <summary>
	/// What a test case found.
	/// </summary>
	sealed class CaseOutcome {
		/// <summary>Human readable result, e.g. "ok=True maxStack=3 writerEmits=3".</summary>
		public string Text;

		/// <summary>
		/// null if the case has no expectation, "" if the expectation was met, otherwise why it wasn't.
		/// Only cases based on a known bug or a clear rule have an expectation. For everything else the
		/// driver reports whether the candidate differs from the baseline, and a human decides.
		/// </summary>
		public string CheckFailure;

		public static CaseOutcome WithoutCheck(string text) => new CaseOutcome { Text = text, CheckFailure = null };

		public static CaseOutcome WithCheck(string text, bool passed, string failureReason) =>
			new CaseOutcome { Text = text, CheckFailure = passed ? "" : failureReason };
	}

	/// <summary>
	/// Measures time and allocations of only the interesting part of a test case (not the setup).
	/// </summary>
	sealed class Meter {
		public double Milliseconds;
		public long AllocatedBytes;

		public T Measure<T>(Func<T> work) {
			GC.Collect();
			long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
			var stopwatch = Stopwatch.StartNew();
			T result = work();
			stopwatch.Stop();
			Milliseconds += stopwatch.Elapsed.TotalMilliseconds;
			AllocatedBytes += GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
			return result;
		}
	}

	/// <summary>
	/// The result of running MaxStackCalculator on one method body.
	/// </summary>
	sealed class CalculatorResult {
		public bool Ok;
		public uint MaxStack;
		public string Exception;
		public uint WriterEmits => WriterStyleCalculator.ValueWrittenByWriter(Ok, MaxStack);

		public override string ToString() => Exception is not null
			? "exception " + Exception
			: $"ok={Ok} maxStack={MaxStack} writerEmits={WriterEmits}";

		public static CalculatorResult Calculate(MethodIl il) {
			try {
				var calculator = new WriterStyleCalculator();
				bool ok = calculator.Calculate(il.Instructions, il.Handlers, out uint maxStack);
				return new CalculatorResult { Ok = ok, MaxStack = maxStack };
			}
			catch (Exception ex) {
				return new CalculatorResult { Exception = ex.GetType().Name };
			}
		}
	}

	/// <summary>
	/// Helpers that turn a method body into a test case.
	/// </summary>
	static class CaseFactory {
		/// <summary>
		/// Runs MaxStackCalculator on the body. If <paramref name="check"/> is given, it returns null when the
		/// result is fine, otherwise the reason it isn't.
		/// </summary>
		public static TestCase MaxStack(string name, string description, Action<MethodIl> buildBody, Func<CalculatorResult, string> check = null) =>
			new TestCase {
				Name = name,
				Category = "maxstack",
				Description = description,
				Run = meter => {
					var il = new MethodIl();
					buildBody(il);
					var result = meter.Measure(() => CalculatorResult.Calculate(il));
					if (check is null)
						return CaseOutcome.WithoutCheck(result.ToString());
					string failure = result.Exception is not null ? "threw " + result.Exception : check(result);
					return CaseOutcome.WithCheck(result.ToString(), failure is null, failure);
				},
			};

		/// <summary>
		/// A large body: only the calculator is timed. The driver compares time and memory with the baseline.
		/// </summary>
		public static TestCase Slow(string name, string description, Action<MethodIl> buildBody) =>
			new TestCase {
				Name = name,
				Category = "slow",
				Description = description,
				Run = meter => {
					var il = new MethodIl();
					buildBody(il);
					var result = meter.Measure(() => CalculatorResult.Calculate(il));
					return CaseOutcome.WithoutCheck(result.ToString());
				},
			};

		/// <summary>
		/// Puts the body in a new module and saves it twice: with the default options (errors throw) and with
		/// a logger that ignores errors. Prints the max stack value that ended up in the saved file.
		/// </summary>
		public static TestCase Save(string name, string description, Action<MethodIl> buildBody, Func<uint?, string> checkMaxStackWhenIgnoringErrors = null) =>
			new TestCase {
				Name = name,
				Category = "save",
				Description = description,
				Run = meter => {
					string withDefaultOptions = SaveAndDescribe(buildBody, ignoreErrors: false, out _);
					string whenIgnoringErrors = SaveAndDescribe(buildBody, ignoreErrors: true, out uint? savedMaxStack);
					string text = $"default: {withDefaultOptions} | ignore-errors: {whenIgnoringErrors}";
					if (checkMaxStackWhenIgnoringErrors is null)
						return CaseOutcome.WithoutCheck(text);
					string failure = checkMaxStackWhenIgnoringErrors(savedMaxStack);
					return CaseOutcome.WithCheck(text, failure is null, failure);
				},
			};

		/// <summary>
		/// Saves a module with one big method and times the whole save.
		/// </summary>
		public static TestCase SlowSave(string name, string description, Action<MethodIl> buildBody) =>
			new TestCase {
				Name = name,
				Category = "slow-save",
				Description = description,
				Run = meter => {
					var module = TestModule.Create(buildBody, out _);
					var options = new ModuleWriterOptions(module);
					long savedBytes = meter.Measure(() => {
						var output = new MemoryStream();
						module.Write(output, options);
						return output.Length;
					});
					return CaseOutcome.WithoutCheck($"saved {savedBytes:N0} bytes");
				},
			};

		static string SaveAndDescribe(Action<MethodIl> buildBody, bool ignoreErrors, out uint? savedMaxStack) {
			savedMaxStack = null;
			var module = TestModule.Create(buildBody, out _);
			var options = new ModuleWriterOptions(module);
			if (ignoreErrors) {
				options.Logger = DummyLogger.NoThrowInstance;
				options.MetadataLogger = DummyLogger.NoThrowInstance;
			}
			try {
				var output = new MemoryStream();
				module.Write(output, options);
				savedMaxStack = TestModule.ReadMaxStackOfTestMethod(output.ToArray());
				return $"saved, maxStack={savedMaxStack}";
			}
			catch (Exception ex) {
				// The writer's error messages are long. The first sentence is enough to tell them apart.
				string firstSentence = ex.Message.Split(new[] { ". ", "\n" }, StringSplitOptions.None)[0];
				return Text.OneLine(ex.GetType().Name + ": " + firstSentence);
			}
		}
	}

	/// <summary>
	/// A module with one type "C" with one static method "M" whose body is built by a test case.
	/// </summary>
	static class TestModule {
		public static ModuleDef Create(Action<MethodIl> buildBody, out MethodDef method) {
			var module = new ModuleDefUser("TestModule.dll");
			new AssemblyDefUser("TestModule").Modules.Add(module);
			var type = new TypeDefUser("", "C", module.CorLibTypes.Object.TypeDefOrRef);
			module.Types.Add(type);
			method = new MethodDefUser("M", MethodSig.CreateStatic(module.CorLibTypes.Void), MethodAttributes.Public | MethodAttributes.Static);
			type.Methods.Add(method);

			var il = new MethodIl(module);
			buildBody(il);
			method.Body = new CilBody();
			foreach (var instruction in il.Instructions)
				method.Body.Instructions.Add(instruction);
			foreach (var handler in il.Handlers)
				method.Body.ExceptionHandlers.Add(handler);
			return module;
		}

		public static uint? ReadMaxStackOfTestMethod(byte[] savedModule) {
			using var module = ModuleDefMD.Load(savedModule);
			var method = module.Find("C", isReflectionName: false)?.FindMethod("M");
			return method?.Body?.MaxStack;
		}
	}
}
