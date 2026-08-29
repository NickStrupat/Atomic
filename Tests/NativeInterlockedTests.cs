using System.Numerics;
using AwesomeAssertions;
using NickStrupat;

namespace Tests;

/// <summary>
/// The extensions which hand a narrowed view of the cell's word to <see cref="Interlocked"/>, and the
/// invariant they must not break: the bytes past the value stay zero, or every later comparison fails.
/// </summary>
public class NativeInterlockedTests
{
	private const Int32 Threads = 8;
	private const Int32 IncrementsPerThread = 10_000;

	[Fact]
	public void Increment_And_Decrement_ReturnTheNewValue()
	{
		var int32 = new Atomic<Int32>(10);
		int32.Increment().Should().Be(11);
		int32.Read().Should().Be(11);
		int32.Decrement().Should().Be(10);

		var int64 = new Atomic<Int64>(10L);
		int64.Increment().Should().Be(11L);
		int64.Decrement().Should().Be(10L);

		var uint32 = new Atomic<UInt32>(10U);
		uint32.Increment().Should().Be(11U);
		uint32.Decrement().Should().Be(10U);

		var uint64 = new Atomic<UInt64>(10UL);
		uint64.Increment().Should().Be(11UL);
		uint64.Decrement().Should().Be(10UL);
	}

	[Fact]
	public void Add_ReturnsTheNewValue()
	{
		new Atomic<Int32>(10).Add(5).Should().Be(15);
		new Atomic<Int64>(10L).Add(5L).Should().Be(15L);
		new Atomic<UInt32>(10U).Add(5U).Should().Be(15U);
		new Atomic<UInt64>(10UL).Add(5UL).Should().Be(15UL);

		var cell = new Atomic<Int32>(10);
		cell.Add(-3).Should().Be(7);
		cell.Read().Should().Be(7);
	}

	[Fact]
	public void And_And_Or_ReturnTheOldValue()
	{
		var flags = new Atomic<Int32>(0b1100);

		flags.Or(0b0011).Should().Be(0b1100);
		flags.Read().Should().Be(0b1111);

		flags.And(0b1010).Should().Be(0b1111);
		flags.Read().Should().Be(0b1010);
	}

	[Fact]
	public void Increment_WhenTheValueWraps_LeavesTheRestOfTheWordAlone()
	{
		// The instruction acts on four bytes of an eight byte word. If it carried into the bytes the
		// cell keeps zeroed, the value would still read back correctly while every later comparison
		// compared a bit pattern nothing could match. The exchange below is what proves it did not.
		var atomic = new Atomic<Int32>(Int32.MaxValue);

		atomic.Increment().Should().Be(Int32.MinValue);
		atomic.Read().Should().Be(Int32.MinValue);

		atomic.TryCompareExchange(7, Int32.MinValue, out var previous).Should().BeTrue();
		previous.Should().Be(Int32.MinValue);
		atomic.Read().Should().Be(7);
	}

	[Fact]
	public void Decrement_WhenTheValueWraps_LeavesTheRestOfTheWordAlone()
	{
		var atomic = new Atomic<Int32>(Int32.MinValue);

		atomic.Decrement().Should().Be(Int32.MaxValue);

		atomic.TryCompareExchange(7, Int32.MaxValue, out _).Should().BeTrue();
		atomic.Read().Should().Be(7);
	}

	[Fact]
	public void Add_WhenTheValueOverflows_LeavesTheRestOfTheWordAlone()
	{
		var atomic = new Atomic<UInt32>(UInt32.MaxValue);

		atomic.Add(2U).Should().Be(1U);

		atomic.TryCompareExchange(7U, 1U, out _).Should().BeTrue();
		atomic.Read().Should().Be(7U);
	}

	[Fact]
	public void TheInstructionAndTheLoopAgree()
	{
		// Both spellings reach the instruction now. The closed overload here wins overload resolution,
		// and naming AtomicExtensions reaches the open one, which specialises to the same call once the
		// JIT knows the type. So the loop they have to agree with is written out below, because the
		// library no longer offers a way to ask for it.
		var byInstruction = new Atomic<Int32>(10);
		var byLoop = new Atomic<Int32>(10);

		byInstruction.Increment().Should().Be(Loop(byLoop, current => current + 1));
		byInstruction.Add(5).Should().Be(Loop(byLoop, current => current + 5));
		byInstruction.Or(0b1010).Should().Be(Loop(byLoop, current => current | 0b1010, returnsOld: true));
		byInstruction.And(0b0110).Should().Be(Loop(byLoop, current => current & 0b0110, returnsOld: true));
		byInstruction.Read().Should().Be(byLoop.Read());
	}

