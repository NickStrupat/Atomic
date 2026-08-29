using System.Numerics;
using System.Runtime.CompilerServices;

namespace NickStrupat;

/// <summary>
/// Read-modify-write operations for the values that support them.
/// </summary>
/// <remarks>
/// <para>
/// Most of these are a compare-and-exchange loop, because nothing here knows the shape of the value
/// well enough to hand a reference to <c>Interlocked.Add</c>. Uncontended that costs nothing
/// measurable, because the first comparison succeeds; contended it costs a retry for every writer that
/// lands first, where a native instruction would not have to retry at all.
/// </para>
/// <para>
/// Where an instruction does exist, these use it. <see cref="Add"/>, <see cref="Increment"/>,
/// <see cref="Decrement"/>, <see cref="And"/> and <see cref="Or"/> test <c>typeof(T)</c> against the
/// four integers <see cref="Interlocked"/> covers, and the JIT folds that test when it specialises the
/// method — so an instantiation with an instruction is the instruction and nothing else, and one
/// without is the loop and nothing else. Neither carries the test.
/// </para>
/// <para>
/// <see cref="AtomicInterlockedExtensions"/> declares the same five against the closed types and wins
/// overload resolution against these. That used to be what got a caller the instruction. It no longer
/// decides anything, because both spellings arrive at the same place — and the difference matters in
/// the other direction: overload resolution can only pick the closed method where the type is written
/// down, while this specialisation applies wherever the JIT knows it. Generic code holding an
/// <see cref="Atomic{T}"/> can only reach these, and paid a loop for every <c>T</c> before it existed.
/// </para>
/// <para>
/// The values returned follow <see cref="Interlocked"/>, including where it is inconsistent:
/// <see cref="Add"/>, <see cref="Increment"/> and <see cref="Decrement"/> return the new value, while
/// <see cref="And"/> and <see cref="Or"/> return the old one.
/// </para>
/// </remarks>
public static class AtomicExtensions
{
	/// <summary>Applies <paramref name="update"/> to the value until it lands.</summary>
	/// <typeparam name="T">The type of the value held by the cell.</typeparam>
	/// <param name="atomic">The cell to update.</param>
	/// <param name="update">
	/// Produces the new value from the current one. It may run more than once, so it should be cheap
	/// and free of side effects.
	/// </param>
	/// <returns>The value stored.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="atomic"/> or <paramref name="update"/> is null.</exception>
	public static T Update<T>(this Atomic<T> atomic, Func<T, T> update)

	{
		ArgumentNullException.ThrowIfNull(atomic);
		ArgumentNullException.ThrowIfNull(update);

		var current = atomic.Read();
		while (true)
		{
			var next = update(current);
			if (atomic.TryCompareExchange(next, current, out var previous))
				return next;
			current = previous;
		}
	}

	/// <summary>Applies <paramref name="update"/> to the value until it lands, without capturing.</summary>
	/// <typeparam name="TState">The type of the state handed to <paramref name="update"/>.</typeparam>
	/// <typeparam name="T">The type of the value held by the cell.</typeparam>
	/// <param name="atomic">The cell to update.</param>
	/// <param name="state">Handed to <paramref name="update"/> on every attempt.</param>
	/// <param name="update">
	/// Produces the new value from the state and the current value. It may run more than once, so it
	/// should be cheap and free of side effects.
	/// </param>
	/// <returns>The value stored.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="atomic"/> or <paramref name="update"/> is null.</exception>
	public static T Update<TState, T>(this Atomic<T> atomic, TState state, Func<TState, T, T> update)

	{
		ArgumentNullException.ThrowIfNull(atomic);
		ArgumentNullException.ThrowIfNull(update);

		var current = atomic.Read();
		while (true)
		{
			var next = update(state, current);
			if (atomic.TryCompareExchange(next, current, out var previous))
				return next;
			current = previous;
		}
	}

	/// <summary>Adds <paramref name="addend"/> to the value.</summary>
	/// <typeparam name="T">The type of the value held by the cell.</typeparam>
	/// <param name="atomic">The cell to add to.</param>
	/// <param name="addend">The value to add.</param>
	/// <returns>The new value, as <see cref="Interlocked.Add(ref Int32, Int32)"/> does.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="atomic"/> is null.</exception>
	public static T Add<T>(this Atomic<T> atomic, T addend)
	where T : IAdditionOperators<T, T, T>
	{
		ArgumentNullException.ThrowIfNull(atomic);

		if (typeof(T) == typeof(Int32)) return Reinterpret<Int32, T>(Interlocked.Add(ref Native<T, Int32>(atomic), Reinterpret<T, Int32>(addend)));
		if (typeof(T) == typeof(Int64)) return Reinterpret<Int64, T>(Interlocked.Add(ref Native<T, Int64>(atomic), Reinterpret<T, Int64>(addend)));
		if (typeof(T) == typeof(UInt32)) return Reinterpret<UInt32, T>(Interlocked.Add(ref Native<T, UInt32>(atomic), Reinterpret<T, UInt32>(addend)));
		if (typeof(T) == typeof(UInt64)) return Reinterpret<UInt64, T>(Interlocked.Add(ref Native<T, UInt64>(atomic), Reinterpret<T, UInt64>(addend)));

		var current = atomic.Read();
		while (true)
		{
			var next = current + addend;
			if (atomic.TryCompareExchange(next, current, out var previous))
				return next;
			current = previous;
		}
	}

