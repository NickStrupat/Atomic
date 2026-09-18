using System.Runtime.CompilerServices;
using AwesomeAssertions;
using NickStrupat;
using ObjectLayoutInspector;

namespace Tests;

/// <summary>
/// Where each implementation puts the value, and what that costs. Unlike
/// <see cref="AtomicContractTests"/>, these are the differences between the shipping cell and the
/// candidates it was chosen over, rather than the behaviour they share.
/// </summary>
public class StorageTests
{
	private const Int32 Iterations = 10_000;

	/// <summary>What one box costs on this runtime, at its cheapest and its dearest.</summary>
	/// <remarks>
	/// <para>
	/// This was written down as 32 — an object header plus a Decimal — until a 32-bit runtime
	/// disagreed. A Decimal wants an 8-byte boundary and a 4-byte object header does not leave it
	/// on one, so such a box is 28 bytes and the allocator puts a 12-byte filler in front of it to
	/// seat it. 28 and 12 come to a multiple of eight, so the next one needs a filler too: measured on an
	/// arm32 Pi, 40 bytes were charged for 19902 boxes out of 20000 and 28 for the rest, in no fixed
	/// proportion. A class holding four Int32s is 24 bytes every single time, so this is the alignment
	/// and not the header. At 64 bits nothing is padded and both ends of this are 32.
	/// </para>
	/// <para>
	/// So what the two tests below assert is the number of boxes and not a byte count: a band wide enough
	/// for one box per call and too narrow for two. It is measured against <see cref="BoxOfDecimal"/>
	/// rather than against the cell under test, because calibrating on the thing being measured would let
	/// a cell that built a box per attempt set its own band and pass — which is the regression the second
	/// test exists for.
	/// </para>
	/// </remarks>
	private static readonly (Int64 Least, Int64 Most) OneBox = MeasureOneBox();

	[Fact]
	public void Atomic_TakesALockOnlyForValuesItCannotSwapInPlace()
	{
		// What fits the word is the word's business, so half of these are stated against its width.
		var wordIsEightBytes = IntPtr.Size == sizeof(Int64);

		Atomic<Int32>.IsLockFree.Should().BeTrue();
		Atomic<Colour>.IsLockFree.Should().BeTrue();
		Atomic<String>.IsLockFree.Should().BeTrue();

		// The 8-byte integers are swapped in place at either width — through the word at 64
		// bits, through Interlocked at 32, where the runtime seats a field of one of these two
		// types for the instructions that reach it. Double is the same size and is not offered that,
		// because its equality is not its bits and the wide path has no tail to reconcile a miss with.
		Atomic<Int64>.IsLockFree.Should().BeTrue();
		Atomic<UInt64>.IsLockFree.Should().BeTrue();
		Atomic<Double>.IsLockFree.Should().Be(wordIsEightBytes);

		// A nullable reference is still just a reference, so it never needs the monitor. Nullable<Int32>
		// is 8 unmanaged bytes, so it fits the word only at 64 bits. Both only compile since
		// T stopped requiring notnull.
		Atomic<String?>.IsLockFree.Should().BeTrue();
		Atomic<Int32?>.IsLockFree.Should().Be(wordIsEightBytes);

		Atomic<Twelve>.IsLockFree.Should().BeFalse();
		Atomic<Guid>.IsLockFree.Should().BeFalse();
		Atomic<Decimal>.IsLockFree.Should().BeFalse();
		Atomic<WithReference>.IsLockFree.Should().BeFalse();
	}

