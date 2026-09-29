using System.Numerics;

namespace NickStrupat;

/// <summary>
/// Atomic read-modify-write operations — add, subtract, increment, decrement, and, or, xor, max and
/// min — for cells whose type supports the matching operator.
/// </summary>
/// <remarks>
/// <para>
/// Each operation picks one of three implementations. The choice depends only on the type, so the
/// compiler folds it away and each instantiation contains exactly one of them:
/// </para>
/// <list type="number">
/// <item><description>
/// A single <see cref="Interlocked"/> instruction. Used by <see cref="Add"/>, <see cref="Subtract"/>,
/// <see cref="Increment"/>, <see cref="Decrement"/>, <see cref="And"/> and <see cref="Or"/> when
/// <c>T</c> is <see cref="Int32"/>, <see cref="Int64"/>, <see cref="UInt32"/> or
/// <see cref="UInt64"/> and the cell swaps its value in place. This holds at 32 bits as well: the
/// 4-byte types go through the word and the 8-byte types through the same locked 8-byte instructions
/// the cell itself uses.
/// </description></item>
/// <item><description>
/// A lock on the cell, when the cell keeps its value behind its lock (<c>!Atomic&lt;T&gt;.IsLockFree</c>).
/// The operator runs once, directly on the stored value, while the lock is held.
/// </description></item>
/// <item><description>
/// A compare-exchange loop for everything else. Uncontended it costs nothing measurable, because the
/// first attempt succeeds. Under contention every writer that gets in first forces a retry, which an
/// instruction would not.
/// </description></item>
/// </list>
/// <para>
/// The instruction is only used when the cell's strategy allows it, not just when the type matches.
/// Each of the six checks <c>Atomic&lt;T&gt;.IsInline</c> first. Without that check, an
/// <c>Interlocked.Increment</c> on a field the cell guards with its lock would be a second writer the lock
/// knows nothing about, and a concurrent <see cref="Atomic{T}.CompareExchange"/> could read, compare and
/// store across the increment and lose it. The check is a constant, so it costs nothing, and because it
/// asks the cell rather than restating a word size, it stays correct if the strategy changes.
/// </para>
/// <para>
/// The lock path exists because a loop is the wrong tool there. Each attempt would take the lock twice —
/// once to read, once to compare-exchange — and run the operator in between with nothing held, so any
/// writer landing in that gap forces another round. Holding the lock throughout means the operator runs
/// once, nothing is compared, and other writers wait instead of causing retries. The check folds like
/// the others, so lock-free instantiations carry no <c>try</c>/<c>finally</c>. The trade-off is that the
/// type's operator runs under the lock, just as its <c>Equals</c> already does inside compare-exchange: an
/// operator that takes another lock can deadlock with code taking the two in the opposite order.
/// </para>
/// <para>
/// To reach the instruction, the code needs a <c>ref Int32</c> (say) to the storage. Inside the
/// <c>typeof(T) == typeof(Int32)</c> test the cell already is an <c>Atomic&lt;Int32&gt;</c>; the compiler
/// just does not know it. Casting the cell through <see cref="Object"/> tells it, and <c>Storage</c> on the
/// result is then an ordinary <c>ref Int32</c>. Values are cast through <see cref="Object"/> the same way.
/// All of these casts disappear along with the test: the reference cast compiles to nothing, and the
/// boxing and unboxing of values is removed before any code is emitted, so nothing here allocates.
/// </para>
/// <para>
/// This replaces an earlier set of overloads written against the closed types
/// (<c>Atomic&lt;Int64&gt;</c> and so on). Those only helped callers who wrote the concrete type:
/// overload resolution happens where the call is written, and in generic code <c>T</c> is still a type
/// parameter there, so generic callers always got the loop. Specialising inside the generic method
/// covers both kinds of caller.
/// </para>
/// <para>
/// Return values follow <see cref="Interlocked"/>, including its inconsistency: <see cref="Add"/>,
/// <see cref="Subtract"/>, <see cref="Increment"/> and <see cref="Decrement"/> return the new value, while
/// <see cref="And"/>, <see cref="Or"/> and <see cref="Xor"/> return the old one.
/// </para>
/// </remarks>
public static class AtomicExtensions
{
	/// <summary>Atomically adds <paramref name="addend"/> to the value.</summary>
	/// <typeparam name="T">The type of the value held by the cell.</typeparam>
	/// <param name="atomic">The cell to add to.</param>
	/// <param name="addend">The value to add.</param>
	/// <returns>The new value, as <see cref="Interlocked.Add(ref Int32, Int32)"/> does.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="atomic"/> is null.</exception>
	public static T Add<T>(this Atomic<T> atomic, T addend)
	where T : IAdditionOperators<T, T, T>
	{
		ArgumentNullException.ThrowIfNull(atomic);

		if (Atomic<T>.IsInline)
		{
			if (typeof(T) == typeof(Int32))
				return (T)(Object)Interlocked.Add(ref ((Atomic<Int32>)(Object)atomic).Storage, (Int32)(Object)addend);
			if (typeof(T) == typeof(Int64))
				return (T)(Object)Interlocked.Add(ref ((Atomic<Int64>)(Object)atomic).Storage, (Int64)(Object)addend);
			if (typeof(T) == typeof(UInt32))
				return (T)(Object)Interlocked.Add(ref ((Atomic<UInt32>)(Object)atomic).Storage, (UInt32)(Object)addend);
			if (typeof(T) == typeof(UInt64))
				return (T)(Object)Interlocked.Add(ref ((Atomic<UInt64>)(Object)atomic).Storage, (UInt64)(Object)addend);
		}

		if (!Atomic<T>.IsLockFree)
			lock (atomic)
				return atomic.Storage += addend;

		var current = atomic.Read();
		while (true)
		{
			var next = current + addend;
			if (atomic.TryCompareExchange(next, current, out var previous))
				return next;
			current = previous;
		}
	}

