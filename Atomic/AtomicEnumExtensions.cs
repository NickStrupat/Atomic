using System.Numerics;
using System.Runtime.CompilerServices;

namespace NickStrupat;

/// <summary>
/// Atomic bitwise operations — and, or, xor — for cells holding an enum, typically a set of flags.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="AtomicExtensions"/> cannot serve enums. Its bitwise methods require
/// <see cref="IBitwiseOperators{TSelf,TOther,TResult}"/>, and an enum cannot declare interface
/// implementations of its own, so it never implements that one, even though C# lets you apply
/// <c>&amp;</c>, <c>|</c> and <c>^</c> to an enum. Flags are the most common reason to
/// want atomic bitwise operations at all, so these fill that gap.
/// </para>
/// <para>
/// They live in a separate class rather than as extra overloads because constraints are not part of a
/// method signature: a second <c>Or</c> beside the first would be a duplicate member. In separate
/// classes both are candidates and the constraints decide which applies. An enum rules out the
/// operator-constrained one, a type with the operators rules out this one, and no type satisfies both.
/// The caller writes <c>cell.Or(flag)</c> either way, without naming a type argument.
/// </para>
/// <para>
/// When the cell swaps its value in place (<c>Atomic&lt;T&gt;.IsInline</c>), <see cref="And"/> and
/// <see cref="Or"/> use a single <see cref="Interlocked"/> instruction, on the same terms as
/// <see cref="AtomicExtensions"/>:
/// </para>
/// <list type="bullet">
/// <item><description>4- and 8-byte enums use the instruction of that width directly.</description></item>
/// <item><description>
/// 1- and 2-byte enums use the 4-byte instruction on the word they are stored in. This is only correct
/// because the mask is zero-extended: the spare bytes after the value stay zero under <see cref="Or"/>
/// and are set back to zero under <see cref="And"/>, which is what the cell requires.
/// </description></item>
/// <item><description>
/// Everything else is a compare-exchange loop: <see cref="Xor"/> at every width, since there is no
/// interlocked exclusive or, and an 8-byte enum on a 32-bit runtime, which the cell keeps behind its lock.
/// </description></item>
/// </list>
/// <para>
/// The storage is reinterpreted with <see cref="Unsafe"/> here, rather than by casting the cell through
/// <see cref="Object"/> as <see cref="AtomicExtensions"/> does, because that cast fails for enums.
/// Generics are invariant, so an <c>Atomic&lt;Access&gt;</c> is not an <c>Atomic&lt;Int32&gt;</c> even when
/// <c>Access</c> is backed by <see cref="Int32"/>. The field is reinterpreted instead, the way
/// <see cref="Atomic{T}"/> does internally, under a size test rather than a type test.
/// </para>
/// <para>
/// Return values follow <see cref="Interlocked"/>, as in <see cref="AtomicExtensions"/>: all three return
/// the old value.
/// </para>
/// </remarks>
public static class AtomicEnumExtensions
{
	/// <summary>Atomically clears every flag not set in <paramref name="value"/> (a bitwise AND).</summary>
	/// <typeparam name="T">The type of the value held by the cell.</typeparam>
	/// <param name="atomic">The cell to clear flags in.</param>
	/// <param name="value">The flags to keep.</param>
	/// <returns>The old value, as <see cref="Interlocked.And(ref Int32, Int32)"/> does.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="atomic"/> is null.</exception>
	public static T And<T>(this Atomic<T> atomic, T value) where T : struct, Enum
	{
		ArgumentNullException.ThrowIfNull(atomic);

		if (Atomic<T>.IsInline)
		{
			if (Unsafe.SizeOf<T>() <= sizeof(Int32))
			{
				var previous = Interlocked.And(ref AsInt32(ref atomic.Storage), Widen<Int32, T>(value));
				return Narrow<T, Int32>(previous);
			}
			if (Unsafe.SizeOf<T>() == sizeof(Int64))
			{
				var previous = Interlocked.And(ref AsInt64(ref atomic.Storage), Widen<Int64, T>(value));
				return Narrow<T, Int64>(previous);
			}
		}

		var current = atomic.Read();
		while (true)
		{
			if (atomic.TryCompareExchange(Narrow<T, UInt64>(Widen<UInt64, T>(current) & Widen<UInt64, T>(value)), current, out var previous))
				return current;
			current = previous;
		}
	}

