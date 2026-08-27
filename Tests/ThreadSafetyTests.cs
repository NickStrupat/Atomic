using System.Collections.Concurrent;
using AwesomeAssertions;
using NickStrupat;

namespace Tests;

/// <summary>
/// What each implementation has to hold up under concurrent access. Every implementation runs the whole
/// suite through its own subclass, so a candidate cannot win on speed by being quietly unsafe.
/// </summary>
/// <remarks>
/// <para>
/// These falsify rather than prove. A race that never happened to be scheduled is indistinguishable
/// from one that cannot happen, so a passing run is evidence and not a proof — which is why each test
/// below is built to make the window it is looking for as wide as it can: threads released together on
/// a barrier rather than started in sequence, values chosen so that a torn or lost one is visible
/// rather than plausible, and enough repetitions that a narrow window is hit rather than missed.
/// </para>
/// <para>
/// The ordering tests have real teeth on the arm64 this is developed on, where the hardware genuinely
/// reorders, and much less on x86, where the hardware mostly does not. A green run on x86 says less
/// than a green run here.
/// </para>
/// </remarks>
public abstract class ThreadSafetyTests
{
	private const Int32 Threads = 8;
	private const Int32 IncrementsPerThread = 10_000;
	private const Int64 Total = Threads * (Int64)IncrementsPerThread;

	/// <summary>How long the tests that race a writer against a reader run for.</summary>
	private static readonly TimeSpan Window = TimeSpan.FromMilliseconds(250);

	/// <summary>Creates a cell of the implementation under test.</summary>
	/// <typeparam name="T">The type of the value held by the cell.</typeparam>
	/// <param name="value">The initial value.</param>
	/// <returns>A new cell holding <paramref name="value"/>.</returns>
	protected abstract IAtomic<T> Create<T>(T value);

	[Fact]
	public void CompareExchange_WhenValueFitsInAWordAndIsContended_LosesNoUpdates()
	{
		var atomic = Create(0L);

		RunOnDedicatedThreads((_, _) => Increment(atomic, current => current + 1));

		atomic.Read().Should().Be(Total);
	}

	[Fact]
	public void CompareExchange_WhenValueIsWiderThanAWordAndIsContended_LosesNoUpdates()
	{
		var atomic = Create(0m);

		RunOnDedicatedThreads((_, _) => Increment(atomic, current => current + 1));

		atomic.Read().Should().Be(Total);
	}

	[Fact]
	public void CompareExchange_WhenValueHoldsAReferenceAndIsContended_LosesNoUpdates()
	{
		var atomic = Create((Count: 0L, Tag: "tag"));

		RunOnDedicatedThreads((_, _) => Increment(atomic, current => (current.Count + 1, current.Tag)));

		atomic.Read().Should().Be((Total, "tag"));
	}

	[Fact]
	public void Exchange_WhenContended_NeitherLosesNorDuplicatesAValue()
	{
		// Every thread hands the cell a token no other thread has and keeps whatever it displaced. If the
		// swap is genuinely one operation then each token is displaced exactly once, so the tokens taken
		// out, plus the one left in the cell at the end, are precisely the tokens put in plus the one it
		// started holding. A swap that dropped a write would leave a token unaccounted for; one that
		// returned a value another thread had already been handed would produce a duplicate. Counting
		// updates, as the tests above do, would notice neither.
		const Int32 PerThread = 5_000;
		const Int32 SyncEvery = 250;
		var atomic = Create(0L);
		var displaced = new Int64[Threads][];

		RunOnDedicatedThreads((thread, barrier) =>
		{
			var taken = new Int64[PerThread];
			for (var i = 0; i < PerThread; i++)
			{
				// Every thread reaches this the same number of times, so the barrier stays balanced. It is
				// here because releasing the threads once was not enough: on a machine busy with the rest
				// of the suite they drifted apart, stopped overlapping, and this test went from catching a
				// plainly broken cell every time to catching it three runs in four.
				if (i % SyncEvery == 0)
					barrier.SignalAndWait();
				taken[i] = atomic.Exchange(Token(thread, i));
			}
			displaced[thread] = taken;
		});

		var observed = displaced.SelectMany(taken => taken).Append(atomic.Read()).Order().ToArray();
		var written = Enumerable.Range(0, Threads)
			.SelectMany(thread => Enumerable.Range(0, PerThread).Select(i => Token(thread, i)))
			.Append(0L)
			.Order()
			.ToArray();

		observed.Should().Equal(written);
	}

