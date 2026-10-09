using System;
using Microsoft.CodeAnalysis;

namespace NickStrupat.Analyzers;

/// <summary>The diagnostics reported by <see cref="MonitorOnAtomicAnalyzer"/>.</summary>
public static class Rules
{
	/// <summary>The id of the diagnostic reported for a <see langword="lock"/> statement on an atomic cell.</summary>
	public const String LockStatementId = "NSAT0001";

	/// <summary>The id of the diagnostic reported for a <c>Monitor</c> call on an atomic cell.</summary>
	public const String MonitorMethodId = "NSAT0002";

	private const String Category = "Usage";
	private const String HelpLinkPrefix = "https://github.com/NickStrupat/Atomic#";

	/// <summary>
	/// What happens to a monitor taken on a cell, which is the same whichever way it was taken. Kept in one
	/// place so the two rules cannot drift apart.
	/// </summary>
	private const String Consequence =
		"Each operation on an atomic cell is atomic on its own, and taking the cell's monitor does not " +
		"combine several into one. Where Atomic<T>.IsLockFree is true the cell never takes its monitor, so " +
		"other threads' operations on it run straight through. Where it is false the cell guards its value " +
		"with that same monitor, so holding it blocks every other thread's operations on the cell. Which of " +
		"the two happens depends on T and on the runtime's word size. To update the value atomically, use a " +
		"read-modify-write method such as Add or Max, or a TryCompareExchange loop; to guard other state, " +
		"lock a separate object.";

	internal static readonly DiagnosticDescriptor LockStatement = new(
		LockStatementId,
		"Do not use the 'lock' statement on an atomic cell",
		"'{0}' is an atomic cell; 'lock' on it does not make the code inside atomic, and can block the cell's own operations. Use a read-modify-write method or a 'TryCompareExchange' loop, or lock a separate object.",
		Category,
		DiagnosticSeverity.Error,
		isEnabledByDefault: true,
		description: Consequence,
		helpLinkUri: HelpLinkPrefix + LockStatementId.ToLowerInvariant());

	internal static readonly DiagnosticDescriptor MonitorMethod = new(
		MonitorMethodId,
		"Do not call 'Monitor' methods on an atomic cell",
		"'{0}' is an atomic cell; 'Monitor.{1}' on it does not make the code it guards atomic, and can block the cell's own operations. Use a read-modify-write method or a 'TryCompareExchange' loop, or lock a separate object.",
		Category,
		DiagnosticSeverity.Error,
		isEnabledByDefault: true,
		description: Consequence,
		helpLinkUri: HelpLinkPrefix + MonitorMethodId.ToLowerInvariant());
}
