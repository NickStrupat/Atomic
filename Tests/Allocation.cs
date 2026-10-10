using System.Runtime.CompilerServices;

namespace Tests;

/// <summary>Measures what code allocates on the calling thread, without counting the runtime's one-off work.</summary>
/// <remarks>
/// <para>
/// <see cref="GC.GetAllocatedBytesForCurrentThread"/> counts everything allocated on the thread, including
/// what the runtime itself allocates there — most likely while compiling or recompiling code the thread is
/// running. Exact assertions on a single measurement failed on CI three times in eleven attempts, by 16,
/// 48 and 7,592 bytes, in three different tests: amounts no per-call allocation in the code under test
/// could add up to.
/// </para>
/// <para>
/// So the code is run once to warm up, then measured several times, and the least is reported. Work the
/// runtime does once lands in one repetition at most; an allocation the code makes on every call lands in
/// every repetition, so the least still contains it in full. The answer stays exact, with no allowance to
/// choose and none for a regression to hide in.
/// </para>
/// </remarks>
internal static class Allocation
{
	/// <summary>How many measured repetitions follow the warm-up.</summary>
	private const Int32 Repetitions = 3;

	/// <summary>Runs <paramref name="run"/> once to warm up, then reports the least it allocated in several runs.</summary>
	/// <param name="run">
	/// The code to measure. Mark it <see cref="MethodImplOptions.AggressiveOptimization"/> if it loops, so it
	/// is compiled once, fully optimised, rather than recompiled on this thread partway through a measurement.
	/// </param>
	/// <returns>The fewest bytes allocated on this thread by a single measured run.</returns>
	[MethodImpl(MethodImplOptions.AggressiveOptimization)]
	public static Int64 Least(Action run)
	{
		run();
		var least = Int64.MaxValue;
		for (var repetition = 0; repetition < Repetitions; repetition++)
		{
			var before = GC.GetAllocatedBytesForCurrentThread();
			run();
			least = Math.Min(least, GC.GetAllocatedBytesForCurrentThread() - before);
		}
		return least;
	}
}