	/// <summary>Atomically subtracts <paramref name="subtrahend"/> from the value.</summary>
	/// <typeparam name="T">The type of the value held by the cell.</typeparam>
	/// <param name="atomic">The cell to subtract from.</param>
	/// <param name="subtrahend">The value to subtract.</param>
	/// <returns>The new value.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="atomic"/> is null.</exception>
	public static T Subtract<T>(this Atomic<T> atomic, T subtrahend)
	where T : ISubtractionOperators<T, T, T>
	{
		ArgumentNullException.ThrowIfNull(atomic);

		if (Atomic<T>.IsInline)
		{
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
		}

		if (!Atomic<T>.IsLockFree)
			lock (atomic)
				return atomic.Storage -= subtrahend;

		var current = atomic.Read();
		while (true)
		{
			var next = current - subtrahend;
			if (atomic.TryCompareExchange(next, current, out var previous))
				return next;
			current = previous;
		}
	}

	/// <summary>Atomically adds one to the value.</summary>
	/// <typeparam name="T">The type of the value held by the cell.</typeparam>
	/// <param name="atomic">The cell to increment.</param>
	/// <returns>The new value, as <see cref="Interlocked.Increment(ref Int32)"/> does.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="atomic"/> is null.</exception>
	public static T Increment<T>(this Atomic<T> atomic)
	where T : IIncrementOperators<T>
	{
		ArgumentNullException.ThrowIfNull(atomic);

		if (Atomic<T>.IsInline)
		{
			if (typeof(T) == typeof(Int32))
				return (T)(Object)Interlocked.Increment(ref ((Atomic<Int32>)(Object)atomic).Storage);
			if (typeof(T) == typeof(Int64))
				return (T)(Object)Interlocked.Increment(ref ((Atomic<Int64>)(Object)atomic).Storage);
			if (typeof(T) == typeof(UInt32))
				return (T)(Object)Interlocked.Increment(ref ((Atomic<UInt32>)(Object)atomic).Storage);
			if (typeof(T) == typeof(UInt64))
				return (T)(Object)Interlocked.Increment(ref ((Atomic<UInt64>)(Object)atomic).Storage);
		}

		if (!Atomic<T>.IsLockFree)
			lock (atomic)
				return ++atomic.Storage;

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

	/// <summary>Atomically subtracts one from the value.</summary>
	/// <typeparam name="T">The type of the value held by the cell.</typeparam>
	/// <param name="atomic">The cell to decrement.</param>
	/// <returns>The new value, as <see cref="Interlocked.Decrement(ref Int32)"/> does.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="atomic"/> is null.</exception>
	public static T Decrement<T>(this Atomic<T> atomic)
	where T : IDecrementOperators<T>
	{
		ArgumentNullException.ThrowIfNull(atomic);

		if (Atomic<T>.IsInline)
		{
			if (typeof(T) == typeof(Int32))
				return (T)(Object)Interlocked.Decrement(ref ((Atomic<Int32>)(Object)atomic).Storage);
			if (typeof(T) == typeof(Int64))
				return (T)(Object)Interlocked.Decrement(ref ((Atomic<Int64>)(Object)atomic).Storage);
			if (typeof(T) == typeof(UInt32))
				return (T)(Object)Interlocked.Decrement(ref ((Atomic<UInt32>)(Object)atomic).Storage);
			if (typeof(T) == typeof(UInt64))
				return (T)(Object)Interlocked.Decrement(ref ((Atomic<UInt64>)(Object)atomic).Storage);
		}

		if (!Atomic<T>.IsLockFree)
			lock (atomic)
				return --atomic.Storage;

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

	/// <summary>Atomically clears every bit not set in <paramref name="value"/> (a bitwise AND).</summary>
	/// <typeparam name="T">The type of the value held by the cell.</typeparam>
	/// <param name="atomic">The cell to clear bits in.</param>
	/// <param name="value">The bits to keep.</param>
	/// <returns>The old value, as <see cref="Interlocked.And(ref Int32, Int32)"/> does.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="atomic"/> is null.</exception>
	public static T And<T>(this Atomic<T> atomic, T value)
	where T : IBitwiseOperators<T, T, T>
	{
		ArgumentNullException.ThrowIfNull(atomic);

		if (Atomic<T>.IsInline)
		{
			if (typeof(T) == typeof(Int32))
				return (T)(Object)Interlocked.And(ref ((Atomic<Int32>)(Object)atomic).Storage, (Int32)(Object)value);
			if (typeof(T) == typeof(Int64))
				return (T)(Object)Interlocked.And(ref ((Atomic<Int64>)(Object)atomic).Storage, (Int64)(Object)value);
			if (typeof(T) == typeof(UInt32))
				return (T)(Object)Interlocked.And(ref ((Atomic<UInt32>)(Object)atomic).Storage, (UInt32)(Object)value);
			if (typeof(T) == typeof(UInt64))
				return (T)(Object)Interlocked.And(ref ((Atomic<UInt64>)(Object)atomic).Storage, (UInt64)(Object)value);
		}

		if (!Atomic<T>.IsLockFree)
			lock (atomic)
			{
				ref var storage = ref atomic.Storage;
				var previous = storage;
				storage = previous & value;
				return previous;
			}

		var current = atomic.Read();
		while (true)
		{
			if (atomic.TryCompareExchange(current & value, current, out var previous))
				return current;
			current = previous;
		}
	}

	/// <summary>Atomically sets every bit that is set in <paramref name="value"/> (a bitwise OR).</summary>
	/// <typeparam name="T">The type of the value held by the cell.</typeparam>
	/// <param name="atomic">The cell to set bits in.</param>
	/// <param name="value">The bits to set.</param>
	/// <returns>The old value, as <see cref="Interlocked.Or(ref Int32, Int32)"/> does.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="atomic"/> is null.</exception>
	public static T Or<T>(this Atomic<T> atomic, T value)
	where T : IBitwiseOperators<T, T, T>
	{
		ArgumentNullException.ThrowIfNull(atomic);

		if (Atomic<T>.IsInline)
		{
			if (typeof(T) == typeof(Int32))
				return (T)(Object)Interlocked.Or(ref ((Atomic<Int32>)(Object)atomic).Storage, (Int32)(Object)value);
			if (typeof(T) == typeof(Int64))
				return (T)(Object)Interlocked.Or(ref ((Atomic<Int64>)(Object)atomic).Storage, (Int64)(Object)value);
			if (typeof(T) == typeof(UInt32))
				return (T)(Object)Interlocked.Or(ref ((Atomic<UInt32>)(Object)atomic).Storage, (UInt32)(Object)value);
			if (typeof(T) == typeof(UInt64))
				return (T)(Object)Interlocked.Or(ref ((Atomic<UInt64>)(Object)atomic).Storage, (UInt64)(Object)value);
		}

		if (!Atomic<T>.IsLockFree)
			lock (atomic)
			{
				ref var storage = ref atomic.Storage;
				var previous = storage;
				storage = previous | value;
				return previous;
			}

		var current = atomic.Read();
		while (true)
		{
			if (atomic.TryCompareExchange(current | value, current, out var previous))
				return current;
			current = previous;
		}
	}

	/// <summary>Atomically flips every bit that is set in <paramref name="value"/> (a bitwise XOR).</summary>
	/// <typeparam name="T">The type of the value held by the cell.</typeparam>
	/// <param name="atomic">The cell to flip bits in.</param>
	/// <param name="value">The bits to flip.</param>
	/// <returns>The old value, like <see cref="And"/> and <see cref="Or"/>.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="atomic"/> is null.</exception>
	public static T Xor<T>(this Atomic<T> atomic, T value)
	where T : IBitwiseOperators<T, T, T>
	{
		ArgumentNullException.ThrowIfNull(atomic);

		if (!Atomic<T>.IsLockFree)
			lock (atomic)
			{
				ref var storage = ref atomic.Storage;
				var previous = storage;
				storage = previous ^ value;
				return previous;
			}

		var current = atomic.Read();
		while (true)
		{
			if (atomic.TryCompareExchange(current ^ value, current, out var previous))
				return current;
			current = previous;
		}
	}

	/// <summary>Atomically sets the value to <paramref name="value"/> if the current value is lower.</summary>
	/// <typeparam name="T">The type of the value held by the cell.</typeparam>
	/// <param name="atomic">The cell to raise.</param>
	/// <param name="value">The lowest value the cell should hold afterwards.</param>
	/// <returns>The value held afterwards, whether or not this call changed it.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="atomic"/> is null.</exception>
	public static T Max<T>(this Atomic<T> atomic, T value)
	where T : IComparisonOperators<T, T, Boolean>
	{
		ArgumentNullException.ThrowIfNull(atomic);

		if (!Atomic<T>.IsLockFree)
			lock (atomic)
			{
				ref var storage = ref atomic.Storage;
				if (storage >= value)
					return storage;
				return storage = value;
			}

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

	/// <summary>Atomically sets the value to <paramref name="value"/> if the current value is higher.</summary>
	/// <typeparam name="T">The type of the value held by the cell.</typeparam>
	/// <param name="atomic">The cell to lower.</param>
	/// <param name="value">The highest value the cell should hold afterwards.</param>
	/// <returns>The value held afterwards, whether or not this call changed it.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="atomic"/> is null.</exception>
	public static T Min<T>(this Atomic<T> atomic, T value)
	where T : IComparisonOperators<T, T, Boolean>
	{
		ArgumentNullException.ThrowIfNull(atomic);

		if (!Atomic<T>.IsLockFree)
			lock (atomic)
			{
				ref var storage = ref atomic.Storage;
				if (storage <= value)
					return storage;
				return storage = value;
			}

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
