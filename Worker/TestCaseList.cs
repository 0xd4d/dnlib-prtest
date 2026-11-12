using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using static Worker.MethodIl;

namespace Worker {
	/// <summary>
	/// All test cases. To add a case, add a line to one of the lists below. Names must be unique.
	///
	/// Categories:
	///   maxstack   MaxStackCalculator on one small body (valid, invalid, or odd).
	///   save       Save a module containing the body, with the default logger and with errors ignored.
	///   slow       MaxStackCalculator on a huge body. The driver compares time and memory with the baseline.
	///   slow-save  Save a module containing a huge body. Same comparison.
	/// </summary>
	static class TestCaseList {
		public static IEnumerable<TestCase> All() =>
			MaxStackCases().Concat(SaveCases()).Concat(SlowCases()).Concat(SlowSaveCases());

		public static void PrintAll() {
			foreach (var testCase in All())
				Console.WriteLine($"{testCase.Name}\t{testCase.Category}\t{testCase.Description}");
		}

		/// <summary>
		/// Runs one case in this process.
		/// Result: OK TAB text TAB check TAB milliseconds TAB allocatedBytes TAB peakWorkingSetBytes
		/// where check is "-" (no expectation), "PASS" or "FAIL: reason".
		/// </summary>
		public static string RunByName(string name) {
			var testCase = All().Single(c => c.Name == name);
			var meter = new Meter();
			var outcome = testCase.Run(meter);
			string check = outcome.CheckFailure switch {
				null => "-",
				"" => "PASS",
				string reason => "FAIL: " + reason,
			};
			long peakWorkingSet = System.Diagnostics.Process.GetCurrentProcess().PeakWorkingSet64;
			return $"OK\t{Text.OneLine(outcome.Text)}\t{Text.OneLine(check)}\t{meter.Milliseconds:F1}\t{meter.AllocatedBytes}\t{peakWorkingSet}";
		}

		// ----------------------------------------------------------------------------------------------
		// Checks used by several cases. Each returns null when the result is fine.

		static Func<CalculatorResult, string> IsOkWithMaxStack(uint expected) =>
			result => result.Ok && result.MaxStack == expected ? null : $"expected ok=True maxStack={expected}";

		/// <summary>
		/// When the calculator reports an error, the writer emits the returned value + 8. If the returned value
		/// is lower than the stack the reachable code needs, the saved method throws InvalidProgramException.
		/// (Report on PR #603, problem 2.)
		/// </summary>
		static Func<CalculatorResult, string> WriterEmitsAtLeast(uint needed) =>
			result => result.WriterEmits >= needed ? null : $"the writer would emit maxstack {result.WriterEmits} but the code needs {needed}";

		// ----------------------------------------------------------------------------------------------

