using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace NickStrupat;

/// <summary>
/// A cell holding a value that can be read, written, exchanged and compare-exchanged atomically.
/// </summary>
/// <typeparam name="T">The type of the value held by the cell.</typeparam>
/// <remarks>
/// <para>
/// How the cell reaches its value depends only on <typeparamref name="T"/> and the runtime's word size,
/// and is fixed when the code is compiled. There are four strategies:
/// </para>
/// <list type="bullet">
/// <item><description>
/// A value with no references that fits in a word (<see cref="IntPtr.Size"/> bytes) is read and written
/// as one whole word, whatever its own size — including sizes no instruction matches, such as 3 bytes.
/// </description></item>
/// <item><description>
/// <see cref="Int64"/> and <see cref="UInt64"/> on a 32-bit runtime use the 8-byte
/// <see cref="Interlocked"/> methods.
/// </description></item>
/// <item><description>
/// A reference type uses the <see cref="Object"/> overloads, which keep the GC write barrier.
/// </description></item>
/// <item><description>
/// Everything else — a value wider than those, or one containing references — is guarded by a lock on
/// the cell. Readers take the lock as well as writers.
/// </description></item>
/// </list>
/// <para>
/// The word-sized view relies on two facts about the runtime, and both need <c>storage</c> to be the
/// only field this class declares. First, a lone field starts one word into the object, and objects are
/// word-aligned, so the field is word-aligned. Second, an object's minimum size leaves a whole word for
/// the field, so a value narrower than a word has spare bytes after it that nothing else uses. Every
/// write zeroes those spare bytes, so they never make two equal values look different, and
/// <see cref="CompareExchange"/> can compare whole words. Padding inside the value is copied as it is;
/// when that makes the bits differ, the cell falls back to the type's equality.
/// </para>
/// <para>
/// The view is a word rather than 8 bytes because both facts are stated in words. At 64 bits a word is
/// 8 bytes, so every unmanaged value up to 8 bytes is swapped in place. At 32 bits a word is 4 bytes,
/// and an 8-byte view would be neither aligned nor atomic: ECMA-335 I.12.6.2 only aligns an 8-byte value
/// as far as a <c>native int</c> needs, and I.12.6.6 only promises atomic access up to that width.
/// </para>
/// <para>
/// <see cref="Int64"/> and <see cref="UInt64"/> are the exception at 32 bits. The runtime places fields
/// of these two types on an 8-byte boundary wherever the hardware's instructions need it, which is what
/// lets <see cref="Interlocked"/> work on them there at all. <see cref="Volatile"/> is not enough — an
/// 8-byte volatile read or write can tear on a 32-bit runtime — so reads use
/// <see cref="Interlocked.Read(ref readonly Int64)"/> and writes use
/// <see cref="Interlocked.Exchange(ref Int64, Int64)"/>. No other 8-byte type gets this path: the runtime
/// only promises that alignment for these two types, and the path has no fallback to the type's
/// equality, so it needs a type whose equality is exactly its bits.
/// </para>
/// <para>
/// Do not add a second field. The runtime may place it first, which moves the value off a word boundary
/// (raising <see cref="DataMisalignedException"/> on arm64) and takes away the spare bytes. The alignment
/// is checked by a test that takes the field's address, not at run time: a run-time check would add a
/// static load and a branch to every access, and NativeAOT cannot fold it away. This is also why the lock
/// is taken on the cell itself rather than on a private lock object, which would be a second field. The
/// cost is that outside code locking on the cell can interfere with it.
/// </para>
/// <para>
/// The strategy decides how the value is swapped, not how a comparand is compared. A reference is
/// compared by identity and a value type with <see cref="EqualityComparer{T}.Default"/>, under every
/// strategy, so a type that gains a field and moves to another strategy still matches the same comparands.
/// </para>
/// </remarks>
public sealed class Atomic<T>
{
	/// <summary>
	/// The value. This must stay the only field in the class; the remarks on <see cref="Atomic{T}"/>
	/// explain what a second field would break.
	/// </summary>
	private T storage;

	/// <summary>A reference to the value, for code that applies an instruction to it directly.</summary>
	/// <remarks>
	/// Passing this to <see cref="Interlocked"/> is only safe when two things hold: <typeparamref name="T"/>
	/// is a type the hardware has an instruction for, and <see cref="IsInline"/> is true, meaning the cell
	/// swaps the field itself rather than guarding it with its lock. <see cref="AtomicExtensions"/> checks
	/// both before using it. The two conditions agree at either word size: under
	/// <see cref="IsEightByteIntegerOn32Bit"/>, the cell uses the same locked instructions on the field that
	/// the extension does.
	/// </remarks>
	internal ref T Storage => ref storage;

