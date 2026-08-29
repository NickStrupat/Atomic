namespace NickStrupat;

/// <summary>
/// Read-modify-write operations for the value types the hardware has an instruction for.
/// </summary>
/// <remarks>
/// <para>
/// A cell of one of these types keeps the value in a field of exactly that type, so
/// <see cref="Interlocked"/> can act on it directly and none of these has to read, compute and exchange
/// the way <see cref="AtomicExtensions"/> must.
/// </para>
/// <para>
/// Both sets are declared against <see cref="Atomic{T}"/>, and these take precedence because a closed
/// type beats an open one. That precedence no longer decides anything: since
/// <see cref="AtomicExtensions"/> began testing <c>typeof(T)</c> and folding the result, reaching past
/// these to the open method arrives at the same instruction. What is left is that these say so in the
/// signature rather than leaving it to the JIT, and that they need no operator constraint.
/// </para>
/// <para>
/// A four byte instruction here acts on the same address as the cell's own eight byte view of its
/// field. That is what keeps the bytes past the value zero, which every comparison the cell makes
/// depends on: widening the instruction instead would let an overflow carry into them. It also means
/// two widths of atomic access to one address, which is well defined on x64 and arm64, where the
/// coherence guarantees cover overlapping accesses of different sizes, but is not something the .NET
/// memory model promises in the abstract.
/// </para>
/// </remarks>
public static class AtomicInterlockedExtensions
{
	/// <summary>Increments the value of an <see cref="Atomic{T}"/> of <see cref="Int32"/> with a single interlocked instruction.</summary>
	/// <param name="atomic">The cell to act on.</param>
	/// <returns>The new value, as <see cref="Interlocked"/> returns it.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="atomic"/> is null.</exception>
	public static Int32 Increment(this Atomic<Int32> atomic)
	{
		ArgumentNullException.ThrowIfNull(atomic);
		return Interlocked.Increment(ref atomic.Storage);
	}

	/// <summary>Decrements the value of an <see cref="Atomic{T}"/> of <see cref="Int32"/> with a single interlocked instruction.</summary>
	/// <param name="atomic">The cell to act on.</param>
	/// <returns>The new value, as <see cref="Interlocked"/> returns it.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="atomic"/> is null.</exception>
	public static Int32 Decrement(this Atomic<Int32> atomic)
	{
		ArgumentNullException.ThrowIfNull(atomic);
		return Interlocked.Decrement(ref atomic.Storage);
	}

	/// <summary>Adds <paramref name="addend"/> to the value of an <see cref="Atomic{T}"/> of <see cref="Int32"/> with a single interlocked instruction.</summary>
	/// <param name="atomic">The cell to act on.</param>
	/// <param name="addend">The value to add.</param>
	/// <returns>The new value, as <see cref="Interlocked"/> returns it.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="atomic"/> is null.</exception>
	public static Int32 Add(this Atomic<Int32> atomic, Int32 addend)
	{
		ArgumentNullException.ThrowIfNull(atomic);
		return Interlocked.Add(ref atomic.Storage, addend);
	}

	/// <summary>Replaces the value with its bitwise and against <paramref name="value"/> in the value of an <see cref="Atomic{T}"/> of <see cref="Int32"/> with a single interlocked instruction.</summary>
	/// <param name="atomic">The cell to act on.</param>
	/// <param name="value">The mask.</param>
	/// <returns>The old value, as <see cref="Interlocked"/> returns it.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="atomic"/> is null.</exception>
	public static Int32 And(this Atomic<Int32> atomic, Int32 value)
	{
		ArgumentNullException.ThrowIfNull(atomic);
		return Interlocked.And(ref atomic.Storage, value);
	}

	/// <summary>Replaces the value with its bitwise or against <paramref name="value"/> in the value of an <see cref="Atomic{T}"/> of <see cref="Int32"/> with a single interlocked instruction.</summary>
	/// <param name="atomic">The cell to act on.</param>
	/// <param name="value">The bits to set.</param>
	/// <returns>The old value, as <see cref="Interlocked"/> returns it.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="atomic"/> is null.</exception>
	public static Int32 Or(this Atomic<Int32> atomic, Int32 value)
	{
		ArgumentNullException.ThrowIfNull(atomic);
		return Interlocked.Or(ref atomic.Storage, value);
	}

