using System.Runtime.CompilerServices;
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
	public void CompareExchange_WhenValueFitsInAWord_ComparesValuesRatherThanBits()
	{
		// A NaN matches a NaN, which the operator would not: the cell compares with Equals, and Equals
		// is reflexive where == is not.
		var nan = Create(Double.NaN);
		nan.CompareExchange(1.0, Double.NaN).Should().Be(Double.NaN);
		nan.Read().Should().Be(1.0);

		// Positive and negative zero do not share a bit pattern, and are equal regardless.
		var zero = Create(0.0);
		zero.CompareExchange(1.0, -0.0).Should().Be(0.0);
		zero.Read().Should().Be(1.0);
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
	public void TryCompareExchange_WhenTheOperatorDisagreesWithEquality_ReportsWhatActuallyHappened()
	{
		// The cell compares with Equals, and Equals says a NaN is a NaN, so this stores. Saying so is the
		// whole point of this method: a caller judging success by comparing the value it got back reads
		// the swap it just made as a failure, retries, and stores twice.
		(Double.NaN == Double.NaN).Should().BeFalse();
		Double.NaN.Equals(Double.NaN).Should().BeTrue();

		var nan = Create(Double.NaN);
		nan.TryCompareExchange(1.0, Double.NaN, out var previous).Should().BeTrue();
		(previous == Double.NaN).Should().BeFalse();
		nan.Read().Should().Be(1.0);
	}

	[Fact]
	public void CompareExchange_WhenTwoValuesDifferOnlyInWidth_ComparesThemTheSameWay()
	{
		// Tolerance and WideTolerance mean the same thing by equality and fall on either side of the word
		// boundary that picks the storage strategy. Which strategy a cell got is not the caller's
		// business and must not show through here. Both comparands below name a value the cell holds,
		// and differ from it in the field neither type reads.
		var narrow = Create(new Tolerance(1, 100));
		narrow.TryCompareExchange(new Tolerance(2, 0), new Tolerance(1, 999), out _).Should().BeTrue();
		narrow.Read().Value.Should().Be(2);

		var wide = Create(new WideTolerance(1, 100, 100));
		wide.TryCompareExchange(new WideTolerance(2, 0, 0), new WideTolerance(1, 999, 999), out _).Should().BeTrue();
		wide.Read().Value.Should().Be(2);

		// And they refuse the same comparand too.
		narrow.TryCompareExchange(new Tolerance(3, 0), new Tolerance(9, 0), out _).Should().BeFalse();
		wide.TryCompareExchange(new WideTolerance(3, 0, 0), new WideTolerance(9, 0, 0), out _).Should().BeFalse();
	}

	[Fact]
	public void CompareExchange_WhenTheValueHasPadding_ComparesTheFieldsAndNotThePadding()
	{
		var clean = new Padded { A = 7, B = 9 };
		var dirty = PaddedWithGarbageInThePadding(7, 9);

		// Without these the rest proves nothing: it would be comparing a value against itself.
		clean.Equals(dirty).Should().BeTrue();
		WordOf(clean).Should().NotBe(WordOf(dirty));

		var cell = Create(clean);
		cell.TryCompareExchange(new Padded { A = 1, B = 1 }, dirty, out var previous).Should().BeTrue();
		previous.Should().Be(clean);
		cell.Read().Should().Be(new Padded { A = 1, B = 1 });
	}

	[Fact]
	public void CompareExchange_WhenTheValueDefinesItsOwnEquality_AsksIt()
	{
		// Tolerance ignores its second field, so these two comparands are the same value and a cell
		// reading the whole word would refuse the first swap.
		var cell = Create(new Tolerance(1, 100));
		cell.TryCompareExchange(new Tolerance(2, 0), new Tolerance(1, 999), out var previous).Should().BeTrue();
		previous.Ignored.Should().Be(100);
		cell.Read().Value.Should().Be(2);

		// And the field it does read still decides.
		cell.TryCompareExchange(new Tolerance(3, 0), new Tolerance(9, 0), out _).Should().BeFalse();
		cell.Read().Value.Should().Be(2);
	}

	/// <summary>The 8 bytes of a value, padding included.</summary>
	/// <param name="value">The value to take the bits of.</param>
	/// <returns>The bit pattern, which two equal values need not share.</returns>
	private static Int64 WordOf(Padded value)
	{
		Int64 bits = 0;
		Unsafe.WriteUnaligned(ref Unsafe.As<Int64, Byte>(ref bits), value);
		return bits;
	}

	/// <summary>Builds a value whose fields are the ones asked for and whose padding is not zero.</summary>
	/// <param name="a">The value for <see cref="Padded.A"/>.</param>
	/// <param name="b">The value for <see cref="Padded.B"/>.</param>
	/// <returns>A value equal to one built the ordinary way, with a different bit pattern.</returns>
	/// <remarks>
	/// Reading the struct out of an all-ones word fills the padding, and writing the two fields
	/// afterwards leaves only the padding holding it. Kept out of line so the fields are stored to a
	/// stack slot rather than kept in registers, which would drop the padding on the way back.
	/// </remarks>
	[MethodImpl(MethodImplOptions.NoInlining)]
	private static Padded PaddedWithGarbageInThePadding(Byte a, Int32 b)
	{
		var bits = -1L;
		var value = Unsafe.ReadUnaligned<Padded>(ref Unsafe.As<Int64, Byte>(ref bits));
		value.A = a;
		value.B = b;
		return value;
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
