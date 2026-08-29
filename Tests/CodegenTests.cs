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
/// at run time on each access. Correctness does not depend on the test folding away — performance does,
/// and that is the whole claim the library makes. So this one reads the code the JIT actually produced.
/// </para>
/// <para>
/// It runs <c>CodegenProbe</c> as a separate process with the JIT's disassembler switched on, because
/// there is no way to ask for a method's native code from inside the process running it. The assertions
/// are on the names of the runtime helpers the code calls rather than on instruction mnemonics, so they
/// mean the same thing on arm64 and x64. Each one is paired with a case that must show the opposite: a
/// test that only ever looks for something's absence passes just as happily when it is looking in the
/// wrong place.
/// </para>
/// </remarks>
public class CodegenTests
{
	/// <summary>The methods whose native code is wanted, as the JIT's filter spells them.</summary>
	private const String Filter = "Probe* Increment";

	[Fact]
	public void TheStorageStrategyIsChosenAtCompileTimeRatherThanOnEveryCall()
	{
		var methods = Disassemble();

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

	[Fact]
	public void TheNativeInstructionIsChosenAtCompileTimeRatherThanOnEveryCall()
	{
		// Unlike the monitor, the difference between an instruction and a retry loop is not a call to
		// anything, so there is no helper name to look for: the loop's compare-and-exchange inlines to a
		// bare instruction of its own. It has to be read as mnemonics, and those are per architecture.
		// Only the one this was written and checked on is asserted; somewhere else it says so rather than
		// guessing.
		if (RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
			Assert.Skip($"the mnemonics for {RuntimeInformation.ProcessArchitecture} are not written down here");

		var methods = Disassemble();
		var word = Body(methods, "AtomicExtensions:Increment[long]");

		// The atomic add itself, and no compare-and-swap, which is what a loop would have left behind.
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
	/// <returns>Each disassembled method, by name.</returns>
	private static IReadOnlyDictionary<String, String> Disassemble()
	{
		// An unoptimised build is not the thing being asked about, and reading one would answer a
		// different question: the JIT leaves debugger helpers in and folds less, so every assertion here
		// would fail for a reason that says nothing about the library.
		var debuggable = typeof(Atomic<Int32>).Assembly.GetCustomAttribute<DebuggableAttribute>();
		if (debuggable?.IsJITOptimizerDisabled == true)
			Assert.Skip("codegen is only worth reading from an optimised build; run `dotnet test -c Release`");

		var probe = Path.Combine(AppContext.BaseDirectory,
			"CodegenProbe" + (OperatingSystem.IsWindows() ? ".exe" : String.Empty));
		File.Exists(probe).Should().BeTrue($"the probe should have been built alongside these tests, at {probe}");

		var start = new ProcessStartInfo(probe) { RedirectStandardOutput = true, RedirectStandardError = true };
		start.Environment["DOTNET_JitDisasm"] = Filter;
		start.Environment["DOTNET_TieredCompilation"] = "0";

		using var process = Process.Start(start);
		process.Should().NotBeNull();
		var output = process!.StandardOutput.ReadToEnd();
		process.WaitForExit(milliseconds: 60_000).Should().BeTrue("the probe should not hang");
		process.ExitCode.Should().Be(0, "the probe should run cleanly");

		var methods = Parse(output);
		if (methods.Count == 0)
			Assert.Skip("this runtime produced no disassembly, so there is nothing here to read");

		return methods;
	}

	/// <summary>Splits a disassembly listing into one entry per method.</summary>
	/// <param name="output">Everything the probe printed.</param>
	/// <returns>Each method's listing, by the name in its header.</returns>
	private static Dictionary<String, String> Parse(String output)
	{
		var methods = new Dictionary<String, String>(StringComparer.Ordinal);
		var headers = Regex.Matches(output, @"^; Assembly listing for method (?<name>.+)$", RegexOptions.Multiline);

		for (var i = 0; i < headers.Count; i++)
		{
			var start = headers[i].Index;
			var end = i + 1 < headers.Count ? headers[i + 1].Index : output.Length;
			methods[headers[i].Groups["name"].Value.Trim()] = output[start..end];
		}

		return methods;
	}
}