	/// <summary>Increments the value of an <see cref="Atomic{T}"/> of <see cref="Int64"/> with a single interlocked instruction.</summary>
	/// <param name="atomic">The cell to act on.</param>
	/// <returns>The new value, as <see cref="Interlocked"/> returns it.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="atomic"/> is null.</exception>
	public static Int64 Increment(this Atomic<Int64> atomic)
	{
		ArgumentNullException.ThrowIfNull(atomic);
		return Interlocked.Increment(ref atomic.Storage);
	}

	/// <summary>Decrements the value of an <see cref="Atomic{T}"/> of <see cref="Int64"/> with a single interlocked instruction.</summary>
	/// <param name="atomic">The cell to act on.</param>
	/// <returns>The new value, as <see cref="Interlocked"/> returns it.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="atomic"/> is null.</exception>
	public static Int64 Decrement(this Atomic<Int64> atomic)
	{
		ArgumentNullException.ThrowIfNull(atomic);
		return Interlocked.Decrement(ref atomic.Storage);
	}

	/// <summary>Adds <paramref name="addend"/> to the value of an <see cref="Atomic{T}"/> of <see cref="Int64"/> with a single interlocked instruction.</summary>
	/// <param name="atomic">The cell to act on.</param>
	/// <param name="addend">The value to add.</param>
	/// <returns>The new value, as <see cref="Interlocked"/> returns it.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="atomic"/> is null.</exception>
	public static Int64 Add(this Atomic<Int64> atomic, Int64 addend)
	{
		ArgumentNullException.ThrowIfNull(atomic);
		return Interlocked.Add(ref atomic.Storage, addend);
	}

	/// <summary>Replaces the value with its bitwise and against <paramref name="value"/> in the value of an <see cref="Atomic{T}"/> of <see cref="Int64"/> with a single interlocked instruction.</summary>
	/// <param name="atomic">The cell to act on.</param>
	/// <param name="value">The mask.</param>
	/// <returns>The old value, as <see cref="Interlocked"/> returns it.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="atomic"/> is null.</exception>
	public static Int64 And(this Atomic<Int64> atomic, Int64 value)
	{
		ArgumentNullException.ThrowIfNull(atomic);
		return Interlocked.And(ref atomic.Storage, value);
	}

	/// <summary>Replaces the value with its bitwise or against <paramref name="value"/> in the value of an <see cref="Atomic{T}"/> of <see cref="Int64"/> with a single interlocked instruction.</summary>
	/// <param name="atomic">The cell to act on.</param>
	/// <param name="value">The bits to set.</param>
	/// <returns>The old value, as <see cref="Interlocked"/> returns it.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="atomic"/> is null.</exception>
	public static Int64 Or(this Atomic<Int64> atomic, Int64 value)
	{
		ArgumentNullException.ThrowIfNull(atomic);
		return Interlocked.Or(ref atomic.Storage, value);
	}

	/// <summary>Increments the value of an <see cref="Atomic{T}"/> of <see cref="UInt32"/> with a single interlocked instruction.</summary>
	/// <param name="atomic">The cell to act on.</param>
	/// <returns>The new value, as <see cref="Interlocked"/> returns it.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="atomic"/> is null.</exception>
	public static UInt32 Increment(this Atomic<UInt32> atomic)
	{
		ArgumentNullException.ThrowIfNull(atomic);
		return Interlocked.Increment(ref atomic.Storage);
	}

	/// <summary>Decrements the value of an <see cref="Atomic{T}"/> of <see cref="UInt32"/> with a single interlocked instruction.</summary>
	/// <param name="atomic">The cell to act on.</param>
	/// <returns>The new value, as <see cref="Interlocked"/> returns it.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="atomic"/> is null.</exception>
	public static UInt32 Decrement(this Atomic<UInt32> atomic)
	{
		ArgumentNullException.ThrowIfNull(atomic);
		return Interlocked.Decrement(ref atomic.Storage);
	}

	/// <summary>Adds <paramref name="addend"/> to the value of an <see cref="Atomic{T}"/> of <see cref="UInt32"/> with a single interlocked instruction.</summary>
	/// <param name="atomic">The cell to act on.</param>
	/// <param name="addend">The value to add.</param>
	/// <returns>The new value, as <see cref="Interlocked"/> returns it.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="atomic"/> is null.</exception>
	public static UInt32 Add(this Atomic<UInt32> atomic, UInt32 addend)
	{
		ArgumentNullException.ThrowIfNull(atomic);
		return Interlocked.Add(ref atomic.Storage, addend);
	}