	[Fact]
	public void Atomic_WhenValueIsAnAwkwardSizeOrAlignment_StillSwapsInPlace()
	{
		var wordIsEightBytes = IntPtr.Size == sizeof(Int64);

		// A lone field begins on a word boundary, and the minimum size of an object leaves a whole word
		// there, so a value of a size no instruction matches is widened to a word rather than locked. How
		// many of these that covers is the width of the word: 3 bytes fit either one.
		Atomic<Three>.IsLockFree.Should().BeTrue();
		Atomic<Five>.IsLockFree.Should().Be(wordIsEightBytes);
		Atomic<Six>.IsLockFree.Should().Be(wordIsEightBytes);
		Atomic<Seven>.IsLockFree.Should().Be(wordIsEightBytes);

		// Eight is two Int32 fields, so its own alignment is 4. At 64 bits the field it sits in
		// is word aligned regardless, which is what the instruction actually needs. At 32 nothing
		// promises it an 8-byte boundary — which is the case the wide path exists around, and why that
		// path names Int64 and UInt64 rather than admitting everything of their size.
		Unsafe.SizeOf<Eight>().Should().Be(sizeof(Int64));
		Atomic<Eight>.IsLockFree.Should().Be(wordIsEightBytes);
	}

	[Fact]
	public void BoxAtomic_WhenTypeIsUnmanagedAndFitsInAWord_HoldsItInTheWord()
	{
		BoxAtomic<Int32>.IsInlineStorage.Should().BeTrue();
		BoxAtomic<Double>.IsInlineStorage.Should().BeTrue();
		BoxAtomic<DateTime>.IsInlineStorage.Should().BeTrue();
		BoxAtomic<Eight>.IsInlineStorage.Should().BeTrue();
		BoxAtomic<Three>.IsInlineStorage.Should().BeTrue();
	}

	[Fact]
	public void BoxAtomic_WhenValueIsWiderThanAWordOrHoldsReferences_BoxesIt()
	{
		Unsafe.SizeOf<Twelve>().Should().BeGreaterThan(sizeof(Int64));
		BoxAtomic<Twelve>.IsInlineStorage.Should().BeFalse();
		BoxAtomic<Guid>.IsInlineStorage.Should().BeFalse();
		BoxAtomic<Decimal>.IsInlineStorage.Should().BeFalse();

		// Narrow enough to fit, so only the reference it holds keeps it out of the word.
		Unsafe.SizeOf<WithReference>().Should().BeLessThanOrEqualTo(sizeof(Int64));
		BoxAtomic<WithReference>.IsInlineStorage.Should().BeFalse();
		BoxAtomic<(Int32, String)>.IsInlineStorage.Should().BeFalse();
	}

	[Fact]
	public void BoxAtomic_WhenTypeIsAReference_HoldsItInTheSlot()
	{
		BoxAtomic<String>.IsInlineStorage.Should().BeFalse();
		BoxAtomic<Object>.IsInlineStorage.Should().BeFalse();
		BoxAtomic<List<Int32>>.IsInlineStorage.Should().BeFalse();
	}

	[Fact]
	public void SeqLockAtomic_PutsReadersOnTheMonitorOnlyForValuesHoldingReferences()
	{
		SeqLockAtomic<Int32>.ReadsTakeNoMonitor.Should().BeTrue();
		SeqLockAtomic<String>.ReadsTakeNoMonitor.Should().BeTrue();
		SeqLockAtomic<Decimal>.ReadsTakeNoMonitor.Should().BeTrue();
		SeqLockAtomic<Twelve>.ReadsTakeNoMonitor.Should().BeTrue();

		// A torn read of a reference cannot be retried away, so these take a lock instead.
		SeqLockAtomic<WithReference>.ReadsTakeNoMonitor.Should().BeFalse();
		SeqLockAtomic<(Int32, String)>.ReadsTakeNoMonitor.Should().BeFalse();
	}

