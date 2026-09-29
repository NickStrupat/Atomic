using System.Diagnostics;
using NickStrupat;

namespace Benchmarks;

/// <summary>
/// Measures throughput with several threads using one cell. This is where the candidates differ most,
/// and a single-threaded benchmark cannot show it.
/// </summary>
/// <remarks>
/// <para>
/// Every candidate is called through a struct adapter from a harness that is generic over the adapter,
/// never through <see cref="IAtomic{T}"/>. A generic instantiated with a struct gets its own compiled
/// copy, so the adapter and the cell both inline and no row pays any call overhead. Through the
/// interface, the rows would not even pay the same overhead: a cell called through an adapter costs more
/// than one called directly, which would favour whichever cell was measured without one.
/// </para>
/// <para>
/// Every category that <see cref="CellBenchmarks{T}"/> measures single-threaded is also measured here
/// under contention, including those where all three implementations compile to the same instruction.
/// Those rows are the control: identical code must give identical throughput. When this harness called
/// cells through <see cref="IAtomic{T}"/>, those rows differed by up to 18 times, entirely because of
/// interface dispatch. Rows that agree are what show that rows that disagree are measuring the cells.
/// </para>
/// <para>
/// Compare within a category, not between categories. Within one, the three cells do the same work on
/// the same value, so the numbers show which strategy wins. Between categories they do not: each is a
/// different <c>T</c> of a different width, and reading a reference already costs more than reading a
/// word before any cell is involved.
/// </para>
/// </remarks>
public static class Contention
{
	private const Int32 Threads = 4;
	private const Int32 Batch = 1000;
	private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(500);

	/// <summary>Runs every contention scenario and prints a table of millions of operations per second.</summary>
	/// <returns>Zero, so the process reports success.</returns>
	public static Int32 Run()
	{
		Console.WriteLine($"{Threads} threads on one cell, {Duration.TotalMilliseconds:F0} ms per scenario, millions of operations per second");

		ReportCategory<Int64>();
		ReportCategory<Three>();
		ReportCategory<String>();
		ReportCategory<Decimal>();
		ReportCategory<Tagged>();

		ReportIncrement();
		return 0;
	}

	/// <summary>Measures all three implementations for one category, <typeparamref name="T"/>.</summary>
	/// <typeparam name="T">The type of the value held by the cell.</typeparam>
	private static void ReportCategory<T>()
	{
		var value = Samples.Of<T>();

		Console.WriteLine($"\n{typeof(T).Name} — {Samples.Describe<T>()}\n");
		Console.WriteLine($"{"cell",-24} {"all writers",13} {"all readers",13} {"1 writer, rest readers",23}");

		Report<T, AtomicAdapter<T>>("Atomic", () => new AtomicAdapter<T>(value));
		Report<T, BoxAtomicAdapter<T>>("BoxAtomic", () => new BoxAtomicAdapter<T>(value));
		Report<T, SeqLockAtomicAdapter<T>>("SeqLockAtomic", () => new SeqLockAtomicAdapter<T>(value));
	}

	/// <summary>
	/// Measures incrementing one cell from every thread. Only under contention can the instruction and the
	/// loop differ: the instruction never retries, while the loop retries once for every writer that gets
	/// in between its read and its compare-exchange.
	/// </summary>
	private static void ReportIncrement()
	{
		var field = new Counter();
		var atomic = new Atomic<Int64>(0);

		Console.WriteLine($"\nIncrementing one cell from {Threads} threads, millions of operations per second\n");
		Console.WriteLine($"{"driver",-40} {"Mops/s",10}");
		Console.WriteLine($"{"Interlocked.Increment on a field",-40} {Drive(field.IncrementBatch),10:F1}");
		Console.WriteLine($"{"Atomic<Int64>.Increment (instruction)",-40} {Drive(() => { for (var i = 0; i < Batch; i++) atomic.Increment(); }),10:F1}");
		Console.WriteLine($"{"Atomic<Int64>.Increment (loop)",-40} {Drive(() => { for (var i = 0; i < Batch; i++) IncrementByLoop(atomic); }),10:F1}");
		Console.WriteLine($"{"Atomic<T>.Increment from generic code",-40} {Drive(() => { for (var i = 0; i < Batch; i++) IncrementGenerically(atomic); }),10:F1}");
	}

	/// <summary>Increments from generic code, where the type is still a type parameter.</summary>
	/// <typeparam name="T">The type of the value held by the cell.</typeparam>
	/// <param name="atomic">The cell to increment.</param>
	/// <remarks>
	/// This is the kind of call the specialisation in <see cref="AtomicExtensions"/> exists for; it used to
	/// run at the speed of the loop. It now compiles to the same code as the "instruction" row, because
	/// there is only one <c>Increment</c> and both calls reach it. The two rows matching is the point: it
	/// makes no difference whether the caller names the concrete type.
	/// </remarks>
	private static void IncrementGenerically<T>(Atomic<T> atomic)
	where T : System.Numerics.IIncrementOperators<T> => atomic.Increment();

