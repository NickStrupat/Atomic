using AwesomeAssertions;
using NickStrupat;

namespace Tests;

/// <summary>
/// The behaviour every storage strategy has to agree on, one thread at a time. Each implementation
/// runs the whole suite through its own subclass, so a candidate cannot win on speed by being quietly
/// wrong.
/// </summary>
/// <remarks>
/// What the same implementations owe several threads at once is <see cref="ThreadSafetyTests"/>. The
/// split is worth keeping: a failure here is a plain bug and reproduces on the first run, where a
/// failure there is a race and may not.
/// </remarks>
public abstract class AtomicContractTests
{
	/// <summary>Creates a cell of the implementation under test.</summary>
	/// <typeparam name="T">The type of the value held by the cell.</typeparam>
	/// <param name="value">The initial value.</param>
	/// <returns>A new cell holding <paramref name="value"/>.</returns>
	protected abstract IAtomic<T> Create<T>(T value);

	[Fact]
	public void Value_WhenValueFitsInAWord_RoundTrips()
	{
		Create(42).Read().Should().Be(42);
		Create(Math.PI).Read().Should().Be(Math.PI);
		Create(Colour.Green).Read().Should().Be(Colour.Green);
		Create(new Eight(1, 2)).Read().Should().Be(new Eight(1, 2));
		Create(new DateTime(2026, 8, 19)).Read().Should().Be(new DateTime(2026, 8, 19));
	}

	[Fact]
	public void Value_WhenValueIsWiderThanAWordOrHoldsReferences_RoundTrips()
	{
		var guid = Guid.NewGuid();

		Create(new Twelve(1, 2, 3)).Read().Should().Be(new Twelve(1, 2, 3));
		Create(guid).Read().Should().Be(guid);
		Create(1.005m).Read().Should().Be(1.005m);
		Create(new WithReference("hello")).Read().Should().Be(new WithReference("hello"));
		Create((7, "x")).Read().Should().Be((7, "x"));
	}

	[Fact]
	public void Value_WhenNull_IsAValueLikeAnyOther()
	{
		var cell = Create<String?>(null);
		cell.Read().Should().BeNull();

		cell.Write("a");
		cell.Read().Should().Be("a");

		cell.Exchange(null).Should().Be("a");
		cell.Read().Should().BeNull();

		// Null is a legitimate comparand, not a stand-in for "no value".
		cell.TryCompareExchange("b", null, out var previous).Should().BeTrue();
		previous.Should().BeNull();
		cell.Read().Should().Be("b");

		cell.TryCompareExchange(null, "wrong", out previous).Should().BeFalse();
		previous.Should().Be("b");
		cell.Read().Should().Be("b");
	}

	[Fact]
	public void Value_WhenNullableValueType_RoundTripsThroughBothStates()
	{
		var cell = Create<Int32?>(null);
		cell.Read().Should().BeNull();

		cell.Write(7);
		cell.Read().Should().Be(7);

		cell.Exchange(null).Should().Be(7);
		cell.Read().Should().BeNull();

		cell.TryCompareExchange(9, null, out var previous).Should().BeTrue();
		previous.Should().BeNull();
		cell.Read().Should().Be(9);
	}

	[Fact]
	public void Value_WhenValueIsAnAwkwardSize_RoundTripsAndSwaps()
	{
		// Sizes no interlocked instruction matches. Whether a candidate widens them, boxes them, or locks
		// them, they have to behave like everything else.
		Create(new Three(1, 2, 3)).Read().Should().Be(new Three(1, 2, 3));
		Create(new Five(1, 2, 3, 4, 5)).Read().Should().Be(new Five(1, 2, 3, 4, 5));
		Create(new Six(1, 2, 3, 4, 5, 6)).Read().Should().Be(new Six(1, 2, 3, 4, 5, 6));

		var cell = Create(new Seven(1, 2, 3, 4, 5, 6, 7));
		cell.CompareExchange(new Seven(8, 9, 10, 11, 12, 13, 14), new Seven(0, 0, 0, 0, 0, 0, 0))
			.Should().Be(new Seven(1, 2, 3, 4, 5, 6, 7));
		cell.Read().Should().Be(new Seven(1, 2, 3, 4, 5, 6, 7));

		cell.CompareExchange(new Seven(8, 9, 10, 11, 12, 13, 14), new Seven(1, 2, 3, 4, 5, 6, 7))
			.Should().Be(new Seven(1, 2, 3, 4, 5, 6, 7));
		cell.Read().Should().Be(new Seven(8, 9, 10, 11, 12, 13, 14));

		cell.Exchange(new Seven(1, 1, 1, 1, 1, 1, 1)).Should().Be(new Seven(8, 9, 10, 11, 12, 13, 14));
		cell.Read().Should().Be(new Seven(1, 1, 1, 1, 1, 1, 1));
	}

	[Fact]
	public void Value_WhenValueIsAReference_RoundTripsTheSameInstance()
	{
		var list = new List<Int32> { 1, 2, 3 };

		Create(list).Read().Should().BeSameAs(list);
	}

	[Fact]
	public void Value_WhenSet_HoldsTheNewValue()
	{
		var inline = Create(1);
		inline.Write(2);
		inline.Read().Should().Be(2);

		var wide = Create(1m);
		wide.Write(2m);
		wide.Read().Should().Be(2m);

		var reference = Create("a");
		reference.Write("b");
		reference.Read().Should().Be("b");
	}

