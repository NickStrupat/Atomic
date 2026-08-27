using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using NickStrupat;

namespace Benchmarks;

/// <summary>
/// One operation at a time, on a cell nobody else is touching, for each category of
/// <typeparamref name="T"/> the implementations treat differently.
/// </summary>
/// <typeparam name="T">The type of the value held by the cell.</typeparam>
/// <remarks>
/// <para>
/// The categories are what decides a strategy, so they are what the comparison is organised around.
/// A cell can hold a value in the word it already has, hold a reference in that word, or hold neither
/// and need somewhere else to put it — and the awkward sizes are where the implementations first stop
/// agreeing, because a three byte value is only swappable if a cell is laid out so it can be widened.
/// </para>
/// <para>
/// One generic class rather than one class per category: five copies differing in a type name is five
/// chances for a category to be measured slightly differently from its neighbours and for the
/// difference to be read as a result. It also rules out arithmetic in the loop. An earlier version
/// accumulated what it read, which for <see cref="Decimal"/> measured decimal addition alongside the
/// read and reported it as the cost of the read.
/// </para>
/// <para>
/// Each benchmark repeats its operation <see cref="Operations"/> times and divides, because a single
/// volatile read costs less than the harness overhead subtracted from it and reports as zero. Every
/// comparand is the value the cell already holds, so the exchanges measured are the ones that succeed.
/// </para>
/// </remarks>
[MemoryDiagnoser, ShortRunJob, CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[GenericTypeArguments(typeof(Int64))]
[GenericTypeArguments(typeof(Three))]
[GenericTypeArguments(typeof(String))]
[GenericTypeArguments(typeof(Decimal))]
[GenericTypeArguments(typeof(Tagged))]
public class CellBenchmarks<T>
{
	private const Int32 Operations = 64;

	private readonly T held = Samples.Of<T>();
	private readonly Atomic<T> atomic = new(Samples.Of<T>());
	private readonly BoxAtomic<T> boxAtomic = new(Samples.Of<T>());
	private readonly SeqLockAtomic<T> seqLockAtomic = new(Samples.Of<T>());

	[BenchmarkCategory("Read"), Benchmark(Baseline = true, OperationsPerInvoke = Operations)]
	public T Atomic_Read()
	{
		var last = held;
		for (var i = 0; i < Operations; i++)
			last = atomic.Read();
		return last;
	}

	[BenchmarkCategory("Read"), Benchmark(OperationsPerInvoke = Operations)]
	public T BoxAtomic_Read()
	{
		var last = held;
		for (var i = 0; i < Operations; i++)
			last = boxAtomic.Read();
		return last;
	}

	[BenchmarkCategory("Read"), Benchmark(OperationsPerInvoke = Operations)]
	public T SeqLockAtomic_Read()
	{
		var last = held;
		for (var i = 0; i < Operations; i++)
			last = seqLockAtomic.Read();
		return last;
	}

	[BenchmarkCategory("Write"), Benchmark(Baseline = true, OperationsPerInvoke = Operations)]
	public void Atomic_Write()
	{
		for (var i = 0; i < Operations; i++)
			atomic.Write(held);
	}

	[BenchmarkCategory("Write"), Benchmark(OperationsPerInvoke = Operations)]
	public void BoxAtomic_Write()
	{
		for (var i = 0; i < Operations; i++)
			boxAtomic.Write(held);
	}

	[BenchmarkCategory("Write"), Benchmark(OperationsPerInvoke = Operations)]
	public void SeqLockAtomic_Write()
	{
		for (var i = 0; i < Operations; i++)
			seqLockAtomic.Write(held);
	}

	[BenchmarkCategory("CompareExchange"), Benchmark(Baseline = true, OperationsPerInvoke = Operations)]
	public void Atomic_CompareExchange()
	{
		for (var i = 0; i < Operations; i++)
			atomic.CompareExchange(held, held);
	}

	[BenchmarkCategory("CompareExchange"), Benchmark(OperationsPerInvoke = Operations)]
	public void BoxAtomic_CompareExchange()
	{
		for (var i = 0; i < Operations; i++)
			boxAtomic.CompareExchange(held, held);
	}

	[BenchmarkCategory("CompareExchange"), Benchmark(OperationsPerInvoke = Operations)]
	public void SeqLockAtomic_CompareExchange()
	{
		for (var i = 0; i < Operations; i++)
			seqLockAtomic.CompareExchange(held, held);
	}
}