	/// <summary>Replaces the value with its bitwise and against <paramref name="value"/> in the value of an <see cref="Atomic{T}"/> of <see cref="UInt32"/> with a single interlocked instruction.</summary>
	/// <param name="atomic">The cell to act on.</param>
	/// <param name="value">The mask.</param>
	/// <returns>The old value, as <see cref="Interlocked"/> returns it.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="atomic"/> is null.</exception>
	public static UInt32 And(this Atomic<UInt32> atomic, UInt32 value)
	{
		ArgumentNullException.ThrowIfNull(atomic);
		return Interlocked.And(ref atomic.Storage, value);
	}

	/// <summary>Replaces the value with its bitwise or against <paramref name="value"/> in the value of an <see cref="Atomic{T}"/> of <see cref="UInt32"/> with a single interlocked instruction.</summary>
	/// <param name="atomic">The cell to act on.</param>
	/// <param name="value">The bits to set.</param>
	/// <returns>The old value, as <see cref="Interlocked"/> returns it.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="atomic"/> is null.</exception>
	public static UInt32 Or(this Atomic<UInt32> atomic, UInt32 value)
	{
		ArgumentNullException.ThrowIfNull(atomic);
		return Interlocked.Or(ref atomic.Storage, value);
	}

	/// <summary>Increments the value of an <see cref="Atomic{T}"/> of <see cref="UInt64"/> with a single interlocked instruction.</summary>
	/// <param name="atomic">The cell to act on.</param>
	/// <returns>The new value, as <see cref="Interlocked"/> returns it.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="atomic"/> is null.</exception>
	public static UInt64 Increment(this Atomic<UInt64> atomic)
	{
		ArgumentNullException.ThrowIfNull(atomic);
		return Interlocked.Increment(ref atomic.Storage);
	}

	/// <summary>Decrements the value of an <see cref="Atomic{T}"/> of <see cref="UInt64"/> with a single interlocked instruction.</summary>
	/// <param name="atomic">The cell to act on.</param>
	/// <returns>The new value, as <see cref="Interlocked"/> returns it.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="atomic"/> is null.</exception>
	public static UInt64 Decrement(this Atomic<UInt64> atomic)
	{
		ArgumentNullException.ThrowIfNull(atomic);
		return Interlocked.Decrement(ref atomic.Storage);
	}

	/// <summary>Adds <paramref name="addend"/> to the value of an <see cref="Atomic{T}"/> of <see cref="UInt64"/> with a single interlocked instruction.</summary>
	/// <param name="atomic">The cell to act on.</param>
	/// <param name="addend">The value to add.</param>
	/// <returns>The new value, as <see cref="Interlocked"/> returns it.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="atomic"/> is null.</exception>
	public static UInt64 Add(this Atomic<UInt64> atomic, UInt64 addend)
	{
		ArgumentNullException.ThrowIfNull(atomic);
		return Interlocked.Add(ref atomic.Storage, addend);
	}

	/// <summary>Replaces the value with its bitwise and against <paramref name="value"/> in the value of an <see cref="Atomic{T}"/> of <see cref="UInt64"/> with a single interlocked instruction.</summary>
	/// <param name="atomic">The cell to act on.</param>
	/// <param name="value">The mask.</param>
	/// <returns>The old value, as <see cref="Interlocked"/> returns it.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="atomic"/> is null.</exception>
	public static UInt64 And(this Atomic<UInt64> atomic, UInt64 value)
	{
		ArgumentNullException.ThrowIfNull(atomic);
		return Interlocked.And(ref atomic.Storage, value);
	}

	/// <summary>Replaces the value with its bitwise or against <paramref name="value"/> in the value of an <see cref="Atomic{T}"/> of <see cref="UInt64"/> with a single interlocked instruction.</summary>
	/// <param name="atomic">The cell to act on.</param>
	/// <param name="value">The bits to set.</param>
	/// <returns>The old value, as <see cref="Interlocked"/> returns it.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="atomic"/> is null.</exception>
	public static UInt64 Or(this Atomic<UInt64> atomic, UInt64 value)
	{
		ArgumentNullException.ThrowIfNull(atomic);
		return Interlocked.Or(ref atomic.Storage, value);
	}
}
