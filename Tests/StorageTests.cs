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
	private const Int64 BoxSize = 32; // object header plus a Decimal

	[Fact]
	public void Atomic_TakesALockOnlyForValuesItCannotSwapInPlace()
	{
		Atomic<Int32>.IsLockFree.Should().BeTrue();
		Atomic<Int64>.IsLockFree.Should().BeTrue();
		Atomic<Double>.IsLockFree.Should().BeTrue();
		Atomic<Colour>.IsLockFree.Should().BeTrue();
		Atomic<String>.IsLockFree.Should().BeTrue();

		// A nullable reference is still just a reference, and Nullable<Int32> is eight unmanaged bytes,
		// so neither needs the monitor. Both only compile since T stopped requiring notnull.
		Atomic<String?>.IsLockFree.Should().BeTrue();
		Atomic<Int32?>.IsLockFree.Should().BeTrue();

		Atomic<Twelve>.IsLockFree.Should().BeFalse();
		Atomic<Guid>.IsLockFree.Should().BeFalse();
		Atomic<Decimal>.IsLockFree.Should().BeFalse();
		Atomic<WithReference>.IsLockFree.Should().BeFalse();
	}

	[Fact]
	public void Atomic_WhenValueIsAnAwkwardSizeOrAlignment_StillSwapsInPlace()
	{
		// A lone field begins on a word boundary, and the minimum size of an object leaves a whole word
		// there, so a value of a size no instruction matches is widened to eight bytes rather than locked.
		Atomic<Three>.IsLockFree.Should().BeTrue();
		Atomic<Five>.IsLockFree.Should().BeTrue();
		Atomic<Six>.IsLockFree.Should().BeTrue();
		Atomic<Seven>.IsLockFree.Should().BeTrue();

		// Eight is two Int32 fields, so its own alignment is four. The field it sits in is word aligned
		// regardless, which is what the instruction actually needs.
		Unsafe.SizeOf<Eight>().Should().Be(sizeof(Int64));
		Atomic<Eight>.IsLockFree.Should().BeTrue();
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
		// Only a sixty four bit runtime promises this. ECMA-335 I.12.6.2 aligns an eight byte value on the
		// boundary a native int needs, so a thirty two bit runtime may seat it four bytes in — which is why
		// Atomic<T> sends everything to the monitor there, and why there is nothing to assert.
		if (IntPtr.Size != sizeof(Int64))
			return;

		FieldIsWordAligned<Byte>().Should().BeTrue();
		FieldIsWordAligned<Int16>().Should().BeTrue();
		FieldIsWordAligned<Int32>().Should().BeTrue();
		FieldIsWordAligned<Int64>().Should().BeTrue();
		FieldIsWordAligned<Double>().Should().BeTrue();
		FieldIsWordAligned<Colour>().Should().BeTrue();

		// The awkward sizes matter most: these are the ones with slack behind them.
		FieldIsWordAligned<Three>().Should().BeTrue();
		FieldIsWordAligned<Five>().Should().BeTrue();
		FieldIsWordAligned<Six>().Should().BeTrue();
		FieldIsWordAligned<Seven>().Should().BeTrue();

		// Eight bytes with an alignment of four, the case that faulted on arm64 when a second field
		// pushed it off a word boundary.
		FieldIsWordAligned<Eight>().Should().BeTrue();
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
	// One field, laid out to fit T. A three byte value still gets a whole word of field area, which is
	// what makes widening it safe.
	[InlineData(typeof(Atomic<Int32>), 8)]
	[InlineData(typeof(Atomic<Three>), 8)]
	[InlineData(typeof(Atomic<String>), 8)]
	[InlineData(typeof(Atomic<Decimal>), 16)]
	// A word and an object slot, whatever T is.
	[InlineData(typeof(BoxAtomic<Int32>), 16)]
	[InlineData(typeof(BoxAtomic<String>), 16)]
	[InlineData(typeof(BoxAtomic<Decimal>), 16)]
	// One field plus the version counter. At sixty four bits the counter costs a word of its own for a T
	// smaller than one — the instantiations that never read it — and disappears into padding for the wide
	// ones that do.
	[InlineData(typeof(SeqLockAtomic<Byte>), 16)]
	[InlineData(typeof(SeqLockAtomic<Int32>), 16)]
	[InlineData(typeof(SeqLockAtomic<String>), 16)]
	[InlineData(typeof(SeqLockAtomic<Decimal>), 24)]
	public void TypeLayout_MatchesTheStrategy(Type type, Int32 expectedFieldBytes) =>
		// The fields alone; the object header is another 16 bytes on top.
		TypeLayout.GetLayout(type).Size.Should().Be(expectedFieldBytes);

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
		MeasureWrites(new BoxAtomic<Decimal>(0m), 1m).Should().Be(Iterations * BoxSize);

		MeasureWrites(new AtomicAdapter<Decimal>(new Atomic<Decimal>(0m)), 1m).Should().Be(0);
		MeasureWrites(new SeqLockAtomic<Decimal>(0m), 1m).Should().Be(0);
	}

	[Fact]
	public void BoxAtomic_WhenAnExchangeRetries_BuildsOneBoxPerCallRatherThanOnePerAttempt()
	{
		// Every thread exchanges the value the cell already holds, so the comparison always passes and a
		// thread whose exchange loses finds the same comparand still waiting and goes round the inner
		// loop. Building the box inside that loop spent one on every attempt: at eight threads this
		// measured 155 bytes per call rather than 32. The box cannot be built any later than it is —
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

		bytes.Should().Be(Threads * (Int64)PerThread * BoxSize);
	}

	/// <summary>Checks that a cell's field really does begin on a word boundary.</summary>
	/// <typeparam name="T">The type of the value held by the cell.</typeparam>
	/// <returns><see langword="true"/> when an eight byte view of the field would be aligned.</returns>
	/// <remarks>
	/// The alignment follows from <see cref="Atomic{T}"/> declaring a single field, so this asserts an
	/// invariant rather than discovering a fact. A collection moving the cell is harmless: the offset of
	/// the field within the object is fixed and every object begins on a word boundary, so the answer
	/// does not depend on where it sits.
	/// </remarks>
	private static unsafe Boolean FieldIsWordAligned<T>() where T : unmanaged
	{
		var probe = new Atomic<T>(default);
		return ((nint)Unsafe.AsPointer(ref probe.Storage) & (sizeof(Int64) - 1)) == 0;
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
