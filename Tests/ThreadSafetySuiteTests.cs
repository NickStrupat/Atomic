using System.Runtime.CompilerServices;
using NickStrupat;

namespace Tests;

/// <summary>
/// Tests <see cref="ThreadSafetyTests"/> itself: checks that it fails against a cell that is not
/// thread-safe.
/// </summary>
/// <remarks>
/// <para>
/// Concurrency tests easily end up passing for the wrong reason. Narrow a timing window or let the
/// compiler cache a value, and every implementation looks correct, including broken ones. So the suite
/// is run against a cell with no synchronisation at all, and must report it.
/// </para>
/// <para>
/// Only the properties that fail by a clear margin are asserted here. The whole suite does catch
/// <see cref="NaiveAtomic{T}"/>, including the tearing and publication tests, but those depend on a race
/// actually happening, and requiring a race to show up makes a worse test than requiring a count to be
/// wrong. Even the ones below depend on timing a little, which is what <see cref="MustCatch"/> handles.
/// </para>
/// </remarks>
public class ThreadSafetySuiteTests
{
	/// <summary>How many runs a property gets to catch the broken cell before the test fails.</summary>
	private const Int32 Attempts = 5;

	/// <summary>How long the half-at-a-time writer races a reader in each attempt.</summary>
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
		// absence is also what a check looking in the wrong place reports. Its 8-byte integer row is
		// the one that matters where the word is 4 bytes — which is not where this is developed, so
		// that row cannot be falsified by the machine that runs it and would sit there unexercised.
		//
		// NaiveAtomic is no use as the opposite case here. It stores an Int64 with one plain store, and
		// whether that tears is the hardware's business: ECMA-335 I.12.6.6 permits it below a native int,
		// but AArch32 on an ARMv8 core makes an 8-byte aligned store single-copy atomic anyway, so a
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
	/// <returns><see langword="true"/> if the reader saw an <see cref="Int64"/> whose halves differed.</returns>
	/// <remarks>
	/// The reader uses <see cref="Interlocked"/> so that any tear it sees came from the writer. A plain
	/// 8-byte read can itself tear where the word is 4 bytes, and then the check could not tell which side
	/// tore.
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

	/// <summary>
	/// Runs <paramref name="property"/> up to <see cref="Attempts"/> times, and fails if it never fails.
	/// </summary>
	/// <param name="property">The property that should catch <see cref="NaiveAtomic{T}"/>.</param>
	/// <remarks>
	/// Several attempts are not a way of tolerating a flaky test. The question is whether the suite is
	/// able to catch this cell; whether one particular run does depends on the scheduler. With the rest of
	/// the suite running in parallel there are far more threads than cores, and a race that needs two
	/// threads in the same nanosecond sometimes does not happen at all — measured at about one run in
	/// eight. A single run would report that as "the suite cannot catch this", which is false. Missing it
	/// on every attempt in a row is the result worth failing on.
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
/// A deliberately broken cell with no synchronisation: plain field reads and writes, an exchange that
/// reads then writes, and a compare that is a separate step from the store.
/// </summary>
/// <typeparam name="T">The type of the value held by the cell.</typeparam>
/// <remarks>
/// This is what the operations look like written without any thought for other threads. It exists for
/// the suite to catch.
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
/// An 8-byte integer written one half at a time, which no hardware can make atomic.
/// </summary>
/// <remarks>
/// Both halves are written with the same <see cref="Int32"/>, so a value whose halves differ was read
/// between the two stores. Which half is written first does not matter, so byte order is irrelevant.
/// </remarks>
internal sealed class HalfWrittenInt64
{
	/// <summary>The value. It is the only field, so it sits on an 8-byte boundary.</summary>
	private Int64 value;

	/// <summary>Reads all 8 bytes atomically, at either word size.</summary>
	/// <returns>The value, whose halves may come from two different writes.</returns>
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
/// The thread-safety suite run against <see cref="NaiveAtomic{T}"/>. It is internal so the test runner
/// does not pick it up: every test in it is meant to fail, and <see cref="ThreadSafetySuiteTests"/> runs
/// them.
/// </summary>
internal sealed class NaiveThreadSafetyTests : ThreadSafetyTests
{
	protected override IAtomic<T> Create<T>(T value) => new NaiveAtomic<T>(value);
}