		static IEnumerable<TestCase> MaxStackCases() {
			// Valid code. These must give the exact answer.
			yield return CaseFactory.MaxStack("push-3-pop-3", "3 pushes, 3 pops, ret",
				il => il.Push(3).Pop(3).Add(Op(OpCodes.Ret)),
				IsOkWithMaxStack(3));

			yield return CaseFactory.MaxStack("conditional-branch-over-push", "ldc; brtrue L; ldc; pop; L: ret",
				il => {
					var label = Op(OpCodes.Ret);
					il.Push(1).Add(Branch(OpCodes.Brtrue, label)).Push(1).Pop(1).Add(label);
				},
				IsOkWithMaxStack(1));


			yield return CaseFactory.MaxStack("valid-try-catch", "try { leave } catch { pop; leave }",
				il => {
					var tryStart = Op(OpCodes.Nop);
					var handlerStart = Op(OpCodes.Pop);
					var end = Op(OpCodes.Ret);
					il.Add(tryStart, Branch(OpCodes.Leave, end), handlerStart, Branch(OpCodes.Leave, end), end);
					il.AddCatch(tryStart, handlerStart, handlerStart, end);
				},
				IsOkWithMaxStack(1));

			yield return CaseFactory.MaxStack("valid-try-filter", "try { leave } filter { pop; ldc; endfilter } { pop; leave }",
				il => {
					var tryStart = Op(OpCodes.Nop);
					var filterStart = Op(OpCodes.Pop);
					var handlerStart = Op(OpCodes.Pop);
					var end = Op(OpCodes.Ret);
					il.Add(tryStart, Branch(OpCodes.Leave, end), filterStart, Op(OpCodes.Ldc_I4_1), Op(OpCodes.Endfilter), handlerStart, Branch(OpCodes.Leave, end), end);
					il.Handlers.Add(new ExceptionHandler(ExceptionHandlerType.Filter) {
						TryStart = tryStart, TryEnd = filterStart, FilterStart = filterStart, HandlerStart = handlerStart, HandlerEnd = end,
					});
				},
				IsOkWithMaxStack(1));

			yield return CaseFactory.MaxStack("65536-pushes", "65,536 pushes then 65,536 pops",
				il => il.Push(65536).Pop(65536).Add(Op(OpCodes.Ret)),
				IsOkWithMaxStack(65536));

			// Invalid code where the error fallback matters (report on PR #603, problem 2).
			yield return CaseFactory.MaxStack("deep-stack-then-error", "20-deep stack, then a branch to an instruction that isn't in the body (an error for some implementations)",
				il => il.Push(20).Pop(20).Add(Op(OpCodes.Ldc_I4_0), Branch(OpCodes.Brtrue, Op(OpCodes.Ret)), Op(OpCodes.Ret)),
				WriterEmitsAtLeast(20));

			yield return CaseFactory.MaxStack("error-then-deep-stack", "a branch to an instruction that isn't in the body (an error for some implementations), then a 20-deep stack",
				il => il.Add(Op(OpCodes.Ldc_I4_0), Branch(OpCodes.Brtrue, Op(OpCodes.Ret))).Push(20).Pop(20).Add(Op(OpCodes.Ret)),
				WriterEmitsAtLeast(20));

			yield return CaseFactory.MaxStack("stack-mismatch-then-deep-stack", "two paths reach L with stack 0 and 1, then a 20-deep stack",
				il => {
					var join = Op(OpCodes.Pop);
					il.Push(1).Add(Branch(OpCodes.Brtrue, join)).Push(1).Add(join).Push(20).Pop(20).Add(Op(OpCodes.Ret));
				},
				WriterEmitsAtLeast(20));

			// Exception handlers in unusual places (report on PR #603, problems 3 and 4).
			yield return CaseFactory.MaxStack("unreachable-try-block-with-catch", "ret; then a dead try block whose catch handler uses 20 stack slots. Mono checks dead handlers.",
				il => {
					var tryStart = Op(OpCodes.Nop);
					var handlerStart = Op(OpCodes.Pop);
					var end = Op(OpCodes.Ret);
					il.Add(Op(OpCodes.Ret), tryStart, Branch(OpCodes.Leave, end), handlerStart).Push(20).Pop(20).Add(Branch(OpCodes.Leave, end), end);
					il.AddCatch(tryStart, handlerStart, handlerStart, end);
				},
				result => result.MaxStack >= 20 ? null : $"maxStack {result.MaxStack} is too small for the catch handler (needs 20)");

			yield return CaseFactory.MaxStack("catch-handler-try-start-not-in-body", "the handler's TryStart is not in the body; the handler uses 20 stack slots",
				il => {
					var first = Op(OpCodes.Ret);
					var handlerStart = Op(OpCodes.Pop);
					il.Add(first, handlerStart).Push(20).Pop(20).Add(Branch(OpCodes.Leave, first));
					il.AddCatch(Op(OpCodes.Nop), handlerStart, handlerStart, null);
				},
				result => !result.Ok || result.MaxStack >= 20 ? null : "the handler was skipped without reporting an error");

			yield return CalculateTwiceWithoutReset();

			// Odd or invalid code. No expectation: the driver shows when the candidate differs from the baseline.
			// ECMA-335 III.1.7.5: an instruction right after an unconditional branch that is only reached by a
			// backward branch is assumed to have an empty stack, so this code is invalid and Mono rejects it.
			// Master reports an error. It was the motivating case of PR #572.
			yield return CaseFactory.MaxStack("backward-branch-into-code-after-br", "br L2; L1: pop; ret; L2: ldc; br L1 (L1 is entered with stack 1)",
				il => {
					var label1 = Op(OpCodes.Pop);
					var label2 = Op(OpCodes.Ldc_I4_0);
					il.Add(Branch(OpCodes.Br, label2), label1, Op(OpCodes.Ret), label2, Branch(OpCodes.Br, label1));
				});
			yield return CaseFactory.MaxStack("dead-code-after-ret", "ret; then 5 pushes and 5 pops that are never reached",
				il => il.Add(Op(OpCodes.Ret)).Push(5).Pop(5).Add(Op(OpCodes.Ret)));
			yield return CaseFactory.MaxStack("falls-off-end", "ldc; ldc; pop (no ret)",
				il => il.Push(2).Pop(1));
			yield return CaseFactory.MaxStack("ret-with-2-values", "ldc; ldc; ret",
				il => il.Push(2).Add(Op(OpCodes.Ret)));
			yield return CaseFactory.MaxStack("leave-with-2-values", "ldc; ldc; leave L; L: ret (leave empties the stack)",
				il => {
					var end = Op(OpCodes.Ret);
					il.Push(2).Add(Branch(OpCodes.Leave, end), end);
				});
			yield return CaseFactory.MaxStack("try-entered-with-non-empty-stack", "ldc; try { pop; leave } finally { endfinally }",
				il => {
					var tryStart = Op(OpCodes.Pop);
					var handlerStart = Op(OpCodes.Endfinally);
					var end = Op(OpCodes.Ret);
					il.Push(1).Add(tryStart, Branch(OpCodes.Leave, end), handlerStart, end);
					il.AddFinally(tryStart, handlerStart, handlerStart, end);
				});
			yield return CaseFactory.MaxStack("jmp-with-non-empty-stack", "ldc; jmp M",
				il => il.Push(1).Add(Instruction.Create(OpCodes.Jmp, new MemberRefUser(il.Module, "M", MethodSig.CreateStatic(il.Module.CorLibTypes.Void)))));
			yield return CaseFactory.MaxStack("throw-then-dead-instruction-at-end", "ldc; throw; ldc (the dead last instruction falls off the end)",
				il => il.Push(1).Add(Op(OpCodes.Throw)).Push(1));
			yield return CaseFactory.MaxStack("empty-body", "no instructions",
				il => { });
			yield return CaseFactory.MaxStack("null-instruction", "nop; null; ret",
				il => il.Add(Op(OpCodes.Nop), null, Op(OpCodes.Ret)));
			yield return CaseFactory.MaxStack("null-exception-handler", "ret, and a null in the handler list",
				il => { il.Add(Op(OpCodes.Ret)); il.Handlers.Add(null); });
			yield return CaseFactory.MaxStack("same-instruction-twice", "the same Instruction object appears twice",
				il => { var nop = Op(OpCodes.Nop); il.Add(nop, nop, Op(OpCodes.Ret)); });
			yield return CaseFactory.MaxStack("handler-start-null", "a catch handler whose HandlerStart is null",
				il => {
					var tryStart = Op(OpCodes.Nop);
					var after = Op(OpCodes.Pop);
					il.Add(tryStart, Op(OpCodes.Ret), after, Op(OpCodes.Rethrow));
					il.AddCatch(tryStart, after, null, null);
				});
			yield return CaseFactory.MaxStack("br-null-target", "br null; ret",
				il => il.Add(WithRawOperand(OpCodes.Br, null), Op(OpCodes.Ret)));
			yield return CaseFactory.MaxStack("brtrue-null-target", "ldc; brtrue null; ret",
				il => il.Push(1).Add(WithRawOperand(OpCodes.Brtrue, null), Op(OpCodes.Ret)));
			yield return CaseFactory.MaxStack("brtrue-int-operand", "ldc; brtrue 5 (an int, not an Instruction); ret",
				il => il.Push(1).Add(WithRawOperand(OpCodes.Brtrue, 5), Op(OpCodes.Ret)));
			yield return CaseFactory.MaxStack("brtrue-with-switch-target-list", "ldc; brtrue [L] (an Instruction[] like a switch); ret; L: 3 pushes, 3 pops, ret (review comment on PR #603)",
				il => {
					var label = Op(OpCodes.Ldc_I4_0);
					il.Push(1).Add(WithRawOperand(OpCodes.Brtrue, new[] { label }), Op(OpCodes.Ret), label).Push(2).Pop(3).Add(Op(OpCodes.Ret));
				});
			yield return CaseFactory.MaxStack("switch-null-target", "ldc; switch [L, null]; L: ret",
				il => { var label = Op(OpCodes.Ret); il.Push(1).Add(Switch(label, null), label); });
			yield return CaseFactory.MaxStack("switch-null-operand", "ldc; switch null; ret",
				il => il.Push(1).Add(WithRawOperand(OpCodes.Switch, null), Op(OpCodes.Ret)));
		}

