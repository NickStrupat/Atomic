using System.Collections.Concurrent;
using AwesomeAssertions;
using NickStrupat;

namespace Tests;

/// <summary>
/// The guarantees every implementation must keep under concurrent access. Each implementation runs the
/// whole suite through its own subclass, so a candidate cannot win on speed by being quietly unsafe.
/// </summary>
/// <remarks>
/// <para>
/// These tests can find bugs but cannot prove their absence: a race that was never scheduled looks the
/// same as one that cannot happen. So each test makes the window it looks for as wide as possible:
/// </para>
/// <list type="bullet">
/// <item><description>
/// Threads are released together from a spinning barrier rather than started one by one, and there are
/// never more of them than cores, so released threads are all running at once.
/// </description></item>
/// <item><description>
/// Values are chosen so that a torn or lost value is obviously wrong, not plausible.
/// </description></item>
/// <item><description>
/// There are enough repetitions that a narrow window is hit rather than missed.
/// </description></item>
/// </list>
/// <para>
/// The memory-ordering tests are much stronger on arm64 (where this is developed), whose hardware really
/// does reorder, than on x86, whose hardware mostly does not. A pass on x86 says less than a pass here.
/// </para>
/// </remarks>
public abstract class ThreadSafetyTests
{
	/// <summary>How many threads race: one per core, between 2 and 8.</summary>
	/// <remarks>
	/// A race needs its threads executing at the same instant, which takes a core each. With more threads
	/// than cores, the extra ones are queued behind the scheduler and come out after the others' few
	/// nanoseconds of read-compare-store are over. Eight threads on a 4-core CI runner let a cell with no
	/// synchronisation at all win every round of the one-winner test five times running.
	/// </remarks>
	private static readonly Int32 Threads = Math.Clamp(Environment.ProcessorCount, 2, 8);

	private const Int32 IncrementsPerThread = 10_000;
	private static readonly Int64 Total = Threads * (Int64)IncrementsPerThread;

