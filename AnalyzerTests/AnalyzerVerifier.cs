using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using NickStrupat;

namespace AnalyzerTests;

/// <summary>
/// Compiles a snippet, runs an analyzer over it, and compares the reported diagnostics against the
/// locations marked up in the snippet with <c>[|</c> and <c>|]</c>.
/// </summary>
internal static class AnalyzerVerifier
{
	/// <summary>The assemblies the test host was loaded with, without the Atomic library.</summary>
	public static readonly ImmutableArray<MetadataReference> Platform = BuildPlatformReferences();

	/// <summary>The platform plus the real Atomic library, as a consuming project would see them.</summary>
	public static readonly ImmutableArray<MetadataReference> WithAtomic =
		Platform.Add(MetadataReference.CreateFromFile(typeof(Atomic<>).Assembly.Location));

	/// <summary>
	/// Asserts that running <typeparamref name="TAnalyzer"/> over <paramref name="markup"/>, compiled
	/// against the real Atomic library, reports exactly <paramref name="expectedIds"/>.
	/// </summary>
	/// <typeparam name="TAnalyzer">The analyzer to run.</typeparam>
	/// <param name="markup">The snippet, with each expected diagnostic location wrapped in <c>[| |]</c>.</param>
	/// <param name="expectedIds">The diagnostic ids expected at the marked spans, in source order.</param>
	/// <returns>A task that completes when the comparison has been made.</returns>
	public static Task VerifyAsync<TAnalyzer>(String markup, params String[] expectedIds)
		where TAnalyzer : DiagnosticAnalyzer, new() =>
		VerifyAsync<TAnalyzer>(markup, WithAtomic, expectedIds);

	/// <summary>
	/// Asserts that running <typeparamref name="TAnalyzer"/> over <paramref name="markup"/>, compiled
	/// against <paramref name="references"/>, reports exactly <paramref name="expectedIds"/>, one per
	/// marked span, in source order.
	/// </summary>
	/// <typeparam name="TAnalyzer">The analyzer to run.</typeparam>
	/// <param name="markup">The snippet, with each expected diagnostic location wrapped in <c>[| |]</c>.</param>
	/// <param name="references">What the snippet is compiled against.</param>
	/// <param name="expectedIds">The diagnostic ids expected at the marked spans, in source order.</param>
	/// <returns>A task that completes when the comparison has been made.</returns>
	public static async Task VerifyAsync<TAnalyzer>(
		String markup,
		IEnumerable<MetadataReference> references,
		params String[] expectedIds)
		where TAnalyzer : DiagnosticAnalyzer, new()
	{
		var (source, spans) = ParseMarkup(markup);
		Assert.Equal(expectedIds.Length, spans.Length);

		var text = SourceText.From(source);
		var compilation = Compile("AnalyzerTestAssembly", text, references);

		var diagnostics = await compilation
			.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new TAnalyzer()))
			.GetAnalyzerDiagnosticsAsync(TestContext.Current.CancellationToken);

		var expected = expectedIds.Zip(spans, (id, span) => Format(id, span, text)).ToArray();
		var actual = diagnostics
			.OrderBy(x => x.Location.SourceSpan.Start)
			.Select(x => Format(x.Id, x.Location.SourceSpan, text))
			.ToArray();
		Assert.Equal(expected, actual);
	}

	/// <summary>Compiles a library, for a snippet to be compiled against in turn.</summary>
	/// <param name="assemblyName">The name of the library.</param>
	/// <param name="source">The library's source, which must compile without errors.</param>
	/// <returns>A reference to the compiled library.</returns>
	public static MetadataReference CompileLibrary(String assemblyName, String source) =>
		Compile(assemblyName, SourceText.From(source), Platform).ToMetadataReference();

	private static CSharpCompilation Compile(
		String assemblyName,
		SourceText text,
		IEnumerable<MetadataReference> references)
	{
		var syntaxTree = CSharpSyntaxTree.ParseText(text, new CSharpParseOptions(LanguageVersion.Latest));
		var compilation = CSharpCompilation.Create(
			assemblyName,
			[syntaxTree],
			references,
			new CSharpCompilationOptions(
				OutputKind.DynamicallyLinkedLibrary,
				nullableContextOptions: NullableContextOptions.Enable));

		var compileErrors = compilation.GetDiagnostics()
			.Where(x => x.Severity == DiagnosticSeverity.Error)
			.Select(x => x.ToString())
			.ToArray();
		Assert.True(compileErrors.Length == 0, "The test snippet does not compile:\n" + String.Join("\n", compileErrors));
		return compilation;
	}

	private static String Format(String id, TextSpan span, SourceText text)
	{
		var start = text.Lines.GetLinePosition(span.Start);
		return $"{id} at ({start.Line + 1},{start.Character + 1}) on '{text.ToString(span)}'";
	}

	/// <summary>Strips the <c>[| |]</c> markers, returning the real source and the spans they delimited.</summary>
	private static (String Source, ImmutableArray<TextSpan> Spans) ParseMarkup(String markup)
	{
		var source = new StringBuilder(markup.Length);
		var spans = ImmutableArray.CreateBuilder<TextSpan>();
		var starts = new Stack<Int32>();
		for (var i = 0; i < markup.Length; i++)
		{
			if (i + 1 < markup.Length && markup[i] == '[' && markup[i + 1] == '|')
			{
				starts.Push(source.Length);
				i++;
			}
			else if (i + 1 < markup.Length && markup[i] == '|' && markup[i + 1] == ']')
			{
				spans.Add(TextSpan.FromBounds(starts.Pop(), source.Length));
				i++;
			}
			else
				source.Append(markup[i]);
		}

		Assert.Empty(starts);
		spans.Sort((x, y) => x.Start.CompareTo(y.Start));
		return (source.ToString(), spans.ToImmutable());
	}

	/// <summary>
	/// References every assembly the test host itself was loaded with except Atomic, so a snippet can be
	/// compiled either against the real library or against a stand-in that declares the same type.
	/// </summary>
	private static ImmutableArray<MetadataReference> BuildPlatformReferences()
	{
		var atomic = Path.GetFileNameWithoutExtension(typeof(Atomic<>).Assembly.Location);
		var paths = ((String?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "")
			.Split(Path.PathSeparator)
			.Where(x => x.Length != 0 && Path.GetFileNameWithoutExtension(x) != atomic)
			.GroupBy(Path.GetFileNameWithoutExtension)
			.Select(x => x.First());
		return [..paths.Select(x => (MetadataReference)MetadataReference.CreateFromFile(x))];
	}
}
