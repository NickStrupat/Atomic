using System.Numerics;

namespace NickStrupat;

/// <summary>
/// Read-modify-write operations for the values that support them.
/// </summary>
/// <remarks>
/// <para>
/// Each of these is a compare-and-exchange loop rather than a single instruction: nothing here knows
/// the shape of the value well enough to hand a reference to <c>Interlocked.Add</c>, so
/// the loop is what remains. Uncontended that costs nothing measurable, because the first comparison
/// succeeds; contended it costs a retry for every writer that lands first, where a native instruction
/// would not have to retry at all.
/// </para>
/// <para>
/// Where a native instruction does exist — <see cref="Atomic{T}"/> of <see cref="Int32"/>,
/// <see cref="Int64"/>, <see cref="UInt32"/> or <see cref="UInt64"/> —
/// <see cref="AtomicInterlockedExtensions"/> declares the same names against the closed type. Those win
/// overload resolution against these open ones, so a caller gets the instruction without asking for it.
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