		/// <summary>
		/// Calculate twice on the same calculator without Reset. Internal API, not used this way today
		/// (report on PR #603, problem 6), but it should not silently change the answer.
		/// </summary>
		static TestCase CalculateTwiceWithoutReset() => new TestCase {
			Name = "calculate-twice-without-reset",
			Category = "maxstack",
			Description = "Reset once, Calculate twice (internal API) on 2 pushes and 2 pops",
			Run = meter => {
				var il = new MethodIl().Push(2).Pop(2).Add(Op(OpCodes.Ret));
				var calculator = new WriterStyleCalculator();
				bool firstOk = calculator.Calculate(il.Instructions, il.Handlers, out uint firstMaxStack);
				bool secondOk = calculator.CalculateAgainWithoutReset(out uint secondMaxStack);
				string text = $"first: ok={firstOk} maxStack={firstMaxStack}, second: ok={secondOk} maxStack={secondMaxStack}";
				bool same = firstOk == secondOk && firstMaxStack == secondMaxStack;
				return CaseOutcome.WithCheck(text, same, "the second call gave a different answer");
			},
		};

		// ----------------------------------------------------------------------------------------------

		static IEnumerable<TestCase> SaveCases() {
			yield return CaseFactory.Save("save-valid-deep-stack", "20 pushes, 20 pops, ret",
				il => il.Push(20).Pop(20).Add(Op(OpCodes.Ret)),
				savedMaxStack => savedMaxStack == 20 ? null : $"saved maxstack is {savedMaxStack}, expected 20");

			yield return CaseFactory.Save("save-deep-stack-then-error", "20-deep stack, then a stack mismatch",
				il => {
					var join = Op(OpCodes.Pop);
					il.Push(20).Pop(20).Push(1).Add(Branch(OpCodes.Brtrue, join)).Push(1).Add(join, Op(OpCodes.Ret));
				},
				savedMaxStack => savedMaxStack >= 20 ? null : $"saved maxstack is {savedMaxStack}, but the code needs 20");

			yield return CaseFactory.Save("save-unreachable-try-block-with-catch", "ret; then a dead try block whose catch handler uses 20 stack slots",
				il => {
					var tryStart = Op(OpCodes.Nop);
					var handlerStart = Op(OpCodes.Pop);
					var end = Op(OpCodes.Ret);
					il.Add(Op(OpCodes.Ret), tryStart, Branch(OpCodes.Leave, end), handlerStart).Push(20).Pop(20).Add(Branch(OpCodes.Leave, end), end);
					il.AddCatch(tryStart, handlerStart, handlerStart, end);
				},
				savedMaxStack => savedMaxStack >= 20 ? null : $"saved maxstack is {savedMaxStack}; Mono rejects this (needs 20)");

			yield return CaseFactory.Save("save-null-instruction", "nop; null; ret",
				il => il.Add(Op(OpCodes.Nop), null, Op(OpCodes.Ret)));
			yield return CaseFactory.Save("save-null-exception-handler", "ret, and a null in the handler list",
				il => { il.Add(Op(OpCodes.Ret)); il.Handlers.Add(null); });
			yield return CaseFactory.Save("save-same-instruction-twice", "the same Instruction object appears twice",
				il => { var nop = Op(OpCodes.Nop); il.Add(nop, nop, Op(OpCodes.Ret)); });
			yield return CaseFactory.Save("save-br-null-target", "br null; ret",
				il => il.Add(WithRawOperand(OpCodes.Br, null), Op(OpCodes.Ret)));
			yield return CaseFactory.Save("save-try-entered-with-non-empty-stack", "ldc; try { pop; leave } finally { endfinally }",
				il => {
					var tryStart = Op(OpCodes.Pop);
					var handlerStart = Op(OpCodes.Endfinally);
					var end = Op(OpCodes.Ret);
					il.Push(1).Add(tryStart, Branch(OpCodes.Leave, end), handlerStart, end);
					il.AddFinally(tryStart, handlerStart, handlerStart, end);
				});
		}

