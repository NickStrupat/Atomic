using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using NickStrupat;

namespace Tests;

/// <summary>
/// Checks, by reading the generated machine code, that each cell's strategy is chosen once by the
/// compiler rather than tested on every call.
/// </summary>
/// <remarks>
/// <para>
/// Every other test would still pass if <see cref="Atomic{T}"/> tested its storage strategy at run time
/// on each access. Correctness does not depend on the test folding away; performance does, and that is
/// the library's main claim. So these tests read the code a compiler actually produced.
/// </para>
/// <para>
/// The JIT and the NativeAOT compiler are held to the same assertions, because the library makes the
/// same claim for both, and one folding the test says nothing about the other. Each assertion is paired
/// with a case that must show the opposite, because a test that only checks something is absent passes
/// just as well when it is looking in the wrong place.
/// </para>
/// <para>
/// Whether the lock is used is read from the names of the runtime helpers called, which are the same on
/// every architecture. Whether an instruction or a retry loop was chosen cannot be read that way — the
/// loop's compare-exchange also inlines to a bare instruction, so neither calls anything — so it is read
/// from instruction mnemonics, and only on the architecture those are written for (arm64).
/// </para>
/// </remarks>
public class CodegenTests
{
	/// <summary>The methods to disassemble, in the filter syntax both compilers accept.</summary>
	private const String Filter = "Probe* Increment TryCompareExchange";

	/// <summary>The name of the compare-exchange on a word-sized value, as both compilers print it.</summary>
	/// <remarks>
	/// This reads the method itself rather than the probe's wrapper. Unlike <c>Read</c> and <c>Write</c>,
	/// <c>TryCompareExchange</c> is not marked for aggressive inlining, so it is compiled under its own
	/// name and the wrapper only contains a call to it. The wrapper just makes sure it gets compiled.
	/// </remarks>
	private const String WordCompareExchange = "[long]:TryCompareExchange";

	/// <summary>The name of the compare-exchange that must take the lock, read directly likewise.</summary>
	private const String WideCompareExchange = "[System.Decimal]:TryCompareExchange";

	private static readonly Lazy<Listing> JustInTime = new(RunTheProbe);
	private static readonly Lazy<Listing> AheadOfTime = new(CompileTheProbeAheadOfTime);

	[Fact]
	public void TheStorageStrategyIsChosenWhenTheJitCompilesTheCell() =>
		AssertTheStrategyFolded(Methods(JustInTime));

	[Fact]
	public void TheStorageStrategyIsChosenWhenTheAotCompilerCompilesTheCell() =>
		AssertTheStrategyFolded(Methods(AheadOfTime));

	[Fact]
	public void TheNativeInstructionIsChosenWhenTheJitCompilesTheExtension() =>
		AssertTheInstructionWasChosen(Methods(JustInTime));

	[Fact]
	public void TheNativeInstructionIsChosenWhenTheAotCompilerCompilesTheExtension() =>
		AssertTheInstructionWasChosen(Methods(AheadOfTime));

	/// <summary>Asserts that no test of the storage strategy survived compilation.</summary>
	/// <param name="methods">Every method the compiler was asked to disassemble.</param>
	private static void AssertTheStrategyFolded(IReadOnlyDictionary<String, String> methods)
	{
		// A cell holding a word never reaches the monitor, and the branch that would have is gone: no
		// call of any kind survives in either of these.
		Body(methods, "ProbeReadWord").Should().NotContain("Monitor");
		Calls(Body(methods, "ProbeReadWord")).Should().BeEmpty();
		Body(methods, "ProbeWriteWord").Should().NotContain("Monitor");
		Calls(Body(methods, "ProbeWriteWord")).Should().BeEmpty();

		// Nor does a cell holding a reference.
		Body(methods, "ProbeReadReference").Should().NotContain("Monitor");
		Calls(Body(methods, "ProbeReadReference")).Should().BeEmpty();

		// Nor does a compare-exchange on a word. Its calls are not asserted empty: the tail that consults
		// the type's own equality is out of line, so a call to it sits in the body whether or not any
		// execution reaches it. What matters is that the monitor is gone.
		Body(methods, WordCompareExchange).Should().NotContain("Monitor");

		// And the cases that must look different, without which those above would prove only that this
		// is reading the wrong thing.
		Body(methods, "ProbeReadWide").Should().Contain("Monitor");
		Body(methods, WideCompareExchange).Should().Contain("Monitor");
	}

