using System.Numerics;
using NickStrupat;

namespace Tests;

/// <summary>A 1-byte enum: no references and narrower than a word, so it is swapped in place.</summary>
public enum Colour : Byte { Red, Green, Blue }

/// <summary>4-byte flags (backed by the default <see cref="Int32"/>), which have an instruction.</summary>
[Flags]
public enum Access { None = 0, Read = 1, Write = 2, Execute = 4 }

/// <summary>8-byte flags, the other width with an interlocked instruction.</summary>
[Flags]
public enum WideAccess : UInt64 { None = 0, Read = 1, Write = 2, Reserved = 1UL << 40 }

/// <summary>2-byte flags, narrower than any interlocked and/or instruction.</summary>
[Flags]
public enum MidAccess : UInt16 { None = 0, Read = 1, Write = 2, Execute = 4, High = 1 << 15 }

/// <summary>1-byte flags, the narrowest an enum can be.</summary>
[Flags]
public enum NarrowAccess : Byte { None = 0, Read = 1, Write = 2, Execute = 4 }

/// <summary>
/// 8 bytes with no references: a whole word at 64 bits, so swapped in place there. Made of two
/// <see cref="Int32"/>s, so only 4-byte aligned.
/// </summary>
public readonly record struct Eight(Int32 A, Int32 B);

/// <summary>
/// 12 bytes with no references: wider than a word, so <see cref="Atomic{T}"/> puts it behind its lock.
/// </summary>
public readonly record struct Twelve(Int32 A, Int32 B, Int32 C);

/// <summary>
/// One word wide but holding a reference, so it is not swapped in place. Size alone does not decide the
/// strategy.
/// </summary>
public readonly record struct WithReference(String Text);

/// <summary>3 bytes: no interlocked instruction has this width, so the cell must widen it to a word.</summary>
public readonly record struct Three(Byte A, Byte B, Byte C);

/// <summary>5 bytes, an odd size for the same reason as <see cref="Three"/>.</summary>
public readonly record struct Five(Byte A, Byte B, Byte C, Byte D, Byte E);

/// <summary>6 bytes, an odd size for the same reason as <see cref="Three"/>.</summary>
public readonly record struct Six(Byte A, Byte B, Byte C, Byte D, Byte E, Byte F);

/// <summary>7 bytes, an odd size for the same reason as <see cref="Three"/>.</summary>
public readonly record struct Seven(Byte A, Byte B, Byte C, Byte D, Byte E, Byte F, Byte G);

/// <summary>
/// A wide value with no references whose <see cref="Equals(Reentrant)"/> reads the cell holding it.
/// </summary>
/// <remarks>
/// Nothing stops a caller writing a type like this. If <see cref="SeqLockAtomic{T}"/> called
/// <see cref="Equals(Reentrant)"/> while holding its counter odd, the read inside would wait for the
/// counter forever, because the counter is not reentrant. This type proves the comparison runs outside
/// the counter.
/// </remarks>
public struct Reentrant : IEquatable<Reentrant>
{
	public Int64 A;
	public Int64 B;
	public Int64 C;

	/// <summary>A field <see cref="Equals(Reentrant)"/> ignores.</summary>
	/// <remarks>
	/// It lets a comparand differ from the held value in its bits while still comparing equal. A cell
	/// compares bits before asking the type, so a comparand with identical bits would never reach
	/// <see cref="Equals(Reentrant)"/>, and a test about where that call runs would prove nothing.
	/// </remarks>
	public Int64 Ignored;

	/// <summary>The cell <see cref="Equals(Reentrant)"/> reads from, or null to read nothing.</summary>
	public static SeqLockAtomic<Reentrant>? Cell;

	/// <summary>How many times <see cref="Equals(Reentrant)"/> has read <see cref="Cell"/>.</summary>
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
/// A struct holding a reference: a number and a string that must always be written together. A torn
/// write is visible without inspecting bits, because the string read back does not match the number.
/// </summary>
public readonly record struct Tagged(Int32 Number, String Text);

/// <summary>
/// 8 bytes, 3 of them padding, so two equal values can have different bit patterns.
/// </summary>
/// <remarks>
/// Nothing guarantees padding is zero. C# zero-initialises locals, so a value built the normal way has
/// zeroes there, but values from <c>[SkipLocalsInit]</c> code, interop, or a reinterpreted buffer may not.
/// A cell that only compared whole words would call two such values different and never match either.
/// </remarks>
public struct Padded : IEquatable<Padded>
{
	/// <summary>A byte, followed by 3 bytes of padding to align <see cref="B"/>.</summary>
	public Byte A;

	/// <summary>A 4-byte-aligned integer, which is what forces the padding.</summary>
	public Int32 B;

