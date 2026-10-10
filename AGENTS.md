# Working notes for agents

`Atomic<T>` — a generic atomic cell for .NET 10, published to
<https://github.com/NickStrupat/Atomic> as `NickStrupat.Atomic` 0.1.0, MIT.

The README is the specification and is kept accurate; read it before changing behaviour. This file is
the part that does not belong there: the invariants a change can silently break, the decisions already
settled, and the traps this repo has already fallen into.

## Layout

| | |
|---|---|
| `Atomic/` | The only project that ships. `Atomic<T>`, `AtomicExtensions`, `AtomicEnumExtensions`. |
| `Atomic.Analyzers/` | `NSAT0001`/`NSAT0002`: `lock` and `Monitor` on a cell. Packed inside `Atomic`'s package, not on its own. |
| `AnalyzerTests/` | The analyzer over snippets compiled against the real library, or against a stand-in declaring the same type. |
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

Release is 160 tests (140 in `Tests`, 20 in `AnalyzerTests`); Debug is 156 + 4 skipped. Zero warnings is the standing state — keep it, because
`GenerateDocumentationFile` is on and it is what catches a `cref` to something you just deleted.

The 32-bit strategies are only exercised on real 32-bit hardware — an arm32 Raspberry Pi 3B on the
development network (`pi@raspberrypi3.local`, Raspbian trixie, glibc 2.41, four cores). A Cortex-A53
implements AArch32 at EL0, which Apple Silicon does not, so this is not reachable by container or
emulator; QEMU would also serialise the atomics and make every race test pass for the wrong reason.
xunit v3 under MTP builds an executable, so nothing needs an SDK on the device:

```
dotnet publish Tests -c Release -r linux-arm --self-contained
tar -czf - -C Tests/bin/Release/net10.0/linux-arm publish | ssh pi@raspberrypi3.local \
  'rm -rf atomic && mkdir atomic && tar -xzf - -C atomic --strip-components=1'
ssh pi@raspberrypi3.local 'cd atomic && chmod +x Tests CodegenProbe && ./Tests'
```

136 there when last run, before the enum and no-`IEquatable` tests were added — rerun before quoting
a new figure. 14 skipped: the 11 `TypeLayout` rows, the two arm64 mnemonic assertions, and the
NativeAOT leg, since ILC does not target 32-bit `linux-arm`. `TheStorageStrategyIsChosenWhenTheJitCompilesTheCell`
does run there and passes. Do not build on the device — it has 1 GB of RAM.

## Invariants

**`Atomic<T>` declares exactly one field.** A second field lets the runtime seat it first, which pushes
`storage` off a word boundary and raises `DataMisalignedException` on arm64, and takes away the slack
that lets a 3-byte value be widened to a word. This is why the lock is on the instance (a private lock
object would be that second field) and why the alignment claim is asserted by a test taking the
address, not by a runtime check. `StorageTests` holds the line; do not add a field to make something
convenient.

**Every strategy term must fold to a constant.** `FitsInWord` / `IsEightByteIntegerOn32Bit` /
`IsReference` — and `IsInline`, which is the first two — are built only from `typeof(T)`,
`RuntimeHelpers.IsReferenceOrContainsReferences<T>()`, `Unsafe.SizeOf<T>()` and `IntPtr.Size`. One term
NativeAOT cannot evaluate keeps the monitor path live, drags a `try`/`finally` in, and pushes the method
past the inlining budget. `CodegenTests` is the only test that would notice.

**The widened view is a word, and the word is `IntPtr.Size`.** Not 8 bytes — a word is what the
field's alignment and the object's minimum size are both stated in, so writing it this way is what makes
the trick hold at 32 bits instead of surrendering every value type to the monitor there. `Widen<TView>` /
`Narrow<TView>` are generic over the view for this: `IntPtr` for `FitsInWord`, `Int64` for the other.
The caller owes them a view at least as wide as `T`, which both terms establish before reaching them —
`Widen` writes `sizeof(T)` bytes into it and will run off the end of a narrower one.

