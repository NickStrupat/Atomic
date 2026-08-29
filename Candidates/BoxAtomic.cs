using System.Runtime.CompilerServices;

namespace NickStrupat;

/// <summary>
/// Keeps a machine word beside an object slot, whatever <typeparamref name="T"/> is: a value fitting
/// in the word goes in the word, a reference goes in the slot, and anything else is boxed and the box
/// goes in the slot.
/// </summary>
/// <typeparam name="T">The type of the value held by the cell.</typeparam>
/// <remarks>
/// The word is a declared <see cref="Int64"/>, so it is always aligned and always has room, which makes
/// this the one design where storage needs no assumptions about layout. The cost is that both fields
/// exist for every <typeparamref name="T"/>, and that a value too wide for the word allocates a box on
/// every write.
/// </remarks>
public sealed class BoxAtomic<T> : IAtomic<T>
{
	private Int64 unmanaged;
	private Object? managed;

	/// <summary>Initializes a new cell holding <paramref name="value"/>.</summary>
	/// <param name="value">The initial value.</param>
	public BoxAtomic(T value)
	{
		if (IsInline)
			unmanaged = ToBits(value);
		else
			managed = ToSlot(value);
	}

	/// <summary>Gets a value indicating whether the value is held in the word rather than the slot.</summary>
	public static Boolean IsInlineStorage => IsInline;

	private static Boolean IsInline
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		get => !RuntimeHelpers.IsReferenceOrContainsReferences<T>() && Unsafe.SizeOf<T>() <= sizeof(Int64);
	}

	private static Boolean IsReference
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		get => !typeof(T).IsValueType;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static Int64 ToBits(T value)
	{
		Int64 bits = 0;
		Unsafe.WriteUnaligned(ref Unsafe.As<Int64, Byte>(ref bits), value);
		return bits;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static T FromBits(Int64 bits) => Unsafe.ReadUnaligned<T>(ref Unsafe.As<Int64, Byte>(ref bits));

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static Object? ToSlot(T value) => IsReference ? (Object?)value : new Box(value);

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static T FromSlot(Object? slot) =>
		IsReference ? Unsafe.As<Object?, T>(ref slot) : ((Box)slot!).Value;

	/// <inheritdoc />
	public T Read() =>
		IsInline ? FromBits(Volatile.Read(ref unmanaged)) : FromSlot(Volatile.Read(ref managed));

	/// <inheritdoc />
	public void Write(T value)
	{
		if (IsInline)
			Volatile.Write(ref unmanaged, ToBits(value));
		else
			Volatile.Write(ref managed, ToSlot(value));
	}

	/// <inheritdoc />
	public T Exchange(T value) =>
		IsInline
			? FromBits(Interlocked.Exchange(ref unmanaged, ToBits(value)))
			: FromSlot(Interlocked.Exchange(ref managed, ToSlot(value)));

	/// <inheritdoc />
	public T CompareExchange(T value, T comparand)
	{
		TryCompareExchange(value, comparand, out var previous);
		return previous;
	}

	/// <inheritdoc />
	public Boolean TryCompareExchange(T value, T comparand, out T previous)
	{
		if (IsInline)
		{
			var comparandBits = ToBits(comparand);
			var previousBits = Interlocked.CompareExchange(ref unmanaged, ToBits(value), comparandBits);
			previous = FromBits(previousBits);
			return previousBits == comparandBits;
		}

		if (IsReference)
		{
			var comparandSlot = ToSlot(comparand);
			var previousSlot = Interlocked.CompareExchange(ref managed, ToSlot(value), comparandSlot);
			previous = FromSlot(previousSlot);
			return ReferenceEquals(previousSlot, comparandSlot);
		}

		// Built at most once, and only once the comparison has passed. The box is immutable and is
		// published only by an exchange that wins, so an attempt that loses leaves it unobserved and the
		// next attempt can offer the same one again. Building it before the loop instead would allocate
		// on the path that returns below without exchanging at all, which is the common one.
		Object? slot = null;
		while (true)
		{
			var currentSlot = Volatile.Read(ref managed);
			previous = FromSlot(currentSlot);
			if (!EqualityComparer<T>.Default.Equals(previous, comparand))
				return false;
			slot ??= ToSlot(value);
			if (ReferenceEquals(Interlocked.CompareExchange(ref managed, slot, currentSlot), currentSlot))
				return true;
		}
	}

	private sealed class Box(T value)
	{
		internal readonly T Value = value;
	}
}