	/// <summary>Subtracts <paramref name="subtrahend"/> from the value.</summary>
	/// <typeparam name="T">The type of the value held by the cell.</typeparam>
	/// <param name="atomic">The cell to subtract from.</param>
	/// <param name="subtrahend">The value to subtract.</param>
	/// <returns>The new value.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="atomic"/> is null.</exception>
	public static T Subtract<T>(this Atomic<T> atomic, T subtrahend)
	where T : ISubtractionOperators<T, T, T>
	{
		ArgumentNullException.ThrowIfNull(atomic);

		var current = atomic.Read();
		while (true)
		{
			var next = current - subtrahend;
			if (atomic.TryCompareExchange(next, current, out var previous))
				return next;
			current = previous;
		}
	}

	/// <summary>Increments the value.</summary>
	/// <typeparam name="T">The type of the value held by the cell.</typeparam>
	/// <param name="atomic">The cell to increment.</param>
	/// <returns>The new value, as <see cref="Interlocked.Increment(ref Int32)"/> does.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="atomic"/> is null.</exception>
	public static T Increment<T>(this Atomic<T> atomic)
	where T : IIncrementOperators<T>
	{
		ArgumentNullException.ThrowIfNull(atomic);

		if (typeof(T) == typeof(Int32)) return Reinterpret<Int32, T>(Interlocked.Increment(ref Native<T, Int32>(atomic)));
		if (typeof(T) == typeof(Int64)) return Reinterpret<Int64, T>(Interlocked.Increment(ref Native<T, Int64>(atomic)));
		if (typeof(T) == typeof(UInt32)) return Reinterpret<UInt32, T>(Interlocked.Increment(ref Native<T, UInt32>(atomic)));
		if (typeof(T) == typeof(UInt64)) return Reinterpret<UInt64, T>(Interlocked.Increment(ref Native<T, UInt64>(atomic)));

		var current = atomic.Read();
		while (true)
		{
			var next = current;
			next++;
			if (atomic.TryCompareExchange(next, current, out var previous))
				return next;
			current = previous;
		}
	}

	/// <summary>Decrements the value.</summary>
	/// <typeparam name="T">The type of the value held by the cell.</typeparam>
	/// <param name="atomic">The cell to decrement.</param>
	/// <returns>The new value, as <see cref="Interlocked.Decrement(ref Int32)"/> does.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="atomic"/> is null.</exception>
	public static T Decrement<T>(this Atomic<T> atomic)
	where T : IDecrementOperators<T>
	{
		ArgumentNullException.ThrowIfNull(atomic);

		if (typeof(T) == typeof(Int32)) return Reinterpret<Int32, T>(Interlocked.Decrement(ref Native<T, Int32>(atomic)));
		if (typeof(T) == typeof(Int64)) return Reinterpret<Int64, T>(Interlocked.Decrement(ref Native<T, Int64>(atomic)));
		if (typeof(T) == typeof(UInt32)) return Reinterpret<UInt32, T>(Interlocked.Decrement(ref Native<T, UInt32>(atomic)));
		if (typeof(T) == typeof(UInt64)) return Reinterpret<UInt64, T>(Interlocked.Decrement(ref Native<T, UInt64>(atomic)));

		var current = atomic.Read();
		while (true)
		{
			var next = current;
			next--;
			if (atomic.TryCompareExchange(next, current, out var previous))
				return next;
			current = previous;
		}
	}