	[Fact]
	public void Exchange_WhenCalled_StoresTheNewValueAndReturnsTheOld()
	{
		var inline = Create(1);
		inline.Exchange(2).Should().Be(1);
		inline.Read().Should().Be(2);

		var wide = Create(1m);
		wide.Exchange(2m).Should().Be(1m);
		wide.Read().Should().Be(2m);

		var reference = Create("a");
		reference.Exchange("b").Should().Be("a");
		reference.Read().Should().Be("b");
	}

	[Fact]
	public void CompareExchange_WhenTheComparandMatches_StoresTheNewValue()
	{
		var inline = Create(1);
		inline.CompareExchange(2, 1).Should().Be(1);
		inline.Read().Should().Be(2);

		var wide = Create(1m);
		wide.CompareExchange(2m, 1m).Should().Be(1m);
		wide.Read().Should().Be(2m);

		var reference = Create("a");
		reference.CompareExchange("b", "a").Should().Be("a");
		reference.Read().Should().Be("b");
	}

	[Fact]
	public void CompareExchange_WhenTheComparandDoesNotMatch_LeavesTheValueAlone()
	{
		var inline = Create(1);
		inline.CompareExchange(2, 99).Should().Be(1);
		inline.Read().Should().Be(1);

		var wide = Create(1m);
		wide.CompareExchange(2m, 99m).Should().Be(1m);
		wide.Read().Should().Be(1m);

		var reference = Create("a");
		reference.CompareExchange("b", "z").Should().Be("a");
		reference.Read().Should().Be("a");
	}

	[Fact]
	public void CompareExchange_WhenValueFitsInAWord_ComparesBitsRatherThanValues()
	{
		// A NaN matches a NaN of the same bit pattern, even though the two are never ==.
		var nan = Create(Double.NaN);
		nan.CompareExchange(1.0, Double.NaN).Should().Be(Double.NaN);
		nan.Read().Should().Be(1.0);

		// Positive and negative zero are ==, but their bits differ, so they do not match.
		var zero = Create(0.0);
		zero.CompareExchange(1.0, -0.0).Should().Be(0.0);
		zero.Read().Should().Be(0.0);
	}

	[Fact]
	public void CompareExchange_WhenValueIsAReference_ComparesIdentityRatherThanEquality()
	{
		var original = "abc";
		var equalButDistinct = new String(['a', 'b', 'c']);
		equalButDistinct.Should().Be(original).And.NotBeSameAs(original);

		var atomic = Create(original);
		atomic.CompareExchange("z", equalButDistinct).Should().BeSameAs(original);
		atomic.Read().Should().BeSameAs(original);

		atomic.CompareExchange("z", original).Should().BeSameAs(original);
		atomic.Read().Should().Be("z");
	}

	[Fact]
	public void CompareExchange_WhenValueIsWiderThanAWord_ComparesValuesRatherThanBits()
	{
		// The two are equal but do not share a bit pattern, so only a value comparison matches.
		Decimal.GetBits(1.0m).Should().NotEqual(Decimal.GetBits(1.00m));

		var atomic = Create(1.0m);
		atomic.CompareExchange(2m, 1.00m).Should().Be(1.0m);
		atomic.Read().Should().Be(2m);
	}

	[Fact]
	public void TryCompareExchange_WhenTheComparandMatches_StoresAndReportsTrue()
	{
		var inline = Create(1);
		inline.TryCompareExchange(2, 1, out var fromInline).Should().BeTrue();
		fromInline.Should().Be(1);
		inline.Read().Should().Be(2);

		var wide = Create(1m);
		wide.TryCompareExchange(2m, 1m, out var fromWide).Should().BeTrue();
		fromWide.Should().Be(1m);
		wide.Read().Should().Be(2m);

		var reference = Create("a");
		reference.TryCompareExchange("b", "a", out var fromReference).Should().BeTrue();
		fromReference.Should().Be("a");
		reference.Read().Should().Be("b");
	}

	[Fact]
	public void TryCompareExchange_WhenTheComparandDoesNotMatch_ReportsFalseAndLeavesTheValueAlone()
	{
		var inline = Create(1);
		inline.TryCompareExchange(2, 99, out var fromInline).Should().BeFalse();
		fromInline.Should().Be(1);
		inline.Read().Should().Be(1);

		var wide = Create(1m);
		wide.TryCompareExchange(2m, 99m, out var fromWide).Should().BeFalse();
		fromWide.Should().Be(1m);
		wide.Read().Should().Be(1m);
	}

	[Fact]
	public void TryCompareExchange_WhenBitsDisagreeWithEquality_ReportsWhatActuallyHappened()
	{
		// -0.0 and 0.0 are equal under Equals but differ in their bits, so a cell comparing bits does not
		// store. Saying so is the whole point of this method: a caller judging success by comparing the
		// value it got back would read this as a success and drop the update.
		(-0.0).Equals(0.0).Should().BeTrue();

		var zero = Create(0.0);
		zero.TryCompareExchange(1.0, -0.0, out var previous).Should().BeFalse();
		previous.Should().Be(0.0);
		zero.Read().Should().Be(0.0);
	}
}

/// <summary>
/// The shipping cell, held to the same contract as the candidates through
/// <see cref="AtomicAdapter{T}"/>. It does not implement <see cref="IAtomic{T}"/> itself, because that
/// interface is a convenience for these tests rather than part of the package.
/// </summary>
public sealed class AtomicTests : AtomicContractTests
{
	protected override IAtomic<T> Create<T>(T value) => new AtomicAdapter<T>(value);
}

public sealed class BoxAtomicTests : AtomicContractTests
{
	protected override IAtomic<T> Create<T>(T value) => new BoxAtomic<T>(value);
}

public sealed class SeqLockAtomicTests : AtomicContractTests
{
	protected override IAtomic<T> Create<T>(T value) => new SeqLockAtomic<T>(value);
}
