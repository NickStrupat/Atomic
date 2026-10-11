using Microsoft.Coyote;
using Microsoft.Coyote.Logging;
using Microsoft.Coyote.SystematicTesting;
using NickStrupat;
using Tests;

namespace CoyoteTests;

/// <summary>
/// Explores the interleavings of each cell's operations with Coyote, which schedules the tasks itself
/// rather than leaving it to the operating system, and reports any interleaving that breaks a property.
/// </summary>
/// <remarks>
/// <para>
/// This complements the thread-safety suite in <c>Tests</c>; it does not replace it. That suite runs on
/// real threads and real hardware, which is the only place a torn value or a reordered store can show
/// up. Here one task runs at a time and every operation takes effect in order, so tearing and reordering
/// are invisible. What this has instead is control: Coyote chooses which task runs at every scheduling
/// point, so whether a race is found does not depend on whether a machine happened to run two threads at
/// once — which on a 3- or 4-core CI runner, often it did not.
/// </para>
/// <para>
/// Scheduling points come from the assemblies being rewritten after the build (see the project file):
/// every <see cref="Interlocked"/> and <see cref="Volatile"/> call, every lock, and, with memory-access
/// race checking on, every field read and write. The last is what lets Coyote come between the read and
/// the write of <see cref="NaiveCell{T}"/>; without it that cell passes every exploration.
/// </para>
/// <para>
/// Each exploration says whether a bug is expected. The real cells must survive every iteration; the
/// naive cell must be caught, which is what keeps this from passing because it explored nothing. The
/// seed is fixed, so a given build explores the same schedules every run; set <c>COYOTE_SEED</c> to
/// explore others, and <c>COYOTE_ITERATIONS</c> to explore more.
/// </para>
/// </remarks>
internal static class Program
{
	private const UInt32 DefaultIterations = 1_000;
	private const UInt32 DefaultSeed = 20261010;

	public static Int32 Main()
	{
		var iterations = ReadSetting("COYOTE_ITERATIONS", DefaultIterations);
		var seed = ReadSetting("COYOTE_SEED", DefaultSeed);
		Console.WriteLine($"Exploring {iterations} schedules per test, seed {seed}.");

		var unexpected = Explorations.All()
			.Count(exploration => !Explore(exploration, iterations, seed));

		Console.WriteLine(unexpected == 0
			? "Every exploration came out as expected."
			: $"{unexpected} exploration(s) did not come out as expected.");
		return unexpected == 0 ? 0 : 1;
	}

	/// <summary>Runs one exploration and reports whether its outcome matched its expectation.</summary>
	/// <param name="exploration">The test, and whether a bug is expected in it.</param>
	/// <param name="iterations">How many schedules to explore.</param>
	/// <param name="seed">The seed for Coyote's choice of schedules.</param>
	/// <returns><see langword="true"/> if a bug was found exactly when one was expected.</returns>
	private static Boolean Explore(Exploration exploration, UInt32 iterations, UInt32 seed)
	{
		var configuration = Configuration.Create()
			.WithTestingIterations(iterations)
			.WithRandomGeneratorSeed(seed)
			.WithMemoryAccessRaceCheckingEnabled(true)
			.WithVerbosityEnabled(VerbosityLevel.None)
			.WithConsoleLoggingEnabled(false)
			// Coyote reports usage to Microsoft unless told not to; a test run is not the place to opt in.
			.WithTelemetryEnabled(false);
		using var engine = TestingEngine.Create(configuration, exploration.Test);

		// The engine writes its progress to the console whatever the verbosity; the summary line below is
		// what this reports instead.
		var console = Console.Out;
		Console.SetOut(TextWriter.Null);
		try
		{
			engine.Run();
		}
		finally
		{
			Console.SetOut(console);
		}

		var report = engine.TestReport;
		var found = report.NumOfFoundBugs > 0;
		var asExpected = found == exploration.ExpectBug;
		var outcome = found
			? $"bug after {report.NumOfExploredFairPaths + report.NumOfExploredUnfairPaths} schedule(s): {report.BugReports.First()}"
			: $"no bug in {report.NumOfExploredFairPaths + report.NumOfExploredUnfairPaths} schedules";
		Console.WriteLine($"{(asExpected ? "  ok  " : "  FAIL")} {exploration.Name}: {outcome}");
		return asExpected;
	}

	private static UInt32 ReadSetting(String name, UInt32 fallback) =>
		UInt32.TryParse(Environment.GetEnvironmentVariable(name), out var value) ? value : fallback;
}

/// <summary>One Coyote test, and whether it is expected to find a bug.</summary>
/// <param name="Name">What is being explored, for the report.</param>
/// <param name="Test">The test Coyote runs once per explored schedule.</param>
/// <param name="ExpectBug">Whether the cell under test is expected to break the property.</param>
internal sealed record Exploration(String Name, Func<Task> Test, Boolean ExpectBug);
