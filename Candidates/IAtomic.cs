namespace NickStrupat;

/// <summary>
/// The operations every cell design provides, however it stores its value.
/// </summary>
/// <typeparam name="T">The type of the value held by the cell.</typeparam>
/// <remarks>
/// <para>
/// This does not ship. It exists so that one test suite and one set of benchmarks can drive every
/// design, and nothing in the library needs it. <see cref="Atomic{T}"/> does not implement it;
/// <see cref="AtomicAdapter{T}"/> wraps it in this interface for the comparisons.
/// </para>
/// <para>
/// The implementations differ only in where they store the value. The shared contract tests hold each
/// of them to the same behaviour:
/// <list type="bullet">
/// <item><description><see cref="Read"/> and <see cref="Write"/> are atomic and never tear.</description></item>
/// <item><description>
/// <see cref="CompareExchange"/> compares a reference by identity and a value type with
/// <see cref="EqualityComparer{T}.Default"/>, whatever its width and however the cell stores it.
/// </description></item>
/// </list>
/// </para>
/// </remarks>
public interface IAtomic<T>
{
	/// <summary>Reads the value held by the cell.</summary>
	/// <returns>The value the cell held at some point during the call.</returns>
	T Read();

	/// <summary>Writes a value to the cell.</summary>
	/// <param name="value">The value to store.</param>
	void Write(T value);

	/// <summary>Stores a value and returns the one it replaced, as a single atomic operation.</summary>
	/// <param name="value">The value to store.</param>
	/// <returns>The value held before the call.</returns>
	T Exchange(T value);

	/// <summary>
	/// Stores <paramref name="value"/> if the cell currently holds <paramref name="comparand"/>, and returns
	/// the value held before the call either way.
	/// </summary>
	/// <param name="value">The value to store if the comparison succeeds.</param>
	/// <param name="comparand">The value the cell is expected to hold.</param>
	/// <returns>The value held before the call.</returns>
	T CompareExchange(T value, T comparand);

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
	Boolean TryCompareExchange(T value, T comparand, out T previous);
}
