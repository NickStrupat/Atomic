using System.Runtime.CompilerServices;
using AwesomeAssertions;
using NickStrupat;

namespace Tests;

/// <summary>
/// Tests for the read-modify-write extensions. They extend <see cref="Atomic{T}"/> rather than
/// <see cref="IAtomic{T}"/>, so they are not part of the shared contract suite.
/// </summary>
/// <remarks>
/// Extending <see cref="Atomic{T}"/> directly removes an interface call from every iteration of the
/// loop, and lets the extensions reach the storage directly for the four integer types that have an
/// instruction. The cost is that the candidates in <c>Candidates</c> no longer run these methods. What
/// they do share — <see cref="IAtomic{T}.TryCompareExchange"/> called in a loop from many threads — is
/// covered by <see cref="AtomicContractTests"/>.
/// </remarks>
public class AtomicExtensionsTests
{
	private const Int32 Threads = 8;
	private const Int32 IncrementsPerThread = 10_000;
	private const Int64 Total = Threads * (Int64)IncrementsPerThread;

	[Fact]
	public void Add_And_Subtract_ReturnTheNewValue()
	{
		var inline = new Atomic<Int32>(10);
		inline.Add(5).Should().Be(15);
		inline.Read().Should().Be(15);
		inline.Subtract(3).Should().Be(12);
		inline.Read().Should().Be(12);

		var wide = new Atomic<Decimal>(10m);
		wide.Add(2.5m).Should().Be(12.5m);
		wide.Read().Should().Be(12.5m);
	}

	[Fact]
	public void Increment_And_Decrement_ReturnTheNewValue()
	{
		var cell = new Atomic<Int64>(10L);
		cell.Increment().Should().Be(11L);
		cell.Read().Should().Be(11L);
		cell.Decrement().Should().Be(10L);
		cell.Read().Should().Be(10L);
	}

	[Fact]
	public void And_Or_And_Xor_ReturnTheOldValue()
	{
		var flags = new Atomic<Int32>(0b1100);

		flags.Or(0b0011).Should().Be(0b1100);
		flags.Read().Should().Be(0b1111);

		flags.And(0b1010).Should().Be(0b1111);
		flags.Read().Should().Be(0b1010);

		flags.Xor(0b1111).Should().Be(0b1010);
		flags.Read().Should().Be(0b0101);
	}

	[Fact]
	public void Min_And_Max_ReturnTheValueHeldAfterwards()
	{
		var cell = new Atomic<Int32>(10);

		cell.Max(5).Should().Be(10);
		cell.Max(20).Should().Be(20);
		cell.Read().Should().Be(20);

		cell.Min(30).Should().Be(20);
		cell.Min(7).Should().Be(7);
		cell.Read().Should().Be(7);
	}

	[Fact]
	public async Task Increment_WhenContended_LosesNoUpdates()
	{
		var inline = new Atomic<Int64>(0L);
		var wide = new Atomic<Decimal>(0m);

		await RunOnAllThreads(() =>
		{
			for (var i = 0; i < IncrementsPerThread; i++)
			{
				inline.Increment();
				wide.Add(1m);
			}
		});

		inline.Read().Should().Be(Total);
		wide.Read().Should().Be(Total);
	}

	private static Task RunOnAllThreads(Action action) =>
		Task.WhenAll(Enumerable.Range(0, Threads).Select(_ => Task.Run(action)));

	[Fact]
	public void ReadModifyWrite_WhenTheCellLocks_ComputesUnderTheLockRatherThanBetweenTwo()
	{
		// A cell behind its monitor can do the whole operation in one acquisition, and a loop would take
		// one to read and another to exchange, running the operator in the gap with nothing held — where
		// a second writer can land and send it round again. One thread at a time both give the same
		// answer, so what is checked is where the operator ran.
		Atomic<Witnessed>.IsLockFree.Should().BeFalse("this is about the strategy that locks");
		var cell = new Atomic<Witnessed>(new Witnessed(0b0110, 0));
		Witnessed.Watch(cell);

		cell.Add(new Witnessed(4, 0)).Value.Should().Be(10);
		cell.Subtract(new Witnessed(2, 0)).Value.Should().Be(8);
		cell.Increment().Value.Should().Be(9);
		cell.Decrement().Value.Should().Be(8);
		cell.Or(new Witnessed(0b0011, 0)).Value.Should().Be(8, "these return the old value");
		cell.And(new Witnessed(0b1010, 0)).Value.Should().Be(0b1011);
		cell.Xor(new Witnessed(0b0001, 0)).Value.Should().Be(0b1010);
		cell.Max(new Witnessed(20, 0)).Value.Should().Be(20);
		cell.Min(new Witnessed(3, 0)).Value.Should().Be(3);
		cell.Read().Value.Should().Be(3);

		// Nine operations, each running at least one operator: none having run would pass the next line.
		Witnessed.Calls.Should().BeGreaterThanOrEqualTo(9);
		Witnessed.CallsOutsideTheLock.Should().Be(0, "the operator runs under the lock the cell already holds");
	}

