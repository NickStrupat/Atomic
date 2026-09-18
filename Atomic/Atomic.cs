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
/// word is read and written through a word sized view of that field, whatever its own size or
/// alignment — including sizes no instruction matches, such as three bytes.
/// </para>
/// <para>
/// Two facts about the runtime allow that, and both depend on <c>storage</c> being the only field this
/// class declares. A lone field begins one word into the object and objects are word aligned, so the
/// view is aligned; and the minimum size of an object leaves a whole word there, so a three byte value
/// has slack behind it which belongs to nobody. Writes zero the slack, so the bit pattern of a given
/// value is always the same and <see cref="CompareExchange"/> compares something meaningful.
/// </para>
/// <para>
/// Both facts are about a word rather than about eight bytes, which is why a word is what the view is.
/// At sixty four bits that is eight bytes and every unmanaged value up to that size is swapped where it
/// lies; at thirty two it is four, and a wider value has neither the alignment nor the slack. ECMA-335
/// I.12.6.2 aligns an eight byte value only on the boundary a <c>native int</c> needs, and I.12.6.6
/// grants atomicity only up to that same width, so there the eight byte view would be both misaligned
/// and torn.
/// </para>
/// <para>
/// The eight byte integers are the exception, because the runtime already owes them more than ECMA
/// requires: a field of type <see cref="Int64"/> or <see cref="UInt64"/> is seated on an eight byte
/// boundary wherever the hardware's instructions demand it, which is what lets
/// <see cref="Interlocked"/> be applied to one on a thirty two bit runtime at all. Only through
/// <see cref="Interlocked"/>, though — an eight byte <see cref="Volatile"/> read or write can tear
/// there — so on that path the read is <see cref="Interlocked.Read(ref readonly Int64)"/> and the write is
/// <see cref="Interlocked.Exchange(ref Int64, Int64)"/>. No other eight byte type joins them: the view
/// has to be exact, there being no slack to zero, and the type's equality has to be its bits, this
/// path having no tail to reconcile a miss with.
/// </para>
/// <para>
/// Adding a second field breaks both facts at once: the runtime is free to seat that field first, which
/// pushes the value off a word boundary and raises <see cref="DataMisalignedException"/> on arm64. The
/// bet is kept honest by a test that takes the address of the field and looks, rather than by a run
/// time check, because branching on it costs every access a static load and a branch that NativeAOT
/// cannot fold away.
/// </para>
/// <para>
/// Where the value is kept decides how it is swapped, and nothing else. It does not decide how a
/// comparand is compared: a reference is compared by identity, a value type with
/// <see cref="EqualityComparer{T}.Default"/>, and neither answer moves when the same type gains a
/// field and crosses to the other strategy.
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

	/// <summary>The value, by reference, for anything that applies an instruction to it directly.</summary>
	/// <remarks>
	/// Handing this to <see cref="Interlocked"/> is only sound for a value the hardware has an
	/// instruction for, which is why <see cref="AtomicExtensions"/> reaches for it only under a
	/// <c>typeof</c> test naming one of those types — and only where <see cref="IsInline"/> says the cell
	/// swaps that field itself rather than standing behind its monitor. The two agree at either word
	/// size, <see cref="IsEightByteIntegerOn32Bit"/> being the case where the cell reaches the field with
	/// the same locked instructions the extension does.
	/// </remarks>
	internal ref T Storage => ref storage;

	/// <summary>Initializes a new cell holding <paramref name="value"/>.</summary>
	/// <param name="value">The initial value.</param>
	public Atomic(T value) => storage = value;

	/// <summary>Gets a value indicating whether the value is read and written without a lock.</summary>
	public static Boolean IsLockFree => IsInline || IsReference;

	/// <summary>Gets a value indicating whether the value is kept in the field, swapped where it lies.</summary>
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
	/// <see cref="AtomicExtensions"/> consults this before issuing an interlocked instruction against
	/// <see cref="Storage"/>, which is why it is visible past this class. An instruction issued where this
	/// is false would be a second writer to a field the cell believes only its monitor touches, and the two
	/// would not exclude each other.
	/// </para>
	/// </remarks>
	internal static Boolean IsInline
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		get => FitsInWord || IsEightByteIntegerOn32Bit;
	}

	/// <summary>Gets a value indicating whether the value fits the word the field is seated on.</summary>
	/// <remarks>
	/// The size is measured against <see cref="IntPtr"/>.<see cref="IntPtr.Size"/> rather than against
	/// eight, because the word is what the field's alignment and the object's minimum size are both
	/// stated in. Both are constants to either compiler, so saying it costs nothing, and a sixty four bit
	/// build reads exactly as it did when this said eight.
	/// </remarks>
	private static Boolean FitsInWord
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		get => !RuntimeHelpers.IsReferenceOrContainsReferences<T>()
		       && Unsafe.SizeOf<T>() <= IntPtr.Size;
	}

	/// <summary>Gets a value indicating whether the value is an eight byte integer past the word.</summary>
	/// <remarks>
	/// <para>
	/// <see cref="Int64"/> and <see cref="UInt64"/> earn a view the word does not cover, because a field
	/// of either is seated on an eight byte boundary wherever the instructions require it — which is what
	/// makes them reachable by <see cref="Interlocked"/> there at all. Nothing here can establish that of
	/// an arbitrary eight byte value, so nothing else is offered it.
	/// </para>
	/// <para>
	/// Naming the two types rather than testing the size is also what keeps the compare-exchange on this
	/// path honest without a tail. The view is exactly the value, so there is no slack to zero, and an
	/// integer's equality is its bits, so a miss is a miss and there is nothing to ask the type about.
	/// <see cref="Double"/> is eight bytes and is not here for the second reason, not the first.
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

	/// <summary>Widens a value to a whole view, zeroing whatever the value does not occupy.</summary>
	/// <typeparam name="TView">The view the field is swapped through.</typeparam>
	/// <param name="value">The value to widen.</param>
	/// <returns>The bits of the value, zero extended to the width of the view.</returns>
	/// <remarks>
	/// The caller owes this a <typeparamref name="TView"/> at least as wide as <typeparamref name="T"/>,
	/// which both strategies establish before they get here: <see cref="FitsInWord"/> measures the size
	/// against the view, and <see cref="IsEightByteIntegerOn32Bit"/> names two types the view is exactly.
	/// </remarks>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static TView Widen<TView>(T value) where TView : unmanaged
	{
		TView view = default;
		Unsafe.WriteUnaligned(ref Unsafe.As<TView, Byte>(ref view), value);
		return view;
	}

	/// <summary>Narrows a view back to a value, ignoring the bits the value does not occupy.</summary>
	/// <typeparam name="TView">The view the field is swapped through.</typeparam>
	/// <param name="view">A view previously produced by <see cref="Widen{TView}"/>.</param>
	/// <returns>The value those bits stand for.</returns>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static T Narrow<TView>(TView view) where TView : unmanaged =>
		Unsafe.ReadUnaligned<T>(ref Unsafe.As<TView, Byte>(ref view));

	/// <summary>Reads the value held by the cell.</summary>
	/// <returns>The value held at some point during the call.</returns>
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

	/// <summary>Sets the value and returns the one it replaced, as a single atomic operation.</summary>
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
	/// Sets the value to <paramref name="value"/> if the value currently held matches
	/// <paramref name="comparand"/>, and returns the value held before the call.
	/// </summary>
	/// <param name="value">The value to store when the comparison succeeds.</param>
	/// <param name="comparand">The value the cell is expected to hold.</param>
	/// <returns>The value held before the call.</returns>
	/// <remarks>
	/// A reference is compared by identity and by nothing else. A value type is compared with
	/// <see cref="EqualityComparer{T}.Default"/> whatever its width and wherever the cell keeps it, so
	/// two values the type calls equal match here even when their bits differ. Prefer
	/// <see cref="TryCompareExchange"/> in a loop rather than judging from the value returned whether
	/// the exchange happened.
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
	/// alone. The cell compares with <see cref="EqualityComparer{T}.Default"/> and a caller reaching for
	/// <c>==</c> does not: a cell holding <see cref="Double.NaN"/> stores over it when handed a
	/// <see cref="Double.NaN"/> comparand, and a caller judging that by the value it got back reads the
	/// swap it just made as a failure, retries, and stores twice.
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
			if (!EqualityComparer<T>.Default.Equals(previous, comparand))
				return false;
			storage = value;
			return true;
		}
	}

	/// <summary>
	/// Finishes a compare-exchange whose single instruction did not match, for a value held in a word.
	/// </summary>
	/// <param name="slot">The word the value is kept in.</param>
	/// <param name="value">The value to store when the comparison succeeds.</param>
	/// <param name="comparand">The value the cell is expected to hold.</param>
	/// <param name="previousWord">The bits that instruction found.</param>
	/// <param name="previous">The value held before the call.</param>
	/// <returns><see langword="true"/> when the value was stored, otherwise <see langword="false"/>.</returns>
	/// <remarks>
	/// <para>
	/// Bits differing is not the same as values differing: <c>-0.0</c> against <c>0.0</c>, a type whose
	/// <see cref="Object.Equals(Object)"/> reads some of its fields and not others, or a value whose
	/// padding arrived from somewhere that did not zero it. So the type is asked, and if it says equal,
	/// the exchange is retried against the bits actually seen rather than the ones the caller offered.
	/// </para>
	/// <para>
	/// Nothing is lost by trying the instruction first. Identical bits are the same value and
	/// <see cref="Object.Equals(Object)"/> is required to be reflexive, so a match there is a match here
	/// — which is what keeps the ordinary case one instruction with none of this in front of it. A type
	/// that breaks reflexivity, and calls a value unequal to itself, is stored over anyway.
	/// </para>
	/// <para>
	/// Kept out of line so the caller stays small enough to inline into the compare-exchange loops in
	/// <see cref="AtomicExtensions"/>, which is where every read-modify-write goes.
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