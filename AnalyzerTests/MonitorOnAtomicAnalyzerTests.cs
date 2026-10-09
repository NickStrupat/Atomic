using NickStrupat.Analyzers;

namespace AnalyzerTests;

public class MonitorOnAtomicAnalyzerTests
{
	private const String Usings = """
		using System;
		using System.Threading;
		using NickStrupat;

		""";

	/// <summary>
	/// A stand-in for the library: a type with the same metadata name as the cell, locking on itself the
	/// way the real cell does on its lock path.
	/// </summary>
	private const String StandIn = """
		namespace NickStrupat
		{
			public sealed class Atomic<T>
			{
				private T storage;
				public Atomic(T value) => storage = value;
				public T Read() { lock (this) return storage; }
				public void Write(T value) { System.Threading.Monitor.Enter(this); storage = value; System.Threading.Monitor.Exit(this); }
			}
		}
		""";

	private static Task VerifyAsync(String body, params String[] expectedIds) =>
		AnalyzerVerifier.VerifyAsync<MonitorOnAtomicAnalyzer>(Usings + body, expectedIds);

	/// <summary>Both strategies are reported: the lock is wrong for a different reason on each.</summary>
	[Theory]
	[InlineData("Int32")]
	[InlineData("Decimal")]
	[InlineData("String")]
	public Task Atomic_WhenLockStatementUsed_IsReported(String type) => VerifyAsync($$"""
		class C
		{
			private readonly Atomic<{{type}}> cell = new(default!);
			public void M() { lock ([|cell|]) { } }
		}
		""", Rules.LockStatementId);

	/// <summary>A cell whose type argument is still a type parameter is still a cell.</summary>
	[Fact]
	public Task Atomic_WhenLockedInGenericCode_IsReported() => VerifyAsync("""
		class C
		{
			public static void M<T>(Atomic<T> cell) { lock ([|cell|]) { } }
		}
		""", Rules.LockStatementId);

	/// <summary>A cast to Object hides the type from the reader, but not from the analyzer.</summary>
	[Fact]
	public Task Atomic_WhenLockedThroughAnObjectCast_IsReported() => VerifyAsync("""
		class C
		{
			private readonly Atomic<Int64> cell = new(0);
			public void M() { lock ((Object)[|cell|]) { } }
		}
		""", Rules.LockStatementId);

	[Fact]
	public Task Atomic_WhenPropertyOrArrayElementIsLocked_IsReported() => VerifyAsync("""
		class C
		{
			private readonly Atomic<Int64>[] cells = [new(0)];
			private Atomic<Int64> Cell => cells[0];
			public void M()
			{
				lock ([|Cell|]) { }
				lock ([|cells[0]|]) { }
			}
		}
		""", Rules.LockStatementId, Rules.LockStatementId);

	[Theory]
	[InlineData("Monitor.Enter([|cell|]);")]
	[InlineData("Boolean taken = false; Monitor.Enter([|cell|], ref taken);")]
	[InlineData("Monitor.TryEnter([|cell|]);")]
	[InlineData("Monitor.TryEnter([|cell|], 10);")]
	[InlineData("Monitor.Exit([|cell|]);")]
	[InlineData("Monitor.Wait([|cell|]);")]
	[InlineData("Monitor.Pulse([|cell|]);")]
	[InlineData("Monitor.PulseAll([|cell|]);")]
	public Task Atomic_WhenPassedToASynchronisingMonitorMethod_IsReported(String statement) => VerifyAsync($$"""
		class C
		{
			private readonly Atomic<Decimal> cell = new(0m);
			public void M() { {{statement}} }
		}
		""", Rules.MonitorMethodId);

	/// <summary>Named arguments must not let a Monitor call slip past the first-parameter lookup.</summary>
	[Fact]
	public Task Atomic_WhenPassedToMonitorByName_IsReported() => VerifyAsync("""
		class C
		{
			private readonly Atomic<Decimal> cell = new(0m);
			public void M() { Monitor.TryEnter(millisecondsTimeout: 10, obj: [|cell|]); }
		}
		""", Rules.MonitorMethodId);

	/// <summary>Monitor.IsEntered only queries the monitor, so it is left alone for use in assertions.</summary>
	[Fact]
	public Task Atomic_WhenPassedToMonitorIsEntered_IsNotReported() => VerifyAsync("""
		class C
		{
			private readonly Atomic<Decimal> cell = new(0m);
			public Boolean M() => Monitor.IsEntered(cell);
		}
		""");

	[Fact]
	public Task PlainObject_WhenLockedInATypeThatAlsoHoldsACell_IsNotReported() => VerifyAsync("""
		class C
		{
			private readonly Atomic<Int64> cell = new(0);
			private readonly Object sync = new();
			public void M()
			{
				lock (sync) { cell.Write(cell.Read() + 1); }
				Monitor.Enter(sync);
				Monitor.Exit(sync);
			}
		}
		""");

	[Fact]
	public Task Atomic_WhenUpdatedThroughItsOwnOperations_IsNotReported() => VerifyAsync("""
		class C
		{
			private readonly Atomic<Int64> cell = new(0);
			public void M()
			{
				cell.Increment();
				var current = cell.Read();
				while (!cell.TryCompareExchange(current * 2, current, out var previous))
					current = previous;
			}
		}
		""");

	/// <summary>
	/// The library's own locks on the cell are its lock path, so the assembly declaring the cell is left
	/// alone. Paired with the test below, without which this would pass just as well if the analyzer had
	/// simply failed to recognise the stand-in.
	/// </summary>
	[Fact]
	public Task Atomic_WhenLockedInTheAssemblyThatDeclaresIt_IsNotReported() =>
		AnalyzerVerifier.VerifyAsync<MonitorOnAtomicAnalyzer>(StandIn, AnalyzerVerifier.Platform);

	/// <summary>The same stand-in, compiled as a library and locked from outside it, is reported.</summary>
	[Fact]
	public Task Atomic_WhenLockedOutsideTheAssemblyThatDeclaresIt_IsReported() =>
		AnalyzerVerifier.VerifyAsync<MonitorOnAtomicAnalyzer>(
			Usings + """
			class C
			{
				private readonly Atomic<Int32> cell = new(0);
				public void M()
				{
					lock ([|cell|]) { }
					Monitor.Enter([|cell|]);
				}
			}
			""",
			AnalyzerVerifier.Platform.Add(AnalyzerVerifier.CompileLibrary("StandIn", StandIn)),
			Rules.LockStatementId,
			Rules.MonitorMethodId);
}
