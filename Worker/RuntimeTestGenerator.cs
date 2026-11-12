using System;
using System.Collections.Generic;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.MD;
using dnlib.DotNet.Writer;
using static Worker.MethodIl;

namespace Worker {
	/// <summary>
	/// Writes an exe whose test methods get their max stack from the dnlib being tested. The driver runs
	/// the exe on every runtime it finds (CoreCLR, Mono, .NET Framework). A runtime throws
	/// InvalidProgramException when a method's max stack is too small for code it checks, and runtimes
	/// differ in which code they check (Mono also checks unreachable exception handlers).
	///
	/// Main calls each test method inside try/catch and prints "name: OK" or "name: FAIL ExceptionType".
	/// The exe references mscorlib 4.0.0.0 so it runs on all three runtimes.
	///
	/// This command also prints "name TAB maxStack" for every test method, the value the writer chose.
	///
	/// Two methods fail on some runtime no matter what max stack value they get, because the runtime
	/// rejects the code itself. The driver only flags a method when the candidate does worse than the baseline.
	///   DeadCodeFallsOffEnd            CoreCLR rejects code that falls off the end, even if it's dead.
	///   BackwardBranchIntoCodeAfterBr  Mono follows ECMA-335 III.1.7.5: code after an unconditional branch
	///                                  that is only reached by a backward branch must have an empty stack.
	/// </summary>
	static class RuntimeTestGenerator {
		/// <summary>Each test method is static, takes no arguments and returns an int.</summary>
		static IEnumerable<(string name, Action<MethodIl> buildBody)> TestMethods() {
			yield return ("Live20", il => il.Push(20).Pop(19).Add(Op(OpCodes.Ret)));

			yield return ("DeadCodeAfterRet20", il => il.Push(1).Add(Op(OpCodes.Ret)).Push(20).Pop(19).Add(Op(OpCodes.Ret)));

			yield return ("DeadCodeAfterThrow20", il => {
				var skip = Op(OpCodes.Ldc_I4_0);
				var exceptionCtor = new MemberRefUser(il.Module, ".ctor", MethodSig.CreateInstance(il.Module.CorLibTypes.Void), il.ExceptionType);
				il.Push(1).Add(Branch(OpCodes.Brfalse, skip), Instruction.Create(OpCodes.Newobj, exceptionCtor), Op(OpCodes.Throw));
				il.Push(20).Pop(20).Add(skip, Op(OpCodes.Ret));
			});

			yield return ("LiveTryCatch20", il => {
				var tryStart = Op(OpCodes.Nop);
				var handlerStart = Op(OpCodes.Pop);
				var end = Op(OpCodes.Ldc_I4_0);
				il.Add(tryStart, Branch(OpCodes.Leave, end), handlerStart).Push(20).Pop(20).Add(Branch(OpCodes.Leave, end), end, Op(OpCodes.Ret));
				il.AddCatch(tryStart, handlerStart, handlerStart, end);
			});

			yield return ("DeadTryBlockCatch20", il => {
				var tryStart = Op(OpCodes.Nop);
				var handlerStart = Op(OpCodes.Pop);
				var end = Op(OpCodes.Ldc_I4_0);
				il.Push(1).Add(Op(OpCodes.Ret));
				il.Add(tryStart, Branch(OpCodes.Leave, end), handlerStart).Push(20).Pop(20).Add(Branch(OpCodes.Leave, end), end, Op(OpCodes.Ret));
				il.AddCatch(tryStart, handlerStart, handlerStart, end);
			});

			yield return ("LiveFilter20", il => {
				var tryStart = Op(OpCodes.Nop);
				var filterStart = Op(OpCodes.Pop);
				var handlerStart = Op(OpCodes.Pop);
				var end = Op(OpCodes.Ldc_I4_0);
				il.Add(tryStart, Branch(OpCodes.Leave, end), filterStart).Push(20).Pop(19).Add(Op(OpCodes.Endfilter));
				il.Add(handlerStart, Branch(OpCodes.Leave, end), end, Op(OpCodes.Ret));
				il.Handlers.Add(new ExceptionHandler(ExceptionHandlerType.Filter) {
					TryStart = tryStart, TryEnd = filterStart, FilterStart = filterStart, HandlerStart = handlerStart, HandlerEnd = end,
				});
			});

			yield return ("BackwardBranchIntoCodeAfterBr", il => {
				// br L2; L1: ret; L2: ldc.i4.7; br L1   (L1 is entered with stack 1)
				var label1 = Op(OpCodes.Ret);
				var label2 = Op(OpCodes.Ldc_I4_7);
				il.Add(Branch(OpCodes.Br, label2), label1, label2, Branch(OpCodes.Br, label1));
			});

			yield return ("DeadCodeFallsOffEnd", il => il.Push(1).Add(Op(OpCodes.Ret)).Push(2));

			yield return ("DeadCodeStackMismatch", il => {
				// ldc; ret; (dead:) ldc; brtrue X; ldc; X: pop; ldc; ret
				var x = Op(OpCodes.Pop);
				il.Push(1).Add(Op(OpCodes.Ret));
				il.Push(1).Add(Branch(OpCodes.Brtrue, x)).Push(1).Add(x).Push(1).Add(Op(OpCodes.Ret));
			});
		}