	[Fact]
	public void BitwiseOperations_WhenTheValueIsAnEnum_ApplyToItAtEveryWidth()
	{
		// An enum implements no interfaces, so it reaches none of the operator-constrained extensions.
		// All four widths an enum can be: the two an instruction exists for outright, and the two
		// narrower than any of them, which are masked through the word the cell keeps them in. The high
		// bit of MidAccess goes into an Or mask because a 2-byte mask sign extended rather than zero
		// extended would set bits in the slack there; And cannot, since anding the sign bits into a zero
		// slack leaves it zero. Nothing read out of the cell can show that either — Read and the old values
		// returned both narrow the word, and a compare-exchange that misses on the slack falls back to
		// Equals and succeeds anyway — so the narrow two check the word itself after every operation.
		var wide = new Atomic<WideAccess>(WideAccess.Read);
		wide.Or(WideAccess.Reserved).Should().Be(WideAccess.Read, "these return the old value");
		wide.Read().Should().Be(WideAccess.Read | WideAccess.Reserved);
		wide.And(WideAccess.Reserved).Should().Be(WideAccess.Read | WideAccess.Reserved);
		wide.Read().Should().Be(WideAccess.Reserved);

		var word = new Atomic<Access>(Access.Read);
		word.Or(Access.Write).Should().Be(Access.Read);
		word.Read().Should().Be(Access.Read | Access.Write);
		word.And(Access.Write).Should().Be(Access.Read | Access.Write);
		word.Read().Should().Be(Access.Write);
		word.Xor(Access.Write | Access.Execute).Should().Be(Access.Write);
		word.Read().Should().Be(Access.Execute);

		var mid = new Atomic<MidAccess>(MidAccess.Read);
		mid.Or(MidAccess.Write | MidAccess.High).Should().Be(MidAccess.Read);
		mid.Read().Should().Be(MidAccess.Read | MidAccess.Write | MidAccess.High);
		ShouldHaveZeroSlack(mid);
		mid.And(MidAccess.High | MidAccess.Write).Should().Be(MidAccess.Read | MidAccess.Write | MidAccess.High);
		mid.Read().Should().Be(MidAccess.Write | MidAccess.High);
		ShouldHaveZeroSlack(mid);
		mid.Xor(MidAccess.High).Should().Be(MidAccess.Write | MidAccess.High);
		mid.Read().Should().Be(MidAccess.Write);
		ShouldHaveZeroSlack(mid);

		var narrow = new Atomic<NarrowAccess>(NarrowAccess.Read);
		narrow.Or(NarrowAccess.Write).Should().Be(NarrowAccess.Read);
		narrow.Read().Should().Be(NarrowAccess.Read | NarrowAccess.Write);
		ShouldHaveZeroSlack(narrow);
		narrow.And(NarrowAccess.Write).Should().Be(NarrowAccess.Read | NarrowAccess.Write);
		narrow.Read().Should().Be(NarrowAccess.Write);
		ShouldHaveZeroSlack(narrow);
		narrow.Xor(NarrowAccess.Execute).Should().Be(NarrowAccess.Write);
		narrow.Read().Should().Be(NarrowAccess.Write | NarrowAccess.Execute);
		ShouldHaveZeroSlack(narrow);
	}

	[Fact]
	public void BitwiseOperations_WhenTheValueIsAnEnum_AllocateNothing()
	{
		var cell = new Atomic<Access>(Access.None);
		cell.Or(Access.Read);

		var before = GC.GetAllocatedBytesForCurrentThread();
		for (var i = 0; i < 1_000; i++)
		{
			cell.Or(Access.Write);
			cell.And(Access.Read);
			cell.Xor(Access.Execute);
		}

		(GC.GetAllocatedBytesForCurrentThread() - before).Should().Be(0);
	}

	/// <summary>
	/// Asserts that the word holding a narrow value contains that value followed by zeros.
	/// </summary>
	/// <typeparam name="T">A type narrower than a word, so the cell stores it in a word.</typeparam>
	/// <param name="atomic">The cell to inspect.</param>
	/// <remarks>
	/// <see cref="Atomic{T}.Read"/> ignores the spare bytes, so they have to be read from the field
	/// directly. Non-zero spare bytes do not produce wrong answers, but they break the cell's invariant:
	/// every later compare-exchange misses on the instruction and has to fall back to the type's equality,
	/// and a type with no equality of its own allocates to do so.
	/// </remarks>
	private static void ShouldHaveZeroSlack<T>(Atomic<T> atomic) where T : unmanaged
	{
		var expected = UIntPtr.Zero;
		Unsafe.WriteUnaligned(ref Unsafe.As<UIntPtr, Byte>(ref expected), atomic.Read());
		var word = Unsafe.As<T, UIntPtr>(ref atomic.Storage);
		((UInt64)word).Should().Be((UInt64)expected, "writes zero the slack, and so must a mask");
	}
}
