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
		// Called normally, the closed overload in AtomicInterlockedExtensions wins and this is the
		// instruction. Naming AtomicExtensions reaches past it to the open one, which is the loop. Both
		// are in scope for the same receiver, and they are meant to be indistinguishable.
		var byInstruction = new Atomic<Int32>(10);
		var byLoop = new Atomic<Int32>(10);

		byInstruction.Increment().Should().Be(AtomicExtensions.Increment(byLoop));
		byInstruction.Add(5).Should().Be(AtomicExtensions.Add(byLoop, 5));
		byInstruction.Or(0b1010).Should().Be(AtomicExtensions.Or(byLoop, 0b1010));
		byInstruction.And(0b0110).Should().Be(AtomicExtensions.And(byLoop, 0b0110));
		byInstruction.Read().Should().Be(byLoop.Read());
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