	/// <summary>Initializes a new cell holding <paramref name="value"/>.</summary>
	/// <param name="value">The initial value.</param>
	public Atomic(T value) => storage = value;

	/// <summary>
	/// Gets a value indicating whether every operation on a cell of this type completes without taking
	/// a lock.
	/// </summary>
	/// <remarks>
	/// The answer depends on <typeparamref name="T"/> and the runtime's word size, and is a constant for
	/// each. Where it is <see langword="false"/>, reads, writes and every operation in
	/// <see cref="AtomicExtensions"/> take a lock on the cell.
	/// </remarks>
	public static Boolean IsLockFree => IsInline || IsReference;

	/// <summary>
	/// Gets a value indicating whether the value is swapped in place in the field, rather than through a
	/// reference or under the lock.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Every term is a constant once <typeparamref name="T"/> is known, so each instantiation compiles down
	/// to a single strategy with no test left in it. That rules out checking the field's alignment here:
	/// it can only be learned by taking an object's address, which the JIT can fold after the type
	/// initializer has run but NativeAOT cannot evaluate when it builds the image. A single term that does
	/// not fold keeps the lock path alive, and its <c>try</c>/<c>finally</c> makes the method too big to
	/// inline.
	/// </para>
	/// <para>
	/// This is <see langword="internal"/> because <see cref="AtomicExtensions"/> checks it before applying
	/// an interlocked instruction to <see cref="Storage"/>. If it applied one while this was false, the
	/// instruction would be a second writer to a field the cell believes only its lock guards, and the two
	/// would not exclude each other.
	/// </para>
	/// </remarks>
	internal static Boolean IsInline
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		get => FitsInWord || IsEightByteIntegerOn32Bit;
	}

	/// <summary>
	/// Gets a value indicating whether the value holds no references and fits in the word the field
	/// occupies.
	/// </summary>
	/// <remarks>
	/// The size is compared with <see cref="IntPtr"/>.<see cref="IntPtr.Size"/> rather than with 8, because
	/// the field's alignment and the object's minimum size are both measured in words. Both are constants
	/// to either compiler, so this costs nothing, and on a 64-bit build it is exactly the same test as 8.
	/// </remarks>
	private static Boolean FitsInWord
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		get => !RuntimeHelpers.IsReferenceOrContainsReferences<T>()
		       && Unsafe.SizeOf<T>() <= IntPtr.Size;
	}

	/// <summary>
	/// Gets a value indicating whether the value is an <see cref="Int64"/> or <see cref="UInt64"/> on a
	/// runtime whose word is narrower than 8 bytes.
	/// </summary>
	/// <remarks>
	/// <para>
	/// These two types get an 8-byte view even though it is wider than the word, because the runtime
	/// places a field of either type on an 8-byte boundary wherever the instructions require it. That is
	/// what makes <see cref="Interlocked"/> usable on them at 32 bits. Nothing here can prove the same for
	/// an arbitrary 8-byte value, so no other type gets this path.
	/// </para>
	/// <para>
	/// Naming the two types, rather than testing the size, also means the compare-exchange on this path
	/// needs no fallback. The view is exactly the value, so there are no spare bytes to zero, and an
	/// integer's equality is exactly its bits, so a mismatch in bits is a genuine mismatch.
	/// <see cref="Double"/> is also 8 bytes but is excluded for that second reason: its equality is not its
	/// bits.
	/// </para>
	/// </remarks>
	private static Boolean IsEightByteIntegerOn32Bit
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		get => IntPtr.Size < sizeof(Int64)
		       && (typeof(T) == typeof(Int64) || typeof(T) == typeof(UInt64));
	}

	private static Boolean IsReference
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		get => !typeof(T).IsValueType;
	}

	/// <summary>Copies a value into a wider view, zeroing the bytes the value does not fill.</summary>
	/// <typeparam name="TView">The type the field is swapped as.</typeparam>
	/// <param name="value">The value to widen.</param>
	/// <returns>The bits of the value, zero-extended to the width of the view.</returns>
	/// <remarks>
	/// <typeparamref name="TView"/> must be at least as wide as <typeparamref name="T"/>, or this writes past
	/// the end of it. Both callers guarantee that before getting here: <see cref="FitsInWord"/> checks the
	/// size against the view, and <see cref="IsEightByteIntegerOn32Bit"/> only admits two types exactly as
	/// wide as the view.
	/// </remarks>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static TView Widen<TView>(T value) where TView : unmanaged
	{
		TView view = default;
		Unsafe.WriteUnaligned(ref Unsafe.As<TView, Byte>(ref view), value);
		return view;
	}

	/// <summary>Reads a value back out of a view, ignoring the bytes the value does not fill.</summary>
	/// <typeparam name="TView">The type the field is swapped as.</typeparam>
	/// <param name="view">A view produced by <see cref="Widen{TView}"/>.</param>
	/// <returns>The value stored in the view.</returns>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static T Narrow<TView>(TView view) where TView : unmanaged =>
		Unsafe.ReadUnaligned<T>(ref Unsafe.As<TView, Byte>(ref view));

	/// <summary>Reads the value held by the cell.</summary>
	/// <returns>The value the cell held at some point during the call.</returns>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public T Read()
	{
		if (FitsInWord)
			return Narrow(Volatile.Read(ref Unsafe.As<T, IntPtr>(ref storage)));
		if (IsEightByteIntegerOn32Bit)
			return Narrow(Interlocked.Read(ref Unsafe.As<T, Int64>(ref storage)));
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
		if (FitsInWord)
			Volatile.Write(ref Unsafe.As<T, IntPtr>(ref storage), Widen<IntPtr>(value));
		else if (IsEightByteIntegerOn32Bit)
			Interlocked.Exchange(ref Unsafe.As<T, Int64>(ref storage), Widen<Int64>(value));
		else if (IsReference)
			Volatile.Write(ref Unsafe.As<T, Object?>(ref storage), value);
		else
			lock (this)
				storage = value;
	}

	/// <summary>Stores a value and returns the one it replaced, as a single atomic operation.</summary>
	/// <param name="value">The value to store.</param>
	/// <returns>The value held before the call.</returns>
	public T Exchange(T value)
	{
		if (FitsInWord)
			return Narrow(Interlocked.Exchange(ref Unsafe.As<T, IntPtr>(ref storage), Widen<IntPtr>(value)));
		if (IsEightByteIntegerOn32Bit)
			return Narrow(Interlocked.Exchange(ref Unsafe.As<T, Int64>(ref storage), Widen<Int64>(value)));
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
	/// Stores <paramref name="value"/> if the cell currently holds <paramref name="comparand"/>, and returns
	/// the value held before the call either way.
	/// </summary>
	/// <param name="value">The value to store if the comparison succeeds.</param>
	/// <param name="comparand">The value the cell is expected to hold.</param>
	/// <returns>The value held before the call.</returns>
	/// <remarks>
	/// <para>
	/// A reference is compared by identity only. A value type matches if either of these is true, whatever
	/// its width and whichever strategy the cell uses:
	/// </para>
	/// <list type="bullet">
	/// <item><description>
	/// <see cref="EqualityComparer{T}.Default"/> says the two are equal, even if their bits differ.
	/// </description></item>
	/// <item><description>
	/// For a value type holding no references, the two have identical bits, even if the type says they
	/// are unequal. Without this, a cell could refuse a comparand naming the exact value it holds.
	/// </description></item>
	/// </list>
	/// <para>
	/// The type's equality is only called when the bits differ. For a struct with neither an
	/// <see cref="Object.Equals(Object)"/> override nor <see cref="IEquatable{T}"/>, that call boxes both
	/// values, so a comparison that misses allocates.
	/// </para>
	/// <para>
	/// To find out whether the exchange happened, call <see cref="TryCompareExchange"/> in a loop rather
	/// than comparing the returned value yourself.
	/// </para>
	/// </remarks>
	public T CompareExchange(T value, T comparand)
	{
		TryCompareExchange(value, comparand, out var previous);
		return previous;
	}

	/// <summary>
	/// Does what <see cref="CompareExchange"/> does, and also reports whether the value was stored.
	/// </summary>
	/// <param name="value">The value to store if the comparison succeeds.</param>
	/// <param name="comparand">The value the cell is expected to hold.</param>
	/// <param name="previous">The value held before the call.</param>
	/// <returns><see langword="true"/> if the value was stored; otherwise <see langword="false"/>.</returns>
	/// <remarks>
	/// A retry loop cannot reliably tell success from failure by looking at <paramref name="previous"/>,
	/// because the cell and the caller may compare differently. For example, the cell compares with
	/// <see cref="EqualityComparer{T}.Default"/>, which calls <see cref="Double.NaN"/> equal to itself, so a
	/// cell holding <see cref="Double.NaN"/> stores over it when given a <see cref="Double.NaN"/> comparand.
	/// A caller checking the result with <c>==</c> would see <c>NaN != NaN</c>, decide the exchange had
	/// failed, retry, and store twice.
	/// </remarks>
	public Boolean TryCompareExchange(T value, T comparand, out T previous)
	{
		if (FitsInWord)
		{
			ref var slot = ref Unsafe.As<T, IntPtr>(ref storage);
			var comparandWord = Widen<IntPtr>(comparand);
			var previousWord = Interlocked.CompareExchange(ref slot, Widen<IntPtr>(value), comparandWord);
			if (previousWord == comparandWord)
			{
				// The bits matched, so what was there was this value, padding and all.
				previous = comparand;
				return true;
			}

			return TryCompareExchangeEqualValue(ref slot, value, comparand, previousWord, out previous);
		}

		if (IsEightByteIntegerOn32Bit)
		{
			// No tail, because an integer's equality is its bits: a miss here is a genuine mismatch, not
			// a comparison the instruction was the wrong tool for. See the property for why nothing else
			// of this width joins it on the path.
			var comparandBits = Widen<Int64>(comparand);
			ref var slot = ref Unsafe.As<T, Int64>(ref storage);
			var previousBits = Interlocked.CompareExchange(ref slot, Widen<Int64>(value), comparandBits);
			previous = Narrow(previousBits);
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
			if (!HoldsTheSameValueAs(previous, comparand))
				return false;
			storage = value;
			return true;
		}
	}

	/// <summary>On the lock path, decides whether the value the cell holds is the comparand's value.</summary>
	/// <param name="held">The value read from the cell.</param>
	/// <param name="comparand">The value the caller expects.</param>
	/// <returns><see langword="true"/> if the two are the same value.</returns>
	/// <remarks>
	/// <para>
	/// Bits are compared first, and the type's equality is only asked if they differ. This is not an
	/// optimisation. It makes the lock path give the same answer as the word path, where the instruction
	/// compares bits before anything else runs, so a type that calls a value unequal to itself is stored
	/// over anyway. When the type was asked first, a cell refused to exchange a value it was holding, and a
	/// retry loop offering that value back spun forever.
	/// </para>
	/// <para>
	/// Bits are only compared for a type with no references. A value holding a reference is left to its
	/// type's equality: reading its bits races a moving GC, and what matters is the object referred to,
	/// not the address it currently sits at.
	/// </para>
	/// </remarks>
	private static Boolean HoldsTheSameValueAs(T held, T comparand) =>
		(!RuntimeHelpers.IsReferenceOrContainsReferences<T>() && SameBits(held, comparand))
		|| EqualityComparer<T>.Default.Equals(held, comparand);

	/// <summary>Checks whether two values have exactly the same bytes, padding included.</summary>
	/// <param name="a">One value.</param>
	/// <param name="b">The other value.</param>
	/// <returns><see langword="true"/> if every byte matches.</returns>
	private static Boolean SameBits(T a, T b) =>
		MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<T, Byte>(ref a), Unsafe.SizeOf<T>())
			.SequenceEqual(MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<T, Byte>(ref b), Unsafe.SizeOf<T>()));

	/// <summary>
	/// Finishes a compare-exchange on the word path after the instruction found different bits from the
	/// comparand's.
	/// </summary>
	/// <param name="slot">The word the value is kept in.</param>
	/// <param name="value">The value to store if the comparison succeeds.</param>
	/// <param name="comparand">The value the cell is expected to hold.</param>
	/// <param name="previousWord">The bits the instruction found.</param>
	/// <param name="previous">The value held before the call.</param>
	/// <returns><see langword="true"/> if the value was stored; otherwise <see langword="false"/>.</returns>
	/// <remarks>
	/// <para>
	/// Different bits do not always mean different values. Examples: <c>-0.0</c> and <c>0.0</c>; a type
	/// whose <see cref="Object.Equals(Object)"/> ignores some of its fields; a value whose padding was not
	/// zeroed where it came from. So this asks the type. If the type says equal, it retries the exchange
	/// against the bits actually found in the cell, not the bits the caller passed.
	/// </para>
	/// <para>
	/// Trying the instruction first loses nothing. Identical bits are the same value, and
	/// <see cref="Object.Equals(Object)"/> must be reflexive, so a bit match is always an equality match.
	/// That keeps the common case to one instruction. A type that breaks reflexivity (calling a value
	/// unequal to itself) is stored over anyway.
	/// </para>
	/// <para>
	/// This is <see cref="MethodImplOptions.NoInlining"/> so that <see cref="TryCompareExchange"/> stays
	/// small, with the aim of letting it inline into the loops in <see cref="AtomicExtensions"/>. Neither
	/// compiler currently inlines it there, and no test checks either way.
	/// </para>
	/// </remarks>
	[MethodImpl(MethodImplOptions.NoInlining)]
	private static Boolean TryCompareExchangeEqualValue(
		ref IntPtr slot,
		T value,
		T comparand,
		IntPtr previousWord,
		out T previous)
	{
		var valueWord = Widen<IntPtr>(value);
		while (true)
		{
			previous = Narrow(previousWord);
			if (!EqualityComparer<T>.Default.Equals(previous, comparand))
				return false;
			var seenWord = Interlocked.CompareExchange(ref slot, valueWord, previousWord);
			if (seenWord == previousWord)
				return true;

			// A race lost, not an inequality: something else wrote between the read and the exchange, so
			// this goes round on what it wrote rather than reporting a mismatch that never happened.
			previousWord = seenWord;
		}
	}
}