# dnlib PR test suite

Compares two dnlib checkouts, a **baseline** (usually `master`) and a **candidate** (your branch). It looks
for changed results, crashes, slowdowns and extra memory use. It was written to test changes to
`MaxStackCalculator` (PRs #572 and #603). The corpus `save` test works for any change to the reader or writer.

The suite is source code only. The `download` command fetches the test corpus (the top 3000 NuGet packages)
from nuget.org. Everything else, including the synthetic test methods and assemblies, is generated
when the tests run.

## Requirements

- .NET 10 SDK (dnlib itself targets `net10.0`, among others)
- git (only used to print the commit of each checkout in the report)
- Optional: Mono (`mono` in `PATH`). It checks more IL than CoreCLR, for example exception handlers in dead code.
  On Windows, the .NET Framework is used as a third runtime automatically.
- Disk space: about 11 GB for the full corpus, or about 1 GB with `--top 300`.

## Quick start

```sh
# A baseline checkout next to your dnlib clone (any dnlib checkout works)
git -C ../dnlib worktree add ../dnlib-master origin/master

# Once: download the corpus (resumable; run it again if it was interrupted)
./run.sh download                  # full corpus, 3000 packages, about 19,000 unique assemblies
./run.sh download --top 300        # or a smaller one

# Everything
./run.sh all --baseline ../dnlib-master --candidate ../dnlib

# Or one part
./run.sh cases   --baseline ../dnlib-master --candidate ../dnlib
```

On Windows, use `.\run.ps1` instead of `./run.sh`. Both scripts only run `dotnet run --project Driver -- <args>`.

Each run writes `results/<time>-<command>/report.md` (also printed to the console) plus the raw
results, so you can look at the details or diff two runs.

## Commands

| Command | What it does | How long |
|---|---|---|
| `cases` | About 50 hand-written cases: small valid and invalid bodies, save with and without a no-throw logger, and huge bodies that expose quadratic time or memory. Each case runs in its own process. | short; a case that hangs is killed after 60 s |
| `runtime` | Each build writes an exe whose methods get their max stack from that build. The exe is run on CoreCLR, Mono and .NET Framework. A method that works with the baseline but throws `InvalidProgramException` with the candidate is a regression. | seconds |
| `corpus` | Every method of every corpus file: compares the max stack results; for files that differ (the first 200, `--detail-files`), lists the methods. Then loads and saves every file with both builds and compares the output bytes, errors and save time. | by far the longest: every corpus file is processed 4 times (max stack and save, with each build) |
| `perf` | Times only the calculator on corpus files (default: first 3000), best of 3 runs, alternating baseline and candidate. Also compares allocated bytes. | a few minutes with the default 3000 files |
| `all` | All of the above. | |

Run time depends on your machine. Work runs in parallel worker processes, by default one per CPU core
(but at most one per GB of RAM, since a worker saving a big assembly can use about 1 GB). While a step runs,
its progress line shows an estimate of the time left. For a quicker first run, use a smaller corpus
(`download --top 300`) or `--files 2000`.

Options (all commands): `--jobs N` (parallel worker processes), `--files N` (use only the first N corpus files),
`--timeout SECONDS`, `--max-memory-mb N`, `--output DIR`. `perf` also takes `--iterations` and `--rounds`.
Run `./run.sh help` for the full list.

## Reading the report

Problems are **in bold**. In the `cases` tables:

- `pass`: the case has an expectation and the candidate meets it.
- `**FAIL: ...**`: the candidate breaks an expectation that the baseline meets. Expectations exist only
  for clear rules (e.g. "the max stack written must cover the code that runs") and the bugs found in
  earlier PRs.
- `fails on both`: the baseline fails it too, so the PR isn't the cause.
- `fixed`: the baseline fails or crashes and the candidate doesn't.
- `changed`: no expectation (or `pass, but changed`), but the answer differs from the baseline. This is often fine for invalid IL.
  Look at it and decide.
- `**SLOWER 25x**`, `**ALLOCATES ...**`, `**PEAK MEMORY ...**`: only for the huge-body cases, and only when the
  difference is big (time more than 3x + 100 ms, allocations more than 3x + 10 MB, or peak memory more than 2x + 200 MB).
- `**TIMEOUT**`, `**CRASH**`, `**MEMORY**`: the worker was killed or died, e.g. from a stack overflow.

Results that master itself gets "wrong" (useful to know before you start):

- `backward-branch-into-code-after-br`: master reports an error. ECMA-335 III.1.7.5 says code right after an
  unconditional branch, reached only by a backward branch, is assumed to start with an empty stack, so this
  IL really is invalid. Mono rejects it too.
- Runtime test `DeadCodeFallsOffEnd`: CoreCLR rejects it whatever the max stack is. `BackwardBranchIntoCodeAfterBr`:
  Mono rejects it whatever the max stack is (same ECMA rule).
- About 470 corpus files can't be loaded by dnlib at all. They are counted but don't affect the comparison.

## How it works

```
Driver/   The program you run. Doesn't reference dnlib. Builds the worker twice, runs it, compares the results.
Worker/   The test code. Built once against each dnlib checkout:
            dotnet build Worker -p:DnlibDir=<checkout> --artifacts-path build/<baseline|candidate>
nuget-packages.txt   The pinned corpus: "id version", most downloaded first (from nuget.org search, 2026-11)
```

- **Separate processes.** The baseline and candidate dnlib have the same assembly name, so each runs in its own
  worker process. The driver gives each worker a list of items (file paths or case names). The worker prints
  `BEGIN i` and `RESULT i ...` for each one (`Worker/ItemLoop.cs`). If a worker crashes, hangs (`--timeout`)
  or uses too much memory (`--max-memory-mb`), the driver records that for the current item and starts a new
  worker at the next item (`Driver/WorkerRunner.cs`). So one bad input shows up as one line in the report.
- **1 MB stack.** Workers run their work on a thread with a 1 MB stack, the Windows default. Deep recursion
  then fails on Linux and macOS too, where the main thread has 8 MB.
- **Same call path as the writer.** The module writer creates one `MaxStackCalculator` per module and calls the
  internal `Reset` + `Calculate` for each method. The worker does the same through `[UnsafeAccessor]`
  (`Worker/Calculator.cs`), so reuse and allocation behaviour match real saves. If those internal methods are
  renamed, it falls back to the public static `GetMaxStack` and prints a warning to the worker's stderr log
  (`*-stderr.txt` in the results folder).
- **Corpus comparison.** For each file the worker prints a SHA-1 of every method's `(token, ok, maxStack)`.
  Equal hashes mean identical results. For files that differ, the driver asks both workers for per-method
  results and lists the differences (`method-differences.tsv`).
- **Safety.** Downloaded packages are only parsed by dnlib, never executed. Zip entries are flattened into one
  folder per package. The only program the suite runs (other than dotnet, git and mono) is the
  `RuntimeTest.exe` it generates itself.

## Adding a test case

Add a line to one of the lists in `Worker/TestCaseList.cs`, for example:

```csharp
yield return CaseFactory.MaxStack("my-case", "ldc; ldc; add; ret",
    il => il.Push(2).Add(Op(OpCodes.Add), Op(OpCodes.Ret)),
    IsOkWithMaxStack(2));                  // optional expectation
```

`CaseFactory.Save(...)` saves the body in a module, `CaseFactory.Slow(...)` compares time and memory, and
`Worker/RuntimeTestGenerator.cs` has the methods that run on real runtimes. The driver picks up new cases
automatically.

## Testing something other than MaxStackCalculator

`corpus` (the save half) compares full load and save output for any dnlib change. The max stack half of
`corpus`, the `perf` command and most cases call `MaxStackCalculator`. For another class, change `Worker/CorpusCommands.cs`
(what is computed per method or file) and add cases. The driver doesn't need to change as long as the
worker prints the same result columns.
