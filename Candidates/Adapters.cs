namespace NickStrupat;

/// <summary>
/// Wraps <see cref="Atomic{T}"/> as an <see cref="IAtomic{T}"/>, so the shipping cell runs through the
/// same contract tests and benchmarks as the candidates it was chosen over.
/// </summary>
/// <typeparam name="T">The type of the value held by the cell.</typeparam>
/// <remarks>
/// <para>
/// <see cref="Atomic{T}"/> does not implement the interface itself, because the interface only exists
/// for comparing implementations and does not belong in the package.
/// </para>
/// <para>
/// The adapters are structs so that a harness that is generic over the adapter type (rather than taking
/// the interface) gets its own compiled copy per adapter, and both calls — into the adapter and into the
/// cell — inline away. Using an adapter through <see cref="IAtomic{T}"/> boxes it, which costs more than
/// a class would. The remarks on <c>Contention</c> say which style each harness uses.
/// </para>
/// </remarks>
/// <param name="cell">The cell to wrap.</param>
public readonly struct AtomicAdapter<T>(Atomic<T> cell) : IAtomic<T>
{
	private readonly Atomic<T> cell = cell ?? throw new ArgumentNullException(nameof(cell));

	/// <summary>Creates an adapter around a new cell holding <paramref name="value"/>.</summary>
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
/// Wraps <see cref="BoxAtomic{T}"/> the same way <see cref="AtomicAdapter{T}"/> wraps
/// <see cref="Atomic{T}"/>.
/// </summary>
/// <typeparam name="T">The type of the value held by the cell.</typeparam>
/// <remarks>
/// <see cref="BoxAtomic{T}"/> already implements <see cref="IAtomic{T}"/>; this exists only so it is
/// called the same way as the others. Used directly through the interface, it is a class, and a generic
/// harness shares one compiled body with every other reference type. Through this struct, the harness
/// gets its own compiled copy, like the other adapters.
/// </remarks>
/// <param name="cell">The cell to wrap.</param>
public readonly struct BoxAtomicAdapter<T>(BoxAtomic<T> cell) : IAtomic<T>
{
	private readonly BoxAtomic<T> cell = cell ?? throw new ArgumentNullException(nameof(cell));

	/// <summary>Creates an adapter around a new cell holding <paramref name="value"/>.</summary>
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
/// Wraps <see cref="SeqLockAtomic{T}"/> the same way <see cref="AtomicAdapter{T}"/> wraps
/// <see cref="Atomic{T}"/>.
/// </summary>
/// <typeparam name="T">The type of the value held by the cell.</typeparam>
/// <remarks>
/// See <see cref="BoxAtomicAdapter{T}"/> for why a type that already implements the interface still gets
/// an adapter.
/// </remarks>
/// <param name="cell">The cell to wrap.</param>
public readonly struct SeqLockAtomicAdapter<T>(SeqLockAtomic<T> cell) : IAtomic<T>
{
	private readonly SeqLockAtomic<T> cell = cell ?? throw new ArgumentNullException(nameof(cell));

	/// <summary>Creates an adapter around a new cell holding <paramref name="value"/>.</summary>
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