	[Fact]
	public void WhenTheCallerIsGeneric_TheInstructionIsStillWhatRuns()
	{
		// Overload resolution happens where the type is written down, so generic code cannot reach the
		// closed overloads at all — before AtomicExtensions specialised, this was a compare-and-exchange
		// loop no matter what T turned out to be. Only the results are checked here; that the code
		// generated is the bare instruction is a fact about codegen, not something a test can observe.
		Generic.Increment(new Atomic<Int32>(10)).Should().Be(11);
		Generic.Increment(new Atomic<Int64>(10L)).Should().Be(11L);
		Generic.Increment(new Atomic<UInt32>(10U)).Should().Be(11U);
		Generic.Increment(new Atomic<UInt64>(10UL)).Should().Be(11UL);

		// A type with no instruction takes the loop, through the same call.
		Generic.Increment(new Atomic<Decimal>(10m)).Should().Be(11m);
		Generic.Increment(new Atomic<Int16>(10)).Should().Be(11);

		Generic.Add(new Atomic<Int32>(10), 5).Should().Be(15);
		Generic.Add(new Atomic<UInt64>(UInt64.MaxValue), 2UL).Should().Be(1UL);
		Generic.Add(new Atomic<Decimal>(10m), 5m).Should().Be(15m);
	}

	[Fact]
	public void WhenTheCallerIsGeneric_NothingIsAllocated()
	{
		// The specialisation crosses between T and the type the instruction acts on by casting through
		// Object, in both directions, which is a box and an unbox written down. Neither survives: the JIT
		// removes them where it knows the type, and it knows it here from the instantiation alone, before
		// any tiering. Were that ever to stop being true the code would still be correct and would
		// quietly allocate on every call, which is what this watches for.
		var counter = new Atomic<Int64>(0);
		var flags = new Atomic<Int32>(0);
		Generic.Increment(counter);
		Generic.Add(flags, 1);

		var before = GC.GetAllocatedBytesForCurrentThread();
		for (var i = 0; i < 1_000; i++)
		{
			Generic.Increment(counter);
			Generic.Add(flags, 1);
		}

		(GC.GetAllocatedBytesForCurrentThread() - before).Should().Be(0);
	}

	[Fact]
	public void WhenTheCallerIsGeneric_TheRestOfTheWordIsStillLeftAlone()
	{
		// The specialisation hands a four byte instruction a reference into an eight byte field, the same
		// as the closed overload does. An overflow carrying into the bytes the cell keeps zeroed would
		// read back correctly and fail every later comparison.
		var atomic = new Atomic<Int32>(Int32.MaxValue);

		Generic.Increment(atomic).Should().Be(Int32.MinValue);
		atomic.TryCompareExchange(7, Int32.MinValue, out _).Should().BeTrue();
		atomic.Read().Should().Be(7);
	}

	/// <summary>Applies <paramref name="update"/> with a compare-and-exchange loop written out by hand.</summary>
	/// <param name="atomic">The cell to update.</param>
	/// <param name="update">Produces the new value from the current one.</param>
	/// <param name="returnsOld">Whether to return the value replaced rather than the one stored.</param>
	/// <returns>The new value, or the old one when <paramref name="returnsOld"/> is set.</returns>
	private static Int32 Loop(Atomic<Int32> atomic, Func<Int32, Int32> update, Boolean returnsOld = false)
	{
		var current = atomic.Read();
		while (true)
		{
			var next = update(current);
			if (atomic.TryCompareExchange(next, current, out var previous))
				return returnsOld ? current : next;
			current = previous;
		}
	}

	/// <summary>
	/// Calls the extensions from code where <c>T</c> is still a type parameter, which is the only place
	/// the specialisation is what decides anything.
	/// </summary>
	private static class Generic
	{
		public static T Increment<T>(Atomic<T> atomic) where T : IIncrementOperators<T> => atomic.Increment();

		public static T Add<T>(Atomic<T> atomic, T addend) where T : IAdditionOperators<T, T, T> =>
			atomic.Add(addend);
	}

	[Fact]
	public async Task Increment_WhenContended_LosesNoUpdates()
	{
		var atomic = new Atomic<Int64>(0);

		await Task.WhenAll(Enumerable.Range(0, Threads).Select(_ => Task.Run(() =>
		{
			for (var i = 0; i < IncrementsPerThread; i++)
				atomic.Increment();
		})));

		atomic.Read().Should().Be(Threads * (Int64)IncrementsPerThread);
	}
}
