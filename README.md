# Atomic

A generic atomic cell for .NET. Lock-free and allocation-free for references and any unmanaged value up
to a word — including the three, five, six and seven byte structs that no interlocked instruction
matches. A monitor covers everything else, and `IsLockFree` tells you which one you got.

```csharp
var counter = new Atomic<Int64>(0);
counter.Increment();                       // a single interlocked instruction

var state = new Atomic<Colour>(Colour.Red);
state.CompareExchange(Colour.Green, Colour.Red);

var current = new Atomic<String?>(null);   // null is an ordinary value, including as a comparand
current.TryCompareExchange("first", comparand: null, out _);
```

Targets `net10.0`. One field, no boxing, nothing allocated per operation.

## How a value is stored

The strategy is chosen from `T` alone and folds to a constant when the JIT or the AOT compiler
specialises the cell, so a given `Atomic<T>` compiles to one path with no test left in it.

| `T` | Strategy | Cell size |
|---|---|---|
| Any reference type | `Interlocked` on the reference, write barrier intact | 24 B |
| Unmanaged, ≤ 8 bytes | `Interlocked` on an eight byte view of the field | 24 B |
| Wider than a word | `lock (this)` | 32 B and up |
| A value type holding references | `lock (this)` | 32 B and up |

```csharp
Atomic<Int32>.IsLockFree     // true
Atomic<String>.IsLockFree    // true
Atomic<Int32?>.IsLockFree    // true  — Nullable<Int32> is eight unmanaged bytes
Atomic<Decimal>.IsLockFree   // false — 16 bytes, so the monitor
```

### Awkward sizes

No processor has a three byte compare-and-swap, so a three byte struct would normally need a lock. It
doesn't here. Because `storage` is the only field the class declares, it begins on a word boundary and
the minimum size of an object leaves a full eight bytes there — so the cell swaps the whole word and
lets the slack ride along. Writes zero the slack, so a given value always has the same bit pattern and
`CompareExchange` compares something meaningful.

That is why the class has exactly one field, and why it uses a monitor rather than a private lock object
for the values it cannot swap in place: a second field would let the runtime seat the value off a word
boundary and take the trick away from every instantiation.

Widening is not quite free, and it is lopsided. Reading a three byte value costs 0.79 ns against 0.56
for a full word, because the word is loaded once and the value taken out of it. Writing costs 1.52 ns
against 0.40, because the value has to be put into a zeroed word first — three byte stores and an eight
byte load of the same stack slot, which is a store-to-load forward the hardware cannot satisfy from the
store buffer. Still cheaper than the 13 ns a lock would cost, which is the comparison that matters.

## Read-modify-write

Generic, over any `T` with the matching operator, via a compare-and-swap loop:

```csharp
Add  Subtract  Increment  Decrement  And  Or  Xor  Max  Min  Update
```

Return values follow `Interlocked`, inconsistencies included: `Add`, `Subtract`, `Increment` and
`Decrement` return the **new** value; `And`, `Or` and `Xor` return the **old** one.

`Update` takes a function, with an overload that threads state through so the delegate needn't capture:

```csharp
history.Update(entry, static (e, current) => current.Add(e));
```

For `Atomic<Int32>`, `Atomic<Int64>`, `Atomic<UInt32>` and `Atomic<UInt64>`, `Increment`, `Decrement`,
`Add`, `And` and `Or` resolve instead to overloads that issue the instruction directly with no loop.
Being declared on the concrete type, they win overload resolution automatically — you don't opt in.

Uncontended this makes no difference, because an uncontended loop succeeds on its first try — 6.96 ns
against 7.07. Under contention it does: four threads incrementing one `Atomic<Int64>` sustain roughly
25 Mops through the instruction against roughly 10 through the loop, because every failed comparison
costs another cache line migration.

## What "atomic" guarantees here

Three things people reasonably expect from the name, one of which is true.

**Each operation is linearizable, and operations do not compose.** A read, a write, an exchange or a
compare-exchange each take effect at a single instant, and no caller ever sees a torn value. Two of them
in a row are still two operations:

```csharp
cell.Write(cell.Read() + 1);   // not atomic
cell.Increment();              // atomic
```

`Read` and `Write` are methods rather than a property for this reason — `cell.Value += 1` reads as one
step and isn't.

**Success cannot be inferred from the returned value, so ask.** The comparison a cell performs depends
on `T`: identity for references, bits for a value in a word, `EqualityComparer<T>.Default` for anything
wider. No caller-side comparison reproduces all three:

```csharp
var cell = new Atomic<Double>(-0.0);
var previous = cell.CompareExchange(99.0, comparand: 0.0);
previous == 0.0          // true  — but nothing was stored, because the bits differ
previous.Equals(0.0)     // true  — likewise wrong

cell.TryCompareExchange(99.0, comparand: 0.0, out previous);   // false, correctly
```

`Interlocked.CompareExchange(ref Double, …)` has the same trap and no way out of it. Use
`TryCompareExchange` in any loop that branches on whether the swap happened.

**Ordering is acquire/release, not sequential consistency.** Reads are acquire, writes are release,
read-modify-writes are both. That is enough for publication across two cells:

```csharp
// thread A                     // thread B
data.Write(42);                 if (flag.Read())
flag.Write(true);                   use(data.Read());   // sees 42
```