**`Int64` and `UInt64` are named, not measured, on the wide path.** `IsEightByteIntegerOn32Bit` is
those two types by name, and may not be loosened to "eight unmanaged bytes". Three separate things
would break: the runtime seats a field of *these* types on an 8-byte boundary for the instructions that
reach it and promises nothing of the sort for `Eight` (two `Int32`s); the view has to be the value exactly,
there being no slack to zero at that width; and the path has no `TryCompareExchangeEqualValue` tail, which
is only sound because an integer's equality is its bits. `Double` fails the third and is on the monitor at
32 bits for that reason, not for its size.

**On the wide path, `Volatile` is not good enough.** ECMA-335 I.12.6.6 grants atomicity only up to a
native int, so an 8-byte `Volatile.Read` or `Volatile.Write` can tear on a 32-bit runtime. `Read` is
`Interlocked.Read` and `Write` is `Interlocked.Exchange` there — full fences rather than acquire/release,
which is stronger than the README promises and so breaks nothing, but is not free.

**The specialisation idiom is one idiom.** Under a `typeof(T) == typeof(Int32)` guard, cast the cell
`((Atomic<Int32>)(Object)atomic).Storage` and the values `(T)(Object)x`. The reference cast folds to
nothing; the box/unbox is removed at import, not at tier 1 (measured: zero bytes in the first 30
tier-0 calls). `Unsafe.As` and a `Reinterpret` helper were both tried and both removed — they are not
better and they make two spellings of one thing. `NativeInterlockedTests` asserts the zero allocation.

**Enums get `And`, `Or` and `Xor` from a second class, not more overloads.** Constraints are not part
of a signature, so a second `Or` beside the operator-constrained one is a duplicate member;
`AtomicEnumExtensions` puts it in its own class, where both are candidates and the constraints settle
it. Nothing satisfies both — an enum cannot implement an interface — so there is no ambiguity and the
caller never names a type argument. `Unsafe.As` on the storage is deliberate there and is the one place
the cast-through-`Object` idiom cannot reach: generics are invariant, so `(Atomic<Int32>)(Object)` a
cell of an enum throws whatever the enum is backed by. `And` and `Or` take the instruction at every
width the cell keeps inline: 4 and 8 bytes outright, 1 and 2 bytes through the `Int32` over the word
they are kept in. That is only sound because the mask is zero extended — a byte copy, not a numeric
conversion — so `Or` leaves the slack zero and `And` puts it back to zero. A sign-extended mask would set
slack bits under `Or` that `Read`, the returned values and even a later compare-exchange all hide (the
last misses on the instruction and the `Equals` tail rescues it), so the enum test reads the word itself;
it was falsified against exactly that break. An 8-byte enum at 32 bits is not inline and takes the loop,
as does every `Xor`. `Add`, `Increment`, `Max` and the rest are absent on purpose — flags are the reason
to want this.

**Six operations specialise:** `Add`, `Subtract`, `Increment`, `Decrement`, `And`, `Or`, each over
`Int32`/`Int64`/`UInt32`/`UInt64`. `Subtract` adds the negation (`unchecked(-x)`, `unchecked(0U - x)`).
`Xor`, `Max` and `Min` stay compare-exchange loops — see below.

**A specialisation is gated on the cell's own strategy, not just on `typeof(T)`.** Each of the six asks
`Atomic<T>.IsInline` before issuing the instruction, which is why that property is `internal` rather than
`private`. `typeof(T) == typeof(Int32)` alone was a bug: where the cell keeps a value behind its monitor,
an unguarded `Interlocked.Increment` is a second writer the monitor knows nothing about, and a
`CompareExchange` can read, compare and store across an increment and lose it. Naming the cell's strategy
rather than restating the word size is what makes the gate survive a change to the strategy — it already
has: all four types keep their instruction at 32 bits, `Int32`/`UInt32` through the word and
`Int64`/`UInt64` through the wide path, and the gate needed no edit to say so. No test can catch the
original bug on a 64-bit machine, where the term folds to true and nothing changes; what the codegen tests
hold is the other half, that the gate is free — `Increment[long]` asserts `casal` is *absent*, and a term
that failed to fold would leave the loop's compare-and-swap in the body.

