using System.Runtime.CompilerServices;
using NickStrupat;

namespace Tests;

/// <summary>
/// A check on <see cref="ThreadSafetyTests"/> itself: that it can go red.
/// </summary>
/// <remarks>
/// A concurrency suite decays into one that passes for the wrong reason more easily than most — narrow
/// a window, cache a value the compiler was free to cache, and every implementation looks correct
/// including the ones that are not. So the suite is pointed at a cell that is plainly unsafe, and is
/// required to say so.
/// <para>
/// Only the properties that fail by an unmistakable margin are asserted here. The whole suite catches
/// <see cref="NaiveAtomic{T}"/> — the tearing and publication tests included — but those two depend on
/// a window opening, and demanding that a race show up is a worse test than demanding a count be wrong.
/// Even the three below depend on it a little, which is what <see cref="MustCatch"/> is for.
/// </para>
/// </remarks>
public class ThreadSafetySuiteTests
{
	/// <summary>How many times a property may fail to catch the broken cell before that is a verdict.</summary>
	private const Int32 Attempts = 5;

	/// <summary>How long the half-written writer is raced against a reader, per attempt.</summary>
	private static readonly TimeSpan Window = TimeSpan.FromMilliseconds(250);

	[Fact]
	public void TheSuite_WhenPointedAtACellThatIsNotThreadSafe_Fails()
	{
		var unsafeCell = new NaiveThreadSafetyTests();

		// Updates land on top of one another, so the total comes up short.
		MustCatch(unsafeCell.CompareExchange_WhenValueFitsInAWordAndIsContended_LosesNoUpdates);

		// Two threads read the same value out of the cell, so one token is handed out twice and another
		// never at all.
		MustCatch(unsafeCell.Exchange_WhenContended_NeitherLosesNorDuplicatesAValue);

		// The comparison and the store are two steps, so a whole round can be won more than once.
		MustCatch(unsafeCell.CompareExchange_WhenEveryThreadRacesForTheSameComparand_LetsExactlyOneWin);

		// And again where the comparand is equal without being identical, which is the path that has to
		// retry rather than report a mismatch. The window is the same one, and just as wide.
		MustCatch(unsafeCell.CompareExchange_WhenTheComparandIsEqualButNotIdentical_StillLetsExactlyOneWin);
	}

	[Fact]
	public void TheTearingCheck_WhenPointedAtAWriterThatStoresHalfAValueAtATime_SeesIt()
	{
		// Read_WhenWrittenConcurrently_NeverObservesAValueThatWasNeverWritten asserts an absence, and an
		// absence is also what a check looking in the wrong place reports. Its eight byte integer row is
		// the one that matters where the word is four bytes — which is not where this is developed, so
		// that row cannot be falsified by the machine that runs it and would sit there unexercised.
		//
		// NaiveAtomic is no use as the opposite case here. It stores an Int64 with one plain store, and
		// whether that tears is the hardware's business: ECMA-335 I.12.6.6 permits it below a native int,
		// but AArch32 on an ARMv8 core makes an eight byte aligned store single-copy atomic anyway, so a
		// cell with no synchronisation at all can still come through clean. Demanding that it tear would
		// be demanding the hardware be weak.
		//
		// So the check is pointed at a writer that cannot be atomic on any hardware: two Int32 stores,
		// one per half. Nothing able to see a half-written Int64 can miss this one, which is what makes
		// the absence above worth reading.
		//
		// Attempts rather than a single run, for the reason MustCatch gives below.
		for (var attempt = 0; attempt < Attempts; attempt++)
			if (HalvesWereSeenToDisagree())
				return;

		Assert.Fail($"an Int64 written one half at a time was read across {Attempts} windows without a "
			+ "single half-written value being seen; the tearing check can no longer see one.");
	}

	/// <summary>Races a half-at-a-time writer against a reader for one <see cref="Window"/>.</summary>
	/// <returns><see langword="true"/> when the reader read an <see cref="Int64"/> whose halves disagreed.</returns>
	/// <remarks>
	/// The reader uses <see cref="Interlocked"/> so that anything it sees came from the writer. A plain
	/// eight byte read is itself allowed to tear where the word is four bytes, and a check that cannot
	/// say which side tore is a weaker one than this needs to be.
	/// </remarks>
	private static Boolean HalvesWereSeenToDisagree()
	{
		var cell = new HalfWrittenInt64();
		using var cancellation = new CancellationTokenSource(Window);
		var disagreed = false;

		var writer = new Thread(() =>
		{
			for (var i = 0; !cancellation.IsCancellationRequested; i++)
				cell.Write(i);
		}) { IsBackground = true };

		var reader = new Thread(() =>
		{
			while (!cancellation.IsCancellationRequested)
			{
				var value = cell.Read();
				if ((Int32)(value >> 32) != (Int32)value)
					disagreed = true;
			}
		}) { IsBackground = true };

		writer.Start();
		reader.Start();
		writer.Join();
		reader.Join();

		return disagreed;
	}

