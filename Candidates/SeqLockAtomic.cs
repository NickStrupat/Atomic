using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace NickStrupat;

/// <summary>
/// Holds the value in a single field of type <typeparamref name="T"/> beside a version counter, so a
/// value too wide to swap atomically can still be read without taking a monitor and written without
/// allocating.
/// </summary>
/// <typeparam name="T">The type of the value held by the cell.</typeparam>
/// <remarks>
/// <para>
/// The fast paths match <see cref="Atomic{T}"/>, minus the widening it can afford, and leave the
/// counter unused. A wider value holding no references uses the counter as a seqlock: a writer marks it odd, writes, then marks it
/// even, while a reader takes a snapshot between two even readings of the same value and retries if
/// they disagree. A reader can therefore copy a half-written value, which is harmless for bytes and
/// unsurvivable for references — so a value type holding references falls back to a monitor instead.
/// </para>
/// <para>
/// The counter is a lock, not an absence of one. It spares readers from mutual exclusion with each other
/// and from queueing behind a writer on a monitor, but a writer holds it for the duration of its write
/// and every other thread waits. Reads linearize between the two readings of the counter and writes at
/// the swap that makes it odd, so operations are atomic; neither side is lock free.
/// </para>
/// <para>
/// The fast paths are duplicated from <see cref="Atomic{T}"/> rather than shared, so that each
/// candidate is measured whole rather than through a call into common code.
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
	/// Gets a value indicating whether a read completes in a bounded number of steps no matter what other
	/// threads are doing.
	/// </summary>
	/// <remarks>
	/// True only where the read is a single load. The seqlock path is not wait free: it retries whenever a
	/// write lands between the two readings of the counter, so a steady stream of writers can starve a
	/// reader indefinitely. Nor is it lock free, since a writer descheduled with the counter odd blocks
	/// every reader until it is scheduled again. It is obstruction free, which is a weaker promise than
	/// this property makes.
	/// </remarks>
	public static Boolean ReadsAreWaitFree => IsInline || IsReference;

	/// <summary>
	/// Gets a value indicating whether a read stays off the monitor, whether by loading the value outright
	/// or by taking a seqlock snapshot.
	/// </summary>
	/// <remarks>
	/// This is the property that separates the candidates: it is what lets readers of a wide value run
	/// alongside a writer rather than queue behind it. It is not a claim about progress; see
	/// <see cref="ReadsAreWaitFree"/> for that.
	/// </remarks>
	public static Boolean ReadsTakeNoMonitor => IsInline || IsReference || IsUnmanaged;

	/// <summary>
	/// Gets a value indicating whether the value can be read and written where it lies: it holds no
	/// references, it matches an interlocked width, and the runtime is obliged to align it for one.
	/// </summary>
	/// <remarks>
	/// The alignment test is not pedantry. An eight byte struct of two <see cref="Int32"/> fields has an
	/// alignment of four, so the runtime may seat it four bytes into the object, and a sixty four bit
	/// atomic instruction on that address raises <see cref="DataMisalignedException"/> on arm64. Copying
	/// the bytes into a field which is itself eight bytes wide, as <see cref="Atomic{T}"/> does, sidesteps
	/// the question; reinterpreting the value where it lies does not.
	/// </remarks>
	private static Boolean IsInline
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		get => !RuntimeHelpers.IsReferenceOrContainsReferences<T>()
			&& Unsafe.SizeOf<T>() is 1 or 2 or 4 or 8
			&& AlignmentOfT >= Unsafe.SizeOf<T>();
	}

	/// <summary>Gets the alignment the runtime gives a field of type <typeparamref name="T"/>.</summary>
	private static Int32 AlignmentOfT
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		get => Unsafe.SizeOf<AlignmentProbe>() - Unsafe.SizeOf<T>();
	}

	/// <summary>
	/// A byte followed by the value. How far the value gets pushed along to satisfy its own alignment is
	/// the difference between this and the size of the value alone.
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
			switch (Unsafe.SizeOf<T>())
			{
				case 1: { var c = Unsafe.As<T, Byte>(ref comparand); var p = Interlocked.CompareExchange(ref Unsafe.As<T, Byte>(ref storage), Unsafe.As<T, Byte>(ref value), c); previous = Unsafe.As<Byte, T>(ref p); return p == c; }
				case 2: { var c = Unsafe.As<T, UInt16>(ref comparand); var p = Interlocked.CompareExchange(ref Unsafe.As<T, UInt16>(ref storage), Unsafe.As<T, UInt16>(ref value), c); previous = Unsafe.As<UInt16, T>(ref p); return p == c; }
				case 4: { var c = Unsafe.As<T, UInt32>(ref comparand); var p = Interlocked.CompareExchange(ref Unsafe.As<T, UInt32>(ref storage), Unsafe.As<T, UInt32>(ref value), c); previous = Unsafe.As<UInt32, T>(ref p); return p == c; }
				case 8: { var c = Unsafe.As<T, UInt64>(ref comparand); var p = Interlocked.CompareExchange(ref Unsafe.As<T, UInt64>(ref storage), Unsafe.As<T, UInt64>(ref value), c); previous = Unsafe.As<UInt64, T>(ref p); return p == c; }
				default: throw new UnreachableException();
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
			while (true)
			{
				var snapshot = ReadSeqLock();
				if (!EqualityComparer<T>.Default.Equals(snapshot, comparand))
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

	/// <summary>Compares two values by their bytes, which runs no user code.</summary>
	/// <param name="a">The first value.</param>
	/// <param name="b">The second value.</param>
	/// <returns><see langword="true"/> if the two are identical byte for byte.</returns>
	/// <remarks>
	/// Sound only for a <typeparamref name="T"/> holding no references, which is the only place it is
	/// called. Padding makes it conservative rather than wrong: values it separates may still compare equal,
	/// which costs a retry, while values it joins are identical and so equal under any comparer.
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

	/// <summary>Marks the counter odd, which tells readers a write is under way.</summary>
	/// <returns>The even value the counter is restored past on release.</returns>
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

	/// <summary>Marks the counter even again, at a value no reader can mistake for the one before.</summary>
	/// <param name="stamp">The value returned by <see cref="AcquireWrite"/>.</param>
	/// <remarks>
	/// The counter is sixty four bits wide so that it cannot come back around to a value a reader is still
	/// holding. At thirty two bits a reader descheduled across two billion writes would see the counter it
	/// started with and accept a torn snapshot; at sixty four the wrap is unreachable. The width costs
	/// nothing for the values this path serves, since a counter beside a value of a word or more lands in
	/// padding either way.
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