	/// <summary>How long each test that races a writer against a reader runs.</summary>
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
	public void CompareExchange_WhenTheComparandIsEqualButNotIdentical_StillLetsExactlyOneWin()
	{
		// Tolerance ignores its second field, so every comparand below is the value the cell holds and
		// none of them is its bit pattern. That puts every thread past the single instruction and
		// into the retry that follows it, every round, which is the point: a thread that loses the race
		// there has to go round on what it saw rather than report an inequality that never happened.
		const Int32 Rounds = 500;
		var atomic = Create(new Tolerance(0, 0));
		var winners = new Int32[Rounds];

		RunOnDedicatedThreads((_, barrier) =>
		{
			for (var round = 0; round < Rounds; round++)
			{
				barrier.SignalAndWait();
				var comparand = new Tolerance(round, Int32.MaxValue);
				if (atomic.TryCompareExchange(new Tolerance(round + 1, round + 1), comparand, out Tolerance _))
					Interlocked.Increment(ref winners[round]);
			}
		});

		winners.Should().AllSatisfy(count => count.Should().Be(1));
		atomic.Read().Value.Should().Be(Rounds);
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
		//
		// The 8-byte integer is a strategy of its own wherever the word is 4 bytes, and is not
		// covered by Eight, which is the same size and goes behind the monitor there. UInt64 takes the
		// identical path and would be asserting this twice. Where the word is 8 bytes this row is a
		// formality, which is the point — the shapes are the same everywhere and the strategy under them
		// is not.
		const Int32 Distinct = 16;
		var texts = Enumerable.Range(0, Distinct).Select(i => new String((Char)('a' + i), 1)).ToArray();

		var integer = Create(0L);
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
					integer.Write(((Int64)i << 32) | (UInt32)i);
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
					var n = integer.Read();
					if ((Int32)(n >> 32) != (Int32)n)
						torn.Enqueue($"Int64 {n:x16}");

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

	/// <summary>Makes a value unique to one thread and one iteration, and never zero.</summary>
	/// <param name="thread">The thread the value belongs to.</param>
	/// <param name="index">The iteration the value belongs to.</param>
	/// <returns>A token different from every other token and from a cell's initial value of zero.</returns>
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

	/// <summary>Runs each of <paramref name="bodies"/> on its own thread and waits for them all.</summary>
	/// <param name="bodies">The work to run, one body per thread.</param>
	private static void RunConcurrently(params Action[] bodies) =>
		Join(bodies.Select(body => new Thread(() => body()) { IsBackground = true }).ToArray());

	/// <summary>
	/// Runs <paramref name="body"/> on <see cref="Threads"/> new threads, released together, and waits for
	/// them all.
	/// </summary>
	/// <param name="body">
	/// The work to run. It receives the index of its thread and a barrier that every thread must signal
	/// the same number of times, which it can use to keep the threads in step once they have started.
	/// </param>
	/// <remarks>
	/// <para>
	/// This uses dedicated threads rather than the thread pool on purpose. When the pool is busy with the
	/// rest of the suite, it runs these bodies one after another, the race never happens, and the test
	/// passes without testing anything. <see cref="ThreadSafetySuiteTests"/> caught exactly that when this
	/// used <see cref="Task.Run(Action)"/>.
	/// </para>
	/// <para>
	/// The starting barrier is there for the same reason. Starting a thread takes longer than a short body
	/// takes to run, so threads started in a loop would each finish before the next began and never
	/// contend. Before the barrier was added, the exchange test lost almost all of its power this way.
	/// </para>
	/// <para>
	/// Starting together is not enough either. On a busy machine the scheduler pauses a thread mid-run and
	/// the others finish without it, so the body also gets the barrier and can re-synchronise as it goes.
	/// </para>
	/// <para>
	/// The barrier spins rather than blocks; see <see cref="SpinBarrier"/> for why.
	/// </para>
	/// </remarks>
	private static void RunOnDedicatedThreads(Action<Int32, SpinBarrier> body)
	{
		var barrier = new SpinBarrier(Threads);
		Join(Enumerable.Range(0, Threads)
			.Select(index => new Thread(() =>
			{
				barrier.SignalAndWait();
				body(index, barrier);
			}) { IsBackground = true })
			.ToArray());
	}

	/// <summary>Starts every thread, then waits for them all to finish.</summary>
	/// <param name="threads">The threads to run.</param>
	private static void Join(Thread[] threads)
	{
		foreach (var thread in threads)
			thread.Start();
		foreach (var thread in threads)
			thread.Join();
	}

	/// <summary>A reusable barrier whose waiters spin rather than block, so they all leave it at once.</summary>
	/// <param name="participants">How many threads must arrive before any is released.</param>
	/// <remarks>
	/// <see cref="Barrier"/> puts its waiters to sleep, and waking them goes through the scheduler, which
	/// resumes them one after another. Each resumed thread can finish its round before the next is running,
	/// so the race the round exists for never happens. A waiter here is already running when it is released:
	/// it only has to see one counter change, which reaches every core within nanoseconds. It never backs
	/// off, since backing off is what would spread the departures out again, and it yields only now and
	/// then, so a thread the scheduler has paused still gets to arrive.
	/// </remarks>
	private sealed class SpinBarrier(Int32 participants)
	{
		/// <summary>How many spins a waiter makes between yields.</summary>
		private const Int32 SpinsPerYield = 256;

		private Int32 arrived;
		private Int32 round;

		/// <summary>Arrives at the barrier and spins until every participant has arrived.</summary>
		public void SignalAndWait()
		{
			var current = Volatile.Read(ref round);
			if (Interlocked.Increment(ref arrived) == participants)
			{
				// Reset before releasing: a released thread may arrive for the next round at once.
				Volatile.Write(ref arrived, 0);
				Interlocked.Increment(ref round);
				return;
			}

			for (var spins = 1; Volatile.Read(ref round) == current; spins++)
			{
				if (spins % SpinsPerYield == 0)
					Thread.Yield();
				else
					Thread.SpinWait(1);
			}
		}
	}
}

/// <summary>
/// Runs the thread-safety suite against the shipping cell, through <see cref="AtomicAdapter{T}"/>.
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