	/// <inheritdoc />
	public Boolean Equals(Padded other) => A == other.A && B == other.B;

	/// <inheritdoc />
	public override Boolean Equals(Object? obj) => obj is Padded other && Equals(other);

	/// <inheritdoc />
	public override Int32 GetHashCode() => HashCode.Combine(A, B);
}

/// <summary>
/// <see cref="Tolerance"/> with a third field, which makes it wider than a word and moves it to the lock
/// strategy without changing its equality.
/// </summary>
/// <remarks>
/// The pair checks that both strategies give the same answer. A type does not change how it compares by
/// gaining a field, and a caller cannot see which strategy a cell uses.
/// </remarks>
public readonly struct WideTolerance(Int32 value, Int32 ignored, Int32 alsoIgnored) : IEquatable<WideTolerance>
{
	/// <summary>The only field equality reads.</summary>
	public Int32 Value { get; } = value;

	/// <summary>A field equality ignores.</summary>
	public Int32 Ignored { get; } = ignored;

	/// <summary>Another field equality ignores.</summary>
	public Int32 AlsoIgnored { get; } = alsoIgnored;

	/// <inheritdoc />
	public Boolean Equals(WideTolerance other) => Value == other.Value;

	/// <inheritdoc />
	public override Boolean Equals(Object? obj) => obj is WideTolerance other && Equals(other);

	/// <inheritdoc />
	public override Int32 GetHashCode() => Value.GetHashCode();
}

/// <summary>
/// 8 bytes with no references, whose <see cref="Equals(Tolerance)"/> ignores half of them.
/// </summary>
/// <remarks>
/// A value's size says nothing about whether its equality matches its bits. This type deliberately
/// disagrees, so a cell that only compares bits fails in a way a caller would notice: a swap that should
/// have happened does not.
/// </remarks>
public readonly struct Tolerance(Int32 value, Int32 ignored) : IEquatable<Tolerance>
{
	/// <summary>The half equality reads.</summary>
	public Int32 Value { get; } = value;

	/// <summary>The half equality ignores.</summary>
	public Int32 Ignored { get; } = ignored;

	/// <inheritdoc />
	public Boolean Equals(Tolerance other) => Value == other.Value;

	/// <inheritdoc />
	public override Boolean Equals(Object? obj) => obj is Tolerance other && Equals(other);

	/// <inheritdoc />
	public override Int32 GetHashCode() => Value.GetHashCode();
}

/// <summary>
/// 8 bytes whose <see cref="Equals(Unreflexive)"/> can call a value unequal to itself.
/// </summary>
/// <remarks>
/// <see cref="Object.Equals(Object)"/> is supposed to be reflexive. This type breaks that the way real
/// code does by accident: it compares a <see cref="Double"/> with <c>==</c>, which is false for
/// <see cref="Double.NaN"/>. Together with <see cref="WideUnreflexive"/> it checks, like
/// <see cref="Tolerance"/> and <see cref="WideTolerance"/>, that both storage strategies give the same
/// answer — here in the one case where the right answer is not obvious.
/// </remarks>
public readonly struct Unreflexive(Double value) : IEquatable<Unreflexive>
{
	/// <summary>The field equality reads with <c>==</c>, which is why it is not reflexive.</summary>
	public Double Value { get; } = value;

	/// <inheritdoc />
	public Boolean Equals(Unreflexive other) => Value == other.Value;

	/// <inheritdoc />
	public override Boolean Equals(Object? obj) => obj is Unreflexive other && Equals(other);

	/// <inheritdoc />
	public override Int32 GetHashCode() => Value.GetHashCode();
}

/// <summary>
/// <see cref="Unreflexive"/> with two more fields, which makes it wider than a word and moves it to the
/// lock strategy without changing its equality.
/// </summary>
public readonly struct WideUnreflexive(Double value)
	: IEquatable<WideUnreflexive>, IAdditionOperators<WideUnreflexive, WideUnreflexive, WideUnreflexive>
{
	/// <summary>The field equality reads with <c>==</c>, which is why it is not reflexive.</summary>
	public Double Value { get; } = value;

	/// <summary>A field that only exists to make the type wider than a word.</summary>
	public Double Second { get; } = value;

	/// <summary>Another field that only exists to make the type wider than a word.</summary>
	public Double Third { get; } = value;

	/// <inheritdoc />
	public Boolean Equals(WideUnreflexive other) => Value == other.Value;

	/// <inheritdoc />
	public override Boolean Equals(Object? obj) => obj is WideUnreflexive other && Equals(other);

	/// <inheritdoc />
	public override Int32 GetHashCode() => Value.GetHashCode();

	/// <summary>Adds two values, so the read-modify-write operations can be used with this type.</summary>
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
