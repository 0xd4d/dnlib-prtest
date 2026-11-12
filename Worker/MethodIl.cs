using System.Collections.Generic;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace Worker {
	/// <summary>
	/// A small helper for writing method bodies in test cases:
	///
	///   var il = new MethodIl();
	///   var end = MethodIl.Op(OpCodes.Ret);
	///   il.Push(20).Pop(20).Add(MethodIl.Branch(OpCodes.Br, end), end);
	/// </summary>
	sealed class MethodIl {
		public readonly List<Instruction> Instructions = new List<Instruction>();
		public readonly List<ExceptionHandler> Handlers = new List<ExceptionHandler>();

		/// <summary>Module used to create type references, e.g. the catch type of a handler.</summary>
		public readonly ModuleDef Module;

		public MethodIl(ModuleDef module = null) => Module = module ?? new ModuleDefUser("TestModule.dll");

		public static Instruction Op(OpCode opCode) => Instruction.Create(opCode);
		public static Instruction Branch(OpCode opCode, Instruction target) => Instruction.Create(opCode, target);
		public static Instruction Switch(params Instruction[] targets) => Instruction.Create(OpCodes.Switch, targets);

		/// <summary>An instruction whose operand doesn't match its opcode (e.g. a branch to null).</summary>
		public static Instruction WithRawOperand(OpCode opCode, object operand) => new Instruction(opCode, operand);

		public MethodIl Add(params Instruction[] instructions) {
			Instructions.AddRange(instructions);
			return this;
		}

		/// <summary>Adds <paramref name="count"/> x ldc.i4.0, so the stack grows by count.</summary>
		public MethodIl Push(int count) {
			for (int i = 0; i < count; i++)
				Instructions.Add(Op(OpCodes.Ldc_I4_0));
			return this;
		}

		/// <summary>Adds <paramref name="count"/> x pop.</summary>
		public MethodIl Pop(int count) {
			for (int i = 0; i < count; i++)
				Instructions.Add(Op(OpCodes.Pop));
			return this;
		}

		/// <summary>Adds <paramref name="count"/> x nop.</summary>
		public MethodIl Nops(int count) {
			for (int i = 0; i < count; i++)
				Instructions.Add(Op(OpCodes.Nop));
			return this;
		}

		public MethodIl AddCatch(Instruction tryStart, Instruction tryEnd, Instruction handlerStart, Instruction handlerEnd) {
			Handlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch) {
				TryStart = tryStart,
				TryEnd = tryEnd,
				HandlerStart = handlerStart,
				HandlerEnd = handlerEnd,
				CatchType = ExceptionType,
			});
			return this;
		}

		public MethodIl AddFinally(Instruction tryStart, Instruction tryEnd, Instruction handlerStart, Instruction handlerEnd) {
			Handlers.Add(new ExceptionHandler(ExceptionHandlerType.Finally) {
				TryStart = tryStart,
				TryEnd = tryEnd,
				HandlerStart = handlerStart,
				HandlerEnd = handlerEnd,
			});
			return this;
		}

		public ITypeDefOrRef ExceptionType => new TypeRefUser(Module, "System", "Exception", Module.CorLibTypes.AssemblyRef);
	}
}
