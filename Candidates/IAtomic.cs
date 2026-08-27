namespace NickStrupat;

/// <summary>
/// The behaviour a cell has to have, whichever way it stores the value.
/// </summary>
/// <typeparam name="T">The type of the value held by the cell.</typeparam>
/// <remarks>
/// <para>
/// This lives here rather than beside <see cref="Atomic{T}"/> and does not ship: it exists so that one
/// suite of tests and one set of harnesses can drive every storage strategy, and nothing in the library
/// needs it. <see cref="Atomic{T}"/> therefore does not implement it — <see cref="AtomicAdapter{T}"/>
/// presents it under this shape for the comparisons.
/// </para>
/// <para>
/// The implementations differ only in where they put the value. They agree on the behaviour below,
/// which the shared contract tests hold each of them to:
/// <list type="bullet">
/// <item><description><see cref="Read"/> and <see cref="Write"/> are atomic and never tear.</description></item>
/// <item><description>
/// <see cref="CompareExchange"/> compares a reference by identity, a value type held in a machine
/// word by its bits, and any other value type with <see cref="EqualityComparer{T}.Default"/>.
/// </description></item>
/// </list>
/// </para>
/// </remarks>
public interface IAtomic<T>
{
	/// <summary>Reads the value held by the cell.</summary>
	/// <returns>The value held at some point during the call.</returns>
	T Read();

	/// <summary>Writes a value to the cell.</summary>
	/// <param name="value">The value to store.</param>
	void Write(T value);

	/// <summary>Sets the value and returns the one it replaced, as a single atomic operation.</summary>
	/// <param name="value">The value to store.</param>
	/// <returns>The value held before the call.</returns>
	T Exchange(T value);

	/// <summary>
	/// Sets the value to <paramref name="value"/> if the value currently held matches
	/// <paramref name="comparand"/>, and returns the value held before the call.
	/// </summary>
	/// <param name="value">The value to store when the comparison succeeds.</param>
	/// <param name="comparand">The value the cell is expected to hold.</param>
	/// <returns>The value held before the call.</returns>
	T CompareExchange(T value, T comparand);

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
	Boolean TryCompareExchange(T value, T comparand, out T previous);
}
