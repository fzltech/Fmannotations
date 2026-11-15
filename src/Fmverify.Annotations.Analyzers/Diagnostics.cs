namespace Fmverify.Annotations.Analyzers;

using Microsoft.CodeAnalysis;

public static class Diagnostics
{
    private const string Category = "Usage";

    public static readonly DiagnosticDescriptor FMV0001 = new(
        id: "FMV0001",
        title: "Predicate expression syntax error",
        messageFormat: "Predicate expression '{0}' syntax error: {1}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Predicate expression contains a syntax error.");

    public static readonly DiagnosticDescriptor FMV0002 = new(
        id: "FMV0002",
        title: "Illegal old(...) placement",
        messageFormat: "old({0}) cannot be used in [{1}]; old(...) is only permitted in [Ensures]",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "old(...) cannot be used outside [Ensures] contracts.");

    public static readonly DiagnosticDescriptor FMV0003 = new(
        id: "FMV0003",
        title: "Member not defined on type",
        messageFormat: "Member '{0}' is not defined on type '{1}'",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Member is not defined on the containing type.");

    public static readonly DiagnosticDescriptor FMV0004 = new(
        id: "FMV0004",
        title: "Predicate expression type mismatch",
        messageFormat: "Predicate expression evaluates to '{0}', but [{1}] requires '{2}'",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Predicate expression evaluates to an incompatible type.");

    public static readonly DiagnosticDescriptor FMV0005 = new(
        id: "FMV0005",
        title: "Predicate call has no callee contract",
        messageFormat: "Predicate call '{0}' has no [Requires] or [Ensures] contract",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "A predicate call must target a method with a contract.");

    public static readonly DiagnosticDescriptor FMV0006 = new(
        id: "FMV0006",
        title: "Bound variable problem",
        messageFormat: "Bound variable '{0}' {1}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "A quantifier's bound variable is double-bound, of a non-admitted declared type, or untypable from its uses.");

    public static readonly DiagnosticDescriptor FMV0007 = new(
        id: "FMV0007",
        title: "Indexer read problem",
        messageFormat: "Indexer read '{0}' {1}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "An index read's target is not an indexable collection, or its index is not an int.");
}
