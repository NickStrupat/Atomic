using System.Numerics;
using NickStrupat;

namespace Tests;

/// <summary>Unmanaged and narrower than a word, so it is stored inline.</summary>
public enum Colour : Byte { Red, Green, Blue }

/// <summary>Flags backed by the default <see cref="Int32"/>, which an interlocked instruction covers.</summary>
[Flags]
public enum Access { None = 0, Read = 1, Write = 2, Execute = 4 }

/// <summary>Flags 8 bytes wide, the other width an instruction covers.</summary>
[Flags]
public enum WideAccess : UInt64 { None = 0, Read = 1, Write = 2, Reserved = 1UL << 40 }

/// <summary>Flags 2 bytes wide, narrower than any interlocked instruction.</summary>
[Flags]
public enum MidAccess : UInt16 { None = 0, Read = 1, Write = 2, Execute = 4, High = 1 << 15 }

/// <summary>Flags 1 byte wide, the narrowest an enum can be.</summary>
[Flags]
public enum NarrowAccess : Byte { None = 0, Read = 1, Write = 2, Execute = 4 }

/// <summary>Exactly one word wide and holding no references, so it is stored inline.</summary>
public readonly record struct Eight(Int32 A, Int32 B);

/// <summary>Wider than a word, so it is boxed.</summary>
public readonly record struct Twelve(Int32 A, Int32 B, Int32 C);

/// <summary>
/// One word wide, but holding a reference, so it is boxed rather than stored inline. Size alone is
/// not enough to decide the strategy.
/// </summary>
public readonly record struct WithReference(String Text);

/// <summary>Three bytes: no interlocked instruction is that wide, so it has to be widened to one.</summary>
public readonly record struct Three(Byte A, Byte B, Byte C);

/// <summary>Five bytes, for the same reason as <see cref="Three"/>.</summary>
public readonly record struct Five(Byte A, Byte B, Byte C, Byte D, Byte E);

/// <summary>Six bytes, for the same reason as <see cref="Three"/>.</summary>
public readonly record struct Six(Byte A, Byte B, Byte C, Byte D, Byte E, Byte F);

/// <summary>Seven bytes, for the same reason as <see cref="Three"/>.</summary>
public readonly record struct Seven(Byte A, Byte B, Byte C, Byte D, Byte E, Byte F, Byte G);

/// <summary>
/// Wide, unmanaged, and with an <see cref="Equals(Reentrant)"/> that reads the very cell holding it.
/// </summary>
/// <remarks>
/// Nothing stops a caller from writing a type like this, and a comparison invoked while the seqlock
/// counter is held odd would never return: the counter is not reentrant, so the thread would wait on
/// itself. This exists to prove the comparison happens outside the counter.
/// </remarks>
public struct Reentrant : IEquatable<Reentrant>
{
	public Int64 A;
	public Int64 B;
	public Int64 C;

	/// <summary>A field <see cref="Equals(Reentrant)"/> does not read.</summary>
	/// <remarks>
	/// Its only job is to let a comparand differ from the cell in bits while still comparing equal. A
	/// cell tries bits before it asks the type, so a comparand identical to what is held never reaches
	/// <see cref="Equals(Reentrant)"/> at all — and a test about where that comparison runs would prove
	/// nothing.
	/// </remarks>
	public Int64 Ignored;

	/// <summary>The cell to read from inside <see cref="Equals(Reentrant)"/>, if any.</summary>
	public static SeqLockAtomic<Reentrant>? Cell;

	/// <summary>How many times a comparison has read <see cref="Cell"/>.</summary>
	public static Int32 Reads;

	/// <inheritdoc />
	public Boolean Equals(Reentrant other)
	{
		if (Cell is not null)
		{
			Interlocked.Increment(ref Reads);
			_ = Cell.Read();
		}
		return A == other.A && B == other.B && C == other.C;
	}

	/// <inheritdoc />
	public override Boolean Equals(Object? obj) => obj is Reentrant other && Equals(other);

	/// <inheritdoc />
	public override Int32 GetHashCode() => HashCode.Combine(A, B, C);
}

/// <summary>
/// A managed struct: an index and the instance it names, which have to be written together or not at
/// all. Tearing between the two fields is visible without any bit inspection, because the reference
/// read back simply is not the one the index points at.
/// </summary>
public readonly record struct Tagged(Int32 Number, String Text);

/// <summary>
/// Eight bytes with three of them padding, so two equal values need not share a bit pattern.
/// </summary>
/// <remarks>
/// The padding belongs to nobody and nothing promises it is zero. C# zero-initialises locals, so a
/// value built the ordinary way carries zeroes there, but <c>[SkipLocalsInit]</c>, interop, and any
/// value reinterpreted out of a buffer do not. A cell comparing the whole word would read two such
/// values as different and never match either of them.
/// </remarks>
public struct Padded : IEquatable<Padded>
{
	/// <summary>A byte, which the layout follows with padding to seat <see cref="B"/>.</summary>
	public Byte A;

	/// <summary>A word-aligned integer, which is what forces the padding.</summary>
	public Int32 B;

	/// <inheritdoc />
	public Boolean Equals(Padded other) => A == other.A && B == other.B;

	/// <inheritdoc />
	public override Boolean Equals(Object? obj) => obj is Padded other && Equals(other);

	/// <inheritdoc />
	public override Int32 GetHashCode() => HashCode.Combine(A, B);
}

