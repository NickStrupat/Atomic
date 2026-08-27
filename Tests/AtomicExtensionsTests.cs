using AwesomeAssertions;
using NickStrupat;

namespace Tests;

/// <summary>
/// The read-modify-write extensions, which are declared against <see cref="Atomic{T}"/> rather than
/// <see cref="IAtomic{T}"/> and so are not part of the shared contract suite.
/// </summary>
/// <remarks>
/// Declaring them on the closed type is what lets <see cref="AtomicInterlockedExtensions"/> take over
/// for the four integers that have an instruction, and it drops an interface dispatch from every
/// iteration of the loop for everything else. The cost is that the candidates in <c>Candidates</c> no
/// longer exercise these methods; what they still share — <see cref="IAtomic{T}.TryCompareExchange"/>
/// driven in a loop by many threads — is covered by <see cref="AtomicContractTests"/> directly.
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
	public void Update_WhenCalled_AppliesTheFunctionAndReturnsWhatItStored()
	{
		var cell = new Atomic<String>("a");
		cell.Update(current => current + "b").Should().Be("ab");
		cell.Read().Should().Be("ab");

		cell.Update("c", (suffix, current) => current + suffix).Should().Be("abc");
		cell.Read().Should().Be("abc");
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
}