	/// <summary>Atomically sets every flag that is set in <paramref name="value"/> (a bitwise OR).</summary>
	/// <typeparam name="T">The type of the value held by the cell.</typeparam>
	/// <param name="atomic">The cell to set flags in.</param>
	/// <param name="value">The flags to set.</param>
	/// <returns>The old value, as <see cref="Interlocked.Or(ref Int32, Int32)"/> does.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="atomic"/> is null.</exception>
	public static T Or<T>(this Atomic<T> atomic, T value) where T : struct, Enum
	{
		ArgumentNullException.ThrowIfNull(atomic);

		if (Atomic<T>.IsInline)
		{
			if (Unsafe.SizeOf<T>() <= sizeof(Int32))
			{
				var previous = Interlocked.Or(ref AsInt32(ref atomic.Storage), Widen<Int32, T>(value));
				return Narrow<T, Int32>(previous);
			}
			if (Unsafe.SizeOf<T>() == sizeof(Int64))
			{
				var previous = Interlocked.Or(ref AsInt64(ref atomic.Storage), Widen<Int64, T>(value));
				return Narrow<T, Int64>(previous);
			}
		}

		var current = atomic.Read();
		while (true)
		{
			if (atomic.TryCompareExchange(Narrow<T, UInt64>(Widen<UInt64, T>(current) | Widen<UInt64, T>(value)), current, out var previous))
				return current;
			current = previous;
		}
	}

	/// <summary>Atomically flips every flag that is set in <paramref name="value"/> (a bitwise XOR).</summary>
	/// <typeparam name="T">The type of the value held by the cell.</typeparam>
	/// <param name="atomic">The cell to flip flags in.</param>
	/// <param name="value">The flags to flip.</param>
	/// <returns>The old value, like <see cref="And"/> and <see cref="Or"/>.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="atomic"/> is null.</exception>
	/// <remarks>
	/// Always a compare-exchange loop. arm64 has an atomic exclusive or (<c>ldeoral</c>), but
	/// <see cref="Interlocked"/> does not expose one and nothing else in C# reaches it.
	/// </remarks>
	public static T Xor<T>(this Atomic<T> atomic, T value) where T : struct, Enum
	{
		ArgumentNullException.ThrowIfNull(atomic);

		var current = atomic.Read();
		while (true)
		{
			if (atomic.TryCompareExchange(Narrow<T, UInt64>(Widen<UInt64, T>(current) ^ Widen<UInt64, T>(value)), current, out var previous))
				return current;
			current = previous;
		}
	}

	/// <summary>Treats the cell's field as the first 4 bytes of the word it is stored in.</summary>
	/// <typeparam name="T">The enum type.</typeparam>
	/// <param name="value">The field to view.</param>
	/// <returns>The same storage, as an <see cref="Int32"/>.</returns>
	/// <remarks>
	/// Only use this on <c>Atomic&lt;T&gt;.Storage</c>, which always has a whole word behind it however
	/// narrow <typeparamref name="T"/> is. On any other 1- or 2-byte enum it would read past the end of the
	/// value, which is why masks are built with <see cref="Widen{TView,T}"/> instead.
	/// </remarks>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static ref Int32 AsInt32<T>(ref T value) where T : struct, Enum => ref Unsafe.As<T, Int32>(ref value);

	/// <summary>Treats a reference to an 8-byte enum as a reference to an <see cref="Int64"/>.</summary>
	/// <typeparam name="T">The enum type.</typeparam>
	/// <param name="value">The value to view.</param>
	/// <returns>The same storage, as an <see cref="Int64"/>.</returns>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static ref Int64 AsInt64<T>(ref T value) where T : struct, Enum => ref Unsafe.As<T, Int64>(ref value);

	/// <summary>Copies an enum into a wider view, zeroing the bytes it does not fill.</summary>
	/// <typeparam name="TView">The view to widen into.</typeparam>
	/// <typeparam name="T">The enum type.</typeparam>
	/// <param name="value">The value to widen.</param>
	/// <returns>The bits of the value, zero-extended to the width of the view.</returns>
	/// <remarks>
	/// <para>
	/// This copies bytes rather than converting numerically, so the value lands at the same offset in the
	/// view as it occupies in the cell's word. A numeric conversion would only line up where the
	/// low-order byte comes first in memory, which is not true of every platform .NET targets. It also
	/// zero-extends where a numeric conversion of a signed enum would sign-extend, and a sign-extended
	/// mask would set the spare bytes under <see cref="Or"/>.
	/// </para>
	/// <para>
	/// The copy is always in bounds: an enum is backed by an integral type, and the widest is 8 bytes.
	/// </para>
	/// </remarks>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static TView Widen<TView, T>(T value) where TView : unmanaged where T : struct, Enum
	{
		TView view = default;
		Unsafe.WriteUnaligned(ref Unsafe.As<TView, Byte>(ref view), value);
		return view;
	}

	/// <summary>Reads an enum back out of a view, ignoring the bytes it does not fill.</summary>
	/// <typeparam name="T">The enum type.</typeparam>
	/// <typeparam name="TView">The view to narrow from.</typeparam>
	/// <param name="view">A view produced by <see cref="Widen{TView,T}"/>.</param>
	/// <returns>The value stored in the view.</returns>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static T Narrow<T, TView>(TView view) where T : struct, Enum where TView : unmanaged =>
		Unsafe.ReadUnaligned<T>(ref Unsafe.As<TView, Byte>(ref view));
}