	/// <summary>Replaces the value with its bitwise and against <paramref name="value"/>.</summary>
	/// <typeparam name="T">The type of the value held by the cell.</typeparam>
	/// <param name="atomic">The cell to mask.</param>
	/// <param name="value">The mask.</param>
	/// <returns>The old value, as <see cref="Interlocked.And(ref Int32, Int32)"/> does.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="atomic"/> is null.</exception>
	public static T And<T>(this Atomic<T> atomic, T value)
	where T : IBitwiseOperators<T, T, T>
	{
		ArgumentNullException.ThrowIfNull(atomic);

		if (typeof(T) == typeof(Int32)) return Reinterpret<Int32, T>(Interlocked.And(ref Native<T, Int32>(atomic), Reinterpret<T, Int32>(value)));
		if (typeof(T) == typeof(Int64)) return Reinterpret<Int64, T>(Interlocked.And(ref Native<T, Int64>(atomic), Reinterpret<T, Int64>(value)));
		if (typeof(T) == typeof(UInt32)) return Reinterpret<UInt32, T>(Interlocked.And(ref Native<T, UInt32>(atomic), Reinterpret<T, UInt32>(value)));
		if (typeof(T) == typeof(UInt64)) return Reinterpret<UInt64, T>(Interlocked.And(ref Native<T, UInt64>(atomic), Reinterpret<T, UInt64>(value)));

		var current = atomic.Read();
		while (true)
		{
			if (atomic.TryCompareExchange(current & value, current, out var previous))
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
	public static T Or<T>(this Atomic<T> atomic, T value)
	where T : IBitwiseOperators<T, T, T>
	{
		ArgumentNullException.ThrowIfNull(atomic);

		if (typeof(T) == typeof(Int32)) return Reinterpret<Int32, T>(Interlocked.Or(ref Native<T, Int32>(atomic), Reinterpret<T, Int32>(value)));
		if (typeof(T) == typeof(Int64)) return Reinterpret<Int64, T>(Interlocked.Or(ref Native<T, Int64>(atomic), Reinterpret<T, Int64>(value)));
		if (typeof(T) == typeof(UInt32)) return Reinterpret<UInt32, T>(Interlocked.Or(ref Native<T, UInt32>(atomic), Reinterpret<T, UInt32>(value)));
		if (typeof(T) == typeof(UInt64)) return Reinterpret<UInt64, T>(Interlocked.Or(ref Native<T, UInt64>(atomic), Reinterpret<T, UInt64>(value)));

		var current = atomic.Read();
		while (true)
		{
			if (atomic.TryCompareExchange(current | value, current, out var previous))
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
	public static T Xor<T>(this Atomic<T> atomic, T value)
	where T : IBitwiseOperators<T, T, T>
	{
		ArgumentNullException.ThrowIfNull(atomic);

		var current = atomic.Read();
		while (true)
		{
			if (atomic.TryCompareExchange(current ^ value, current, out var previous))
				return current;
			current = previous;
		}
	}

	/// <summary>Raises the value to <paramref name="value"/> if it is below it.</summary>
	/// <typeparam name="T">The type of the value held by the cell.</typeparam>
	/// <param name="atomic">The cell to raise.</param>
	/// <param name="value">The floor to raise the value to.</param>
	/// <returns>The value held afterwards, whether or not this call is what put it there.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="atomic"/> is null.</exception>
	public static T Max<T>(this Atomic<T> atomic, T value)
	where T : IComparisonOperators<T, T, Boolean>
	{
		ArgumentNullException.ThrowIfNull(atomic);

		var current = atomic.Read();
		while (true)
		{
			if (current >= value)
				return current;
			if (atomic.TryCompareExchange(value, current, out var previous))
				return value;
			current = previous;
		}
	}

	/// <summary>Lowers the value to <paramref name="value"/> if it is above it.</summary>
	/// <typeparam name="T">The type of the value held by the cell.</typeparam>
	/// <param name="atomic">The cell to lower.</param>
	/// <param name="value">The ceiling to lower the value to.</param>
	/// <returns>The value held afterwards, whether or not this call is what put it there.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="atomic"/> is null.</exception>
	public static T Min<T>(this Atomic<T> atomic, T value)
	where T : IComparisonOperators<T, T, Boolean>
	{
		ArgumentNullException.ThrowIfNull(atomic);

		var current = atomic.Read();
		while (true)
		{
			if (current <= value)
				return current;
			if (atomic.TryCompareExchange(value, current, out var previous))
				return value;
			current = previous;
		}
	}

	/// <summary>The cell's storage, seen as the type an interlocked instruction acts on.</summary>
	/// <typeparam name="T">The type of the value held by the cell.</typeparam>
	/// <typeparam name="TNative">The type the instruction acts on, which <typeparamref name="T"/> is.</typeparam>
	/// <param name="atomic">The cell whose storage to take a reference to.</param>
	/// <returns>A reference to the storage.</returns>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static ref TNative Native<T, TNative>(Atomic<T> atomic) =>
		ref Unsafe.As<T, TNative>(ref atomic.Storage);

	/// <summary>Reinterprets a value as the type it already is, which the compiler cannot be told.</summary>
	/// <typeparam name="TFrom">The type the value is held as.</typeparam>
	/// <typeparam name="TTo">The type it is being read as, which is the same type.</typeparam>
	/// <param name="value">The value to reinterpret.</param>
	/// <returns>The same value, typed the other way.</returns>
	/// <remarks>
	/// Only ever reached under a <c>typeof</c> test that has already established the two are the same
	/// type. A cast would box; this compiles to nothing.
	/// </remarks>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static TTo Reinterpret<TFrom, TTo>(TFrom value) => Unsafe.As<TFrom, TTo>(ref value);
}