	[Fact]
	public void CompareExchange_WhenEveryThreadRacesForTheSameComparand_LetsExactlyOneWin()
	{
		// Started in sequence, threads would mostly find the comparand already gone and fail without ever
		// having collided, which proves nothing. The barrier releases them into the same round together,
		// and no thread can run ahead: reaching the next round means signalling it, and signalling blocks
		// until the stragglers have finished the round before.
		const Int32 Rounds = 500;
		var atomic = Create(0L);
		var winners = new Int32[Rounds];

		RunOnDedicatedThreads((_, barrier) =>
		{
			for (var round = 0; round < Rounds; round++)
			{
				barrier.SignalAndWait();
				if (atomic.TryCompareExchange(round + 1L, round, out Int64 _))
					Interlocked.Increment(ref winners[round]);
			}
		});

		winners.Should().AllSatisfy(count => count.Should().Be(1));
		atomic.Read().Should().Be(Rounds);
	}

	[Fact]
	public void Write_WhenPublishedThroughASecondCell_IsVisibleToAReaderThatSawThePublication()
	{
		// The guarantee the library actually claims: writes release and reads acquire, so a reader that
		// sees a publication sees everything written before it. Two cells are what makes this a question
		// at all — within one cell there is nothing to reorder against.
		//
		// The writer stores the payload and then publishes it. If the two writes were reordered, or the
		// reader's two reads were, the reader would see a sequence number newer than the payload standing
		// behind it. Nothing here is allowed to observe that.
		var payload = Create(0L);
		var published = Create(0L);
		using var cancellation = new CancellationTokenSource(Window);
		var stale = 0L;
		var checks = 0L;

		RunConcurrently(
			() =>
			{
				for (var i = 1L; !cancellation.IsCancellationRequested; i++)
				{
					payload.Write(i);
					published.Write(i);
				}
			},
			() =>
			{
				while (!cancellation.IsCancellationRequested)
				{
					var seen = published.Read();
					if (payload.Read() < seen)
						stale++;
					checks++;
				}
			});

		checks.Should().BeGreaterThan(0, "a reader that never got to run proves nothing");
		stale.Should().Be(0, "a reader that saw a publication must see the payload written before it");
	}

	[Fact]
	public void Read_WhenWrittenConcurrently_NeverObservesAValueThatWasNeverWritten()
	{
		// Every value written here has parts that agree with one another, so any value read whose parts
		// disagree is a value nobody wrote — a read that caught a write half done. The shapes cover each
		// strategy an implementation might pick: sizes it has to widen, a size that fits exactly, sizes
		// too wide to swap, a struct holding a reference, and a bare reference.
		const Int32 Distinct = 16;
		var texts = Enumerable.Range(0, Distinct).Select(i => new String((Char)('a' + i), 1)).ToArray();

		var three = Create(new Three(0, 0, 0));
		var seven = Create(new Seven(0, 0, 0, 0, 0, 0, 0));
		var eight = Create(new Eight(0, 0));
		var twelve = Create(new Twelve(0, 0, 0));
		var wide = Create(0m);
		var tagged = Create(new Tagged(0, texts[0]));
		var reference = Create(texts[0]);

		using var cancellation = new CancellationTokenSource(Window);
		var torn = new ConcurrentQueue<String>();
		var checks = 0L;

		RunConcurrently(
			() =>
			{
				for (var i = 0; !cancellation.IsCancellationRequested; i++)
				{
					var b = (Byte)i;
					var slot = i % Distinct;
					three.Write(new(b, b, b));
					seven.Write(new(b, b, b, b, b, b, b));
					eight.Write(new(i, i));
					twelve.Write(new(i, i, i));
					wide.Write(new Decimal(i, i, i, false, 0));
					tagged.Write(new(slot, texts[slot]));
					reference.Write(texts[slot]);
				}
			},
			() =>
			{
				Span<Int32> bits = stackalloc Int32[4];
				while (!cancellation.IsCancellationRequested)
				{
					var a = three.Read();
					if (a.B != a.A || a.C != a.A)
						torn.Enqueue($"Three {a}");

					var s = seven.Read();
					if (s.B != s.A || s.C != s.A || s.D != s.A || s.E != s.A || s.F != s.A || s.G != s.A)
						torn.Enqueue($"Seven {s}");

					var e = eight.Read();
					if (e.B != e.A)
						torn.Enqueue($"Eight {e}");

					var t = twelve.Read();
					if (t.B != t.A || t.C != t.A)
						torn.Enqueue($"Twelve {t}");

					Decimal.GetBits(wide.Read(), bits);
					if (bits[1] != bits[0] || bits[2] != bits[0])
						torn.Enqueue($"Decimal {bits[0]} {bits[1]} {bits[2]}");

					var g = tagged.Read();
					if ((UInt32)g.Number >= Distinct || !ReferenceEquals(g.Text, texts[g.Number]))
						torn.Enqueue($"Tagged {g.Number}");

					var r = reference.Read();
					if (Array.IndexOf(texts, r) < 0)
						torn.Enqueue($"String {r}");

					checks++;
				}
			});

		checks.Should().BeGreaterThan(0, "a reader that never got to run proves nothing");
		torn.Should().BeEmpty();
	}

