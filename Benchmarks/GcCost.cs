using System.Diagnostics;
using NickStrupat;

namespace Benchmarks;

/// <summary>
/// Measures what a cell's allocations cost the whole process, not just the thread that allocates.
/// </summary>
/// <remarks>
/// <para>
/// A single-threaded benchmark charges an allocating cell almost nothing, and rightly so: the allocation
/// is a pointer bump, the boxes are dead before the next collection, and the collections that do happen
/// fall inside the thread's own timing. <see cref="CellBenchmarks{T}"/> measures a
/// <see cref="BoxAtomic{T}"/> write of a <see cref="Decimal"/> at a third of the cost of
/// <see cref="Atomic{T}"/>, and that is accurate as far as it goes.
/// </para>
/// <para>
/// It misses that a generation 0 collection stops every thread in the process, not only the one that
/// made the garbage. So each cell is run twice: once with the writer alone, and once alongside
/// "bystander" threads doing unrelated work. The bystanders allocate nothing, so every collection during
/// a run is the writer's. Their own throughput is not reported, because it turned out to track how fast
/// the writer runs rather than how much it allocates: a cell that allocates nothing slows them just as
/// much, simply by keeping a core busy.
/// </para>
/// <para>
/// The second run shows that the pause is not a fixed cost paid by the allocating thread. The same writes
/// cost several times more once there are other threads to suspend and resume. The last column charges
/// the pause to every thread it stops. That column is a model, not a measurement: it assumes the other
/// threads had work to do, which is the case that matters. Reads are not measured, because no cell
/// allocates to read.
/// </para>
/// </remarks>
public static class GcCost
{
	private const Int32 Bystanders = 7;
	private const Int32 Batch = 1000;
	private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(1500);

	/// <summary>Runs every category and prints what its allocations cost.</summary>
	/// <returns>Zero, so the process reports success.</returns>
	public static Int32 Run()
	{
		Console.WriteLine($"One thread writing a cell beside {Bystanders} threads doing unrelated, non-allocating work.");
		Console.WriteLine($"{Duration.TotalMilliseconds:F0} ms per scenario.");

		ReportCategory<Int64>();
		ReportCategory<Three>();
		ReportCategory<String>();
		ReportCategory<Decimal>();
		ReportCategory<Tagged>();
		return 0;
	}

	/// <summary>Measures all three implementations for one category, <typeparamref name="T"/>.</summary>
	/// <typeparam name="T">The type of the value held by the cell.</typeparam>
	private static void ReportCategory<T>()
	{
		var value = Samples.Of<T>();

		Console.WriteLine($"\n{typeof(T).Name} — {Samples.Describe<T>()}\n");
		Console.WriteLine($"{"cell",-16}{"writes M/s",12}{"B/write",9}{"gen0",7}{"pause alone",13}{$"pause with {Bystanders}",17}{"charged",11}");

		Report("Atomic", Measure<T, AtomicAdapter<T>>(new AtomicAdapter<T>(value), value, 0),
			Measure<T, AtomicAdapter<T>>(new AtomicAdapter<T>(value), value, Bystanders));
		Report("BoxAtomic", Measure<T, BoxAtomicAdapter<T>>(new BoxAtomicAdapter<T>(value), value, 0),
			Measure<T, BoxAtomicAdapter<T>>(new BoxAtomicAdapter<T>(value), value, Bystanders));
		Report("SeqLockAtomic", Measure<T, SeqLockAtomicAdapter<T>>(new SeqLockAtomicAdapter<T>(value), value, 0),
			Measure<T, SeqLockAtomicAdapter<T>>(new SeqLockAtomicAdapter<T>(value), value, Bystanders));
	}

	/// <summary>Prints one row of the table.</summary>
	/// <param name="name">The label for the row.</param>
	/// <param name="alone">The run with the writer on its own.</param>
	/// <param name="crowded">The run with the writer alongside <see cref="Bystanders"/> other threads.</param>
	private static void Report(String name, Result alone, Result crowded)
	{
		// The writer's own nanoseconds already include its share of the pause, because it was stopped
		// too. What they do not include is the same pause charged to everyone else.
		var own = 1000.0 / alone.Writes;
		var charged = own + crowded.PausePerWrite * Bystanders;

		Console.WriteLine($"{name,-16}{alone.Writes,12:F1}{alone.Bytes,9:F1}{alone.Gen0,7}"
			+ $"{alone.PausePerWrite,10:F2} ns{crowded.PausePerWrite,14:F2} ns{charged,8:F1} ns");
	}

	/// <summary>Runs one writer alongside the bystanders and records the results.</summary>
	/// <typeparam name="T">The type of the value held by the cell.</typeparam>
	/// <typeparam name="TCell">The adapter wrapping the cell.</typeparam>
	/// <param name="cell">The cell to write to.</param>
	/// <param name="value">The value the writer stores.</param>
	/// <param name="bystanders">How many threads doing unrelated work to run alongside the writer.</param>
	/// <returns>The run's throughput, allocation and GC pause.</returns>
	private static Result Measure<T, TCell>(TCell cell, T value, Int32 bystanders)
	where TCell : struct, IAtomic<T>
	{
		using var cancellation = new CancellationTokenSource(Duration);
		var writes = 0L;
		var spins = 0L;
		var bytes = 0L;
		var gen0 = GC.CollectionCount(0);
		var pause = GC.GetTotalPauseDuration();
		var elapsed = Stopwatch.StartNew();

		var threads = new List<Thread>
		{
			new(() =>
			{
				var completed = 0L;
				var before = GC.GetAllocatedBytesForCurrentThread();
				while (!cancellation.IsCancellationRequested)
				{
					for (var i = 0; i < Batch; i++)
						cell.Write(value);
					completed += Batch;
				}
				Interlocked.Add(ref bytes, GC.GetAllocatedBytesForCurrentThread() - before);
				Interlocked.Add(ref writes, completed);
			}) { IsBackground = true },
		};

		for (var b = 0; b < bystanders; b++)
			threads.Add(new Thread(() =>
			{
				var completed = 0L;
				var accumulator = 0.0;
				while (!cancellation.IsCancellationRequested)
				{
					for (var i = 0; i < Batch; i++)
						accumulator = accumulator * 1.000001 + 1.0;
					completed += Batch;
				}
				Interlocked.Add(ref spins, completed);
				GC.KeepAlive(accumulator);
			}) { IsBackground = true });

		foreach (var thread in threads)
			thread.Start();
		foreach (var thread in threads)
			thread.Join();
		elapsed.Stop();

		var pauseNanoseconds = (GC.GetTotalPauseDuration() - pause).TotalNanoseconds;
		return new Result(
			Writes: writes / elapsed.Elapsed.TotalSeconds / 1e6,
			Bystander: spins / elapsed.Elapsed.TotalSeconds / 1e6,
			Bytes: (Double)bytes / writes,
			Gen0: GC.CollectionCount(0) - gen0,
			PausePerWrite: pauseNanoseconds / writes);
	}

	/// <summary>The results of one run of <see cref="Measure{T, TCell}"/>.</summary>
	/// <param name="Writes">Millions of writes per second.</param>
	/// <param name="Bystander">Millions of unrelated operations per second, across all bystanders.</param>
	/// <param name="Bytes">Bytes allocated per write.</param>
	/// <param name="Gen0">Number of generation 0 collections during the run.</param>
	/// <param name="PausePerWrite">Nanoseconds of stop-the-world pause per write.</param>
	private readonly record struct Result(
		Double Writes,
		Double Bystander,
		Double Bytes,
		Int32 Gen0,
		Double PausePerWrite);
}
