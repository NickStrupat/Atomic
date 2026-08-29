using System.Numerics;

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
/// Where an instruction does exist, these use it. <see cref="Add"/>, <see cref="Subtract"/>,
/// <see cref="Increment"/>, <see cref="Decrement"/>, <see cref="And"/> and <see cref="Or"/> test
/// <c>typeof(T)</c> against the
/// four integers <see cref="Interlocked"/> covers, and the JIT folds that test when it specialises the
/// method — so an instantiation with an instruction is the instruction and nothing else, and one
/// without is the loop and nothing else. Neither carries the test.
/// </para>
/// <para>
/// Getting at the instruction means getting a reference of the right type to the storage, and under
/// the test the cell is that type already — it is only the compiler that has not been told. Casting
/// the cell through <see cref="Object"/> tells it, and taking <c>Storage</c> from the result is then
/// an ordinary <c>ref Int32</c>. The values cross the same way. Every one of those casts folds away
/// with the test that guards it: the reference cast to nothing at all, and the box and unbox around
/// the values removed before they are ever emitted, so nothing here allocates.
/// </para>
/// <para>
/// This used to be a second set of extensions declared against the closed types, which won overload
/// resolution and so got the instruction to anyone who named <c>Atomic&lt;Int64&gt;</c> outright. It
/// could not help generic code, because overload resolution happens where the type is written down and
/// there it is still a parameter — so every <c>T</c> paid for a loop. Specialising here covers both,
/// and the closed set was two hundred lines saying the same thing a second time.
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

		if (typeof(T) == typeof(Int32))
			return (T)(Object)Interlocked.Add(ref ((Atomic<Int32>)(Object)atomic).Storage, (Int32)(Object)addend);
		if (typeof(T) == typeof(Int64))
			return (T)(Object)Interlocked.Add(ref ((Atomic<Int64>)(Object)atomic).Storage, (Int64)(Object)addend);
		if (typeof(T) == typeof(UInt32))
			return (T)(Object)Interlocked.Add(ref ((Atomic<UInt32>)(Object)atomic).Storage, (UInt32)(Object)addend);
		if (typeof(T) == typeof(UInt64))
			return (T)(Object)Interlocked.Add(ref ((Atomic<UInt64>)(Object)atomic).Storage, (UInt64)(Object)addend);

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

		// There is no interlocked subtract, but adding the two's complement negation is the same
		// operation on these types, including where the negation itself overflows: negating Int32.MinValue
		// gives Int32.MinValue back, and adding that is subtracting it. The unsigned pair are written as a
		// subtraction from zero because unary minus on them widens to a signed type first.
		if (typeof(T) == typeof(Int32))
		{
			var negated = unchecked(-(Int32)(Object)subtrahend);
			return (T)(Object)Interlocked.Add(ref ((Atomic<Int32>)(Object)atomic).Storage, negated);
		}
		if (typeof(T) == typeof(Int64))
		{
			var negated = unchecked(-(Int64)(Object)subtrahend);
			return (T)(Object)Interlocked.Add(ref ((Atomic<Int64>)(Object)atomic).Storage, negated);
		}
		if (typeof(T) == typeof(UInt32))
		{
			var negated = unchecked(0U - (UInt32)(Object)subtrahend);
			return (T)(Object)Interlocked.Add(ref ((Atomic<UInt32>)(Object)atomic).Storage, negated);
		}
		if (typeof(T) == typeof(UInt64))
		{
			var negated = unchecked(0UL - (UInt64)(Object)subtrahend);
			return (T)(Object)Interlocked.Add(ref ((Atomic<UInt64>)(Object)atomic).Storage, negated);
		}

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

		if (typeof(T) == typeof(Int32))
			return (T)(Object)Interlocked.Increment(ref ((Atomic<Int32>)(Object)atomic).Storage);
		if (typeof(T) == typeof(Int64))
			return (T)(Object)Interlocked.Increment(ref ((Atomic<Int64>)(Object)atomic).Storage);
		if (typeof(T) == typeof(UInt32))
			return (T)(Object)Interlocked.Increment(ref ((Atomic<UInt32>)(Object)atomic).Storage);
		if (typeof(T) == typeof(UInt64))
			return (T)(Object)Interlocked.Increment(ref ((Atomic<UInt64>)(Object)atomic).Storage);

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

		if (typeof(T) == typeof(Int32))
			return (T)(Object)Interlocked.Decrement(ref ((Atomic<Int32>)(Object)atomic).Storage);
		if (typeof(T) == typeof(Int64))
			return (T)(Object)Interlocked.Decrement(ref ((Atomic<Int64>)(Object)atomic).Storage);
		if (typeof(T) == typeof(UInt32))
			return (T)(Object)Interlocked.Decrement(ref ((Atomic<UInt32>)(Object)atomic).Storage);
		if (typeof(T) == typeof(UInt64))
			return (T)(Object)Interlocked.Decrement(ref ((Atomic<UInt64>)(Object)atomic).Storage);

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

		if (typeof(T) == typeof(Int32))
			return (T)(Object)Interlocked.And(ref ((Atomic<Int32>)(Object)atomic).Storage, (Int32)(Object)value);
		if (typeof(T) == typeof(Int64))
			return (T)(Object)Interlocked.And(ref ((Atomic<Int64>)(Object)atomic).Storage, (Int64)(Object)value);
		if (typeof(T) == typeof(UInt32))
			return (T)(Object)Interlocked.And(ref ((Atomic<UInt32>)(Object)atomic).Storage, (UInt32)(Object)value);
		if (typeof(T) == typeof(UInt64))
			return (T)(Object)Interlocked.And(ref ((Atomic<UInt64>)(Object)atomic).Storage, (UInt64)(Object)value);

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

		if (typeof(T) == typeof(Int32))
			return (T)(Object)Interlocked.Or(ref ((Atomic<Int32>)(Object)atomic).Storage, (Int32)(Object)value);
		if (typeof(T) == typeof(Int64))
			return (T)(Object)Interlocked.Or(ref ((Atomic<Int64>)(Object)atomic).Storage, (Int64)(Object)value);
		if (typeof(T) == typeof(UInt32))
			return (T)(Object)Interlocked.Or(ref ((Atomic<UInt32>)(Object)atomic).Storage, (UInt32)(Object)value);
		if (typeof(T) == typeof(UInt64))
			return (T)(Object)Interlocked.Or(ref ((Atomic<UInt64>)(Object)atomic).Storage, (UInt64)(Object)value);

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
}
