using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace NickStrupat;

/// <summary>
/// A candidate cell that stores the value in a field of type <typeparamref name="T"/> next to a version
/// counter, so a value too wide to swap atomically can still be read without taking a lock and written
/// without allocating.
/// </summary>
/// <typeparam name="T">The type of the value held by the cell.</typeparam>
/// <remarks>
/// <para>
/// How the value is stored depends on <typeparamref name="T"/>:
/// </para>
/// <list type="bullet">
/// <item><description>
/// A value with no references whose size and alignment match an interlocked instruction, or a reference,
/// is swapped in place as in <see cref="Atomic{T}"/>, and the counter goes unused. Unlike
/// <see cref="Atomic{T}"/>, this class cannot widen an odd-sized value to a word, because it has a
/// second field.
/// </description></item>
/// <item><description>
/// Any other value with no references — an odd size, an 8-byte value aligned to less than 8, or
/// anything wider — uses the counter as a seqlock. A writer makes the counter odd,
/// writes, then makes it even again. A reader reads the counter, copies the value, and reads the counter
/// again; if the counter was odd or changed, it retries.
/// </description></item>
/// <item><description>
/// A value type containing references uses a lock. A seqlock reader can copy a half-written value and
/// then throw it away, which is harmless for plain bytes but not for references.
/// </description></item>
/// </list>
/// <para>
/// The seqlock is still a lock. Readers do not exclude each other and do not queue behind a writer, but
/// while a writer holds the counter odd every other thread waits. A read takes effect between its two
/// readings of the counter, and a write at the moment it makes the counter odd, so operations are
/// atomic — but neither reads nor writes are lock-free.
/// </para>
/// <para>
/// The in-place paths are copied from <see cref="Atomic{T}"/> rather than shared, so that each candidate
/// is measured on its own and not through a call into common code.
/// </para>
/// </remarks>
public sealed class SeqLockAtomic<T> : IAtomic<T>
{
	private T storage;
	private Int64 version;

	/// <summary>Initializes a new cell holding <paramref name="value"/>.</summary>
	/// <param name="value">The initial value.</param>
	public SeqLockAtomic(T value) => storage = value;

	/// <summary>
	/// Gets a value indicating whether a read always finishes in a bounded number of steps, whatever
	/// other threads are doing.
	/// </summary>
	/// <remarks>
	/// True only when a read is a single load. A seqlock read is not wait-free: it retries whenever a
	/// write lands between its two readings of the counter, so a steady stream of writers can starve it.
	/// It is not lock-free either, because a writer descheduled with the counter odd blocks every reader
	/// until it runs again. It is only obstruction-free, which is weaker than what this property promises.
	/// </remarks>
	public static Boolean ReadsAreWaitFree => IsInline || IsReference;

	/// <summary>
	/// Gets a value indicating whether a read avoids the lock, either by loading the value directly or by
	/// taking a seqlock snapshot.
	/// </summary>
	/// <remarks>
	/// This is what distinguishes this candidate from the others: readers of a wide value run alongside a
	/// writer instead of queueing behind it. It says nothing about progress guarantees; see
	/// <see cref="ReadsAreWaitFree"/> for that.
	/// </remarks>
	public static Boolean ReadsTakeNoMonitor => IsInline || IsReference || IsUnmanaged;

