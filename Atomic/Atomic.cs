using System.Runtime.CompilerServices;

namespace NickStrupat;

/// <summary>
/// A cell whose value can be read, written, and swapped atomically.
/// </summary>
/// <typeparam name="T">The type of the value held by the cell.</typeparam>
/// <remarks>
/// <para>
/// The value lives in a single field of type <typeparamref name="T"/>, so the runtime lays each cell
/// out to fit and nothing is ever boxed. Any value holding no references and no wider than a machine
/// word is read and written through an eight byte view of that field, whatever its own size or
/// alignment — including sizes no instruction matches, such as three bytes.
/// </para>
/// <para>
/// Two facts about the runtime allow that, and both depend on <c>storage</c> being the only field this
/// class declares. On a sixty four bit runtime a lone field begins eight bytes into the object, and
/// objects are eight byte aligned, so the view is aligned; on a thirty two bit one neither holds, which
/// is why <see cref="IsInline"/> tests the word size. And the minimum size of an object leaves a full eight bytes there, so a
/// three byte value has five bytes of slack behind it which belong to nobody. Writes zero the slack, so
/// the bit pattern of a given value is always the same and <see cref="CompareExchange"/> compares
/// something meaningful.
/// </para>
/// <para>
/// Adding a second field breaks both facts at once: the runtime is free to seat that field first, which
/// pushes the value off a word boundary and raises <see cref="DataMisalignedException"/> on arm64. The
/// bet is kept honest by a test calling <see cref="ProbeFieldIsWordAligned"/> rather than by a run time
/// check, because branching on it costs every access a static load and a branch that NativeAOT cannot
/// fold away.
/// </para>
/// <para>
/// A reference is swapped through the object overloads, which keep the GC write barrier. Everything
/// else — a value wider than a word, or one holding references — is guarded by a monitor on the cell,
/// so readers block as well as writers. Locking on the instance means outside code holding a reference
/// to this cell can interfere with it; the alternative, a private lock object, is a second field, which
/// this design cannot afford.
/// </para>
/// </remarks>
public sealed class Atomic<T>

{
	/// <summary>
	/// The value. This must remain the only field in the class: see the remarks on
	/// <see cref="Atomic{T}"/> for what a second one would break.
	/// </summary>
	private T storage;

	/// <summary>The value, by reference, for the extensions which apply an instruction to it directly.</summary>
	/// <remarks>
	/// Handing this to <see cref="Interlocked"/> is only sound for a value the hardware has an
	/// instruction for, which is why <see cref="AtomicExtensions"/> reaches for it only under a
	/// <c>typeof</c> test naming one of those types.
	/// </remarks>
	internal ref T Storage => ref storage;

	/// <summary>Initializes a new cell holding <paramref name="value"/>.</summary>
	/// <param name="value">The initial value.</param>
	public Atomic(T value) => storage = value;

	/// <summary>Gets a value indicating whether the value is read and written without a lock.</summary>
	public static Boolean IsLockFree => IsInline || IsReference;