	/// <summary>A value unique to one thread and one iteration, and never zero.</summary>
	/// <param name="thread">The thread the value belongs to.</param>
	/// <param name="index">The iteration the value belongs to.</param>
	/// <returns>A token distinct from every other token and from the value a cell starts holding.</returns>
	private static Int64 Token(Int32 thread, Int32 index) => ((Int64)(thread + 1) << 32) | (UInt32)index;

	/// <summary>Applies <paramref name="next"/> to the cell <see cref="IncrementsPerThread"/> times.</summary>
	/// <typeparam name="T">The type of the value held by the cell.</typeparam>
	/// <param name="atomic">The cell to update.</param>
	/// <param name="next">Produces the new value from the current one.</param>
	private static void Increment<T>(IAtomic<T> atomic, Func<T, T> next)
	{
		for (var i = 0; i < IncrementsPerThread; i++)
			while (true)
			{
				var current = atomic.Read();
				if (EqualityComparer<T>.Default.Equals(atomic.CompareExchange(next(current), current), current))
					break;
			}
	}

	/// <summary>Runs each of <paramref name="bodies"/> on a thread of its own and waits for all of them.</summary>
	/// <param name="bodies">The work to run, one body per thread.</param>
	private static void RunConcurrently(params Action[] bodies) =>
		Join(bodies.Select(body => new Thread(() => body()) { IsBackground = true }).ToArray());

	/// <summary>
	/// Runs <paramref name="body"/> on <see cref="Threads"/> threads of its own and waits for all of them.
	/// </summary>
	/// <param name="body">
	/// The work to run, given the index of the thread running it and a barrier every thread reaches the
	/// same number of times, for keeping them together once they have started.
	/// </param>
	/// <remarks>
	/// <para>
	/// Dedicated threads rather than the pool, and not as a matter of taste. Under a pool busy with the
	/// rest of the suite these bodies get run one after another, the race never happens, and the test
	/// passes without having tested anything — which is exactly what
	/// <see cref="ThreadSafetySuiteTests"/> caught when this used <see cref="Task.Run(Action)"/>.
	/// </para>
	/// <para>
	/// The barrier is there for the same reason. Starting a thread costs more than a short body takes to
	/// run, so threads started in a loop finish in that loop too, one after another, having never once
	/// been in each other's way. Releasing them together is what makes the contention real, and this cost
	/// the exchange test below almost all of its power before it was added.
	/// </para>
	/// <para>
	/// Releasing them together is not enough on its own either. On a machine busy with the rest of the
	/// suite the scheduler takes a thread away mid-run and the others finish without it, so the body is
	/// handed the barrier as well and can re-sync as it goes.
	/// </para>
	/// </remarks>
	private static void RunOnDedicatedThreads(Action<Int32, Barrier> body)
	{
		using var barrier = new Barrier(Threads);
		Join(Enumerable.Range(0, Threads)
			.Select(index => new Thread(() =>
			{
				barrier.SignalAndWait();
				body(index, barrier);
			}) { IsBackground = true })
			.ToArray());
	}

	/// <summary>Starts every thread and waits for all of them.</summary>
	/// <param name="threads">The threads to run.</param>
	private static void Join(Thread[] threads)
	{
		foreach (var thread in threads)
			thread.Start();
		foreach (var thread in threads)
			thread.Join();
	}
}

/// <summary>
/// The shipping cell, held to the same guarantees as the candidates through
/// <see cref="AtomicAdapter{T}"/>.
/// </summary>
public sealed class AtomicThreadSafetyTests : ThreadSafetyTests
{
	protected override IAtomic<T> Create<T>(T value) => new AtomicAdapter<T>(value);
}

public sealed class BoxAtomicThreadSafetyTests : ThreadSafetyTests
{
	protected override IAtomic<T> Create<T>(T value) => new BoxAtomic<T>(value);
}

public sealed class SeqLockAtomicThreadSafetyTests : ThreadSafetyTests
{
	protected override IAtomic<T> Create<T>(T value) => new SeqLockAtomic<T>(value);
}
