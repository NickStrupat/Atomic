using BenchmarkDotNet.Attributes;
using NickStrupat;

namespace Benchmarks;

/// <summary>
/// What the extension methods cost against the instruction they stand in for. <c>Interlocked</c> acts
/// on a reference to the storage and never retries; the extensions reach the storage only through a
/// compare-and-exchange loop, so they read, compute, and exchange.
/// </summary>
/// <remarks>
/// Only <see cref="Atomic{T}"/> is measured here. Every read-modify-write the candidates offer is the
/// same loop over the compare-and-exchange <see cref="CellBenchmarks{T}"/> already measures for each
/// category, so a row for them would add nothing — and the rows that used to be here added something
/// worse than nothing. They reached the candidates through <see cref="IAtomic{T}"/> while the
/// <see cref="Atomic{T}"/> rows called a concrete type directly, which measures a dispatch the
/// candidates do not have and would not have shipped with.
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
	public void Atomic_Increment_Loop() { for (var i = 0; i < Operations; i++) AtomicExtensions.Increment(atomic); }

	[Benchmark(OperationsPerInvoke = Operations)]
	public void Atomic_Decimal_Add() { for (var i = 0; i < Operations; i++) wide.Add(1m); }
}