		public static void Generate(string outputPath) {
			var mscorlib = new AssemblyRefUser("mscorlib", new Version(4, 0, 0, 0), new PublicKeyToken("b77a5c561934e089"));
			var module = new ModuleDefUser("RuntimeTest.exe", Guid.NewGuid(), mscorlib) {
				Kind = ModuleKind.Console,
				RuntimeVersion = MDHeaderRuntimeVersion.MS_CLR_40,
			};
			new AssemblyDefUser("RuntimeTest", new Version(1, 0, 0, 0)).Modules.Add(module);
			var corLib = module.CorLibTypes;
			var testsType = new TypeDefUser("", "Tests", corLib.Object.TypeDefOrRef) {
				Attributes = TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed,
			};
			module.Types.Add(testsType);

			var testMethods = new List<MethodDef>();
			foreach (var (name, buildBody) in TestMethods()) {
				var method = new MethodDefUser(name, MethodSig.CreateStatic(corLib.Int32), MethodAttributes.Public | MethodAttributes.Static) {
					ImplAttributes = MethodImplAttributes.IL | MethodImplAttributes.NoInlining,
				};
				var il = new MethodIl(module);
				buildBody(il);
				method.Body = new CilBody { InitLocals = true };
				foreach (var instruction in il.Instructions)
					method.Body.Instructions.Add(instruction);
				foreach (var handler in il.Handlers)
					method.Body.ExceptionHandlers.Add(handler);
				testsType.Methods.Add(method);
				testMethods.Add(method);
			}

			var main = CreateMain(module, testMethods);
			testsType.Methods.Add(main);
			module.EntryPoint = main;

			// Some test methods contain invalid (dead) code. Ignore writer errors so the exe is still written,
			// like a user who saves obfuscated code would.
			var options = new ModuleWriterOptions(module) {
				Logger = DummyLogger.NoThrowInstance,
				MetadataLogger = DummyLogger.NoThrowInstance,
			};
			module.Write(outputPath, options);

			using var saved = ModuleDefMD.Load(outputPath);
			foreach (var method in saved.Find("Tests", isReflectionName: false).Methods) {
				if (method.Name != "Main")
					Console.WriteLine($"{method.Name}\t{method.Body.MaxStack}");
			}
		}

		/// <summary>
		/// static void Main() {
		///     foreach test method:
		///         try { Test(); Console.WriteLine("Test: OK"); }
		///         catch (Exception ex) { Console.WriteLine(string.Concat("Test: FAIL ", ex.GetType().FullName)); }
		/// }
		/// </summary>
		static MethodDef CreateMain(ModuleDef module, List<MethodDef> testMethods) {
			var corLib = module.CorLibTypes;
			var mscorlib = corLib.AssemblyRef;
			var systemType = new TypeRefUser(module, "System", "Type", mscorlib);
			var exceptionType = new TypeRefUser(module, "System", "Exception", mscorlib);
			var consoleType = new TypeRefUser(module, "System", "Console", mscorlib);
			var writeLine = new MemberRefUser(module, "WriteLine", MethodSig.CreateStatic(corLib.Void, corLib.String), consoleType);
			var getType = new MemberRefUser(module, "GetType", MethodSig.CreateInstance(new ClassSig(systemType)), corLib.Object.TypeDefOrRef);
			var getFullName = new MemberRefUser(module, "get_FullName", MethodSig.CreateInstance(corLib.String), systemType);
			var concat = new MemberRefUser(module, "Concat", MethodSig.CreateStatic(corLib.String, corLib.String, corLib.String), corLib.String.TypeDefOrRef);

			var main = new MethodDefUser("Main", MethodSig.CreateStatic(corLib.Void), MethodAttributes.Public | MethodAttributes.Static);
			var body = main.Body = new CilBody();
			var exceptionLocal = body.Variables.Add(new Local(new ClassSig(exceptionType)));
			var instructions = body.Instructions;

			foreach (var test in testMethods) {
				var tryStart = Instruction.Create(OpCodes.Call, test);
				var handlerStart = Instruction.Create(OpCodes.Stloc, exceptionLocal);
				var next = Op(OpCodes.Nop);

				instructions.Add(tryStart);
				instructions.Add(Op(OpCodes.Pop));
				instructions.Add(Instruction.Create(OpCodes.Ldstr, test.Name + ": OK"));
				instructions.Add(Instruction.Create(OpCodes.Call, writeLine));
				instructions.Add(Branch(OpCodes.Leave, next));

				instructions.Add(handlerStart);
				instructions.Add(Instruction.Create(OpCodes.Ldstr, test.Name + ": FAIL "));
				instructions.Add(Instruction.Create(OpCodes.Ldloc, exceptionLocal));
				instructions.Add(Instruction.Create(OpCodes.Callvirt, getType));
				instructions.Add(Instruction.Create(OpCodes.Callvirt, getFullName));
				instructions.Add(Instruction.Create(OpCodes.Call, concat));
				instructions.Add(Instruction.Create(OpCodes.Call, writeLine));
				instructions.Add(Branch(OpCodes.Leave, next));

				instructions.Add(next);
				body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch) {
					TryStart = tryStart, TryEnd = handlerStart, HandlerStart = handlerStart, HandlerEnd = next, CatchType = exceptionType,
				});
			}
			instructions.Add(Op(OpCodes.Ret));
			return main;
		}
	}
}