		// ----------------------------------------------------------------------------------------------
		// Huge bodies, to find code that is quadratic in time or memory. Real assemblies don't look like
		// this, but an attacker or an obfuscator can produce them, and dnlib is often used on such files.

		const int Size = 200_000;

		static IEnumerable<TestCase> SlowCases() {
			yield return CaseFactory.Slow("slow-100k-branches-to-last", "100k x (ldc; brtrue LAST); LAST: ret",
				il => {
					var last = Op(OpCodes.Ret);
					for (int i = 0; i < Size / 2; i++)
						il.Push(1).Add(Branch(OpCodes.Brtrue, last));
					il.Add(last);
				});

			yield return CaseFactory.Slow("slow-switch-200k-targets", "one switch with 200k targets, 200k nops, ret",
				il => {
					var last = Op(OpCodes.Ret);
					il.Push(1).Add(Switch(Enumerable.Repeat(last, Size).ToArray())).Nops(Size).Add(last);
				});

			yield return CaseFactory.Slow("slow-10-switches-20k-targets", "10 switches with 20k targets each, 200k nops, ret",
				il => {
					var last = Op(OpCodes.Ret);
					var targets = Enumerable.Repeat(last, Size / 10).ToArray();
					for (int i = 0; i < 10; i++)
						il.Push(1).Add(Switch(targets));
					il.Nops(Size).Add(last);
				});

			yield return CaseFactory.Slow("slow-20k-handlers-try-at-end", "200k nops, ret; 20k finally handlers that all start at the last instruction",
				il => {
					var last = Op(OpCodes.Ret);
					il.Nops(Size).Add(last);
					for (int i = 0; i < Size / 10; i++)
						il.AddFinally(last, null, last, null);
				});

			yield return CaseFactory.Slow("slow-20k-handlers-try-not-in-body", "200k nops, ret; 20k handlers whose TryStart isn't in the body",
				il => {
					var notInBody = Op(OpCodes.Nop);
					il.Nops(Size).Add(Op(OpCodes.Ret));
					for (int i = 0; i < Size / 10; i++)
						il.AddFinally(notInBody, null, notInBody, null);
				});

			yield return CaseFactory.Slow("slow-20k-handlers-try-at-start", "nop, 200k nops, endfinally; 20k handlers with TryStart=first and HandlerStart=last",
				il => {
					var first = Op(OpCodes.Nop);
					var last = Op(OpCodes.Endfinally);
					il.Add(first).Nops(Size).Add(last);
					for (int i = 0; i < Size / 10; i++)
						il.AddFinally(first, null, last, null);
				});

			yield return CaseFactory.Slow("slow-deep-stack-100k", "100k pushes, 100k pops, ret",
				il => il.Push(Size / 2).Pop(Size / 2).Add(Op(OpCodes.Ret)));

			yield return CaseFactory.Slow("slow-100k-backward-branches", "FIRST: nop; 100k x (ldc; brtrue FIRST); ret",
				il => {
					var first = Op(OpCodes.Nop);
					il.Add(first);
					for (int i = 0; i < Size / 2; i++)
						il.Push(1).Add(Branch(OpCodes.Brtrue, first));
					il.Add(Op(OpCodes.Ret));
				});

			yield return CaseFactory.Slow("slow-100k-branch-chain", "100k x (Li: ldc; brtrue Li+1); ret. A recursive algorithm recurses 100k deep (PR #572).",
				il => AddBranchChain(il, Size / 2));

			yield return BigMethodThenManySmallMethods();
		}