	/// <summary>Asserts that the type test chose the instruction and left no loop behind.</summary>
	/// <param name="methods">Every method the compiler was asked to disassemble.</param>
	private static void AssertTheInstructionWasChosen(IReadOnlyDictionary<String, String> methods)
	{
		if (RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
			Assert.Skip($"the mnemonics for {RuntimeInformation.ProcessArchitecture} are not written down here");

		// The atomic add itself, and no compare-and-swap, which is what a loop would have left behind.
		var word = Body(methods, "AtomicExtensions:Increment[long]");
		word.Should().Contain("ldaddal");
		word.Should().NotContain("casal");
		word.Should().NotContain("Monitor");
		word.Should().NotContainAny("CHKCAST", "ISINSTANCE", "UNBOX", "NEWSFAST");

		// A word with no instruction takes the loop through the same source, so this is what the
		// assertions above are distinguishing themselves from. It carries no lock either: the branch that
		// takes one is for the monitor strategy alone and folds away everywhere else.
		var loop = Body(methods, "AtomicExtensions:Increment[double]");
		loop.Should().NotContain("ldaddal");
		loop.Should().NotContain("Monitor", "a cell that swaps in place never reaches the lock branch");

		// A value behind the monitor takes neither: it computes under the lock the cell already holds.
		var wide = Body(methods, "AtomicExtensions:Increment[System.Decimal]");
		wide.Should().NotContain("ldaddal");
		wide.Should().NotContain("casal");
		wide.Should().Contain("Monitor");

		// A compare-exchange on a word is the instruction with nothing in front of it. The cell compares
		// with the type's own equality, and that costs the ordinary case nothing, because the type is
		// only consulted from a tail the instruction branches to when it misses. So the compare-and-swap
		// comes before the first branch in the body: nothing is read, compared or tested ahead of it.
		// Written the other way round — ask the type, then swap — this is the assertion that fails.
		var swap = Body(methods, WordCompareExchange);
		swap.Should().Contain("casal");

		var branches = Branches(swap);
		branches.Should().NotBeEmpty("a body that branches nowhere would satisfy the next line for free");
		swap.IndexOf("casal", StringComparison.Ordinal).Should()
			.BeLessThan(swap.IndexOf(branches[0], StringComparison.Ordinal),
				"the instruction runs before the type is ever consulted");

		// And a value the instruction cannot hold reaches the same method through the same source.
		Body(methods, WideCompareExchange).Should().NotContain("casal");
	}

	/// <summary>Finds every branch instruction in a method body, in order.</summary>
	/// <param name="body">The lines of a method's listing.</param>
	/// <returns>Each branch line, conditional or not, including calls and tail calls.</returns>
	/// <remarks>
	/// The mnemonics are listed exactly rather than matched by prefix, because some arm64 arithmetic
	/// instructions also start with <c>b</c> (<c>bic</c>, <c>bfi</c>), and counting one as a branch would
	/// quietly weaken the assertion that uses this. arm64 only; the caller already checks that.
	/// </remarks>
	private static String[] Branches(String body) =>
		Regex.Matches(body,
				@"^\s+(?:beq|bne|blt|bgt|ble|bge|bhi|bls|bcs|bcc|bmi|bpl|bvs|bvc|bal|cbn?z|tbn?z|blr|bl|br|b)\s+\S.*$",
				RegexOptions.Multiline)
			.Select(m => m.Value.Trim())
			.ToArray();

	/// <summary>Returns the methods a compiler disassembled, or skips the test if there are none.</summary>
	/// <param name="listing">The compiler run to take them from.</param>
	/// <returns>Each disassembled method, by name.</returns>
	private static IReadOnlyDictionary<String, String> Methods(Lazy<Listing> listing)
	{
		var (methods, reason) = listing.Value;
		if (reason is not null)
			Assert.Skip(reason);

		return methods;
	}

	/// <summary>Returns the listing of the one method whose name contains <paramref name="name"/>.</summary>
	/// <param name="methods">Every method the run disassembled.</param>
	/// <param name="name">Enough of the method's name to identify it uniquely.</param>
	/// <returns>The lines of that method's listing.</returns>
	private static String Body(IReadOnlyDictionary<String, String> methods, String name)
	{
		var matches = methods.Where(m => m.Key.Contains(name, StringComparison.Ordinal)).ToArray();
		matches.Should().ContainSingle($"the run should have disassembled exactly one method named {name}, "
			+ $"and produced {String.Join(", ", methods.Keys)}");
		return matches[0].Value;
	}

	/// <summary>Finds the call instructions in a method body, on arm64 or x64.</summary>
	/// <param name="body">The lines of a method's listing.</param>
	/// <returns>Every line that transfers control to another method.</returns>
	private static String[] Calls(String body) =>
		Regex.Matches(body, @"^\s+(?:bl|blr|call)\s+\S.*$", RegexOptions.Multiline)
			.Select(m => m.Value.Trim())
			.ToArray();

	/// <summary>Runs the probe with JIT disassembly turned on and collects the output.</summary>
	/// <returns>The disassembled methods, or why there are none.</returns>
	private static Listing RunTheProbe()
	{
		// An unoptimised build is not the thing being asked about, and reading one would answer a
		// different question: the JIT leaves debugger helpers in and folds less, so every assertion here
		// would fail for a reason that says nothing about the library.
		var debuggable = typeof(Atomic<Int32>).Assembly.GetCustomAttribute<DebuggableAttribute>();
		if (debuggable?.IsJITOptimizerDisabled == true)
			return Listing.Unavailable("codegen is only worth reading from an optimised build; run `dotnet test -c Release`");

		var probe = Path.Combine(AppContext.BaseDirectory,
			"CodegenProbe" + (OperatingSystem.IsWindows() ? ".exe" : String.Empty));
		if (!File.Exists(probe))
			return Listing.Unavailable($"the probe was not built alongside these tests, at {probe}");

		var start = new ProcessStartInfo(probe) { RedirectStandardOutput = true, RedirectStandardError = true };
		start.Environment["DOTNET_JitDisasm"] = Filter;
		start.Environment["DOTNET_TieredCompilation"] = "0";

		using var process = Process.Start(start)!;
		var output = process.StandardOutput.ReadToEnd();
		process.WaitForExit(milliseconds: 120_000).Should().BeTrue("the probe should not hang");
		process.ExitCode.Should().Be(0, "the probe should run cleanly");

		return Listing.Of(output, "this runtime produced no disassembly, so there is nothing here to read");
	}

	/// <summary>Publishes the probe with NativeAOT, asking the compiler for the same disassembly.</summary>
	/// <returns>The disassembled methods, or why there are none.</returns>
	/// <remarks>
	/// This publishes rather than runs, because NativeAOT generates code at build time. It takes a few
	/// seconds and needs a native toolchain. A machine without one skips the test, which shows up in the
	/// results rather than passing silently.
	/// </remarks>
	private static Listing CompileTheProbeAheadOfTime()
	{
		var debuggable = typeof(Atomic<Int32>).Assembly.GetCustomAttribute<DebuggableAttribute>();
		if (debuggable?.IsJITOptimizerDisabled == true)
			return Listing.Unavailable("the ahead-of-time check runs with the rest of the codegen tests; run `dotnet test -c Release`");

		var project = FindProbeProject();
		if (project is null)
			return Listing.Unavailable("could not find CodegenProbe.csproj above the test assembly");

		var disassembly = Path.Combine(Path.GetTempPath(), $"atomic-aot-{Guid.NewGuid():N}.txt");
		var output = Path.Combine(Path.GetTempPath(), $"atomic-aot-{Guid.NewGuid():N}");
		var start = new ProcessStartInfo("dotnet")
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			ArgumentList =
			{
				"publish", project, "-c", "Release", "-r", RuntimeInformation.RuntimeIdentifier,
				"/p:PublishAot=true", $"/p:AtomicAotDisasmFilter={Filter}",
				$"/p:AtomicAotDisasmFile={disassembly}", "-o", output, "--nologo",
			},
		};

		try
		{
			using var process = Process.Start(start)!;
			var log = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
			if (!process.WaitForExit(milliseconds: 600_000))
				return Listing.Unavailable("the ahead-of-time compiler did not finish within ten minutes");
			if (process.ExitCode != 0)
				return Listing.Unavailable($"could not compile ahead of time on this machine:\n{Tail(log)}");

			return File.Exists(disassembly)
				? Listing.Of(File.ReadAllText(disassembly), "the ahead-of-time compiler produced no disassembly")
				: Listing.Unavailable("the ahead-of-time compiler wrote no disassembly file");
		}
		finally
		{
			Delete(disassembly);
			Delete(output);
		}
	}