	/// <summary>Increments with a compare-exchange loop, which the library no longer uses for this type.</summary>
	/// <param name="atomic">The cell to increment.</param>
	/// <remarks>
	/// The comparison is between this and the "instruction" row. It is written out here because calling
	/// <c>AtomicExtensions.Increment</c> now always gets the instruction for this type.
	/// </remarks>
	private static void IncrementByLoop(Atomic<Int64> atomic)
	{
		var current = atomic.Read();
		while (true)
		{
			if (atomic.TryCompareExchange(current + 1, current, out var previous))
				return;
			current = previous;
		}
	}

	/// <summary>Runs <paramref name="batch"/> repeatedly on every thread until the time is up.</summary>
	/// <param name="batch">Performs <see cref="Batch"/> operations.</param>
	/// <returns>Millions of operations per second across all threads.</returns>
	private static Double Drive(Action batch)
	{
		using var cancellation = new CancellationTokenSource(Duration);
		var operations = 0L;
		var elapsed = Stopwatch.StartNew();

		var threads = Enumerable.Range(0, Threads).Select(_ => Task.Run(() =>
		{
			var completed = 0L;
			while (!cancellation.IsCancellationRequested)
			{
				batch();
				completed += Batch;
			}
			Interlocked.Add(ref operations, completed);
		})).ToArray();

		Task.WaitAll(threads);
		return operations / elapsed.Elapsed.TotalSeconds / 1_000_000.0;
	}

	/// <summary>A plain field, so the instruction can be measured with no cell around it.</summary>
	private sealed class Counter
	{
		private Int64 count;

		/// <summary>Increments the field <see cref="Batch"/> times.</summary>
		public void IncrementBatch()
		{
			for (var i = 0; i < Batch; i++)
				Interlocked.Increment(ref count);
		}
	}

	/// <summary>Measures one cell under three mixes of readers and writers, and prints the row.</summary>
	/// <typeparam name="T">The type of the value held by the cell.</typeparam>
	/// <typeparam name="TCell">
	/// The adapter wrapping the cell. It is a struct, so this method and everything it calls are compiled
	/// specifically for it rather than shared with every reference type.
	/// </typeparam>
	/// <param name="name">The label for the row.</param>
	/// <param name="create">Creates a fresh cell, so no scenario inherits state from the previous one.</param>
	private static void Report<T, TCell>(String name, Func<TCell> create)
	where TCell : struct, IAtomic<T>
	{
		var value = create().Read();
		var writers = Measure<T, TCell>(create(), Threads, value);
		var readers = Measure<T, TCell>(create(), 0, value);
		var mixed = Measure<T, TCell>(create(), 1, value);
		Console.WriteLine($"{name,-24} {writers,13:F1} {readers,13:F1} {mixed,23:F1}");
	}

	/// <summary>Drives one cell from several threads and counts the operations they complete.</summary>
	/// <typeparam name="T">The type of the value held by the cell.</typeparam>
	/// <param name="atomic">The cell under test.</param>
	/// <param name="writers">How many of the threads write; the rest read.</param>
	/// <param name="value">The value the writers store.</param>
	/// <returns>Millions of operations per second across all threads.</returns>
	/// <typeparam name="TCell">The adapter wrapping the cell; see <see cref="Report{T, TCell}"/>.</typeparam>
	private static Double Measure<T, TCell>(TCell atomic, Int32 writers, T value)
	where TCell : struct, IAtomic<T>
	{
		const Int32 batch = 1000;
		using var cancellation = new CancellationTokenSource(Duration);
		var operations = 0L;
		var elapsed = Stopwatch.StartNew();

		var threads = Enumerable.Range(0, Threads).Select(index => Task.Run(() =>
		{
			var completed = 0L;
			if (index < writers)
				while (!cancellation.IsCancellationRequested)
				{
					for (var i = 0; i < batch; i++)
						atomic.Write(value);
					completed += batch;
				}
			else
			{
				var last = value;
				while (!cancellation.IsCancellationRequested)
				{
					for (var i = 0; i < batch; i++)
						last = atomic.Read();
					completed += batch;
				}
				GC.KeepAlive(last);
			}
			Interlocked.Add(ref operations, completed);
		})).ToArray();

		Task.WaitAll(threads);
		return operations / elapsed.Elapsed.TotalSeconds / 1_000_000.0;
	}
}