**A comparand is compared by what `T` is, never by how wide it is.** A reference by identity, a value
type with `EqualityComparer<T>.Default` at every width. The inline path still issues its one `casal`
first and only consults the type when that misses, which is sound because identical bits are the same
value and `Equals` is required to be reflexive — a bits-match is an `Equals`-match for anything honouring
that. **The monitor path compares bits first for the same reason**, through `HoldsTheSameValueAs`, and
this is not an optimisation: asking the type first made the two strategies disagree. A struct whose
`Equals` compares a `Double` with `==` calls a `NaN`-bearing value unequal to itself, so the wide cell
refused to exchange a value it was holding while the narrow one had already stored over it, and every
retry loop offering that value back went round forever. The candidates had it too and were fixed with it: `SeqLockAtomic`'s seqlock path already had
`BitsEqual` and now calls it first, and `BoxAtomic`'s slot path carries the same helper as the cell.
`AtomicContractTests` holds all three to the comparison and `AtomicReflexivityTests` holds the cell to
the loop — a loop of its own over `TryCompareExchange`, not `Add`, which on the monitor path no longer
compares at all and would pass against a cell that asked the type first. Bits are only compared where they mean something — a `T` holding a reference goes straight to
the type, since reading its bits races a moving GC.

Ordering bits before the type also means the type may not be asked at all, which took a test's teeth
out: `SeqLockAtomic_ComparesOutsideTheCounter` proves a reentrant `Equals` cannot deadlock, and its
comparand named the held value exactly, so the bits answered and `Equals` never ran. `Reentrant` gained
an `Ignored` field for the comparand to differ in. A test that needs user equality to run now has to
make the bits differ. The tail is `NoInlining` so that `TryCompareExchange` can stay small enough to inline into the loops
in `AtomicExtensions` — but it does not, under either compiler, and did not at `ef9f5ab` either:
`Increment[double]` calls it. Nothing asserts it one way or the other. `Increment[System.Decimal]`
containing `Monitor` was believed to be the test that would notice, and never could have been: `Read`
inlines and brings its own lock, and that method now takes the lock itself anyway.

**On the monitor path the read-modify-writes lock rather than loop.** Each of the nine in
`AtomicExtensions` takes `lock (atomic)` under `!Atomic<T>.IsLockFree` and computes on `Storage` in
place. The loop there took the lock twice per attempt and ran the operator between with nothing held,
so every writer landing in the gap cost a retry. Measured against `ef9f5ab` in a worktree:
`Decimal.Increment` 26.58 → 13.28 ns uncontended, two to four times the throughput from 1 to 8 threads,
with `Int64.Increment` and `Decimal.Read` unmoved as the control. `IsLockFree` is exactly "the cell uses its monitor", so the gate is the strategy again rather
than a restatement of it, and it folds, so lock-free instantiations carry no `try`/`finally`. The
user's operator now runs under the lock, as `Equals` already did; `Witnessed` in `Tests/TestTypes.cs`
records `Monitor.IsEntered` from inside each operator, which is how the test tells a lock from a loop
that gives the same answer. `AtomicEnumExtensions` does not have this yet: its monitor path is only an
8-byte enum on a 32-bit runtime, which nothing here can run but the Pi.

**Return values follow `Interlocked`, inconsistencies included.** `Add`/`Increment`/`Decrement` return
the new value; `And`/`Or`/`Xor` return the old one.