	/// <summary>Searches upward from the test assembly for the probe's project file.</summary>
	/// <returns>Its path, or <see langword="null"/> if it was not found.</returns>
	private static String? FindProbeProject()
	{
		for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
		{
			var candidate = Path.Combine(directory.FullName, "CodegenProbe", "CodegenProbe.csproj");
			if (File.Exists(candidate))
				return candidate;
		}

		return null;
	}

	/// <summary>Returns the last few non-empty lines of a build log, to keep a skip message readable.</summary>
	/// <param name="log">Everything the build printed.</param>
	/// <returns>The last 8 non-empty lines.</returns>
	private static String Tail(String log) =>
		String.Join(Environment.NewLine, log.Split('\n').Where(l => l.Trim().Length > 0).TakeLast(8));

	/// <summary>Deletes a temporary file or directory, ignoring it if it is already gone.</summary>
	/// <param name="path">What to remove.</param>
	private static void Delete(String path)
	{
		try
		{
			if (Directory.Exists(path))
				Directory.Delete(path, recursive: true);
			else if (File.Exists(path))
				File.Delete(path);
		}
		catch (IOException)
		{
			// A leftover file in the temp directory is not worth failing a test over.
		}
	}

	/// <summary>What one compiler disassembled, or why there is nothing.</summary>
	/// <param name="Methods">Each method's listing, keyed by the name in its header.</param>
	/// <param name="Reason">Why there is nothing to read, or <see langword="null"/> if there is.</param>
	private readonly record struct Listing(
		IReadOnlyDictionary<String, String> Methods,
		String? Reason)
	{
		private static readonly Dictionary<String, String> None = new(StringComparer.Ordinal);

		/// <summary>Creates an empty listing that records why it could not be produced.</summary>
		/// <param name="reason">Why there is no listing.</param>
		/// <returns>An empty listing carrying the reason.</returns>
		public static Listing Unavailable(String reason) => new(None, reason);

		/// <summary>Splits a disassembly listing into one entry per method.</summary>
		/// <param name="output">Everything the compiler printed.</param>
		/// <param name="whenEmpty">The reason to give if the output contains no methods.</param>
		/// <returns>The methods, or an unavailable listing if there were none.</returns>
		public static Listing Of(String output, String whenEmpty)
		{
			var methods = new Dictionary<String, String>(StringComparer.Ordinal);
			var headers = Regex.Matches(output, @"^; Assembly listing for method (?<name>.+)$", RegexOptions.Multiline);

			for (var i = 0; i < headers.Count; i++)
			{
				var start = headers[i].Index;
				var end = i + 1 < headers.Count ? headers[i + 1].Index : output.Length;
				methods[headers[i].Groups["name"].Value.Trim()] = output[start..end];
			}

			return methods.Count == 0 ? Unavailable(whenEmpty) : new Listing(methods, null);
		}
	}
}
