# Working notes for agents

`Atomic<T>` — a generic atomic cell for .NET 10, published to
<https://github.com/NickStrupat/Atomic> as `NickStrupat.Atomic` 0.1.0, MIT.

The README is the specification and is kept accurate; read it before changing behaviour. This file is
the part that does not belong there: the invariants a change can silently break, the decisions already
settled, and the traps this repo has already fallen into.

## Layout

| | |
|---|---|
| `Atomic/` | The only project that ships. `Atomic<T>` plus `AtomicExtensions`, two files. |
| `Candidates/` | `BoxAtomic<T>`, `SeqLockAtomic<T>`, `IAtomic<T>`, and struct adapters. Designs this one was chosen over; kept so the suite and the harness can drive all three. |
| `Tests/` | Contract, thread safety, storage/layout, native instructions, codegen. |
| `Benchmarks/` | BenchmarkDotNet per category, plus `contention` and `gc` modes. |
| `CodegenProbe/` | Non-inlinable one-call wrappers for a disassembler to be pointed at. Nothing is asserted here; `CodegenTests` runs it. |

## Commands

```
dotnet test                       # Debug: codegen tests skip with a note
dotnet test -c Release            # everything, including codegen
dotnet run -c Release --project Benchmarks -- --filter "*"
dotnet run -c Release --project Benchmarks -- contention
dotnet run -c Release --project Benchmarks -- gc
```

Release is 118 tests; Debug is 114 + 4 skipped. Zero warnings is the standing state — keep it, because
`GenerateDocumentationFile` is on and it is what catches a `cref` to something you just deleted.

## Invariants

**`Atomic<T>` declares exactly one field.** A second field lets the runtime seat it first, which pushes
`storage` off a word boundary and raises `DataMisalignedException` on arm64, and takes away the slack
that lets a 3-byte value be widened to 8. This is why the lock is on the instance (a private lock
object would be that second field) and why the alignment claim is asserted by a test taking the
address, not by a runtime check. `StorageTests` holds the line; do not add a field to make something
convenient.

**Every strategy term must fold to a constant.** `IsInline` / `IsReference` are built only from
`typeof(T)`, `RuntimeHelpers.IsReferenceOrContainsReferences<T>()`, `Unsafe.SizeOf<T>()` and
`IntPtr.Size`. One term NativeAOT cannot evaluate keeps the monitor path live, drags a `try`/`finally`
in, and pushes the method past the inlining budget. `CodegenTests` is the only test that would notice.

**The specialisation idiom is one idiom.** Under a `typeof(T) == typeof(Int32)` guard, cast the cell
`((Atomic<Int32>)(Object)atomic).Storage` and the values `(T)(Object)x`. The reference cast folds to
nothing; the box/unbox is removed at import, not at tier 1 (measured: zero bytes in the first 30
tier-0 calls). `Unsafe.As` and a `Reinterpret` helper were both tried and both removed — they are not
better and they make two spellings of one thing. `NativeInterlockedTests` asserts the zero allocation.

**Six operations specialise:** `Add`, `Subtract`, `Increment`, `Decrement`, `And`, `Or`, each over
`Int32`/`Int64`/`UInt32`/`UInt64`. `Subtract` adds the negation (`unchecked(-x)`, `unchecked(0U - x)`).
`Xor`, `Max`, `Min` and `Update` stay compare-exchange loops — see below.

**Return values follow `Interlocked`, inconsistencies included.** `Add`/`Increment`/`Decrement` return
the new value; `And`/`Or`/`Xor` return the old one.

## Settled — do not re-open without new information

- **No atomic `Xor`, `Max` or `Min` is reachable from C#.** arm64 LSE has `ldeoral`, `ldsmaxal` and
  `ldsminal`; neither `Interlocked` nor `System.Runtime.Intrinsics.Arm` exposes them. Checked by
  reflecting over the intrinsics namespaces (including nested `+Arm64` classes — `IsPublic` is false
  for those, use `IsNestedPublic`). Recorded in `3efda5d`.
- **`Atomic<T>` stays a class.** Wrapping a reference in a struct to stop callers `lock`ing the
  instance was evaluated and rejected: it makes `default(Atomic<T>)` a null-dereference waiting to
  happen, and copies of a struct silently share one cell.
