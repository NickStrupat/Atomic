using System;
using System.Collections.Immutable;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace NickStrupat.Analyzers;

/// <summary>
/// Reports monitor-based synchronisation applied to an atomic cell: the <see langword="lock"/> statement
/// (<see cref="Rules.LockStatementId"/>) and the <see cref="System.Threading.Monitor"/> methods
/// (<see cref="Rules.MonitorMethodId"/>).
/// </summary>
/// <remarks>
/// <para>
/// Both compile, because a cell is an ordinary reference type, and neither does what it looks like it
/// does. A cell whose value is lock-free never takes its monitor, so a lock on it excludes nothing; a cell
/// whose value is not keeps that value behind the same monitor, so a lock on it blocks the cell's own
/// operations. Which one a caller gets depends on <c>T</c> and the word size, not on anything at the call.
/// </para>
/// <para>
/// Nothing is reported in the assembly that declares <c>Atomic&lt;T&gt;</c>. There, a lock on the cell is
/// the lock path itself — <c>lock (this)</c> in the cell, <c>lock (atomic)</c> in the read-modify-write
/// extensions — and is exactly what this analyzer keeps everyone else away from.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class MonitorOnAtomicAnalyzer : DiagnosticAnalyzer
{
	/// <summary>The library's cell type, the only type the analyzer treats as an atomic cell.</summary>
	private const String AtomicMetadataName = "NickStrupat.Atomic`1";

	/// <summary>
	/// The <c>Monitor</c> members that synchronise on their first argument. <c>IsEntered</c> is
	/// deliberately excluded; it is a query, and is legitimate in assertions and debug checks.
	/// </summary>
	private static readonly ImmutableHashSet<String> MonitorMethodNames = ImmutableHashSet.Create(
		StringComparer.Ordinal,
		[
			nameof(Monitor.Enter),
			nameof(Monitor.TryEnter),
			nameof(Monitor.Exit),
			nameof(Monitor.Wait),
			nameof(Monitor.Pulse),
			nameof(Monitor.PulseAll)
		]);

	/// <inheritdoc/>
	public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
		ImmutableArray.Create(Rules.LockStatement, Rules.MonitorMethod);

	/// <inheritdoc/>
	public override void Initialize(AnalysisContext context)
	{
		context.EnableConcurrentExecution();
		context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
		context.RegisterCompilationStartAction(static compilationStartContext =>
		{
			var compilation = compilationStartContext.Compilation;

			// Nothing can be reported in a compilation that does not reference Atomic<T>.
			if (compilation.GetTypeByMetadataName(AtomicMetadataName) is not { } atomic)
				return;

			// The library's own locks on the cell are its lock path; see the remarks on this class.
			if (SymbolEqualityComparer.Default.Equals(atomic.ContainingAssembly, compilation.Assembly))
				return;

			compilationStartContext.RegisterOperationAction(c => AnalyzeLockStatement(c, atomic), OperationKind.Lock);
			if (compilation.GetTypeByMetadataName("System.Threading.Monitor") is { } monitor)
				compilationStartContext.RegisterOperationAction(c => AnalyzeInvocation(c, atomic, monitor), OperationKind.Invocation);
		});
	}

	private static void AnalyzeLockStatement(OperationAnalysisContext context, INamedTypeSymbol atomic)
	{
		var lockedValue = Unwrap(((ILockOperation)context.Operation).LockedValue);
		if (!IsAtomic(lockedValue.Type, atomic))
			return;

		context.ReportDiagnostic(Diagnostic.Create(
			Rules.LockStatement, lockedValue.Syntax.GetLocation(), Describe(lockedValue.Type)));
	}

	private static void AnalyzeInvocation(OperationAnalysisContext context, INamedTypeSymbol atomic, INamedTypeSymbol monitor)
	{
		var invocation = (IInvocationOperation)context.Operation;
		var method = invocation.TargetMethod;
		if (!MonitorMethodNames.Contains(method.Name))
			return;
		if (!SymbolEqualityComparer.Default.Equals(method.ContainingType, monitor))
			return;

		// Every Monitor method above takes the object it synchronises on as its first parameter.
		if (FindArgument(invocation, ordinal: 0) is not { } argument)
			return;

		var synchronisedOn = Unwrap(argument.Value);
		if (!IsAtomic(synchronisedOn.Type, atomic))
			return;

		context.ReportDiagnostic(Diagnostic.Create(
			Rules.MonitorMethod, synchronisedOn.Syntax.GetLocation(), Describe(synchronisedOn.Type), method.Name));
	}

	/// <summary>
	/// Whether <paramref name="type"/> is <c>Atomic&lt;T&gt;</c> for any <c>T</c>, including a <c>T</c> that
	/// is still a type parameter. The class is sealed, so nothing derives from it and comparing the
	/// unconstructed definitions suffices.
	/// </summary>
	private static Boolean IsAtomic(ITypeSymbol? type, INamedTypeSymbol atomic) =>
		type is INamedTypeSymbol named && SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, atomic);

	private static IArgumentOperation? FindArgument(IInvocationOperation invocation, Int32 ordinal)
	{
		foreach (var argument in invocation.Arguments)
			if (argument.Parameter?.Ordinal == ordinal)
				return argument;
		return null;
	}

	/// <summary>
	/// Strips conversions so that the cell is still recognised behind the conversion to <c>Object</c> a
	/// <c>Monitor</c> call introduces, and behind an explicit cast such as <c>lock ((Object)cell)</c>.
	/// </summary>
	private static IOperation Unwrap(IOperation operation)
	{
		while (operation is IConversionOperation conversion)
			operation = conversion.Operand;
		return operation;
	}

	private static String Describe(ITypeSymbol? type) =>
		type?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) ?? "?";
}
