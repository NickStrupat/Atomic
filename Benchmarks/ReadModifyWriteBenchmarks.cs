using BenchmarkDotNet.Attributes;
using NickStrupat;

namespace Benchmarks;

/// <summary>
/// Measures the read-modify-write extensions against a bare <c>Interlocked</c> call: the specialised
/// instruction, a hand-written compare-exchange loop, and the lock path for a wide value.
/// </summary>
/// <remarks>
/// Only <see cref="Atomic{T}"/> is measured here. A read-modify-write on any candidate is the same loop
/// over the compare-exchange that <see cref="CellBenchmarks{T}"/> already measures for each category, so
/// rows for them would add nothing. The rows that used to be here were worse than nothing: they called
/// the candidates through <see cref="IAtomic{T}"/> but <see cref="Atomic{T}"/> directly, so they measured
/// an interface dispatch the candidates would never have shipped with.
/// </remarks>
[MemoryDiagnoser, ShortRunJob]
public class ReadModifyWriteBenchmarks
{
	private const Int32 Operations = 64;

	private Int64 native;
	private readonly Atomic<Int64> atomic = new(0);
	private readonly Atomic<Decimal> wide = new(0m);

	[Benchmark(Baseline = true, OperationsPerInvoke = Operations)]
	public void Interlocked_Increment() { for (var i = 0; i < Operations; i++) Interlocked.Increment(ref native); }

	[Benchmark(OperationsPerInvoke = Operations)]
	public void Atomic_Increment_Instruction() { for (var i = 0; i < Operations; i++) atomic.Increment(); }

	[Benchmark(OperationsPerInvoke = Operations)]
	public void Atomic_Increment_Loop() { for (var i = 0; i < Operations; i++) IncrementByLoop(atomic); }

	/// <summary>Increments with a compare-exchange loop, which the library no longer uses for this type.</summary>
	/// <param name="atomic">The cell to increment.</param>
	/// <returns>The new value.</returns>
	/// <remarks>
	/// Written out here because <c>AtomicExtensions.Increment</c> now compiles to the instruction for this
	/// type, so calling it would just measure the instruction row a second time.
	/// </remarks>
	private static Int64 IncrementByLoop(Atomic<Int64> atomic)
	{
		var current = atomic.Read();
		while (true)
		{
			var next = current + 1;
			if (atomic.TryCompareExchange(next, current, out var previous))
				return next;
			current = previous;
		}
	}

	[Benchmark(OperationsPerInvoke = Operations)]
	public void Atomic_Decimal_Add() { for (var i = 0; i < Operations; i++) wide.Add(1m); }
}
