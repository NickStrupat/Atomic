# Atomic

A generic atomic cell for .NET. `Read`, `Write` and `Exchange` allocate nothing, for any `T`. Lock-free
for references and any unmanaged value up to a word — including the 3-, 5-, 6- and 7-byte structs that no
interlocked instruction matches — and for `Int64` and `UInt64` on 32-bit runtimes too. Everything else
takes a monitor instead, and `IsLockFree` tells you which one you got.

```
dotnet add package NickStrupat.Atomic
```

```csharp
var counter = new Atomic<Int64>(0);
counter.Increment();                       // a single interlocked instruction

var state = new Atomic<Colour>(Colour.Red);
state.CompareExchange(Colour.Green, Colour.Red);

var current = new Atomic<String?>(null);   // null is an ordinary value, including as a comparand
current.TryCompareExchange("first", comparand: null, out _);
```

Targets `net10.0`, depends on nothing, and brings its [analyzer](#analyzer) with it. One field, and the
cell never boxes or allocates to store a value.
The one allocation is not the cell's: a losing compare-exchange on a struct with no equality of its own
boxes to call `Object.Equals` (see below).

## How a value is stored

The strategy is chosen from `T` and the runtime's word size, and folds to a constant when the JIT or the
AOT compiler specialises the cell, so a given `Atomic<T>` compiles to one path with no test left in it.

| `T` | Strategy | Cell size at 64 bits |
|---|---|---|
| Any reference type | `Volatile` reads and writes, `Interlocked` exchanges, on the reference; write barrier intact | 24 B |
| Unmanaged, ≤ one word | `Volatile` reads and writes, `Interlocked` exchanges, on a word-sized view of the field | 24 B |
| `Int64` or `UInt64` on a 32-bit runtime | `Interlocked` for everything, reads and writes included | — (32-bit only) |
| Unmanaged, wider than that | `lock (this)` | 32 B and up |
| A value type holding references | `lock (this)` | 24 B and up |

This table is about storage and nothing else. It does not decide how a comparand is compared — that
follows from `T` alone and does not change when a type crosses a row.

```csharp
Atomic<Int32>.IsLockFree     // true
Atomic<String>.IsLockFree    // true
Atomic<Int64>.IsLockFree     // true  — at either word size, see 32-bit runtimes below
Atomic<Int32?>.IsLockFree    // true on 64-bit — Nullable<Int32> is eight unmanaged bytes
Atomic<Decimal>.IsLockFree   // false — 16 bytes, so the monitor
```

### Awkward sizes

No processor has a 3-byte compare-and-swap, so a 3-byte struct would normally need a lock. It
doesn't here. Because `storage` is the only field the class declares, it begins on a word boundary and
the minimum size of an object leaves a whole word there — so the cell swaps the whole word and
lets the slack ride along. Every write zeroes the slack, so the slack never makes two equal values look
different, and a `CompareExchange` whose comparand is the value held succeeds on the first instruction.
Padding *inside* a struct is copied as it is; when that makes the bits differ, the cell asks the type
(see *What "atomic" guarantees here*).

That is why the class has exactly one field, and why it uses a monitor rather than a private lock object
for the values it cannot swap in place: a second field would let the runtime seat the value off a word
boundary and take the trick away from every instantiation.

Widening is not quite free, and it is lopsided. Reading a 3-byte value costs 0.79 ns against 0.56
for a full word, because the word is loaded once and the value taken out of it. Writing costs 1.52 ns
against 0.40, because the value has to be put into a zeroed word first — narrow stores totalling 3
bytes and an 8-byte load of the same stack slot, which is a store-to-load forward the hardware cannot
satisfy from the store buffer. Still cheaper than the 13 ns a lock would cost, which is the comparison
that matters.

## Read-modify-write

Generic, over any `T` with the matching operator — a single instruction where one exists (below),
otherwise a compare-and-swap loop where the cell is lock-free, and one acquisition of its lock where it is
not:

```csharp
Add  Subtract  Increment  Decrement  And  Or  Xor  Max  Min
```

Return values follow `Interlocked`, inconsistencies included: `Add`, `Subtract`, `Increment` and
`Decrement` return the **new** value; `And`, `Or` and `Xor` return the **old** one. `Max` and `Min` return
the new value too — the larger (or smaller) of the old value and the argument.

An exception thrown by `T`'s operator — `Decimal` overflowing, say — propagates and leaves the value
unchanged. On the loop the operator runs once per attempt, so under contention it can run more than once
per call; keep it free of side effects.

For `Atomic<Int32>`, `Atomic<Int64>`, `Atomic<UInt32>` and `Atomic<UInt64>`, `Increment`, `Decrement`,
`Add`, `Subtract`, `And` and `Or` issue the instruction directly with no loop. You don't opt in: the
method tests `typeof(T)`, and the compiler folds that test when it specialises the method, so on arm64
`Atomic<Int64>.Increment` is a null check and one `ldaddal`, `Atomic<Double>.Increment` is the
compare-and-swap loop, and `Atomic<Decimal>.Increment` takes the lock once — with no type test left in any
of them. `Subtract` adds the negation, there being no interlocked subtract — one extra instruction, and
it wraps correctly even for the value whose negation is itself an overflow.

Enums reach `And`, `Or` and `Xor` through `AtomicEnumExtensions`. An enum cannot declare interface
implementations of its own, so it never satisfies the operator constraints above, and the enum versions
live in a separate class because constraints are not part of a signature. You still write `cell.Or(flag)`;
the constraints pick the one that applies. `And` and `Or` take the instruction wherever the cell keeps the
enum in place: 4- and 8-byte enums at their own width, 1- and 2-byte ones through the 4-byte instruction on
the word they are kept in. An 8-byte enum on a 32-bit runtime is kept behind the lock and takes the loop,
and `Xor` is a loop at every width, there being no interlocked exclusive or to reach.

```csharp
var access = new Atomic<Access>(Access.Read);
access.Or(Access.Write);       // returns the old value, as And, Or and Xor do
```

`Xor`, `Max` and `Min` never get a single instruction, for any `T`: they are the loop where the cell is
lock-free and one acquisition of the lock where it is not. arm64 has a single instruction for all three —
`ldeoral`, `ldsmaxal`, `ldsminal` — and nothing reaches them from C#: `Interlocked` has no `Xor`, `Max` or
`Min`, and `System.Runtime.Intrinsics.Arm` exposes no atomics at all, only `AdvSimd`, `ArmBase`, `Crc32`,
`Dp`, `Rdm`, the crypto sets, `Sve` and `Sve2`. The JIT emits those instructions solely when lowering
`Interlocked`, which never asks for them. x86 would want the loop regardless, having no fetch-and-xor to
speak of.

Specialising inside the method rather than declaring overloads on the concrete types is what makes this
reach generic code. Overload resolution happens where the type is written down, and here it isn't:

```csharp
static void Bump<T>(Atomic<T> cell) where T : IIncrementOperators<T> => cell.Increment();
```

Uncontended none of this is measurable, since an uncontended loop succeeds on its first try — 6.97 ns
against 7.11 (see *Benchmarks*). Contended it is the whole difference. Four threads incrementing one cell:

```
Interlocked.Increment on a plain field   30.8 Mops/s
Atomic<Int64>.Increment                  31.1
Atomic<T>.Increment from generic code    30.6
the same, by compare-and-exchange loop   10.0
```

Every failed comparison costs another cache line migration, which is what the bottom row is.

Where the cell keeps its value behind its monitor, a loop would take the lock twice — once to read, once
to exchange — and run the operator in between with nothing held, where any other writer can land and
send it round again. So there the read-modify-writes take the lock once and compute on the value in
place. Uncontended that is one monitor rather than two, `Atomic<Decimal>.Increment` going from 26.58 ns
to 13.28, the same as a `Read`; contended, a writer arriving mid-operation waits instead of costing a
retry:

```
Atomic<Decimal>.Increment, Mops/s   1 thread   2 threads   4 threads   8 threads
compare-and-swap loop                  33–38       15–22       11–21        9–12
one acquisition of the lock            64–75       51–52       40–42       34–42
```

Ranges over six one-second runs each, the two builds alternated on the same machine, with
`Int64.Increment` beside them as the control that did not move.

The price is that the type's operator runs under the lock, as its `Equals` already does inside a
compare-exchange there. An operator that takes some other lock can deadlock against code that takes
the two in the opposite order; nothing in the BCL's numeric types does.

## What "atomic" guarantees here

What each operation promises, and what it does not.

**Each operation is linearizable, and operations do not compose.** A read, a write, an exchange or a
compare-exchange each take effect at a single instant, and no caller ever sees a torn value. Two of them
in a row are still two operations:

```csharp
cell.Write(cell.Read() + 1);   // not atomic
cell.Increment();              // atomic
```

`Read` and `Write` are methods rather than a property for this reason — `cell.Value += 1` reads as one
step and isn't.

**A comparand is compared by what `T` is, not by how wide it is.** A reference is compared by identity
and by nothing else. A value type is compared with `EqualityComparer<T>.Default` — its `IEquatable<T>`
implementation (including the one the compiler writes for a `record struct`), or failing that its
`Equals(Object)` — whatever its width and whichever strategy the cell picked. For a value type holding no
references, identical bits are taken as the same value without asking the type, which only shows for a
type that calls a value unequal to itself:

```csharp
new Atomic<Double>(-0.0).CompareExchange(99.0, comparand: 0.0);    // stores: -0.0 equals 0.0
new Atomic<Decimal>(1.0m).CompareExchange(99m, comparand: 1.00m);  // stores: 1.0m equals 1.00m

// A record struct that gains a field crosses to the monitor, and goes on comparing the same way.
```

Where a cell keeps its value decides how it swaps it and nothing else. `Interlocked.CompareExchange(ref
Double, …)` compares the bits and has no way out of it; that is the difference worth knowing about.

A value type holding references is still a value type, so its own `Equals` decides — including how that
treats the references inside it. `record struct Tagged(Int32 Number, String Text)` compares its `String`
by value, because that is what the `Equals` the compiler wrote does. "Identity and nothing else" governs
`Atomic<SomeClass>`; past that the struct decides, and there is no way to overrule it from here.

**A struct with neither `Equals` nor `IEquatable<T>` can allocate on a miss.** `EqualityComparer<T>.Default`
for such a type is `ObjectEqualityComparer<T>`, which boxes both sides to reach `Object.Equals`. The cell
tries bits first, so a hit — every successful compare-exchange, the common case in a retry loop — never
reaches it; only a miss does, whether or not the type fits in a word:

```csharp
struct Plain { public Int32 A, B, C; }               // no Equals override, no IEquatable<T>

var cell = new Atomic<Plain>(new Plain { A = 1 });
cell.CompareExchange(new Plain { A = 1 }, new Plain { A = 1 });   // hit: 0 bytes, bits matched
cell.CompareExchange(new Plain { A = 9 }, new Plain { A = 7 });   // miss: 64 bytes, Equals was asked
```

`record struct` and anything implementing `IEquatable<T>` are unaffected — the comparer the compiler
writes never boxes. `Read`, `Write` and `Exchange` never call `Equals` and never allocate, whatever `T`
is; this is specific to a losing `CompareExchange` or `TryCompareExchange` against a type with neither,
including one inside a read-modify-write loop.

**Success cannot be inferred from the returned value, so ask.** The cell compares with `Equals`, and a
caller reaching for `==` does not:

```csharp
var cell = new Atomic<Double>(Double.NaN);
var previous = cell.CompareExchange(99.0, comparand: Double.NaN);
previous == Double.NaN   // false — and yet 99.0 was stored

var other = new Atomic<Double>(Double.NaN);
other.TryCompareExchange(99.0, comparand: Double.NaN, out previous);   // true, correctly
```

Use `TryCompareExchange` in any loop that branches on whether the swap happened.

**Comparing by value admits an ABA that comparing bits would not.** A swap can land across a change from
`0.0` to `-0.0`, or across a change to a field the type's `Equals` ignores. That is what asking the type
means; a cell cannot both honour `Equals` and notice changes `Equals` calls invisible.

**Ordering is acquire/release, not sequential consistency.** Reads are acquire, writes are release,
read-modify-writes are both. That is enough for publication across two cells:

```csharp
// thread A                     // thread B
data.Write(42);                 if (flag.Read())
flag.Write(true);                   use(data.Read());   // sees 42
```

One exception runs the other way: on a 32-bit runtime an `Int64` or `UInt64` read and write are full
fences rather than acquire and release, because `Interlocked` is the only API that guarantees an 8-byte
access is indivisible there. That is stronger than what is promised, so nothing that relies on the promise
notices, but it is not free — don't read the 64-bit numbers below as covering it.

It is not enough for a store to one cell followed by a load of another — the StoreLoad case, which is
unordered on x86 and arm64 alike. That needs a full fence, which `Interlocked.MemoryBarrier` provides,
and which `Exchange` and `CompareExchange` already are. Java's `volatile` and C++'s default
`memory_order_seq_cst` are stronger than this; don't carry that assumption over.

## Analyzer

The package ships a Roslyn analyzer that reports monitor-based synchronisation applied to a cell. Both
forms compile — a cell is an ordinary reference type — and neither does what it looks like it does. Where
`IsLockFree` is true the cell never takes its monitor, so a `lock` on it excludes none of the cell's
operations on other threads. Where it is false the cell keeps its value behind that same monitor, so a
`lock` on it blocks every other thread's operations on the cell for as long as it is held. Which one you
get depends on `T` and the word size, not on anything written at the `lock`.

The rules are reported as errors. To relax one, set its severity in an `.editorconfig`:

```ini
[*.cs]
dotnet_diagnostic.NSAT0001.severity = warning
```

Nothing is reported inside the library itself, where locking the cell is the lock path.

### NSAT0001

Do not use the `lock` statement on an atomic cell.

```csharp
var counter = new Atomic<Int64>(0);

lock (counter) { counter.Write(counter.Read() + 1); }   // NSAT0001: not atomic, despite appearances

counter.Increment();                                     // atomic
```

To update the value from its current value, use a read-modify-write method or a `TryCompareExchange`
loop. To guard other state together with the cell, lock a separate object.

### NSAT0002

Do not call `Monitor` methods (`Enter`, `TryEnter`, `Exit`, `Wait`, `Pulse`, `PulseAll`) on an atomic
cell, for the same reasons. `Monitor.IsEntered` is not reported, since it only queries the monitor and is
useful in assertions.

```csharp
Monitor.Enter(counter);   // NSAT0002
```

## Benchmarks

BenchmarkDotNet ShortRun, .NET 10 on an Apple M1 Max (arm64). Nanoseconds per operation, uncontended.
The columns are categories of `T`, because the category is what picks a strategy:

| | |
|---|---|
| `Int64` | unmanaged, one word |
| `Three` | unmanaged, a size no instruction matches |
| `String` | a reference |
| `Decimal` | unmanaged, wider than a word |
| `Tagged` | a struct holding a reference |

**Read**

| | `Int64` | `Three` | `String` | `Decimal` | `Tagged` |
|---|---|---|---|---|---|
| `Atomic` | 0.56 | 0.79 | 0.54 | 13.08 | 13.12 |
| `BoxAtomic` | 0.57 | 0.77 | 0.54 | **0.70** | **0.69** |
| `SeqLockAtomic` | 0.64 | 2.05 | 0.64 | 2.02 | 13.11 |

**Write**

| | `Int64` | `Three` | `String` | `Decimal` | `Tagged` |
|---|---|---|---|---|---|
| `Atomic` | 0.40 | 1.52 | 1.29 | 13.10 | 13.13 |
| `BoxAtomic` | 0.38 | 1.41 | 1.30 | **4.30**¹ | **5.83**¹ |
| `SeqLockAtomic` | 0.38 | 9.14 | 1.30 | 9.06 | 13.26 |

**CompareExchange**

| | `Int64` | `Three` | `String` | `Decimal` | `Tagged` |
|---|---|---|---|---|---|
| `Atomic` | 6.58 | 7.46² | 6.56 | 13.33 | 13.08 |
| `BoxAtomic` | 6.80 | 7.34² | 6.63 | 12.63¹ | 13.72¹ |
| `SeqLockAtomic` | 6.60 | **9.62**² | 6.56 | **8.65** | 13.46 |

¹ Allocates 32 bytes per operation. Nothing else in these tables allocates at all, and what those
bytes actually cost is *Accounting for the allocations* below.

² About 0.8 ns more than the same cell before comparands were compared by value rather than by bits —
the only column that moved. A comparison the instruction cannot settle needs somewhere to go, and the
second exit lands next to the store-to-load forward an awkward size already pays for (see *Awkward
sizes*), where there is nothing to hide it behind. The instruction itself is untouched and the success
path is two instructions shorter; the other four columns measure the same as they did.

The 13 ns wherever a cell locks is the monitor and nothing else: an uncontended `lock` enter and exit
on its own measures 13.45 ns, so copying the value costs nothing worth reporting beside it. The gap
between writing an `Int64` at 0.40 ns and a `String` at 1.29 is the garbage collector's write barrier.

Read down a column rather than across one. Within a column the three do the same work on the same
value, so the numbers answer which strategy wins; between two columns they do not, and a reference load
costs more than a word load before any cell is involved.

Where all three are lock-free they agree to within noise, because they compile to the same
instructions. The categories that separate them are the ones nothing can swap in place:

- **`Decimal` and `Tagged` go to `BoxAtomic` for reads, by a wide margin.** A read is one load of a
  reference to an immutable box — 0.70 ns against 13.08 — and it allocates nothing. Its *write*
  advantage does not survive being charged for the collections it causes.
- **`Three` is where `SeqLockAtomic` falls over.** It swaps only 1-, 2-, 4- and 8-byte values in place,
  at their own width, and sends everything else to the version counter, so a 3-byte write costs 9.14 ns
  against 1.41.
- **`Atomic<T>` ties wherever it is lock-free and trails wherever it locks** — on five of those six
  measurements; the sixth is a three-way tie at the cost of a monitor. That is the trade the library
  makes: it never allocates to store a value, and pays a monitor for the values that do not fit its one
  field.

```
Interlocked.Increment on a plain field    6.926 ns
Atomic<Int64>.Increment                   6.972 ns
the same, by compare-and-exchange loop    7.112 ns
```

The abstraction is free where it can be. `Benchmarks/` also holds a contended harness
(`-- contention`), which is where the wide categories separate hardest: four threads reading one
`Decimal` sustain about 5,040 Mops through `BoxAtomic` and 1,710 through `SeqLockAtomic`, against
fewer than 20 through `Atomic`.

### Accounting for the allocations

A per-operation benchmark is not wrong that a `BoxAtomic<Decimal>` write costs 4.30 ns. The allocation
is a pointer bump, the boxes die before the next collection reaches them, and the collections that do
happen are inside the measured thread's own wall clock. What a thread cannot measure is that a
generation zero collection stops every *other* thread as well.

So `-- gc` writes each cell from one thread twice: alone, and beside seven threads doing unrelated work
that allocate nothing whatsoever, which is what makes every collection during a run attributable to the
writer.

| `Decimal` | writes M/s | B/write | gen0 | pause alone | pause with 7 | charged |
|---|---|---|---|---|---|---|
| `Atomic` | 75.4 | 0 | 0 | 0.00 ns | 0.00 ns | 13.3 ns |
| `BoxAtomic` | 221.1 | 32 | 1690 | 0.40 ns | 2.11 ns | 19.3 ns |
| `SeqLockAtomic` | 112.4 | 0 | 0 | 0.00 ns | 0.00 ns | **8.9 ns** |

The pause per write is five times larger with other threads present, because the collection has to stop
and restart them. Charge that pause to each thread it stops — the last column, a model rather than a
measurement — and the fastest writer in the table becomes the slowest: `BoxAtomic` writes a `Decimal`
at 4.5 ns measured on its own and costs the process about 19 ns. `Tagged` behaves the same way, at
22.4 ns against 13.2.

Nor is 32 bytes per operation quite a constant, though it is nearer to one than it was. The retry loop
used to build a fresh box on every attempt; it now builds one per call, at the latest point it can and
only once the comparison has passed. Eight threads exchanging a value for itself — the shape that
actually re-enters that loop — went from 155 bytes per call to a flat 32.

What is left cannot be fixed by moving the allocation, because nothing can be exchanged in before it
exists. A thread that builds its box and then loses the race has made garbage:

```
             exchanging a value    exchanging an
             for itself            incrementing value
1 thread      32.0 B                 32.0 B
2 threads     32.0 B                 51.0 B
4 threads     32.0 B                 87.6 B
8 threads     32.0 B                177.4 B
```

The left column is one box per call however hard the loop spins. The right is one box per call plus one
for every exchange that lost, which is what it costs to build a candidate value before knowing whether
anyone wants it.

None of this touches reads, which allocate nothing in any implementation. `BoxAtomic` still wins the
wide categories outright for reading; what it loses is the claim to be the cheapest way to write them.

## Requirements and limits

**32-bit runtimes get a 32-bit word.** The widened view is a word, not 8 bytes, because a word is
what the field's alignment and the object's minimum size are both stated in. ECMA-335 I.12.6.2 aligns a
value on the boundary a `native int` needs and I.12.6.6 grants atomicity only up to that same width, so
on x86, arm32 and wasm the word is 4 bytes: `Int32`, `Colour` and the 3-byte struct are swapped in
place there exactly as they are at 64 bits, while `Double`, `Nullable<Int32>` and any 5- to 8-byte
struct take the monitor. `IsLockFree` reports which you got, per type and per runtime.

`Int64` and `UInt64` are lock-free at either width, and they are the only 8-byte types that are.
A field of one of those two is seated on an 8-byte boundary wherever the hardware's instructions
demand it, which is what makes it reachable by `Interlocked` on a 32-bit runtime at all — but only by
`Interlocked`: an 8-byte `Volatile.Read` or `Volatile.Write` can tear there, so on that path the read
is `Interlocked.Read` and the write is `Interlocked.Exchange`. Nothing else joins them. The view has to
be the value exactly, since there is no slack to zero, and the type's equality has to be its bits, since
a compare-exchange there has no tail to fall back on — which is what rules `Double` out, not its size.

All six read-modify-writes keep their instruction at either width, because `Int32` and `UInt32` fit the
word and `Int64` and `UInt64` are reached by the same locked instructions the cell itself uses. They are
gated on the cell's strategy rather than on the word size for exactly that reason: an instruction issued
against a field the cell is guarding with a monitor would be a second writer the monitor knows nothing
about, and a `CompareExchange` could read, compare and store across an `Increment` and drop it.

**NativeAOT is supported and folds fully.** Under both compilers `Atomic<Int64>.Read()` inlines into its
caller with no call and no lock left in it — under the JIT on arm64 that is a single `ldapur` — and
`CodegenTests` holds both compilers to it.

**The monitor path locks on the instance.** Outside code holding a reference to a cell can `lock` on it
and interfere. A private lock object would be a second field, which is the one thing this design cannot
spend; see *Awkward sizes* above for what that field would cost. The [analyzer](#analyzer) reports such a
lock at compile time instead.

## Repository layout

| Project | |
|---|---|
| `Atomic/` | The library. `Atomic<T>` and its extensions. |
| `Atomic.Analyzers/` | The Roslyn analyzer behind `NSAT0001` and `NSAT0002`, packed inside the library's package. |
| `Candidates/` | `BoxAtomic<T>` and `SeqLockAtomic<T>`, the designs this one was chosen over, plus the `IAtomic<T>` interface and the struct adapters that let one suite and one harness drive all three. |
| `Tests/` | Correctness one thread at a time, thread safety under several, and the layout, allocation and strategy facts each implementation rests on. |
| `AnalyzerTests/` | The analyzer, run over snippets compiled against the real library. |
| `Benchmarks/` | Per category: one operation at a time, throughput under contention, and what the allocations cost the rest of the process. |
| `CodegenProbe/` | One call per cell operation, in methods that cannot be inlined away, for the disassembler to be pointed at. |

Only `Atomic/` is packed, with the analyzer inside it. MIT licensed.

### Tests

Two halves, because they fail differently. `AtomicContractTests` is behaviour one thread at a time —
round trips, what each strategy compares, null as an ordinary value — and a failure there is a plain
bug that reproduces on the first run. `ThreadSafetyTests` is what the same implementations owe several
threads at once, and a failure there is a race that may not:

| | |
|---|---|
| No lost updates | One thread per core (2 to 8) incrementing through compare-exchange arrive at the full count, for a word, a wide value and a value holding a reference. |
| Exchange conserves | Every thread swaps in tokens nobody else has and keeps what it displaced. The tokens taken out, plus the one left in the cell, are exactly the tokens put in. A swap that dropped a write or handed one value to two threads shows up here and nowhere else. |
| One winner | Threads released together from a barrier compare-exchange against the same comparand, five hundred times. Exactly one wins each round. |
| Publication | A reader that sees a sequence number published through a second cell sees the payload written before it — the acquire/release guarantee, which needs two cells to be a question at all. |
| No tearing | A writer stores values whose parts agree; a reader that sees parts disagreeing has caught a write half done. Across eight shapes that between them cover every strategy. |

None of that proves thread safety — a race that was never scheduled looks exactly like one that cannot
happen. So `ThreadSafetySuiteTests` points the whole suite at a deliberately unsynchronised cell and
requires it to go red, which is the nearest thing available to a check that the tests can still fail.
It earned its place immediately. It caught a test that had been quietly passing on a busy thread pool,
because the pool was running its eight bodies one after another rather than at once — and four more
tests had the same flaw. It then caught an exchange test that noticed a plainly broken cell one run in
twenty, because starting a thread costs more than that test's body took to finish, so the threads never
overlapped. Both are fixed by releasing dedicated threads together from a barrier, and by re-syncing
them on it as they go rather than only at the start. The barrier spins rather than blocks, and there is
one thread per core: on a 4-core CI runner, eight threads behind a blocking barrier woke one after
another and let the broken cell win every round, five attempts running.

That does not make detection certain, and the check does not pretend otherwise. A race needing two
threads inside the same nanosecond sometimes does not happen — measured at about one run in eight while
the rest of the suite ran beside it. So the check runs on its own, after every parallel test has
finished, and each property gets a few attempts and has to catch the broken cell in one of them. Failing every attempt in
a row is a different claim from losing a coin toss, and it is the one that fails the build.

### Codegen

Every performance claim above rests on the strategy folding away, and nothing else in the suite would
notice if it stopped. A cell that tested `IsInline` on each access would pass every other test here.

So `CodegenTests` reads the code a compiler actually produced: that reading a word reaches no monitor
and calls nothing at all, that reading a wider value does reach one, and that `Increment` on an `Int64`
is an `ldaddal` with no compare-and-swap left beside it. Each assertion is paired with a case that must
show the opposite, because one that only looks for something's absence passes just as happily when
pointed at nothing.

Both compilers are held to the same statements, since the library claims the same of both and the JIT
folding says nothing about NativeAOT. The JIT is read by running `CodegenProbe` with
`DOTNET_JitDisasm` set; NativeAOT by publishing it and asking the ahead-of-time compiler for the same
listing. That publish takes a few seconds and needs a native toolchain — a machine without one skips,
which is a visible outcome rather than a quiet pass.

It needs an optimised build, and skips with a note on a Debug one:

```
dotnet test -c Release
```

The monitor half is read from the names of runtime helpers and holds on any architecture. The
instruction half cannot be — the difference between one instruction and a retry loop is not a call to
anything — so it is read as mnemonics, written down for arm64 only, and skips elsewhere rather than
guessing.

All four were checked against a deliberately broken library rather than assumed to work. That is worth
doing here more than most places: an early attempt to break the folding, by adding a static the JIT
could not treat as constant, still folded under NativeAOT, which sees the whole program and can prove
a field nothing writes. It took a value read from the environment to stop both.

```
dotnet test
dotnet test -c Release
dotnet run -c Release --project Benchmarks -- --filter "*"
dotnet run -c Release --project Benchmarks -- contention
dotnet run -c Release --project Benchmarks -- gc
```
