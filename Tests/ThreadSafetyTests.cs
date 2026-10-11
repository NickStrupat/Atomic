using System.Collections.Concurrent;
using System.Diagnostics;
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

	/// <summary>How many operations a thread makes between re-synchronising with the others.</summary>
	private const Int32 SyncEvery = 250;

	/// <summary>How long each test that races threads against one another runs.</summary>
	private static readonly TimeSpan Window = TimeSpan.FromMilliseconds(250);

	/// <summary>
	/// A ceiling on the rounds a one-winner test races, so its per-round tally has a fixed size. A round
	/// costs a barrier, so <see cref="Window"/> ends a run long before this does on any machine seen so far.
	/// </summary>
	private const Int32 MaxRounds = 1_000_000;

	/// <summary>Creates a cell of the implementation under test.</summary>
	/// <typeparam name="T">The type of the value held by the cell.</typeparam>
	/// <param name="value">The initial value.</param>
	/// <returns>A new cell holding <paramref name="value"/>.</returns>
	protected abstract IAtomic<T> Create<T>(T value);

	[Fact]
	public void CompareExchange_WhenValueFitsInAWordAndIsContended_LosesNoUpdates()
	{
		var atomic = Create(0L);

		var total = IncrementForAWindow(atomic, current => current + 1);

		atomic.Read().Should().Be(total);
	}

	[Fact]
	public void CompareExchange_WhenValueIsWiderThanAWordAndIsContended_LosesNoUpdates()
	{
		var atomic = Create(0m);

		var total = IncrementForAWindow(atomic, current => current + 1);

		atomic.Read().Should().Be(total);
	}

	[Fact]
	public void CompareExchange_WhenValueHoldsAReferenceAndIsContended_LosesNoUpdates()
	{
		var atomic = Create((Count: 0L, Tag: "tag"));

		var total = IncrementForAWindow(atomic, current => (current.Count + 1, current.Tag));

		atomic.Read().Should().Be((total, "tag"));
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
		//
		// The run lasts a fixed time rather than a fixed count, so it spans many scheduler time slices:
		// on a CI virtual machine with three or four cores, a burst under a millisecond can run its threads
		// one after another. That leaves too many tokens to keep, so each side is summarised as a multiset
		// hash, which two different multisets match only by a 64-bit coincidence.
		var atomic = Create(0L);
		var takenOut = new UInt64[Threads];
		var exchanges = 0;

		RunOnDedicatedThreads((thread, barrier) =>
		{
			// Every thread stops at the same sync point, so each makes the same number of exchanges. The
			// periodic re-sync is here because releasing the threads once was not enough: on a machine
			// busy with the rest of the suite they drifted apart and stopped overlapping.
			var hash = 0UL;
			var i = 0;
			for (; i % SyncEvery != 0 || barrier.SignalAndWaitWithin(Window); i++)
				hash += Mix(atomic.Exchange(Token(thread, i)));
			takenOut[thread] = hash;
			exchanges = i;
		});

		var observed = Mix(atomic.Read());
		foreach (var hash in takenOut)
			observed += hash;

		var written = Mix(0L);
		for (var thread = 0; thread < Threads; thread++)
			for (var i = 0; i < exchanges; i++)
				written += Mix(Token(thread, i));

		exchanges.Should().BeGreaterThan(0, "a run that made no exchanges proves nothing");
		observed.Should().Be(written, "the tokens taken out, plus the one left in, must be the tokens put in");
	}

	[Fact]
	public void CompareExchange_WhenEveryThreadRacesForTheSameComparand_LetsExactlyOneWin()
	{
		// Started in sequence, threads would mostly find the comparand already gone and fail without ever
		// having collided, which proves nothing. The barrier releases them into the same round together,
		// and no thread can run ahead: reaching the next round means signalling it, and signalling blocks
		// until the stragglers have finished the round before. Rounds continue for a fixed time, for the
		// reason the exchange test above gives.
		var atomic = Create(0L);
		var winners = new Int32[MaxRounds];
		var rounds = 0;

		RunOnDedicatedThreads((_, barrier) =>
		{
			var round = 0;
			for (; round < MaxRounds && barrier.SignalAndWaitWithin(Window); round++)
				if (atomic.TryCompareExchange(round + 1L, round, out Int64 _))
					Interlocked.Increment(ref winners[round]);
			rounds = round;
		});

		rounds.Should().BeGreaterThan(0, "a run that raced no rounds proves nothing");
		RoundsWithoutExactlyOneWinner(winners, rounds).Should().Be(0, $"each of {rounds} rounds must have exactly one winner");
		atomic.Read().Should().Be(rounds);
	}

	[Fact]
	public void CompareExchange_WhenTheComparandIsEqualButNotIdentical_StillLetsExactlyOneWin()
	{
		// Tolerance ignores its second field, so every comparand below is the value the cell holds and
		// none of them is its bit pattern. That puts every thread past the single instruction and
		// into the retry that follows it, every round, which is the point: a thread that loses the race
		// there has to go round on what it saw rather than report an inequality that never happened.
		var atomic = Create(new Tolerance(0, 0));
		var winners = new Int32[MaxRounds];
		var rounds = 0;

		RunOnDedicatedThreads((_, barrier) =>
		{
			var round = 0;
			for (; round < MaxRounds && barrier.SignalAndWaitWithin(Window); round++)
			{
				var comparand = new Tolerance(round, Int32.MaxValue);
				if (atomic.TryCompareExchange(new Tolerance(round + 1, round + 1), comparand, out Tolerance _))
					Interlocked.Increment(ref winners[round]);
			}
			rounds = round;
		});

		rounds.Should().BeGreaterThan(0, "a run that raced no rounds proves nothing");
		RoundsWithoutExactlyOneWinner(winners, rounds).Should().Be(0, $"each of {rounds} rounds must have exactly one winner");
		atomic.Read().Value.Should().Be(rounds);
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
		var stale = 0L;
		var checks = 0L;

		RunConcurrently(
			stop =>
			{
				for (var i = 1L; !stop.IsCancellationRequested; i++)
				{
					payload.Write(i);
					published.Write(i);
				}
			},
			stop =>
			{
				while (!stop.IsCancellationRequested)
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

		var torn = new ConcurrentQueue<String>();
		var checks = 0L;

		RunConcurrently(
			stop =>
			{
				for (var i = 0; !stop.IsCancellationRequested; i++)
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
			stop =>
			{
				Span<Int32> bits = stackalloc Int32[4];
				while (!stop.IsCancellationRequested)
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

	/// <summary>Counts the rounds that did not have exactly one winner.</summary>
	/// <param name="winners">The number of winners in each round.</param>
	/// <param name="rounds">How many rounds were raced.</param>
	/// <returns>The number of rounds with no winner or with more than one.</returns>
	/// <remarks>
	/// A count rather than an assertion per round: a run can race up to <see cref="MaxRounds"/> rounds, and
	/// asserting each one, and describing every failure, took minutes.
	/// </remarks>
	private static Int32 RoundsWithoutExactlyOneWinner(Int32[] winners, Int32 rounds)
	{
		var wrong = 0;
		for (var round = 0; round < rounds; round++)
			if (winners[round] != 1)
				wrong++;
		return wrong;
	}

	/// <summary>Scrambles a value, so that summing the results hashes a multiset of values.</summary>
	/// <param name="value">The value to scramble.</param>
	/// <returns>The SplitMix64 finaliser applied to it.</returns>
	/// <remarks>
	/// Addition is order-independent, so the sum of these over a collection depends only on which values
	/// it holds and how many times each. Without the scrambling, a duplicate and a loss could cancel out.
	/// </remarks>
	private static UInt64 Mix(Int64 value)
	{
		var z = unchecked((UInt64)value + 0x9E3779B97F4A7C15UL);
		z = unchecked((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL);
		z = unchecked((z ^ (z >> 27)) * 0x94D049BB133111EBUL);
		return z ^ (z >> 31);
	}

	/// <summary>
	/// Has every thread apply <paramref name="next"/> to the cell through a compare-exchange loop for
	/// <see cref="Window"/>, and counts how many times they applied it between them.
	/// </summary>
	/// <typeparam name="T">The type of the value held by the cell.</typeparam>
	/// <param name="atomic">The cell to update.</param>
	/// <param name="next">Produces the new value from the current one.</param>
	/// <returns>How many updates were applied in total, which is what the cell must have counted.</returns>
	/// <remarks>
	/// For a fixed time rather than a fixed count, for the reason the exchange test gives: at a fixed
	/// 10,000 each, the whole run took under a millisecond, and on a macOS CI runner the cell with no
	/// synchronisation at all lost no updates five attempts running.
	/// </remarks>
	private static Int64 IncrementForAWindow<T>(IAtomic<T> atomic, Func<T, T> next)
	{
		var updates = 0L;
		RunOnDedicatedThreads((_, barrier) =>
		{
			// Every thread stops at the same sync point, so each applies the same number of updates.
			var applied = 0;
			for (; applied % SyncEvery != 0 || barrier.SignalAndWaitWithin(Window); applied++)
				while (true)
				{
					var current = atomic.Read();
					if (EqualityComparer<T>.Default.Equals(atomic.CompareExchange(next(current), current), current))
						break;
				}
			Interlocked.Add(ref updates, applied);
		});

		updates.Should().BeGreaterThan(0, "a run that applied no updates proves nothing");
		return updates;
	}

	/// <summary>
	/// Runs each of <paramref name="bodies"/> on its own thread, released together, until
	/// <see cref="Window"/> has passed, and waits for them all.
	/// </summary>
	/// <param name="bodies">The work to run, one body per thread, each given the token that ends the run.</param>
	/// <remarks>
	/// The clock starts once every thread is running. Started by the test, before its threads were
	/// created, it ran out on a busy Windows runner before the publication test's reader had made a single
	/// check.
	/// </remarks>
	private static void RunConcurrently(params Action<CancellationToken>[] bodies)
	{
		using var stop = new CancellationTokenSource();
		var start = new SpinBarrier(bodies.Length);
		var clockStarted = 0;
		Join(bodies.Select(body => new Thread(() =>
		{
			start.SignalAndWait();
			if (Interlocked.Exchange(ref clockStarted, 1) == 0)
				stop.CancelAfter(Window);
			body(stop.Token);
		}) { IsBackground = true }).ToArray());
	}

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
		private Boolean goOn = true;

		/// <summary>
		/// When the first timed round was released, as a <see cref="Stopwatch"/> timestamp, or zero before
		/// then. Only the thread releasing a round touches it, and rounds are released one at a time.
		/// </summary>
		private Int64 started;

		/// <summary>Arrives at the barrier and spins until every participant has arrived.</summary>
		public void SignalAndWait() => SignalAndWaitWithin(Timeout.InfiniteTimeSpan);

		/// <summary>
		/// Arrives at the barrier, spins until every participant has arrived, and reports whether there is
		/// time for another round.
		/// </summary>
		/// <param name="window">
		/// How long rounds continue, counted from the first round released with a limit, or
		/// <see cref="Timeout.InfiniteTimeSpan"/> for no limit.
		/// </param>
		/// <returns><see langword="false"/> once <paramref name="window"/> has passed.</returns>
		/// <remarks>
		/// <para>
		/// The clock starts at the first release, when every participant is already running. A clock
		/// started by the test, before its threads were created, ran out before the first round on a busy
		/// Windows runner, and the test raced nothing.
		/// </para>
		/// <para>
		/// The last thread to arrive decides for all of them and publishes the answer with the release, so
		/// every participant gets the same answer in the same round. Each checking the time itself could
		/// split them, and the ones carrying on would wait forever at a barrier the others had left.
		/// </para>
		/// </remarks>
		public Boolean SignalAndWaitWithin(TimeSpan window)
		{
			var current = Volatile.Read(ref round);
			if (Interlocked.Increment(ref arrived) == participants)
			{
				var decision = true;
				if (window != Timeout.InfiniteTimeSpan)
				{
					if (started == 0)
						started = Stopwatch.GetTimestamp();
					decision = Stopwatch.GetElapsedTime(started) < window;
				}
				Volatile.Write(ref goOn, decision);

				// Reset before releasing: a released thread may arrive for the next round at once.
				Volatile.Write(ref arrived, 0);
				Interlocked.Increment(ref round);
				return decision;
			}

			for (var spins = 1; Volatile.Read(ref round) == current; spins++)
			{
				if (spins % SpinsPerYield == 0)
					Thread.Yield();
				else
					Thread.SpinWait(1);
			}

			// Safe to read after the release: the next round cannot be decided until this thread arrives.
			return Volatile.Read(ref goOn);
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
