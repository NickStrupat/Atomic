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
/// The suite bound to <see cref="NaiveAtomic{T}"/>. Internal so the test runner does not collect it —
/// every one of these is meant to fail, and <see cref="ThreadSafetySuiteTests"/> is what runs them.
/// </summary>
internal sealed class NaiveThreadSafetyTests : ThreadSafetyTests
{
	protected override IAtomic<T> Create<T>(T value) => new NaiveAtomic<T>(value);
}