	/// <summary>
	/// Gets a value indicating whether the value can be swapped in place: it holds no references, its size
	/// is 1, 2, 4 or 8 bytes, and the runtime aligns it to that size.
	/// </summary>
	/// <remarks>
	/// The alignment check matters. A struct of two <see cref="Int32"/> fields is 8 bytes but only 4-byte
	/// aligned, so the runtime may place it 4 bytes into the object, and an 8-byte atomic instruction on
	/// that address raises <see cref="DataMisalignedException"/> on arm64. <see cref="Atomic{T}"/> avoids the
	/// problem by relying on its only field being word-aligned; this class has two fields, so it can only
	/// use the value's own alignment.
	/// </remarks>
	private static Boolean IsInline
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		get => !RuntimeHelpers.IsReferenceOrContainsReferences<T>()
			&& Unsafe.SizeOf<T>() is 1 or 2 or 4 or 8
			&& AlignmentOfT >= Unsafe.SizeOf<T>();
	}

	/// <summary>Gets the alignment, in bytes, of a field of type <typeparamref name="T"/>.</summary>
	private static Int32 AlignmentOfT
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		get => Unsafe.SizeOf<AlignmentProbe>() - Unsafe.SizeOf<T>();
	}

	/// <summary>
	/// A byte followed by the value, used to measure the value's alignment: the runtime pads after the byte
	/// to align the value, so this struct's size minus the value's size is that alignment.
	/// </summary>
	[StructLayout(LayoutKind.Sequential)]
	private struct AlignmentProbe
	{
#pragma warning disable CS0649
		public Byte Padding;
		public T Value;
#pragma warning restore CS0649
	}

	private static Boolean IsReference
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		get => !typeof(T).IsValueType;
	}

	private static Boolean IsUnmanaged
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		get => !RuntimeHelpers.IsReferenceOrContainsReferences<T>();
	}

	/// <inheritdoc />
	public T Read()
	{
		if (IsInline)
			return ReadInline();
		if (IsReference)
			return ReadReference();
		if (IsUnmanaged)
			return ReadSeqLock();
		lock (this)
			return storage;
	}

	/// <inheritdoc />
	public void Write(T value)
	{
		if (IsInline)
			WriteInline(value);
		else if (IsReference)
			WriteReference(value);
		else if (IsUnmanaged)
			WriteSeqLock(value);
		else
			lock (this)
				storage = value;
	}

	/// <inheritdoc />
	public T Exchange(T value)
	{
		if (IsInline)
			return ExchangeInline(value);
		if (IsReference)
			return ExchangeReference(value);
		if (IsUnmanaged)
		{
			var stamp = AcquireWrite();
			var previous = storage;
			storage = value;
			ReleaseWrite(stamp);
			return previous;
		}
		lock (this)
		{
			var previous = storage;
			storage = value;
			return previous;
		}
	}

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
			if (ExchangeBits(value, comparand, out previous))
				return true;

			// Bits differing is not the same as values differing, so the type is asked and the exchange
			// retried against the bits actually seen. The same reasoning as Atomic<T>, which carries it.
			while (true)
			{
				var current = previous;
				if (!EqualityComparer<T>.Default.Equals(current, comparand))
					return false;
				if (ExchangeBits(value, current, out previous))
					return true;
			}
		}

		if (IsReference)
		{
			var previousSlot = Interlocked.CompareExchange(ref Unsafe.As<T, Object?>(ref storage), value, comparand);
			previous = Unsafe.As<Object?, T>(ref previousSlot);
			return ReferenceEquals(previousSlot, comparand);
		}

		if (IsUnmanaged)
		{
			// The comparison is the caller's code, so it runs outside the counter rather than inside it. A slow
			// Equals held off every reader; one that touched this cell again hung the thread for good, because
			// the counter, unlike a monitor, does not readmit the thread already holding it.
			//
			// Bits are tried before the type for the reason BitsEqual gives: values it joins are identical and
			// so equal under any comparer — including one that calls a value unequal to itself, which the
			// inline path above stores over and which this path must not refuse.
			while (true)
			{
				var snapshot = ReadSeqLock();
				if (!BitsEqual(ref snapshot, ref comparand)
					&& !EqualityComparer<T>.Default.Equals(snapshot, comparand))
				{
					// The snapshot was the whole value at a point inside this call, so failing against it is a
					// failure the caller could have seen; no write lock is needed to report it.
					previous = snapshot;
					return false;
				}

				// The caller's comparand matched. Confirm under the counter that nothing has moved since,
				// comparing bytes rather than calling Equals again so that no user code runs here.
				var stamp = AcquireWrite();
				if (BitsEqual(ref storage, ref snapshot))
				{
					previous = snapshot;
					storage = value;
					ReleaseWrite(stamp);
					return true;
				}
				ReleaseWrite(stamp);
			}
		}

		lock (this)
		{
			previous = storage;
			if (!EqualityComparer<T>.Default.Equals(previous, comparand))
				return false;
			storage = value;
			return true;
		}
	}

	/// <summary>
	/// Compare-exchanges the value with one instruction of the width of <typeparamref name="T"/>,
	/// comparing bits.
	/// </summary>
	/// <param name="value">The value to store if the bits match.</param>
	/// <param name="comparand">The value whose bits the cell is expected to hold.</param>
	/// <param name="previous">The value held before the call.</param>
	/// <returns><see langword="true"/> if the bits matched and the value was stored.</returns>
	/// <remarks>
	/// Bit comparison is the only kind the hardware offers. This is tried first because identical bits are
	/// the same value, and then once per retry with the bits the previous attempt actually found.
	/// </remarks>
	private Boolean ExchangeBits(T value, T comparand, out T previous)
	{
		switch (Unsafe.SizeOf<T>())
		{
			case 1: { var c = Unsafe.As<T, Byte>(ref comparand); var p = Interlocked.CompareExchange(ref Unsafe.As<T, Byte>(ref storage), Unsafe.As<T, Byte>(ref value), c); previous = Unsafe.As<Byte, T>(ref p); return p == c; }
			case 2: { var c = Unsafe.As<T, UInt16>(ref comparand); var p = Interlocked.CompareExchange(ref Unsafe.As<T, UInt16>(ref storage), Unsafe.As<T, UInt16>(ref value), c); previous = Unsafe.As<UInt16, T>(ref p); return p == c; }
			case 4: { var c = Unsafe.As<T, UInt32>(ref comparand); var p = Interlocked.CompareExchange(ref Unsafe.As<T, UInt32>(ref storage), Unsafe.As<T, UInt32>(ref value), c); previous = Unsafe.As<UInt32, T>(ref p); return p == c; }
			case 8: { var c = Unsafe.As<T, UInt64>(ref comparand); var p = Interlocked.CompareExchange(ref Unsafe.As<T, UInt64>(ref storage), Unsafe.As<T, UInt64>(ref value), c); previous = Unsafe.As<UInt64, T>(ref p); return p == c; }
			default: throw new UnreachableException();
		}
	}

	/// <summary>Compares two values byte by byte, without running any user code.</summary>
	/// <param name="a">The first value.</param>
	/// <param name="b">The second value.</param>
	/// <returns><see langword="true"/> if the two are identical byte for byte.</returns>
	/// <remarks>
	/// Only valid for a <typeparamref name="T"/> with no references, which is the only case that calls it.
	/// Padding can make it say "different" for values the type considers equal, which costs a retry. It
	/// never says "same" wrongly: identical bytes are equal under any comparer.
	/// </remarks>
	private static Boolean BitsEqual(ref T a, ref T b) =>
		MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<T, Byte>(ref a), Unsafe.SizeOf<T>())
			.SequenceEqual(MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<T, Byte>(ref b), Unsafe.SizeOf<T>()));

	private T ReadSeqLock()
	{
		while (true)
		{
			var before = Volatile.Read(ref version);
			if ((before & 1) != 0)
			{
				Thread.SpinWait(1);
				continue;
			}

			// The snapshot may be half written; the second reading of the counter is what proves it
			// was not. Only reached when T holds no references, so torn bytes are merely discarded.
			var snapshot = storage;
			Interlocked.MemoryBarrier();
			if (Volatile.Read(ref version) == before)
				return snapshot;
		}
	}

	private void WriteSeqLock(T value)
	{
		var stamp = AcquireWrite();
		storage = value;
		ReleaseWrite(stamp);
	}

	/// <summary>Makes the counter odd, telling readers a write is in progress.</summary>
	/// <returns>The even value the counter had before; pass it to <see cref="ReleaseWrite"/>.</returns>
	private Int64 AcquireWrite()
	{
		while (true)
		{
			var current = Volatile.Read(ref version);
			if ((current & 1) == 0 && Interlocked.CompareExchange(ref version, current + 1, current) == current)
				return current;
			Thread.SpinWait(1);
		}
	}

	/// <summary>
	/// Makes the counter even again, at a new value no reader can mistake for the one before the write.
	/// </summary>
	/// <param name="stamp">The value returned by <see cref="AcquireWrite"/>.</param>
	/// <remarks>
	/// The counter is 64 bits so that it can never wrap around to a value a reader is still holding. With
	/// 32 bits, a reader descheduled across 2 billion writes could see its starting value again and accept
	/// a torn snapshot; with 64 bits that is unreachable. Beside a value of a word or more, the extra
	/// 4 bytes land where a 32-bit counter would have left padding, so they cost nothing. Beside the odd
	/// sizes below a word they cost a word per cell: <c>SeqLockAtomic&lt;Three&gt;</c> is 32 bytes where a
	/// 32-bit counter would make it 24.
	/// </remarks>
	private void ReleaseWrite(Int64 stamp) => Volatile.Write(ref version, stamp + 2);

	private T ReadInline()
	{
		switch (Unsafe.SizeOf<T>())
		{
			case 1: { var bits = Volatile.Read(ref Unsafe.As<T, Byte>(ref storage)); return Unsafe.As<Byte, T>(ref bits); }
			case 2: { var bits = Volatile.Read(ref Unsafe.As<T, UInt16>(ref storage)); return Unsafe.As<UInt16, T>(ref bits); }
			case 4: { var bits = Volatile.Read(ref Unsafe.As<T, UInt32>(ref storage)); return Unsafe.As<UInt32, T>(ref bits); }
			case 8: { var bits = Volatile.Read(ref Unsafe.As<T, UInt64>(ref storage)); return Unsafe.As<UInt64, T>(ref bits); }
			default: throw new UnreachableException();
		}
	}

	private void WriteInline(T value)
	{
		switch (Unsafe.SizeOf<T>())
		{
			case 1: Volatile.Write(ref Unsafe.As<T, Byte>(ref storage), Unsafe.As<T, Byte>(ref value)); return;
			case 2: Volatile.Write(ref Unsafe.As<T, UInt16>(ref storage), Unsafe.As<T, UInt16>(ref value)); return;
			case 4: Volatile.Write(ref Unsafe.As<T, UInt32>(ref storage), Unsafe.As<T, UInt32>(ref value)); return;
			case 8: Volatile.Write(ref Unsafe.As<T, UInt64>(ref storage), Unsafe.As<T, UInt64>(ref value)); return;
			default: throw new UnreachableException();
		}
	}

	private T ExchangeInline(T value)
	{
		switch (Unsafe.SizeOf<T>())
		{
			case 1: { var bits = Interlocked.Exchange(ref Unsafe.As<T, Byte>(ref storage), Unsafe.As<T, Byte>(ref value)); return Unsafe.As<Byte, T>(ref bits); }
			case 2: { var bits = Interlocked.Exchange(ref Unsafe.As<T, UInt16>(ref storage), Unsafe.As<T, UInt16>(ref value)); return Unsafe.As<UInt16, T>(ref bits); }
			case 4: { var bits = Interlocked.Exchange(ref Unsafe.As<T, UInt32>(ref storage), Unsafe.As<T, UInt32>(ref value)); return Unsafe.As<UInt32, T>(ref bits); }
			case 8: { var bits = Interlocked.Exchange(ref Unsafe.As<T, UInt64>(ref storage), Unsafe.As<T, UInt64>(ref value)); return Unsafe.As<UInt64, T>(ref bits); }
			default: throw new UnreachableException();
		}
	}

	private T ReadReference()
	{
		var current = Volatile.Read(ref Unsafe.As<T, Object?>(ref storage));
		return Unsafe.As<Object?, T>(ref current);
	}

	private void WriteReference(T value) => Volatile.Write(ref Unsafe.As<T, Object?>(ref storage), value);

	private T ExchangeReference(T value)
	{
		var previous = Interlocked.Exchange(ref Unsafe.As<T, Object?>(ref storage), value);
		return Unsafe.As<Object?, T>(ref previous);
	}

	private T CompareExchangeReference(T value, T comparand)
	{
		var previous = Interlocked.CompareExchange(ref Unsafe.As<T, Object?>(ref storage), value, comparand);
		return Unsafe.As<Object?, T>(ref previous);
	}
}