**The analyzer exempts the assembly that declares `Atomic<T>`, and nothing else.** The library's own
`lock (this)` and `lock (atomic)` are its lock path, and the analyzer runs over the library's build, so
without the exemption the library fails to compile with 13 `NSAT0001`s — which is how the exemption test
was falsified (with `-p:RunAnalyzers=false`, so the snippet test could build at all). The exemption test
is paired with the same stand-in compiled as a library and locked from outside, without which it would
pass just as well if the analyzer had never recognised the stand-in.

**`Atomic.Analyzers` overrides `PublishAot`, `RuntimeIdentifier` and `SelfContained`.** A publish passes them
to every project in the graph, restore included — `UndefineProperties` on the reference does not reach
restore — and `netstandard2.0` rejects them. The first symptom was not a failure: the two NativeAOT codegen
tests turned into skips, because a publish that cannot run is reported as unavailable. Check the skip
count, not just the failure count, after touching any project file.

## Settled — do not re-open without new information

- **No atomic `Xor`, `Max` or `Min` is reachable from C#.** arm64 LSE has `ldeoral`, `ldsmaxal` and
  `ldsminal`; neither `Interlocked` nor `System.Runtime.Intrinsics.Arm` exposes them. Checked by
  reflecting over the intrinsics namespaces (including nested `+Arm64` classes — `IsPublic` is false
  for those, use `IsNestedPublic`). Recorded in `3efda5d`.
- **Value types compare with their own equality, not with their bits.** Settled with the repo owner
  against the alternative (bits everywhere, as `std::atomic` does). Bits would have been free, but it
  makes `Atomic<Decimal>` say `1.0m != 1.00m`, never calls a type's `Equals`, makes padding observable,
  and bit-comparing a struct holding references races with a moving GC. The old rule was width-picked
  and indefensible: `Eight` and `Twelve` are the same `record struct` and compared differently, and an
  8-byte type with interior padding could fail a compare-exchange forever. `Tolerance`/
  `WideTolerance` in `Tests/TestTypes.cs` hold the two strategies to one answer.
- **A struct holding references keeps its own `Equals`, references and all.** `Tagged(Int32, String)`
  goes on comparing its `String` by value. "Identity and nothing else" governs `Atomic<SomeClass>`; once
  `T` is a struct the struct decides, and there is no way to overrule it from here. Documented rather
  than closed.
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
  Fixed with dedicated threads released from a barrier, re-synced periodically rather than only at
  the start — thread startup exceeded one test's body, so the threads never overlapped at all.
- The one-winner test let `NaiveAtomic` through five attempts running on the 4-core Ubuntu CI runner,
  with eight threads behind a `Barrier`: a blocked waiter is woken through the scheduler, so with more
  threads than cores they came out one after another and never raced. Fixed with a spinning barrier
  (`SpinBarrier`, no back-off) and one thread per core, between 2 and 8. The development machine has
  enough cores to hide this, so it could not be reproduced here; `DOTNET_PROCESSOR_COUNT=2` or `=4` sets
  the thread count, but not the cores, and is the closest stand-in.
- That fix moved the failure rather than ending it: the next run passed Ubuntu and failed Windows,
  where the exchange test, now four threads, let the cell through instead. Beside the rest of the suite
  on four cores, some property escapes whatever the thread count. `ThreadSafetySuiteTests` now runs in
  the `RunsAlone` collection, after every parallel test, with the cores to itself. Raising `Attempts`
  was the alternative and was rejected: it makes the build greener without making the suite see more.
- Isolation was not enough either: macOS then let the exchange test through, the whole check having
  taken 110 ms. A burst under a millisecond on a 3- or 4-vCPU CI machine can run its threads one after
  another whatever the guest does, because the host need not run the vCPUs at once. The exchange and
  one-winner tests now run for `Window` (250 ms) rather than a fixed count, as the tearing test, which
  never failed on CI, always did. The exchange test keeps a SplitMix64 multiset hash of each side rather
  than every token, and every thread stops in the same round because the barrier's last arrival decides.
  Trap hit on the way: `AllSatisfy(c => c.Should().Be(1))` over a million rounds, failing against
  `NaiveAtomic`, ran for minutes building its message. Count the bad rounds and assert the count.
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