It is not enough for a store to one cell followed by a load of another — the StoreLoad case, which is
unordered on x86 and arm64 alike. That needs a full fence, which `Interlocked.MemoryBarrier` provides,
and which `Exchange` and `CompareExchange` already are. Java's `volatile` and C++'s default
`memory_order_seq_cst` are stronger than this; don't carry that assumption over.

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
| `Atomic` | 6.58 | 6.56 | 6.56 | 13.33 | 13.08 |
| `BoxAtomic` | 6.80 | 6.57 | 6.63 | 12.63¹ | 13.72 |
| `SeqLockAtomic` | 6.60 | **8.99** | 6.56 | **8.65** | 13.46 |

¹ Allocates 32 bytes per operation. Nothing else in these tables allocates at all, and what those
bytes actually cost is *Accounting for the allocations* below.

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
- **`Three` is where `SeqLockAtomic` falls over.** It widens sizes of one, two, four and eight bytes and
  sends everything else to the version counter, so a three byte write costs 9.14 ns against 1.41.
- **`Atomic<T>` ties wherever it is lock-free and trails wherever it locks** — on five of those six
  measurements; the sixth is a three-way tie at the cost of a monitor. That is the trade the library
  makes: it allocates nothing, ever, and pays a monitor for the values that do not fit its one field.

```
Interlocked.Increment on a plain field    6.907 ns
Atomic<Int64>.Increment                   6.960 ns
Atomic<Int64>.Increment (via the loop)    7.066 ns
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

**64-bit for the lock-free path.** ECMA-335 I.12.6.2 aligns an eight byte value on the boundary the
hardware needs for a `native int`, and I.12.6.6 grants atomicity only up to that same width. On a 32-bit
runtime — x86, arm32, wasm — neither holds for the widened view, so every value goes to the monitor.
`IsLockFree` reports this.

**NativeAOT is supported and folds fully.** `Atomic<Int32>.Read()` compiles to a single `ldapr` and
inlines into its caller, the same as under the JIT.

**The monitor path locks on the instance.** Outside code holding a reference to a cell can `lock` on it
and interfere. A private lock object would be a second field, which is the one thing this design cannot
spend; see *Awkward sizes* above for what that field would cost.

## Repository layout

| Project | |
|---|---|
| `Atomic/` | The library. `Atomic<T>` and its extensions, and nothing else ships. |
| `Candidates/` | `BoxAtomic<T>` and `SeqLockAtomic<T>`, the designs this one was chosen over, plus the `IAtomic<T>` interface and the struct adapters that let one suite and one harness drive all three. |
| `Tests/` | Correctness one thread at a time, thread safety under several, and the layout, allocation and strategy facts each implementation rests on. |
| `Benchmarks/` | Per category: one operation at a time, throughput under contention, and what the allocations cost the rest of the process. |

### Tests

Two halves, because they fail differently. `AtomicContractTests` is behaviour one thread at a time —
round trips, what each strategy compares, null as an ordinary value — and a failure there is a plain
bug that reproduces on the first run. `ThreadSafetyTests` is what the same implementations owe several
threads at once, and a failure there is a race that may not:

| | |
|---|---|
| No lost updates | Eight threads incrementing through compare-exchange arrive at the full count, for a word, a wide value and a value holding a reference. |
| Exchange conserves | Every thread swaps in tokens nobody else has and keeps what it displaced. The tokens taken out, plus the one left in the cell, are exactly the tokens put in. A swap that dropped a write or handed one value to two threads shows up here and nowhere else. |
| One winner | Eight threads released from a barrier compare-exchange against the same comparand, five hundred times. Exactly one wins each round. |
| Publication | A reader that sees a sequence number published through a second cell sees the payload written before it — the acquire/release guarantee, which needs two cells to be a question at all. |
| No tearing | A writer stores values whose parts agree; a reader that sees parts disagreeing has caught a write half done. Across seven shapes, one per strategy. |

None of that proves thread safety — a race that was never scheduled looks exactly like one that cannot
happen. So `ThreadSafetySuiteTests` points the whole suite at a deliberately unsynchronised cell and
requires it to go red, which is the nearest thing available to a check that the tests can still fail.
It earned its place immediately. It caught a test that had been quietly passing on a busy thread pool,
because the pool was running its eight bodies one after another rather than at once — and four more
tests had the same flaw. It then caught an exchange test that noticed a plainly broken cell one run in
twenty, because starting a thread costs more than that test's body took to finish, so the threads never
overlapped. Both are fixed by releasing dedicated threads together from a barrier, and by re-syncing
them on it as they go rather than only at the start.

That does not make detection certain, and the check does not pretend otherwise. Running the whole suite
in parallel puts far more threads on the machine than it has cores, and a race needing two of them
inside the same nanosecond sometimes does not happen — measured at about one run in eight. So each
property gets a few attempts and has to catch the broken cell in one of them. Failing every attempt in
a row is a different claim from losing a coin toss, and it is the one that fails the build.

```
dotnet test
dotnet run -c Release --project Benchmarks -- --filter "*"
dotnet run -c Release --project Benchmarks -- contention
dotnet run -c Release --project Benchmarks -- gc
```
