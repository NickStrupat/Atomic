using System.Numerics;
using System.Runtime.CompilerServices;

namespace NickStrupat;

/// <summary>
/// Bitwise read-modify-write operations for enum values, which no operator interface admits.
/// </summary>
/// <remarks>
/// <para>
/// An enum satisfies none of the constraints <see cref="AtomicExtensions"/> is written against: it
/// implements no interfaces, so <see cref="IBitwiseOperators{TSelf,TOther,TResult}"/> is out of reach
/// however many operators C# lets you write against it. Flags in a cell is the ordinary reason to want
/// an atomic bitwise operation, so the gap was the wrong way round.
/// </para>
/// <para>
/// These are a separate class rather than more overloads because constraints are not part of a
/// signature, so a second <c>Or</c> beside the first is a duplicate member. In separate classes both
/// are candidates and the constraints settle it: an enum discards the operator one and a type with
/// operators discards this one, and nothing can satisfy both, an enum being unable to implement an
/// interface. The caller writes <c>cell.Or(flag)</c> either way and never names a type argument.
/// </para>
/// <para>
/// Where an instruction exists it is used, on the same terms as <see cref="AtomicExtensions"/>: only
/// where <c>Atomic&lt;T&gt;.IsInline</c> says the cell swaps the field itself. An enum is backed by an
/// integral type, so it is 1, 2, 4 or 8 bytes, and <see cref="Interlocked"/> covers only the last two —
/// but a value narrower than the word is kept in one anyway, with the slack behind it zeroed and
/// belonging to nobody. So 1 and 2 byte enums are masked through that word rather than looped over: a
/// mask zero extended from the value leaves the slack at zero under <see cref="Or"/> and puts it back
/// to zero under <see cref="And"/>, which is where the cell wants it either way. Only
/// <see cref="Xor"/> is always a loop, there being no interlocked exclusive or at any width.
/// </para>
/// <para>
/// Reinterpreting the storage with <see cref="Unsafe"/> rather than casting the cell through
/// <see cref="Object"/> is the one place that idiom cannot reach. <c>(Atomic&lt;Int32&gt;)(Object)</c> a
/// cell of an enum and the cast fails: generics are invariant and <c>Atomic&lt;Access&gt;</c> is not
/// <c>Atomic&lt;Int32&gt;</c>, whatever the enum is backed by. So the field is reinterpreted the way
/// <see cref="Atomic{T}"/> reinterprets it internally, under a size test rather than a type test.
/// </para>
/// <para>
/// The values returned follow <see cref="Interlocked"/>, as the operator-constrained ones do:
/// <see cref="And"/>, <see cref="Or"/> and <see cref="Xor"/> return the old value.
/// </para>
/// </remarks>
public static class AtomicEnumExtensions
{
	/// <summary>Replaces the value with its bitwise and against <paramref name="value"/>.</summary>
	/// <typeparam name="T">The type of the value held by the cell.</typeparam>
	/// <param name="atomic">The cell to mask.</param>
	/// <param name="value">The mask.</param>
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

	/// <summary>Replaces the value with its bitwise or against <paramref name="value"/>.</summary>
	/// <typeparam name="T">The type of the value held by the cell.</typeparam>
	/// <param name="atomic">The cell to set bits in.</param>
	/// <param name="value">The bits to set.</param>
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

	/// <summary>Replaces the value with its bitwise exclusive or against <paramref name="value"/>.</summary>
	/// <typeparam name="T">The type of the value held by the cell.</typeparam>
	/// <param name="atomic">The cell to flip bits in.</param>
	/// <param name="value">The bits to flip.</param>
	/// <returns>The old value, matching <see cref="And"/> and <see cref="Or"/>.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="atomic"/> is null.</exception>
	/// <remarks>
	/// A loop at every width, for the reason <see cref="AtomicExtensions"/> gives: arm64 has
	/// <c>ldeoral</c> and nothing in C# reaches it, <see cref="Interlocked"/> having no exclusive or.
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

	/// <summary>Views the cell's field as the low 4 bytes of the word it is seated in.</summary>
	/// <typeparam name="T">The enum type.</typeparam>
	/// <param name="value">The field to view.</param>
	/// <returns>The same storage, seen as an <see cref="Int32"/>.</returns>
	/// <remarks>
	/// Only ever pointed at <c>Atomic&lt;T&gt;.Storage</c>, which is a whole word however narrow
	/// <typeparamref name="T"/> is. Pointing it at a <typeparamref name="T"/> of its own would read past
	/// the end of one — which is why the masks are built by <see cref="Widen{TView,T}"/> instead.
	/// </remarks>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static ref Int32 AsInt32<T>(ref T value) where T : struct, Enum => ref Unsafe.As<T, Int32>(ref value);

	/// <summary>Views a reference to an enum as a reference to the 8 bytes it occupies.</summary>
	/// <typeparam name="T">The enum type.</typeparam>
	/// <param name="value">The value to view.</param>
	/// <returns>The same storage, seen as an <see cref="Int64"/>.</returns>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static ref Int64 AsInt64<T>(ref T value) where T : struct, Enum => ref Unsafe.As<T, Int64>(ref value);

	/// <summary>Widens an enum into a view, zeroing whatever it does not occupy.</summary>
	/// <typeparam name="TView">The view to widen into.</typeparam>
	/// <typeparam name="T">The enum type.</typeparam>
	/// <param name="value">The value to widen.</param>
	/// <returns>Its bits, zero extended to the width of the view.</returns>
	/// <remarks>
	/// A byte copy rather than a numeric conversion, so that the value lands at the same offset within
	/// the view that it occupies within the cell's word. A mask built by truncating a wider number would
	/// agree with the storage only where the low byte is also the first byte, which is not every runtime
	/// .NET targets. Always in bounds: an enum is backed by an integral type, and the widest of those is
	/// 8 bytes.
	/// </remarks>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static TView Widen<TView, T>(T value) where TView : unmanaged where T : struct, Enum
	{
		TView view = default;
		Unsafe.WriteUnaligned(ref Unsafe.As<TView, Byte>(ref view), value);
		return view;
	}

	/// <summary>Narrows a view back to an enum, ignoring the bits it does not occupy.</summary>
	/// <typeparam name="T">The enum type.</typeparam>
	/// <typeparam name="TView">The view to narrow from.</typeparam>
	/// <param name="view">A view previously produced by <see cref="Widen{TView,T}"/>.</param>
	/// <returns>The value those bits stand for.</returns>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static T Narrow<T, TView>(TView view) where T : struct, Enum where TView : unmanaged =>
		Unsafe.ReadUnaligned<T>(ref Unsafe.As<TView, Byte>(ref view));
}