- **A struct with no `Equals` of its own boxes on a `CompareExchange` miss, at any width.** Not a
  monitor-vs-inline thing: `Atomic<NoEquatable>` (4 bytes, word-fitting, `IsLockFree`) allocates 48 bytes
  on a losing comparison, and `Atomic<WideNoEquatable>` (24 bytes, monitor) allocates 80 — both zero on a
  hit — because `EqualityComparer<T>.Default` for a type
  with neither an `Equals` override nor `IEquatable<T>` is `ObjectEqualityComparer<T>`, which boxes both
  operands to reach `Object.Equals`. The cell's own bit-compare runs first and absorbs every hit, so this
  only shows up on a miss — but a losing `CompareExchange` under contention is not rare, it is the normal
  case a retry loop exists for. `record struct` and anything implementing `IEquatable<T>` never reach
  this path. `RuntimeHelpers.IsBitwiseEquatable` — the runtime's own test for a type whose equality is its bits —
  would let a miss against a
  type with no references and no floating-point fields skip `Equals` entirely — bits already differ, and
  for that category differing bits already is the final answer, the way `ValueType.Equals`'s own fast
  path treats it once unboxed. It is `internal` and unreachable from here, which is the whole reason this
  stayed a documented gap rather than a fix: nothing public distinguishes "differing bits genuinely means
  unequal" from "this type's `Equals` might disagree with its bits" for a type that never opted into
  `IEquatable<T>`. `CompareExchange_WhenTypeHasNoEquatable_AllocatesOnlyOnAMiss` pins the current numbers
  so a change to this either direction gets noticed rather than silently shipped.
- **Type benchmark cells as a struct adapter behind a generic, never as `IAtomic<T>`.** Interface
  dispatch blocks inlining and reported differences of up to 18× that were entirely its own. The
  categories where all three implementations emit the same instruction are the control: they have to
  agree.
- **BDN microbenchmarks measure the sink too.** A `sum +=` on a `Decimal` was most of one figure.
- **Comparing by value costs the awkward sizes about 0.8 ns on `CompareExchange`, and nothing else.**
  Measured against HEAD before the change in a worktree, not against the numbers already in the README:
  the machine was loaded on the first attempt and the unmodified `String` column read 3x high, which is
  the control that said so. `Int64`, `String`, `Decimal` and `Tagged` did not move. The cost is the
  second exit the miss needs, landing beside the store-to-load stall `Three` already pays; the success
  path is two instructions shorter than it was and the `casal` is untouched. Read in the disassembly
  rather than guessed at, and assigning the comparand versus narrowing the result makes no difference
  (7.39 against 7.56), so there is nothing to win back by rearranging that line.
- `Atomic<Three>`'s write cost is a store-to-load-forwarding stall (narrow stores, wide load), not
  anything about the strategy. The read path forwards cleanly.
- **A box of a `Decimal` is not a fixed number of bytes at 32 bits.** `BoxSize = 32` was exact at 64
  bits and wrong on arm32, where such a box is 28 bytes and the allocator prepends a 12-byte filler to
  put the `Decimal` on the 8-byte boundary it wants. 28 and 12 come to a multiple of 8, so the next one
  needs a filler too: 40 bytes charged for 19902 boxes out of 20000, 28 for the rest, in no fixed
  proportion. A class holding four `Int32`s is 24 bytes every time, which is the control that says
  alignment rather than header size. `StorageTests` now measures one box on the runtime it is running on
  and asserts a band admitting one box per call — calibrated against a stand-in, never against
  `BoxAtomic`, because calibrating on the thing under test would let a cell building a box per attempt
  set its own band and pass. To separate an object's size from what it is charged, read `m_BaseSize`,
  the second DWORD of its method table; allocation accounting alone conflates the two.
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

- **Tabs, not 4 spaces**, in every `.cs` file. (Project files are 4-space, as the SDK templates write
  them.)
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