- **`BoxAtomic`'s slot stays `Object`, holding an immutable box.** Reassigning `T` inside a `Box<T>`
  after a lost CAS is unsound without hazard pointers or epochs — a box that *lost* was never
  published and can be reused, but one that was displaced can still be read. The retry loop already
  builds the box at most once (`slot ??= ToSlot(value)`); the remaining allocation growth comes from
  losing races and is not fixable at this layer.
- **`AtomicInterlockedExtensions` and `CandidateExtensions` are deleted.** The closed-type overloads
  could not help generic callers, because overload resolution happens where the type is written down.
- **`ProbeFieldIsWordAligned` lives in `Tests`, not `Atomic`.** Moving it removed the last `unsafe`
  code and `AllowUnsafeBlocks` from the shipping project.

## Testing discipline

**Falsify every new test against a deliberately broken implementation before believing it.** This has
caught genuine weaknesses three separate times in this repo. Concretely:

- Five concurrency tests passed vacuously because `Task.Run` serialised the bodies under pool load.
  Fixed with dedicated threads released from a `Barrier`, re-synced periodically rather than only at
  the start — thread startup exceeded one test's body, so the threads never overlapped at all.
- A codegen assertion that only looks for something's *absence* passes just as happily pointed at
  nothing. Test 2 stayed green with the specialisation removed, because the loop's CAS inlines and
  leaves no call symbol. Read mnemonics, and pair every assertion with a case that must show the
  opposite.
- Trying to defeat constant folding with a never-written mutable static does not work under NativeAOT:
  ILC sees the whole program and proves it constant. Only `Environment.GetEnvironmentVariable` defeats
  both compilers.

`ThreadSafetySuiteTests` points the whole suite at `NaiveAtomic<T>` and requires it to go red. Catching
a race is scheduling-dependent — measured at roughly one escape in eight with the suite running in
parallel — so each property gets 5 attempts and must catch it in one. That is not tolerating flakiness;
failing every attempt in a row is the different claim worth failing the build on.

## Measurement traps hit here

- **Type benchmark cells as a struct adapter behind a generic, never as `IAtomic<T>`.** Interface
  dispatch blocks inlining and reported differences of up to 18× that were entirely its own. The
  categories where all three implementations emit the same instruction are the control: they have to
  agree.
- **BDN microbenchmarks measure the sink too.** A `sum +=` on a `Decimal` was most of one figure.
- `Atomic<Three>`'s write cost is a store-to-load-forwarding stall (narrow stores, wide load), not
  anything about the strategy. The read path forwards cleanly.
- **`BoxAtomic`'s write advantage reverses once GC pause is charged.** Gen0 is stop-the-world for every
  thread, so the cost lands on bystanders. `-- gc` measures each cell alone and beside 7 non-allocating
  bystanders for exactly this reason (`GC.GetTotalPauseDuration`,
  `GC.GetAllocatedBytesForCurrentThread`).
- Reading disassembly: `DOTNET_JitDisasm` with `DOTNET_TieredCompilation=0` for the JIT; for ILC,
  `--codegenopt:JitDisasm`, `--codegenopt:JitStdOutFile` and **`--parallelism:1`** — without the last,
  listings are written interleaved and method headers land inside instructions. The mnemonic
  assertions are arm64-only and skip elsewhere rather than guessing.

## Style

Follows the global .NET guidelines with two deliberate divergences, so do not "fix" them:

- **Tabs, not 4 spaces.** Every file except `CodegenProbe/Program.cs`, which is 4-space and the odd one
  out.
- Lines run to ~110 in the library. `SeqLockAtomic`'s dense `switch` cases run to 227 and stay that
  way — pre-existing, deliberate, and in a candidate rather than the package.

XML docs on all public members, and on private ones where the reasoning is not obvious — which here is
most of them. Prose in docs is load-bearing: it explains *why* a strategy is sound, and a compiler will
not tell you when it goes stale. When deleting a type, grep for its name in prose, not just in `cref`.

Commit messages are a sentence in the imperative, lowercase after the first word, no trailing period —
`git log --oneline` for the register.

## Traps outside the code

- `git checkout <path>` reverts to HEAD and will discard an uncommitted new file at that path.
- Scratch disassembly programs that fail to find an instruction should fail loudly. Twice, a type
  inference error produced "no instruction found" that was nearly taken as a finding.
