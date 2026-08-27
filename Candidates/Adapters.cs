namespace NickStrupat;

/// <summary>
/// Presents <see cref="Atomic{T}"/> as an <see cref="IAtomic{T}"/>, so the shipping cell can be held to
/// the same contract, and driven by the same harnesses, as the candidates it was chosen over.
/// </summary>
/// <typeparam name="T">The type of the value held by the cell.</typeparam>
/// <remarks>
/// <see cref="Atomic{T}"/> does not implement the interface, because the interface is a convenience for
/// comparing implementations and has no business in the package.
/// <para>
/// This and its siblings are structs so that a harness generic over the adapter, rather than typed to
/// the interface, gets a distinct instantiation and folds both hops away. Reaching one through
/// <see cref="IAtomic{T}"/> instead boxes it and costs more than a class would; see the remarks on
/// <c>Contention</c> for which of those the harnesses rely on.
/// </para>
/// </remarks>
/// <param name="cell">The cell to present.</param>
public readonly struct AtomicAdapter<T>(Atomic<T> cell) : IAtomic<T>
{
	private readonly Atomic<T> cell = cell ?? throw new ArgumentNullException(nameof(cell));

	/// <summary>Creates an adapter over a new cell holding <paramref name="value"/>.</summary>
	/// <param name="value">The initial value.</param>
	public AtomicAdapter(T value) : this(new Atomic<T>(value)) { }

	/// <inheritdoc />
	public T Read() => cell.Read();

	/// <inheritdoc />
	public void Write(T value) => cell.Write(value);

	/// <inheritdoc />
	public T Exchange(T value) => cell.Exchange(value);

	/// <inheritdoc />
	public T CompareExchange(T value, T comparand) => cell.CompareExchange(value, comparand);

	/// <inheritdoc />
	public Boolean TryCompareExchange(T value, T comparand, out T previous) =>
		cell.TryCompareExchange(value, comparand, out previous);
}

/// <summary>
/// Presents <see cref="BoxAtomic{T}"/> under the same shape as <see cref="AtomicAdapter{T}"/>.
/// </summary>
/// <typeparam name="T">The type of the value held by the cell.</typeparam>
/// <remarks>
/// <see cref="BoxAtomic{T}"/> implements <see cref="IAtomic{T}"/> already, so this exists only to give
/// it the same dispatch as the others. Held as the interface it is a class, and shares one body with
/// every other reference type; held through this it is a struct, and gets its own.
/// </remarks>
/// <param name="cell">The cell to present.</param>
public readonly struct BoxAtomicAdapter<T>(BoxAtomic<T> cell) : IAtomic<T>
{
	private readonly BoxAtomic<T> cell = cell ?? throw new ArgumentNullException(nameof(cell));

	/// <summary>Creates an adapter over a new cell holding <paramref name="value"/>.</summary>
	/// <param name="value">The initial value.</param>
	public BoxAtomicAdapter(T value) : this(new BoxAtomic<T>(value)) { }

	/// <inheritdoc />
	public T Read() => cell.Read();

	/// <inheritdoc />
	public void Write(T value) => cell.Write(value);

	/// <inheritdoc />
	public T Exchange(T value) => cell.Exchange(value);

	/// <inheritdoc />
	public T CompareExchange(T value, T comparand) => cell.CompareExchange(value, comparand);

	/// <inheritdoc />
	public Boolean TryCompareExchange(T value, T comparand, out T previous) =>
		cell.TryCompareExchange(value, comparand, out previous);
}

/// <summary>
/// Presents <see cref="SeqLockAtomic{T}"/> under the same shape as <see cref="AtomicAdapter{T}"/>.
/// </summary>
/// <typeparam name="T">The type of the value held by the cell.</typeparam>
/// <remarks>See <see cref="BoxAtomicAdapter{T}"/> for why a type that already implements the interface
/// still gets one of these.</remarks>
/// <param name="cell">The cell to present.</param>
public readonly struct SeqLockAtomicAdapter<T>(SeqLockAtomic<T> cell) : IAtomic<T>
{
	private readonly SeqLockAtomic<T> cell = cell ?? throw new ArgumentNullException(nameof(cell));

	/// <summary>Creates an adapter over a new cell holding <paramref name="value"/>.</summary>
	/// <param name="value">The initial value.</param>
	public SeqLockAtomicAdapter(T value) : this(new SeqLockAtomic<T>(value)) { }

	/// <inheritdoc />
	public T Read() => cell.Read();

	/// <inheritdoc />
	public void Write(T value) => cell.Write(value);

	/// <inheritdoc />
	public T Exchange(T value) => cell.Exchange(value);

	/// <inheritdoc />
	public T CompareExchange(T value, T comparand) => cell.CompareExchange(value, comparand);

	/// <inheritdoc />
	public Boolean TryCompareExchange(T value, T comparand, out T previous) =>
		cell.TryCompareExchange(value, comparand, out previous);
}
