using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using NickStrupat;

namespace Benchmarks;

/// <summary>
/// Measures single operations on an uncontended cell, for each category of <typeparamref name="T"/> that
/// the implementations handle differently.
/// </summary>
/// <typeparam name="T">The type of the value held by the cell.</typeparam>
/// <remarks>
/// <para>
/// The categories are what decide a cell's strategy, so the comparison is organised around them. A cell
/// can keep a value in the word it already has, keep a reference there, or need somewhere else for the
/// value. Odd sizes are where the implementations first differ: a 3-byte value can only be swapped in
/// place by a cell laid out so it can be widened to a word.
/// </para>
/// <para>
/// This is one generic class rather than one class per category. Five near-identical copies would be
/// five chances to measure a category slightly differently from the others and mistake that for a
/// result. Being generic also rules out arithmetic in the loop: an earlier version summed what it read,
/// which for <see cref="Decimal"/> added the cost of decimal addition to the cost of the read.
/// </para>
/// <para>
/// Each benchmark repeats its operation <see cref="Operations"/> times and divides, because a single
/// volatile read costs less than the harness overhead subtracted from it, and would report as zero.
/// Every comparand is the value the cell already holds, so the compare-exchanges measured all succeed.
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