	[Fact]
	public void Atomic_SeatsTheValueOnAWordBoundary_WhichTheInlineStrategyAssumesRatherThanChecks()
	{
		// Atomic<T> used to consult this at run time and fall back to the monitor if it failed. The check
		// cost every access a static load and a branch under NativeAOT, which cannot fold a probe that
		// reads the address of an object, so it lives here instead — and entirely here, since a shipping
		// type has no business carrying a method only a test calls. A runtime that seated the field
		// differently would now fault rather than quietly slow down, and this is what would catch it.
		//
		// The claim is about a word, which is what the widened view is, so it is the same claim at either
		// width and runs at both. ECMA-335 I.12.6.2 aligns a value on the boundary a native int needs,
		// which is exactly what this asks for.
		FieldIsAlignedTo<Byte>(IntPtr.Size).Should().BeTrue();
		FieldIsAlignedTo<Int16>(IntPtr.Size).Should().BeTrue();
		FieldIsAlignedTo<Int32>(IntPtr.Size).Should().BeTrue();
		FieldIsAlignedTo<Int64>(IntPtr.Size).Should().BeTrue();
		FieldIsAlignedTo<Double>(IntPtr.Size).Should().BeTrue();
		FieldIsAlignedTo<Colour>(IntPtr.Size).Should().BeTrue();

		// The awkward sizes matter most: these are the ones with slack behind them.
		FieldIsAlignedTo<Three>(IntPtr.Size).Should().BeTrue();
		FieldIsAlignedTo<Five>(IntPtr.Size).Should().BeTrue();
		FieldIsAlignedTo<Six>(IntPtr.Size).Should().BeTrue();
		FieldIsAlignedTo<Seven>(IntPtr.Size).Should().BeTrue();

		// Eight bytes with an alignment of 4, the case that faulted on arm64 when a second field
		// pushed it off a word boundary.
		FieldIsAlignedTo<Eight>(IntPtr.Size).Should().BeTrue();
	}

	[Fact]
	public void Atomic_SeatsAnEightByteIntegerForTheInstructionsThatReachIt()
	{
		// At 64 bits this is the word claim over again. At 32 it is a different promise,
		// and the only reason Int64 and UInt64 are swapped in place there at all: a field of either is
		// seated on an 8-byte boundary wherever the hardware's instructions demand it, which is what
		// lets Interlocked be pointed at one. Nothing establishes that of an arbitrary 8-byte value —
		// Eight, one row up, is the counterexample — so the wide path names these two and stops.
		FieldIsAlignedTo<Int64>(sizeof(Int64)).Should().BeTrue();
		FieldIsAlignedTo<UInt64>(sizeof(Int64)).Should().BeTrue();
	}

