using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Writer;

namespace Worker {
	/// <summary>
	/// Calls MaxStackCalculator the same way the module writer does: one calculator per module
	/// (MaxStackCalculator.Create), then Reset + Calculate for every method body. See
	/// dnlib's MethodBodyWriterBase.GetMaxStack.
	///
	/// Create, Reset and Calculate are internal, so they are called through [UnsafeAccessor]
	/// (.NET 8+). If a dnlib version removes or renames them, this falls back to the public static
	/// MaxStackCalculator.GetMaxStack, which creates a new calculator for every method.
	/// </summary>
	sealed class WriterStyleCalculator {
		MaxStackCalculator calculator;
		static readonly bool useInternalApi = IsInternalApiAvailable();

		public WriterStyleCalculator() {
			if (useInternalApi)
				calculator = InternalApi.Create(default);
		}

		/// <summary>Calls Create, Reset and Calculate once to find out if they exist in this dnlib.</summary>
		static bool IsInternalApiAvailable() {
			try {
				var probe = InternalApi.Create(default);
				InternalApi.Reset(ref probe, new List<Instruction>(), new List<ExceptionHandler>());
				InternalApi.Calculate(ref probe, out _);
				return true;
			}
			catch (MissingMethodException) {
				Console.Error.WriteLine("warning: MaxStackCalculator.Create/Reset/Calculate not found, using the public static GetMaxStack instead");
				return false;
			}
		}

		/// <summary>Returns true if no error was found. maxStack is what the calculator returned.</summary>
		public bool Calculate(IList<Instruction> instructions, IList<ExceptionHandler> exceptionHandlers, out uint maxStack) {
			if (!useInternalApi)
				return MaxStackCalculator.GetMaxStack(instructions, exceptionHandlers, out maxStack);
			InternalApi.Reset(ref calculator, instructions, exceptionHandlers);
			return InternalApi.Calculate(ref calculator, out maxStack);
		}

		/// <summary>
		/// Calls Calculate again without calling Reset first. Only used by one test case.
		/// </summary>
		public bool CalculateAgainWithoutReset(out uint maxStack) {
			if (!useInternalApi)
				throw new NotSupportedException("needs the internal MaxStackCalculator API");
			return InternalApi.Calculate(ref calculator, out maxStack);
		}

		/// <summary>
		/// The max stack value the writer puts in the method header. On error, the writer adds 8 to
		/// whatever the calculator returned (MethodBodyWriterBase.GetMaxStack).
		/// </summary>
		public static uint ValueWrittenByWriter(bool ok, uint maxStack) => ok ? maxStack : maxStack + 8;

		static class InternalApi {
			// For a static method, the first parameter only tells the runtime which type declares it.
			[UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "Create")]
			public static extern MaxStackCalculator Create(MaxStackCalculator declaringTypeOnly);

			[UnsafeAccessor(UnsafeAccessorKind.Method, Name = "Reset")]
			public static extern void Reset(ref MaxStackCalculator calculator, IList<Instruction> instructions, IList<ExceptionHandler> exceptionHandlers);

			[UnsafeAccessor(UnsafeAccessorKind.Method, Name = "Calculate")]
			public static extern bool Calculate(ref MaxStackCalculator calculator, out uint maxStack);
		}
	}
}
