using Microsoft.Coyote.Specifications;
using NickStrupat;
using Tests;

namespace CoyoteTests;

/// <summary>The properties each cell must keep, crossed with the cells and the values that matter.</summary>
/// <remarks>
/// The values are chosen to reach each storage strategy of <see cref="Atomic{T}"/>: <see cref="Int64"/>
/// is swapped in place as a word, <see cref="Three"/> is widened into one, <see cref="Decimal"/> and
/// <see cref="Tagged"/> are kept behind the cell's lock, and <see cref="Tolerance"/> makes every
/// comparand equal but not identical, which sends a compare-exchange into the retry that follows a bit
/// mismatch. Each test races two or three tasks over a handful of operations: enough for every
/// interleaving that breaks these properties to exist, few enough for a thousand schedules to cover them.
/// </remarks>
internal static class Explorations
{
	/// <summary>Every exploration, real cells and the naive one alike.</summary>
	/// <returns>The explorations, in the order they are reported.</returns>
	public static IEnumerable<Exploration> All()
	{
		foreach (var cells in Cells.All)
		{
			var expectBug = !cells.IsThreadSafe;
			yield return new($"{cells.Name}<Int64> exchange conserves", () => ExchangeConserves(cells, i => (Int64)i), expectBug);
			yield return new($"{cells.Name}<Three> exchange conserves", () => ExchangeConserves(cells, i => new Three((Byte)i, (Byte)i, (Byte)i)), expectBug);
			yield return new($"{cells.Name}<Decimal> exchange conserves", () => ExchangeConserves(cells, i => (Decimal)i), expectBug);
			yield return new($"{cells.Name}<Tagged> exchange conserves", () => ExchangeConserves(cells, i => new Tagged(i, i.ToString())), expectBug);
			yield return new($"{cells.Name}<Int64> one winner", () => OneWinner(cells, 0L, 0L, 1L), expectBug);
			yield return new($"{cells.Name}<Decimal> one winner", () => OneWinner(cells, 0m, 0m, 1m), expectBug);
			yield return new($"{cells.Name}<Tolerance> one winner, equal but not identical",
				() => OneWinner(cells, new Tolerance(0, 0), new Tolerance(0, Int32.MaxValue), new Tolerance(1, 1)), expectBug);
			yield return new($"{cells.Name}<Int64> no lost updates", () => NoLostUpdates(cells, 0L, x => x + 1, 4L), expectBug);
			yield return new($"{cells.Name}<Decimal> no lost updates", () => NoLostUpdates(cells, 0m, x => x + 1, 4m), expectBug);
		}

		// The read-modify-writes are extensions on Atomic<T> itself, so they are explored on it alone:
		// the instruction, the loop and the lock path each get one.
		yield return new("Atomic<Int64>.Increment (instruction)", () => Increments(0L, 4L), ExpectBug: false);
		yield return new("Atomic<Double>.Increment (loop)", () => Increments(0.0, 4.0), ExpectBug: false);
		yield return new("Atomic<Decimal>.Increment (lock)", () => Increments(0m, 4m), ExpectBug: false);
		yield return new("Atomic<Access>.Or (enum instruction)", OrOnAnEnum, ExpectBug: false);
	}

	/// <summary>
	/// Two tasks each swap in values nobody else has. Everything taken out, plus what is left in the cell,
	/// must be exactly what was put in plus what the cell started holding.
	/// </summary>
	private static async Task ExchangeConserves<T>(ICells cells, Func<Int32, T> token)
	{
		var cell = cells.Create(token(0));
		var first = Task.Run(() => new[] { cell.Exchange(token(1)), cell.Exchange(token(2)) });
		var second = Task.Run(() => new[] { cell.Exchange(token(3)), cell.Exchange(token(4)) });
		var seen = (await first).Concat(await second).Append(cell.Read()).ToList();

		foreach (var expected in Enumerable.Range(0, 5).Select(token))
		{
			var index = seen.FindIndex(value => EqualityComparer<T>.Default.Equals(value, expected));
			Specification.Assert(index >= 0, "{0} was put in and never came out", Describe(expected));
			seen.RemoveAt(index);
		}
		Specification.Assert(seen.Count == 0, "{0} came out more than once", String.Join(", ", seen));
	}

	/// <summary>Three tasks race to compare-exchange against the same comparand; exactly one may win.</summary>
	private static async Task OneWinner<T>(ICells cells, T initial, T comparand, T next)
	{
		var cell = cells.Create(initial);
		var racers = Enumerable.Range(0, 3)
			.Select(racer => Task.Run(() => cell.TryCompareExchange(next, comparand, out _)))
			.ToArray();
		var winners = (await Task.WhenAll(racers)).Count(won => won);
		Specification.Assert(winners == 1, "{0} tasks won the same compare-exchange", winners);
	}

	/// <summary>Two tasks each apply two updates through a compare-exchange loop; none may be lost.</summary>
	private static async Task NoLostUpdates<T>(ICells cells, T initial, Func<T, T> next, T expected)
	{
		var cell = cells.Create(initial);
		void Twice()
		{
			for (var update = 0; update < 2; update++)
			{
				var current = cell.Read();
				while (!cell.TryCompareExchange(next(current), current, out var previous))
					current = previous;
			}
		}

		await Task.WhenAll(Task.Run(Twice), Task.Run(Twice));
		var final = cell.Read();
		Specification.Assert(EqualityComparer<T>.Default.Equals(final, expected),
			"ended at {0}, not {1}", Describe(final), Describe(expected));
	}

	/// <summary>Two tasks each increment twice through the extension; the cell must count all four.</summary>
	private static async Task Increments<T>(T initial, T expected) where T : System.Numerics.IIncrementOperators<T>
	{
		var cell = new Atomic<T>(initial);
		void Twice()
		{
			cell.Increment();
			cell.Increment();
		}

		await Task.WhenAll(Task.Run(Twice), Task.Run(Twice));
		var final = cell.Read();
		Specification.Assert(EqualityComparer<T>.Default.Equals(final, expected),
			"ended at {0}, not {1}", Describe(final), Describe(expected));
	}

	/// <summary>Three tasks each set a different flag; every flag must survive.</summary>
	private static async Task OrOnAnEnum()
	{
		var cell = new Atomic<Access>(Access.None);
		await Task.WhenAll(
			Task.Run(() => cell.Or(Access.Read)),
			Task.Run(() => cell.Or(Access.Write)),
			Task.Run(() => cell.Or(Access.Execute)));
		var final = cell.Read();
		Specification.Assert(final == (Access.Read | Access.Write | Access.Execute), "ended at {0}", final);
	}

	/// <summary>A value as text, for an assertion's arguments.</summary>
	/// <remarks>
	/// Every <see cref="Specification.Assert(Boolean, String, Object)"/> overload treats its message as a
	/// format string, and a record's <c>ToString</c> is full of braces. Interpolating one into the message
	/// threw a <see cref="FormatException"/> that Coyote reported as the bug — which made the naive cell
	/// look caught by properties that never got to run. Values go in as arguments, never in the message.
	/// </remarks>
	private static String Describe<T>(T value) => value?.ToString() ?? "null";
}