	[Fact]
	public async Task SeqLockAtomic_ComparesOutsideTheCounter_SoAReentrantEqualsCannotDeadlock()
	{
		var cell = new SeqLockAtomic<Reentrant>(new Reentrant { A = 1, B = 2, C = 3 });
		Reentrant.Cell = cell;
		try
		{
			var swap = Task.Run(
				() => cell.TryCompareExchange(
					new Reentrant { A = 9, B = 9, C = 9 },
					new Reentrant { A = 1, B = 2, C = 3 },
					out _),
				TestContext.Current.CancellationToken);

			// Comparing under the counter does not fail here, it hangs, so the assertion has to be a deadline.
			var finished = await Task.WhenAny(swap, Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
			finished.Should().BeSameAs(swap, "a comparison that reads the cell must not wait on the counter its own call is holding");

			(await swap).Should().BeTrue();
			cell.Read().A.Should().Be(9);
			Reentrant.Reads.Should().BeGreaterThan(0, "the comparison has to have actually read the cell for this to prove anything");
		}
		finally
		{
			Reentrant.Cell = null;
		}
	}

	[Fact]
	public void SeqLockAtomic_ReadsWithoutRetryingOnlyForValuesItCanLoadOutright()
	{
		SeqLockAtomic<Int32>.ReadsAreWaitFree.Should().BeTrue();
		SeqLockAtomic<String>.ReadsAreWaitFree.Should().BeTrue();

		// Wide enough to need the counter, so the read is a retry loop a writer can starve. Staying off
		// the monitor is not the same promise as finishing in a bounded number of steps.
		SeqLockAtomic<Decimal>.ReadsAreWaitFree.Should().BeFalse();
		SeqLockAtomic<Twelve>.ReadsAreWaitFree.Should().BeFalse();
		SeqLockAtomic<Decimal>.ReadsTakeNoMonitor.Should().BeTrue();
		SeqLockAtomic<Twelve>.ReadsTakeNoMonitor.Should().BeTrue();

		SeqLockAtomic<WithReference>.ReadsAreWaitFree.Should().BeFalse();
		SeqLockAtomic<(Int32, String)>.ReadsAreWaitFree.Should().BeFalse();
	}

	[Theory]
	// One field, laid out to fit T. A 3-byte value still gets a whole word of field area, which is
	// what makes widening it safe.
	[InlineData(typeof(Atomic<Int32>), 8)]
	[InlineData(typeof(Atomic<Three>), 8)]
	[InlineData(typeof(Atomic<String>), 8)]
	[InlineData(typeof(Atomic<Decimal>), 16)]
	// A word and an object slot, whatever T is.
	[InlineData(typeof(BoxAtomic<Int32>), 16)]
	[InlineData(typeof(BoxAtomic<String>), 16)]
	[InlineData(typeof(BoxAtomic<Decimal>), 16)]
	// One field plus the version counter. At 64 bits the counter costs a word of its own for a T
	// smaller than one — the instantiations that never read it — and disappears into padding for the wide
	// ones that do.
	[InlineData(typeof(SeqLockAtomic<Byte>), 16)]
	[InlineData(typeof(SeqLockAtomic<Int32>), 16)]
	[InlineData(typeof(SeqLockAtomic<String>), 16)]
	[InlineData(typeof(SeqLockAtomic<Decimal>), 24)]
	public void TypeLayout_MatchesTheStrategy(Type type, Int32 expectedFieldBytes)
	{
		if (IntPtr.Size != sizeof(Int64))
			Assert.Skip("these sizes are written down for a 64-bit runtime");

		// The fields alone; the object header is another 16 bytes on top.
		TypeLayout.GetLayout(type).Size.Should().Be(expectedFieldBytes);
	}

	[Fact]
	public void Write_WhenValueFitsInAWord_AllocatesNothingInAnyImplementation()
	{
		MeasureWrites(new AtomicAdapter<Int64>(new Atomic<Int64>(0)), 1).Should().Be(0);
		MeasureWrites(new BoxAtomic<Int64>(0), 1).Should().Be(0);
		MeasureWrites(new SeqLockAtomic<Int64>(0), 1).Should().Be(0);
	}

	[Fact]
	public void Write_WhenValueIsAReference_AllocatesNothingInAnyImplementation()
	{
		var text = new String(['a']);

		MeasureWrites(new AtomicAdapter<String>(new Atomic<String>("")), text).Should().Be(0);
		MeasureWrites(new BoxAtomic<String>(""), text).Should().Be(0);
		MeasureWrites(new SeqLockAtomic<String>(""), text).Should().Be(0);
	}

	[Fact]
	public void Write_WhenValueIsWiderThanAWord_AllocatesOnlyWhereTheValueIsBoxed()
	{
		MeasureWrites(new BoxAtomic<Decimal>(0m), 1m)
			.Should().BeInRange(Iterations * OneBox.Least, Iterations * OneBox.Most);

		MeasureWrites(new AtomicAdapter<Decimal>(new Atomic<Decimal>(0m)), 1m).Should().Be(0);
		MeasureWrites(new SeqLockAtomic<Decimal>(0m), 1m).Should().Be(0);
	}

	[Fact]
	public void BoxAtomic_WhenAnExchangeRetries_BuildsOneBoxPerCallRatherThanOnePerAttempt()
	{
		// Every thread exchanges the value the cell already holds, so the comparison always passes and a
		// thread whose exchange loses finds the same comparand still waiting and goes round the inner
		// loop. Building the box inside that loop spent one on every attempt: at eight threads this
		// measured 155 bytes per call, against the 28 to 40 one box costs. The box cannot be built any
		// later than it is —
		// nothing can be exchanged in before it exists — but it need not be built again.
		//
		// A run that happened to see no contention would pass without proving anything. Eight threads
		// released together on one cell is not that run.
		const Int32 Threads = 8;
		const Int32 PerThread = 20_000;
		var cell = new BoxAtomic<Decimal>(7m);
		var bytes = 0L;
		using var start = new Barrier(Threads);

		var threads = Enumerable.Range(0, Threads).Select(_ => new Thread(() =>
		{
			start.SignalAndWait();
			var before = GC.GetAllocatedBytesForCurrentThread();
			for (var i = 0; i < PerThread; i++)
				cell.CompareExchange(7m, 7m);
			Interlocked.Add(ref bytes, GC.GetAllocatedBytesForCurrentThread() - before);
		}) { IsBackground = true }).ToArray();

		foreach (var thread in threads)
			thread.Start();
		foreach (var thread in threads)
			thread.Join();

		bytes.Should().BeInRange(
			Threads * (Int64)PerThread * OneBox.Least,
			Threads * (Int64)PerThread * OneBox.Most);
	}

	/// <summary>Checks what boundary a cell's field really does begin on.</summary>
	/// <typeparam name="T">The type of the value held by the cell.</typeparam>
	/// <param name="alignment">The boundary the field is expected to be seated on, in bytes.</param>
	/// <returns><see langword="true"/> when a view of that width over the field would be aligned.</returns>
	/// <remarks>
	/// The alignment follows from <see cref="Atomic{T}"/> declaring a single field, so this asserts an
	/// invariant rather than discovering a fact. A collection moving the cell is harmless: the offset of
	/// the field within the object is fixed and every object begins on a word boundary, so the answer
	/// does not depend on where it sits.
	/// </remarks>
	private static unsafe Boolean FieldIsAlignedTo<T>(Int32 alignment) where T : unmanaged
	{
		var probe = new Atomic<T>(default);
		return ((nint)Unsafe.AsPointer(ref probe.Storage) & (alignment - 1)) == 0;
	}

	/// <summary>A class shaped like the box <see cref="BoxAtomic{T}"/> builds for a wide value.</summary>
	/// <remarks>
	/// Deliberately not that box, which is private and would be the wrong thing to measure anyway: see
	/// the remarks on <see cref="OneBox"/>.
	/// </remarks>
	private sealed class BoxOfDecimal(Decimal value)
	{
		internal readonly Decimal Value = value;
	}

	/// <summary>Keeps a measured allocation reachable, so that nothing is free to delete it.</summary>
	private static Object? sink;

	/// <summary>Measures what a single box is charged, at its cheapest and its dearest.</summary>
	/// <returns>The least and the most that one allocation cost.</returns>
	private static (Int64 Least, Int64 Most) MeasureOneBox()
	{
		for (var i = 0; i < 1_000; i++)
			sink = new BoxOfDecimal(i);

		Int64 least = Int64.MaxValue, most = 0;
		for (var i = 0; i < 2_000; i++)
		{
			var before = GC.GetAllocatedBytesForCurrentThread();
			sink = new BoxOfDecimal(i);
			var charged = GC.GetAllocatedBytesForCurrentThread() - before;
			least = Math.Min(least, charged);
			most = Math.Max(most, charged);
		}

		return (least, most);
	}

	private static Int64 MeasureWrites<T>(IAtomic<T> atomic, T value)
	{
		atomic.Write(value); // warm up before measuring
		var before = GC.GetAllocatedBytesForCurrentThread();
		for (var i = 0; i < Iterations; i++)
			atomic.Write(value);
		return GC.GetAllocatedBytesForCurrentThread() - before;
	}
}