	/// <remarks>
	/// <para>
	/// Every term folds to a constant the moment <typeparamref name="T"/> is known, so a cell compiles to
	/// one strategy with no test left in it. That is why the alignment of the field is asserted by a test
	/// rather than consulted here: it can only be learned by reading the address of an object, which the
	/// JIT can fold after the type initializer has run but which NativeAOT cannot evaluate at all when it
	/// builds the image. A single term it cannot fold keeps the monitor path live, and with it a
	/// try/finally that pushes this past the size the compiler will inline.
	/// </para>
	/// <para>
	/// The word size is a term rather than an assumption. ECMA-335 I.12.6.2 aligns an eight byte value on
	/// the boundary the hardware needs for a <c>native int</c>, which is four bytes on a thirty two bit
	/// runtime, and I.12.6.6 grants atomicity only up to that same width. So on wasm, x86 and arm32 the
	/// eight byte view of the field could be both misaligned and torn, and every value goes to the monitor
	/// instead. <see cref="IntPtr"/>.<see cref="IntPtr.Size"/> is a constant to both compilers, so saying
	/// this costs nothing.
	/// </para>
	/// </remarks>
	private static Boolean IsInline
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		get => !RuntimeHelpers.IsReferenceOrContainsReferences<T>()
			&& Unsafe.SizeOf<T>() <= sizeof(Int64)
			&& IntPtr.Size == sizeof(Int64);
	}

	private static Boolean IsReference
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		get => !typeof(T).IsValueType;
	}

	/// <summary>Checks that the field really does begin on a word boundary.</summary>
	/// <returns>
	/// <see langword="true"/> when an eight byte view of the field is aligned, which is what
	/// <see cref="IsInline"/> assumes.
	/// </returns>
	/// <remarks>
	/// For the tests, not for <see cref="IsInline"/>. The alignment follows from this class having a
	/// single field, so this asserts an invariant rather than discovering a fact, and a runtime that broke
	/// it would fault rather than quietly fall back to the monitor — which is the louder of the two
	/// failures, and the one more likely to be noticed.
	/// A collection moving the probe is harmless: the offset of the field within the object is fixed,
	/// and every object begins on a word boundary, so the answer does not depend on where it sits.
	/// </remarks>
	internal static unsafe Boolean ProbeFieldIsWordAligned()
	{
		if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
			return false; // never asked of these, and default! would be a null to store

		var probe = new Atomic<T>(default!);
		return ((nint)Unsafe.AsPointer(ref probe.storage) & (sizeof(Int64) - 1)) == 0;
	}

	/// <summary>Widens a value to the whole word, zeroing whatever the value does not occupy.</summary>
	/// <param name="value">The value to widen.</param>
	/// <returns>The bits of the value, zero extended to eight bytes.</returns>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static Int64 ToBits(T value)
	{
		Int64 bits = 0;
		Unsafe.WriteUnaligned(ref Unsafe.As<Int64, Byte>(ref bits), value);
		return bits;
	}

	/// <summary>Narrows a word back to a value, ignoring the bits the value does not occupy.</summary>
	/// <param name="bits">Bits previously produced by <see cref="ToBits"/>.</param>
	/// <returns>The value those bits stand for.</returns>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static T FromBits(Int64 bits) => Unsafe.ReadUnaligned<T>(ref Unsafe.As<Int64, Byte>(ref bits));

	/// <summary>Reads the value held by the cell.</summary>
	/// <returns>The value held at some point during the call.</returns>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public T Read()
	{
		if (IsInline)
			return FromBits(Volatile.Read(ref Unsafe.As<T, Int64>(ref storage)));
		if (IsReference)
		{
			var current = Volatile.Read(ref Unsafe.As<T, Object?>(ref storage));
			return Unsafe.As<Object?, T>(ref current);
		}
		lock (this)
			return storage;
	}

	/// <summary>Writes a value to the cell.</summary>
	/// <param name="value">The value to store.</param>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public void Write(T value)
	{
		if (IsInline)
			Volatile.Write(ref Unsafe.As<T, Int64>(ref storage), ToBits(value));
		else if (IsReference)
			Volatile.Write(ref Unsafe.As<T, Object?>(ref storage), value);
		else
			lock (this)
				storage = value;
	}

	/// <summary>Sets the value and returns the one it replaced, as a single atomic operation.</summary>
	/// <param name="value">The value to store.</param>
	/// <returns>The value held before the call.</returns>
	public T Exchange(T value)
	{
		if (IsInline)
			return FromBits(Interlocked.Exchange(ref Unsafe.As<T, Int64>(ref storage), ToBits(value)));
		if (IsReference)
		{
			var previous = Interlocked.Exchange(ref Unsafe.As<T, Object?>(ref storage), value);
			return Unsafe.As<Object?, T>(ref previous);
		}
		lock (this)
		{
			var previous = storage;
			storage = value;
			return previous;
		}
	}

	/// <summary>
	/// Sets the value to <paramref name="value"/> if the value currently held matches
	/// <paramref name="comparand"/>, and returns the value held before the call.
	/// </summary>
	/// <param name="value">The value to store when the comparison succeeds.</param>
	/// <param name="comparand">The value the cell is expected to hold.</param>
	/// <returns>The value held before the call.</returns>
	/// <remarks>
	/// A reference is compared by identity, a value type held in a machine word by its bits, and any
	/// other value type with <see cref="EqualityComparer{T}.Default"/>. Which one applies is decided by
	/// <typeparamref name="T"/> alone, so prefer <see cref="TryCompareExchange"/> in a loop rather than
	/// inferring from the value returned which comparison was made.
	/// </remarks>
	public T CompareExchange(T value, T comparand)
	{
		TryCompareExchange(value, comparand, out var previous);
		return previous;
	}

	/// <summary>
	/// Does what <see cref="CompareExchange"/> does, and reports whether the exchange happened rather
	/// than leaving it to be inferred from the value returned.
	/// </summary>
	/// <param name="value">The value to store when the comparison succeeds.</param>
	/// <param name="comparand">The value the cell is expected to hold.</param>
	/// <param name="previous">The value held before the call.</param>
	/// <returns><see langword="true"/> when the value was stored, otherwise <see langword="false"/>.</returns>
	/// <remarks>
	/// A loop retrying a failed exchange cannot tell the two apart from <paramref name="previous"/>
	/// alone: the comparison a cell applies depends on where it keeps the value, and a caller comparing
	/// the returned value itself will read <c>-0.0</c> as equal to <c>0.0</c> where a cell comparing bits
	/// did not, and drop an update believing it landed.
	/// </remarks>
	public Boolean TryCompareExchange(T value, T comparand, out T previous)
	{
		if (IsInline)
		{
			var comparandBits = ToBits(comparand);
			var previousBits = Interlocked.CompareExchange(ref Unsafe.As<T, Int64>(ref storage), ToBits(value), comparandBits);
			previous = FromBits(previousBits);
			return previousBits == comparandBits;
		}

		if (IsReference)
		{
			var previousSlot = Interlocked.CompareExchange(ref Unsafe.As<T, Object?>(ref storage), value, comparand);
			previous = Unsafe.As<Object?, T>(ref previousSlot);
			return ReferenceEquals(previousSlot, comparand);
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
}
