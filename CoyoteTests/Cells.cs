using NickStrupat;

namespace CoyoteTests;

/// <summary>Makes cells of one implementation, for any type of value.</summary>
internal interface ICells
{
	/// <summary>The implementation's name, for the report.</summary>
	String Name { get; }

	/// <summary>Whether the implementation is meant to be thread-safe, and so expected to pass.</summary>
	Boolean IsThreadSafe { get; }

	/// <summary>Creates a cell holding <paramref name="value"/>.</summary>
	/// <typeparam name="T">The type of the value held by the cell.</typeparam>
	/// <param name="value">The initial value.</param>
	/// <returns>A new cell.</returns>
	IAtomic<T> Create<T>(T value);
}

/// <summary>The implementations explored: the shipping cell, both candidates, and a broken one.</summary>
internal static class Cells
{
	/// <summary>Every implementation, the naive one last.</summary>
	public static readonly ICells[] All = [new AtomicCells(), new BoxAtomicCells(), new SeqLockAtomicCells(), new NaiveCells()];

	private sealed class AtomicCells : ICells
	{
		public String Name => "Atomic";
		public Boolean IsThreadSafe => true;
		public IAtomic<T> Create<T>(T value) => new AtomicAdapter<T>(value);
	}

	private sealed class BoxAtomicCells : ICells
	{
		public String Name => "BoxAtomic";
		public Boolean IsThreadSafe => true;
		public IAtomic<T> Create<T>(T value) => new BoxAtomic<T>(value);
	}

	private sealed class SeqLockAtomicCells : ICells
	{
		public String Name => "SeqLockAtomic";
		public Boolean IsThreadSafe => true;
		public IAtomic<T> Create<T>(T value) => new SeqLockAtomic<T>(value);
	}

	private sealed class NaiveCells : ICells
	{
		public String Name => "Naive";
		public Boolean IsThreadSafe => false;
		public IAtomic<T> Create<T>(T value) => new NaiveCell<T>(value);
	}
}

/// <summary>
/// A cell with no synchronisation at all, like the thread-safety suite's <c>NaiveAtomic</c>. Every
/// exploration of it must find a bug; one that does not has explored nothing worth trusting.
/// </summary>
/// <typeparam name="T">The type of the value held by the cell.</typeparam>
/// <param name="initial">The initial value.</param>
internal sealed class NaiveCell<T>(T initial) : IAtomic<T>
{
	private T value = initial;

	public T Read() => value;

	public void Write(T value) => this.value = value;

	public T Exchange(T value)
	{
		var previous = this.value;
		this.value = value;
		return previous;
	}

	public T CompareExchange(T value, T comparand)
	{
		TryCompareExchange(value, comparand, out var previous);
		return previous;
	}

	public Boolean TryCompareExchange(T value, T comparand, out T previous)
	{
		previous = this.value;
		if (!EqualityComparer<T>.Default.Equals(previous, comparand))
			return false;
		this.value = value;
		return true;
	}
}
