namespace Benchmarks;

/// <summary>
/// Three bytes, which no interlocked instruction is that wide. Standing in for every size a cell has to
/// widen before it can swap it: three, five, six and seven behave alike.
/// </summary>
public readonly record struct Three(Byte A, Byte B, Byte C);

/// <summary>
/// A struct holding a reference. Narrow enough to fit in a word, and stored out of line anyway: the
/// garbage collector has to see the reference move, which no widened swap lets it do.
/// </summary>
public readonly record struct Tagged(Int32 Number, String Text);

/// <summary>
/// The value each benchmarked cell is built around, one per category.
/// </summary>
/// <remarks>
/// A benchmark generic over <c>T</c> cannot write down a literal, and a default value would not do: a
/// null reference is not what a reference cell costs, because the write barrier a real reference goes
/// through is elided for a store the compiler can see is null.
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

	/// <summary>The sample value for <typeparamref name="T"/>.</summary>
	/// <typeparam name="T">The type of the value held by the cell.</typeparam>
	/// <returns>A value of <typeparamref name="T"/>, the same one on every call.</returns>
	/// <exception cref="NotSupportedException"><typeparamref name="T"/> is not a benchmarked category.</exception>
	public static T Of<T>() => Values.TryGetValue(typeof(T), out var value)
		? (T)value
		: throw new NotSupportedException($"No sample value for {typeof(T)}. Add one to {nameof(Samples)}.");

	/// <summary>What a category is, in words, for the contention table.</summary>
	/// <typeparam name="T">The type of the value held by the cell.</typeparam>
	/// <returns>A description of why <typeparamref name="T"/> is measured separately.</returns>
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
