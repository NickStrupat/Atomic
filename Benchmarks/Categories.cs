namespace Benchmarks;

/// <summary>
/// 3 bytes, a width no interlocked instruction has. It stands for every size a cell must widen before
/// it can swap it: 3, 5, 6 and 7 bytes all behave alike.
/// </summary>
public readonly record struct Three(Byte A, Byte B, Byte C);

/// <summary>
/// A struct holding a reference. Any such struct is kept behind a lock or in a box, whatever its size,
/// because the reference must be written through the GC write barrier, which a raw word swap would
/// bypass. This one is also 16 bytes, wider than a word.
/// </summary>
public readonly record struct Tagged(Int32 Number, String Text);

/// <summary>
/// The sample value each benchmarked cell holds, one per category.
/// </summary>
/// <remarks>
/// A benchmark generic over <c>T</c> cannot write a literal, and <c>default</c> would not do: storing
/// null skips the GC write barrier that storing a real reference goes through, so it would understate
/// what a reference cell costs.
/// </remarks>
internal static class Samples
{
	private static readonly Dictionary<Type, Object> Values = new()
	{
		[typeof(Int64)] = 1L,
		[typeof(Three)] = new Three(1, 2, 3),
		[typeof(String)] = new String(['x']),
		[typeof(Decimal)] = 1m,
		[typeof(Tagged)] = new Tagged(1, new String(['x'])),
	};

	/// <summary>Gets the sample value for <typeparamref name="T"/>.</summary>
	/// <typeparam name="T">The type of the value held by the cell.</typeparam>
	/// <returns>A value of <typeparamref name="T"/>, the same one on every call.</returns>
	/// <exception cref="NotSupportedException"><typeparamref name="T"/> is not a benchmarked category.</exception>
	public static T Of<T>() => Values.TryGetValue(typeof(T), out var value)
		? (T)value
		: throw new NotSupportedException($"No sample value for {typeof(T)}. Add one to {nameof(Samples)}.");

	/// <summary>Describes a category in words, for the contention table.</summary>
	/// <typeparam name="T">The type of the value held by the cell.</typeparam>
	/// <returns>A short description of what makes <typeparamref name="T"/> a separate category.</returns>
	public static String Describe<T>() => typeof(T) switch
	{
		var t when t == typeof(Int64) => "unmanaged, one word",
		var t when t == typeof(Three) => "unmanaged, a size no instruction matches",
		var t when t == typeof(String) => "a reference",
		var t when t == typeof(Decimal) => "unmanaged, wider than a word",
		var t when t == typeof(Tagged) => "a struct holding a reference",
		var t => t.Name,
	};
}