/// <summary>
/// <see cref="Tolerance"/> with a third field, putting it past the word and onto the other storage
/// strategy without changing what its equality means.
/// </summary>
/// <remarks>
/// The pair exists to hold the two strategies to the same answer. A type does not change how it
/// compares by gaining a field, and a caller cannot see which strategy a cell got.
/// </remarks>
public readonly struct WideTolerance(Int32 value, Int32 ignored, Int32 alsoIgnored) : IEquatable<WideTolerance>
{
	/// <summary>The field that counts.</summary>
	public Int32 Value { get; } = value;

	/// <summary>A field that does not.</summary>
	public Int32 Ignored { get; } = ignored;

	/// <summary>Another field that does not.</summary>
	public Int32 AlsoIgnored { get; } = alsoIgnored;

	/// <inheritdoc />
	public Boolean Equals(WideTolerance other) => Value == other.Value;

	/// <inheritdoc />
	public override Boolean Equals(Object? obj) => obj is WideTolerance other && Equals(other);

	/// <inheritdoc />
	public override Int32 GetHashCode() => Value.GetHashCode();
}

/// <summary>
/// Eight bytes, unmanaged, and with an <see cref="Equals(Tolerance)"/> that ignores half of them.
/// </summary>
/// <remarks>
/// Nothing about the size of a value says its own equality agrees with its bits. This is the smallest
/// type that disagrees on purpose rather than by accident, so a cell that compares bits gets it wrong
/// in the direction a caller notices: the swap that should have landed does not.
/// </remarks>
public readonly struct Tolerance(Int32 value, Int32 ignored) : IEquatable<Tolerance>
{
	/// <summary>The half that counts.</summary>
	public Int32 Value { get; } = value;

	/// <summary>The half that does not.</summary>
	public Int32 Ignored { get; } = ignored;

	/// <inheritdoc />
	public Boolean Equals(Tolerance other) => Value == other.Value;

	/// <inheritdoc />
	public override Boolean Equals(Object? obj) => obj is Tolerance other && Equals(other);

	/// <inheritdoc />
	public override Int32 GetHashCode() => Value.GetHashCode();
}

/// <summary>
/// 8 bytes whose <see cref="Equals(Unreflexive)"/> calls a value unequal to itself.
/// </summary>
/// <remarks>
/// <see cref="Object.Equals(Object)"/> is required to be reflexive and this breaks that, but it breaks
/// it the way a caller actually would rather than on purpose: comparing a <see cref="Double"/> with
/// <c>==</c>, which is false for <see cref="Double.NaN"/>. The pair exists for the same reason
/// <see cref="Tolerance"/> and <see cref="WideTolerance"/> do — to hold the two storage strategies to
/// one answer — in the one case where the answer they owe is not obvious.
/// </remarks>
public readonly struct Unreflexive(Double value) : IEquatable<Unreflexive>
{
	/// <summary>The field equality reads, and the reason it is not reflexive.</summary>
	public Double Value { get; } = value;

	/// <inheritdoc />
	public Boolean Equals(Unreflexive other) => Value == other.Value;

	/// <inheritdoc />
	public override Boolean Equals(Object? obj) => obj is Unreflexive other && Equals(other);

	/// <inheritdoc />
	public override Int32 GetHashCode() => Value.GetHashCode();
}

/// <summary>
/// <see cref="Unreflexive"/> with two more fields, putting it past the word and onto the monitor
/// without changing what its equality means.
/// </summary>
public readonly struct WideUnreflexive(Double value)
	: IEquatable<WideUnreflexive>, IAdditionOperators<WideUnreflexive, WideUnreflexive, WideUnreflexive>
{
	/// <summary>The field equality reads, and the reason it is not reflexive.</summary>
	public Double Value { get; } = value;

	/// <summary>A field that only exists to put the type past the word.</summary>
	public Double Second { get; } = value;

	/// <summary>Another one.</summary>
	public Double Third { get; } = value;

	/// <inheritdoc />
	public Boolean Equals(WideUnreflexive other) => Value == other.Value;

	/// <inheritdoc />
	public override Boolean Equals(Object? obj) => obj is WideUnreflexive other && Equals(other);

	/// <inheritdoc />
	public override Int32 GetHashCode() => Value.GetHashCode();

	/// <summary>Adds two values, so that a read-modify-write loop can be pointed at this type.</summary>
	/// <param name="left">The left operand.</param>
	/// <param name="right">The right operand.</param>
	/// <returns>A value holding the sum.</returns>
	public static WideUnreflexive operator +(WideUnreflexive left, WideUnreflexive right) =>
		new(left.Value + right.Value);
}

/// <summary>
/// A plain struct with no <see cref="Equals(Object)"/> override and no <see cref="IEquatable{T}"/>, so
/// <see cref="EqualityComparer{T}.Default"/> falls back to <c>ObjectEqualityComparer&lt;T&gt;</c>, which
/// boxes both sides to reach <see cref="ValueType.Equals(Object)"/>.
/// </summary>
public struct NoEquatable
{
	/// <summary>The only field, and the reason two values compare unequal.</summary>
	public Int32 Value;
}

/// <summary>
/// <see cref="NoEquatable"/> past the word, for the same reason <see cref="WideTolerance"/> exists
/// beside <see cref="Tolerance"/>: the boxing this pair exists to catch happens at either width.
/// </summary>
public struct WideNoEquatable
{
	/// <summary>The first word. <see cref="ValueType.Equals(Object)"/> compares all three.</summary>
	public Int64 A;

	/// <summary>The second word, compared like the first: nothing here is ignored by equality.</summary>
	public Int64 B;

	/// <summary>The third word, which pushes the type past the word and onto the monitor.</summary>
	public Int64 C;
}
