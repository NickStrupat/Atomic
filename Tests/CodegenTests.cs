using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using NickStrupat;

namespace Tests;

/// <summary>
/// That the strategy a cell picks is chosen once, by the compiler, and not on every call.
/// </summary>
/// <remarks>
/// <para>
/// Every other test here would pass just as well if <see cref="Atomic{T}"/> tested its storage strategy
/// at run time on each access. Correctness does not depend on that test folding away — performance
/// does, and it is the whole claim the library makes. So these read the code a compiler actually
/// produced.
/// </para>
/// <para>
/// Both compilers are held to the same statements, because the library claims the same thing of both,
/// and the just-in-time one folding says nothing about the ahead-of-time one. Each assertion is paired
/// with a case that must show the opposite: a test that only ever looks for something's absence passes
/// just as happily when it is looking in the wrong place.
/// </para>
/// <para>
/// The monitor half is read from the names of the runtime helpers the code calls, which are the same
/// wherever it runs. The instruction half cannot be — the difference between one instruction and a
/// retry loop is not a call to anything, since the loop's compare-and-exchange inlines to a bare
/// instruction of its own — so it is read as mnemonics, and only for the architecture they are written
/// down for.
/// </para>
/// </remarks>
public class CodegenTests
{
	/// <summary>The methods whose native code is wanted, as either compiler's filter spells them.</summary>
	private const String Filter = "Probe* Increment";

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

	/// <summary>Requires that no branch on the storage strategy survived compilation.</summary>
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

		// And the case that must look different, without which the three above would prove only that
		// this is reading the wrong thing.
		Body(methods, "ProbeReadWide").Should().Contain("Monitor");
	}

	/// <summary>Requires that the type test picked the instruction and left no loop behind.</summary>
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

		// A type with no instruction takes the loop through the same source, so this is what the
		// assertions above are distinguishing themselves from.
		var wide = Body(methods, "AtomicExtensions:Increment[System.Decimal]");
		wide.Should().NotContain("ldaddal");
		wide.Should().Contain("Monitor");
	}

	/// <summary>The methods a compiler disassembled, or a skip saying why there are none.</summary>
	/// <param name="listing">The run to take them from.</param>
	/// <returns>Each disassembled method, by name.</returns>
	private static IReadOnlyDictionary<String, String> Methods(Lazy<Listing> listing)
	{
		var (methods, reason) = listing.Value;
		if (reason is not null)
			Assert.Skip(reason);

		return methods;
	}

	/// <summary>The body of the one disassembled method whose name contains <paramref name="name"/>.</summary>
	/// <param name="methods">Every method the run disassembled.</param>
	/// <param name="name">Enough of the method's name to pick it out.</param>
	/// <returns>The lines of that method's listing.</returns>
	private static String Body(IReadOnlyDictionary<String, String> methods, String name)
	{
		var matches = methods.Where(m => m.Key.Contains(name, StringComparison.Ordinal)).ToArray();
		matches.Should().ContainSingle($"the run should have disassembled exactly one method named {name}, "
			+ $"and produced {String.Join(", ", methods.Keys)}");
		return matches[0].Value;
	}

	/// <summary>The call instructions in a method body, whatever the architecture spells them.</summary>
	/// <param name="body">The lines of a method's listing.</param>
	/// <returns>Every line that transfers control to another method.</returns>
	private static String[] Calls(String body) =>
		Regex.Matches(body, @"^\s+(?:bl|blr|call)\s+\S.*$", RegexOptions.Multiline)
			.Select(m => m.Value.Trim())
			.ToArray();

	/// <summary>Runs the probe with the JIT's disassembler on and collects what it printed.</summary>
	/// <returns>What it disassembled, or why it could not be read.</returns>
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

	/// <summary>Publishes the probe ahead of time, asking the compiler for the same disassembly.</summary>
	/// <returns>What it disassembled, or why it could not be read.</returns>
	/// <remarks>
	/// Publishing rather than running, because ahead-of-time code is produced by the build. It takes a
	/// few seconds, and needs a native toolchain that not every machine has — a machine without one
	/// skips, which is a visible outcome rather than a quiet pass.
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

	/// <summary>Walks up from the test assembly looking for the probe's project file.</summary>
	/// <returns>The path to it, or <see langword="null"/> if it is not there.</returns>
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

	/// <summary>The last few lines of a build log, for a message that has to stay readable.</summary>
	/// <param name="log">Everything the build printed.</param>
	/// <returns>Its tail.</returns>
	private static String Tail(String log) =>
		String.Join(Environment.NewLine, log.Split('\n').Where(l => l.Trim().Length > 0).TakeLast(8));

	/// <summary>Removes a temporary file or directory, and does not mind if it is already gone.</summary>
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

	/// <summary>What one compiler disassembled, or why nothing could be read from it.</summary>
	/// <param name="Methods">Each method's listing, by the name in its header.</param>
	/// <param name="Reason">Why there is nothing to read, when there is not.</param>
	private readonly record struct Listing(
		IReadOnlyDictionary<String, String> Methods,
		String? Reason)
	{
		private static readonly Dictionary<String, String> None = new(StringComparer.Ordinal);

		/// <summary>A listing that could not be produced.</summary>
		/// <param name="reason">Why not.</param>
		/// <returns>The empty listing, carrying the reason.</returns>
		public static Listing Unavailable(String reason) => new(None, reason);

		/// <summary>Splits a disassembly listing into one entry per method.</summary>
		/// <param name="output">Everything the compiler printed.</param>
		/// <param name="whenEmpty">Why to say there is nothing to read, if there is nothing.</param>
		/// <returns>The methods, or an unavailable listing.</returns>
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
