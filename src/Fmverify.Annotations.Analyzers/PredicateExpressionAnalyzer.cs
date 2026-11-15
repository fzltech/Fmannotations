namespace Fmverify.Annotations.Analyzers;

using System;
using System.Collections.Immutable;
using System.Linq;
using Fmverify.Annotations.Analyzers.Parsing;
using Fmverify.Annotations.Analyzers.Semantic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PredicateExpressionAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(
            Diagnostics.FMV0001,
            Diagnostics.FMV0002,
            Diagnostics.FMV0003,
            Diagnostics.FMV0004,
            Diagnostics.FMV0005,
            Diagnostics.FMV0006,
            Diagnostics.FMV0007);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeAttribute, SyntaxKind.Attribute);
    }

    private static void AnalyzeAttribute(SyntaxNodeAnalysisContext context)
    {
        var attributeSyntax = (AttributeSyntax)context.Node;

        var attrShortName = GetAttributeShortName(context, attributeSyntax);
        if (attrShortName is not ("Invariant" or "Measure" or "Requires" or "Ensures"))
            return;

        var argumentList = attributeSyntax.ArgumentList;
        if (argumentList == null || argumentList.Arguments.Count == 0)
            return;

        var firstArg = argumentList.Arguments[0];
        var exprSyntax = firstArg.Expression;

        string? exprText = null;
        var constant = context.SemanticModel.GetConstantValue(exprSyntax, context.CancellationToken);
        if (constant.HasValue && constant.Value is string s)
        {
            exprText = s;
        }
        else if (exprSyntax is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
        {
            exprText = literal.Token.ValueText;
        }

        if (exprText == null)
            return;

        if (!PredicateParser.TryParse(exprText, out var root, out var error))
        {
            var location = GetSyntaxErrorLocation(attributeSyntax, firstArg, exprText, error?.Position ?? 0);
            context.ReportDiagnostic(Diagnostic.Create(
                Diagnostics.FMV0001,
                location,
                exprText,
                error?.Message ?? "unknown error"));
            return;
        }

        if (root == null)
            return;

        var oldNodes = PredicateParser.FindOldNodes(root).ToList();
        if (oldNodes.Count > 0 && attrShortName != "Ensures")
        {
            foreach (var oldNode in oldNodes)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    Diagnostics.FMV0002,
                    firstArg.GetLocation(),
                    oldNode.Name,
                    attrShortName));
            }
            return;
        }

        // Binder scoping is purely syntactic — a name bound twice is wrong regardless of what any
        // name resolves to — so it runs for every attribute, including [Requires] and [Ensures],
        // and cannot false-positive the way member resolution would.
        PredicateSemanticValidator.ValidateBinderScoping(
            context, attributeSyntax, firstArg, exprText, root);

        // [Requires] and [Ensures] are receiver-independent, so the validator resolves only the
        // calls whose receiver names a type. See PredicateSemanticValidator.Validate.
        PredicateSemanticValidator.Validate(context, attributeSyntax, firstArg, exprText, root, attrShortName);
    }

    private static string? GetAttributeShortName(SyntaxNodeAnalysisContext context, AttributeSyntax attributeSyntax)
    {
        var typeInfo = context.SemanticModel.GetTypeInfo(attributeSyntax, context.CancellationToken);
        string? name = null;
        if (typeInfo.Type is INamedTypeSymbol attributeType)
        {
            name = attributeType.Name;
        }
        else
        {
            name = attributeSyntax.Name switch
            {
                IdentifierNameSyntax id => id.Identifier.ValueText,
                QualifiedNameSyntax q => q.Right.Identifier.ValueText,
                AliasQualifiedNameSyntax a => a.Name.Identifier.ValueText,
                _ => attributeSyntax.Name.ToString(),
            };
        }

        if (name != null && name.EndsWith("Attribute", StringComparison.Ordinal))
        {
            name = name.Substring(0, name.Length - "Attribute".Length);
        }

        return name;
    }

    private static Location GetSyntaxErrorLocation(
        AttributeSyntax attributeSyntax,
        AttributeArgumentSyntax firstArg,
        string exprText,
        int errorPosition)
    {
        if (firstArg.Expression is LiteralExpressionSyntax literal && literal.Token.IsKind(SyntaxKind.StringLiteralToken))
        {
            var token = literal.Token;
            var rawText = token.Text;
            var contentStart = rawText.StartsWith("@\"", StringComparison.Ordinal)
                ? token.SpanStart + 2
                : rawText.StartsWith("\"", StringComparison.Ordinal)
                    ? token.SpanStart + 1
                    : token.SpanStart;

            var start = contentStart + errorPosition;
            if (start >= token.Span.End)
            {
                start = Math.Max(token.SpanStart, token.Span.End - 1);
            }
            var length = Math.Max(1, Math.Min(1, token.Span.End - start));
            return Location.Create(attributeSyntax.SyntaxTree, new TextSpan(start, length));
        }

        return firstArg.GetLocation();
    }
}
