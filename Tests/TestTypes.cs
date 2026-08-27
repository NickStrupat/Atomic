using NickStrupat;

namespace Tests;

/// <summary>Unmanaged and narrower than a word, so it is stored inline.</summary>
public enum Colour : Byte { Red, Green, Blue }

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