		/// <summary>N x (L_i: ldc.i4.0; brtrue L_i+1); L_N: ret. Each branch target is the next instruction pair.</summary>
		static void AddBranchChain(MethodIl il, int count) {
			var last = Op(OpCodes.Ret);
			var loads = new Instruction[count];
			for (int i = 0; i < count; i++)
				loads[i] = Op(OpCodes.Ldc_I4_0);
			for (int i = 0; i < count; i++)
				il.Add(loads[i], Branch(OpCodes.Brtrue, i + 1 < count ? loads[i + 1] : last));
			il.Add(last);
		}

		/// <summary>
		/// The writer reuses one calculator for every method in a module. This checks that one huge method
		/// doesn't make every later method slow, and that small methods don't allocate much.
		/// </summary>
		static TestCase BigMethodThenManySmallMethods() => new TestCase {
			Name = "slow-big-method-then-100k-small",
			Category = "slow",
			Description = "one calculator (like the writer): a 1M-instruction method, then 100k 3-instruction methods",
			Run = meter => {
				var big = new MethodIl().Nops(1_000_000).Add(Op(OpCodes.Ret));
				var small = new MethodIl().Push(1).Pop(1).Add(Op(OpCodes.Ret));
				uint sum = meter.Measure(() => {
					var calculator = new WriterStyleCalculator();
					calculator.Calculate(big.Instructions, big.Handlers, out uint total);
					for (int i = 0; i < 100_000; i++) {
						calculator.Calculate(small.Instructions, small.Handlers, out uint maxStack);
						total += maxStack;
					}
					return total;
				});
				return CaseOutcome.WithoutCheck($"sum of max stack values={sum}");
			},
		};

		static IEnumerable<TestCase> SlowSaveCases() {
			yield return CaseFactory.SlowSave("save-60k-target-switch", "save a DLL whose only method has a 60k-target switch and 500k nops (~740 KB)",
				il => {
					var last = Op(OpCodes.Ret);
					il.Push(1).Add(Switch(Enumerable.Repeat(last, 60_000).ToArray())).Nops(500_000).Add(last);
				});

			yield return CaseFactory.SlowSave("save-100k-branch-chain", "save a DLL whose only method is 100k x (Li: ldc; brtrue Li+1); ret",
				il => AddBranchChain(il, 100_000));
		}
	}
}
