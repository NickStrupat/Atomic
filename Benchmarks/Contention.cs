using System.Diagnostics;
using NickStrupat;

namespace Benchmarks;

/// <summary>
/// Throughput with several threads on one cell, which is where the candidates separate most sharply
/// and where a per-operation benchmark says nothing.
/// </summary>
/// <remarks>
/// <para>
/// Every candidate is driven through a struct adapter and a harness generic over it, not through
/// <see cref="IAtomic{T}"/> directly. That is deliberate: a generic instantiated with a value type gets
/// its own body, so the adapter and the cell both inline and every row pays the same nothing. Typed to
/// the interface instead, the rows would not even pay the same something — a cell reached through one
/// adapter costs more than a cell reached directly, which would have flattered whichever of them was
/// measured without one.
/// </para>
/// <para>
/// Every category <see cref="CellBenchmarks{T}"/> measures one operation at a time is measured here
/// under contention too, including the ones where all three implementations emit the same instruction.
/// Those rows are the control: three strategies that compile to the same code have to report the same
/// throughput, and when this harness typed its cells as <see cref="IAtomic{T}"/> they did not — it
/// reported differences of up to eighteen times, all of them its own dispatch. A row where the three
/// agree is how you know the rows where they disagree are about the cells.
/// </para>
/// <para>
/// Read down a category, not across them. Within one the three cells do the same work on the same
/// value, so the numbers answer which strategy wins. Between two they do not — a category is a
/// different <c>T</c>, of a different width, and a reference read costs more than a word read before
/// any cell is involved.
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

	/// <summary>Measures all three implementations against one category of <typeparamref name="T"/>.</summary>
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
	/// Incrementing one cell from every thread, which is the only place the instruction and the loop can
	/// differ: the instruction never has to retry, and the loop retries once for every writer that lands
	/// between its read and its exchange.
	/// </summary>
	private static void ReportIncrement()
	{
		var field = new Counter();
		var atomic = new Atomic<Int64>(0);

		Console.WriteLine($"\nIncrementing one cell from {Threads} threads, millions of operations per second\n");
		Console.WriteLine($"{"driver",-40} {"Mops/s",10}");
		Console.WriteLine($"{"Interlocked.Increment on a field",-40} {Drive(field.IncrementBatch),10:F1}");
		Console.WriteLine($"{"Atomic<Int64>.Increment (instruction)",-40} {Drive(() => { for (var i = 0; i < Batch; i++) atomic.Increment(); }),10:F1}");
		Console.WriteLine($"{"Atomic<Int64>.Increment (loop)",-40} {Drive(() => { for (var i = 0; i < Batch; i++) AtomicExtensions.Increment(atomic); }),10:F1}");
	}

	/// <summary>Runs a batch of operations on every thread until the time is up.</summary>
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

	/// <summary>A plain field, for the instruction to act on with nothing in the way.</summary>
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

	/// <summary>Measures one cell under three mixes of readers and writers and prints the row.</summary>
	/// <typeparam name="T">The type of the value held by the cell.</typeparam>
	/// <typeparam name="TCell">
	/// The adapter presenting the cell. A struct, so that this method and everything it calls are
	/// compiled for it specifically rather than shared with every other reference type.
	/// </typeparam>
	/// <param name="name">The label for the row.</param>
	/// <param name="create">Produces a fresh cell, so no scenario inherits another's contention.</param>
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
	/// <typeparam name="TCell">The adapter presenting the cell; see <see cref="Report{T, TCell}"/>.</typeparam>
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