	/// <summary>Runs <paramref name="property"/> until it fails, and requires that it does.</summary>
	/// <param name="property">The property that should catch <see cref="NaiveAtomic{T}"/>.</param>
	/// <remarks>
	/// Several attempts rather than one, and not as a way of tolerating a flaky test. What is under test
	/// is whether the suite is able to catch this cell; whether any single run does depends on the
	/// scheduler. With the rest of the suite running in parallel there are far more threads than cores,
	/// and a race that needs two of them inside the same nanosecond sometimes does not happen at all.
	/// One trial reports that as the suite being incapable, which is a different claim and a false one —
	/// measured at roughly one run in eight. Every attempt in a row escaping is the claim worth failing.
	/// </remarks>
	private static void MustCatch(Action property)
	{
		for (var attempt = 0; attempt < Attempts; attempt++)
		{
			try
			{
				property();
			}
			catch
			{
				return;
			}
		}

		Assert.Fail($"{property.Method.Name} ran {Attempts} times against a cell with no synchronisation "
			+ "at all and passed every time; the suite can no longer tell the two apart.");
	}
}

/// <summary>
/// A cell with no synchronisation whatsoever: plain field access, a read-then-write exchange, and a
/// compare that is a separate step from the store it guards.
/// </summary>
/// <typeparam name="T">The type of the value held by the cell.</typeparam>
/// <remarks>
/// This is what an implementation looks like when someone writes down what the operations mean and
/// forgets that another thread is running. It exists to be caught.
/// </remarks>
/// <param name="initial">The initial value.</param>
internal sealed class NaiveAtomic<T>(T initial) : IAtomic<T>
{
	private T value = initial;

	/// <inheritdoc />
	public T Read() => value;

	/// <inheritdoc />
	public void Write(T value) => this.value = value;

	/// <inheritdoc />
	public T Exchange(T value)
	{
		var previous = this.value;
		this.value = value;
		return previous;
	}

	/// <inheritdoc />
	public T CompareExchange(T value, T comparand)
	{
		var previous = this.value;
		if (EqualityComparer<T>.Default.Equals(previous, comparand))
			this.value = value;
		return previous;
	}

	/// <inheritdoc />
	public Boolean TryCompareExchange(T value, T comparand, out T previous)
	{
		previous = this.value;
		if (!EqualityComparer<T>.Default.Equals(previous, comparand))
			return false;
		this.value = value;
		return true;
	}
}

/// <summary>
/// An eight byte integer written one half at a time, which no hardware can make atomic.
/// </summary>
/// <remarks>
/// Both halves are given the same <see cref="Int32"/>, so a value whose halves disagree was read between
/// the two stores. Which half is written first does not matter and is never asked, so this says nothing
/// about byte order and does not need to.
/// </remarks>
internal sealed class HalfWrittenInt64
{
	/// <summary>The value, and the only field, so that it is seated on an eight byte boundary.</summary>
	private Int64 value;

	/// <summary>Reads the whole eight bytes, indivisibly, at either word size.</summary>
	/// <returns>The value, which may have been assembled by the writer from two different stores.</returns>
	public Int64 Read() => Interlocked.Read(ref value);

	/// <summary>Writes <paramref name="half"/> into both halves, as two separate stores.</summary>
	/// <param name="half">The value to put in each half.</param>
	public void Write(Int32 half)
	{
		ref var first = ref Unsafe.As<Int64, Int32>(ref value);
		Volatile.Write(ref first, half);
		Volatile.Write(ref Unsafe.Add(ref first, 1), half);
	}
}

/// <summary>
/// The suite bound to <see cref="NaiveAtomic{T}"/>. Internal so the test runner does not collect it —
/// every one of these is meant to fail, and <see cref="ThreadSafetySuiteTests"/> is what runs them.
/// </summary>
internal sealed class NaiveThreadSafetyTests : ThreadSafetyTests
{
	protected override IAtomic<T> Create<T>(T value) => new NaiveAtomic<T>(value);
}
