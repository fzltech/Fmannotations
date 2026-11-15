namespace Fmverify.Annotations.Analyzers.Semantic;

using System;
using System.Collections.Generic;
using System.Linq;
using Fmverify.Annotations.Analyzers.Parsing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

public static class PredicateSemanticValidator
{
    public static void Validate(
        SyntaxNodeAnalysisContext context,
        AttributeSyntax attributeSyntax,
        AttributeArgumentSyntax argSyntax,
        string exprText,
        PredicateNode root,
        string attrShortName)
    {
        // [Requires] and [Ensures] MUST NOT perform member checks: the receiver at gate time is not
        // the declaring type (receiver-independence rule). A static call, though, names its own type
        // and is safe to resolve here, so its callee contract is still checked.
        if (attrShortName is "Requires" or "Ensures")
        {
            ValidateStaticCalls(
                root, context, attributeSyntax, argSyntax, exprText);
            return;
        }

        var typeDecl = attributeSyntax.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        INamedTypeSymbol? containingType = null;
        if (typeDecl != null)
        {
            containingType = context.SemanticModel.GetDeclaredSymbol(typeDecl, context.CancellationToken);
        }

        if (containingType == null)
        {
            var symbol = context.SemanticModel.GetEnclosingSymbol(attributeSyntax.SpanStart, context.CancellationToken);
            while (symbol != null && symbol is not INamedTypeSymbol)
            {
                symbol = symbol.ContainingType ?? symbol.ContainingSymbol;
            }
            containingType = symbol as INamedTypeSymbol;
        }

        if (containingType == null)
            return;

        var rootType = EvaluateType(
            root,
            containingType,
            context,
            attributeSyntax,
            argSyntax,
            exprText,
            attrShortName,
            scope: null);

        if (rootType != null)
        {
            if (attrShortName == "Invariant" && rootType.SpecialType != SpecialType.System_Boolean)
            {
                ReportTypeMismatch(context, attributeSyntax, argSyntax, exprText, GetNodePosition(root), rootType, attrShortName, "bool");
            }
            else if (attrShortName == "Measure" && rootType.SpecialType != SpecialType.System_Int32)
            {
                ReportTypeMismatch(context, attributeSyntax, argSyntax, exprText, GetNodePosition(root), rootType, attrShortName, "int");
            }
        }
    }

    /// <summary>
    /// Walks the predicate for binder scoping alone: a name bound twice, or shadowing an
    /// enclosing binder, is wrong regardless of what any name resolves to. That is why this pass
    /// runs for every attribute — including <c>[Requires]</c> and <c>[Ensures]</c>, whose members
    /// the receiver-independence rule keeps unresolved — and why it can never false-positive: it
    /// needs no type information at all.
    /// </summary>
    public static void ValidateBinderScoping(
        SyntaxNodeAnalysisContext context,
        AttributeSyntax attributeSyntax,
        AttributeArgumentSyntax argSyntax,
        string exprText,
        PredicateNode root)
    {
        Walk(root, null);
        return;

        void Walk(PredicateNode node, BinderScope? scope)
        {
            switch (node)
            {
                case PredicateNode.BinderQuantifier binder:
                    if (scope != null && scope.Contains(binder.Name))
                    {
                        ReportBoundVariableProblem(
                            context, attributeSyntax, argSyntax, exprText,
                            FindSegmentPosition(exprText, binder.NamePosition, binder.Name), binder.Name,
                            "is already bound by an enclosing quantifier");
                    }

                    // Recurse with the name pushed even after a report, so a deeper double binding
                    // is still found; one diagnostic per offending binding.
                    Walk(binder.Body, new BinderScope(binder.Name, scope));
                    return;

                case PredicateNode.Index index:
                    Walk(index.Target, scope);
                    Walk(index.IndexExpression, scope);
                    return;

                case PredicateNode.Binary binary:
                    Walk(binary.Left, scope);
                    Walk(binary.Right, scope);
                    return;

                case PredicateNode.Not notNode:
                    Walk(notNode.Operand, scope);
                    return;

                case PredicateNode.Call call:
                    if (call.Receiver is not null) Walk(call.Receiver, scope);
                    foreach (var argument in call.Arguments) Walk(argument, scope);
                    return;

                case PredicateNode.MemberAccess access:
                    Walk(access.Target, scope);
                    return;

                case PredicateNode.Membership membership:
                    Walk(membership.Element, scope);
                    Walk(membership.Collection, scope);
                    return;

                case PredicateNode.Quantifier quantifier:
                    Walk(quantifier.Body, scope);
                    return;

                default:
                    return;
            }
        }
    }

    /// <summary>The type of an index read's target, with the named refusals the runtime mirror:
    /// a string has a char indexer and a dictionary indexes by key, and neither is a collection
    /// this grammar reads by position. Everything else is admitted when the runtime could read
    /// it — arrays, members declared as <c>IList</c>/<c>IReadOnlyList&lt;T&gt;</c> or implementing
    /// them, or an int indexer (Roslyn names C# indexers <c>this[]</c>, so the check is
    /// <see cref="IPropertySymbol.IsIndexer"/>, not a name) with an integer
    /// <c>Count</c>/<c>Length</c> for the range. On admission <paramref name="elementType"/> is
    /// what a read produces: the array's element, the type argument, or the indexer's return
    /// type.</summary>
    private static bool IsIndexable(
        ITypeSymbol type, out string? namedRefusal, out ITypeSymbol? elementType)
    {
        namedRefusal = null;
        elementType = null;

        if (type.SpecialType == SpecialType.System_String)
        {
            namedRefusal = "is a string; a string is not an indexable collection in this language";
            return false;
        }

        if (type is INamedTypeSymbol self && IsDictionaryInterface(self))
        {
            namedRefusal = "is a dictionary; its indexer reads by key, not by position";
            return false;
        }

        foreach (var iface in type.AllInterfaces)
        {
            if (IsDictionaryInterface(iface.OriginalDefinition))
            {
                namedRefusal = "is a dictionary; its indexer reads by key, not by position";
                return false;
            }
        }

        if (type is IArrayTypeSymbol arrayType)
        {
            elementType = arrayType.ElementType;
            return true;
        }

        if (type is INamedTypeSymbol selfList && IsListInterface(selfList) &&
            selfList.TypeArguments.Length == 1)
        {
            // A non-generic IList has no type arguments; skip this arm so it falls
            // through to the indexer-plus-Count path below (admits it with elementType
            // object), instead of indexing TypeArguments[0] and throwing inside the analyzer.
            elementType = selfList.TypeArguments[0];
            return true;
        }

        foreach (var iface in type.AllInterfaces)
        {
            if (IsListInterface(iface.OriginalDefinition) &&
                iface is INamedTypeSymbol constructed && constructed.TypeArguments.Length == 1)
            {
                elementType = constructed.TypeArguments[0];
                return true;
            }
        }

        foreach (var member in type.GetMembers())
        {
            if (member is IPropertySymbol property && property.IsIndexer && property.GetMethod != null &&
                property.Parameters.Length == 1 &&
                property.Parameters[0].Type.SpecialType == SpecialType.System_Int32)
            {
                if (!HasIntProperty(type, "Count") && !HasIntProperty(type, "Length")) return false;
                elementType = property.Type;
                return true;
            }
        }

        return false;
    }

    private static bool IsDictionaryInterface(INamedTypeSymbol definition)
    {
        var ns = definition.ContainingNamespace.ToDisplayString();
        return (definition.Name == "IDictionary" || definition.Name == "IReadOnlyDictionary")
            && ns is "System.Collections" or "System.Collections.Generic";
    }

    private static bool IsDictionaryOn(ITypeSymbol type)
    {
        foreach (var iface in type.AllInterfaces)
        {
            if (IsDictionaryInterface(iface.OriginalDefinition)) return true;
        }

        return false;
    }

    private static bool IsListInterface(INamedTypeSymbol definition)
    {
        var ns = definition.ContainingNamespace.ToDisplayString();
        if (definition.Name == "IList" && ns is "System.Collections" or "System.Collections.Generic") return true;
        if (definition.Name == "IReadOnlyList" && ns == "System.Collections.Generic") return true;
        return false;
    }

    private static bool HasIntProperty(ITypeSymbol type, string name)
    {
        foreach (var member in type.GetMembers(name))
        {
            if (member is IPropertySymbol property && property.GetMethod != null &&
                property.Type.SpecialType == SpecialType.System_Int32)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether the body uses the binder inside an index expression — bare (<c>xs[i]</c>)
    /// or compound (<c>xs[i + 1]</c>), including reads reached through another read
    /// (<c>Rows[i][0]</c>). A bare binder elsewhere (a comparison's operand) names a value, not an
    /// index, and does not pin the type. A nested binder whose body rebinds the name is a
    /// boundary; one that does not is walked through, because its reads are this binder's reads.
    /// <c>all</c>/<c>any</c> bodies are boundaries: their receiver is the element, so a read there
    /// belongs to the element's level and the gate could not enumerate it.</summary>
    private static bool IndexReadsBinder(PredicateNode node, string binderName) => node switch
    {
        PredicateNode.Index index => IndexExpressionReadsBinder(index.IndexExpression, binderName) ||
                                     IndexReadsBinder(index.Target, binderName),
        PredicateNode.Binary binary => IndexReadsBinder(binary.Left, binderName) || IndexReadsBinder(binary.Right, binderName),
        PredicateNode.Not notNode => IndexReadsBinder(notNode.Operand, binderName),
        PredicateNode.MemberAccess access => IndexReadsBinder(access.Target, binderName),
        PredicateNode.Membership membership => IndexReadsBinder(membership.Element, binderName) ||
                                               IndexReadsBinder(membership.Collection, binderName),
        PredicateNode.Call call => call.Arguments.Any(argument => IndexReadsBinder(argument, binderName)) ||
                                   (call.Receiver is not null && IndexReadsBinder(call.Receiver, binderName)),
        PredicateNode.BinderQuantifier nested => nested.Name != binderName &&
                                                 IndexReadsBinder(nested.Body, binderName),
        _ => false,
    };

    private static bool IndexExpressionReadsBinder(PredicateNode node, string binderName) => node switch
    {
        PredicateNode.Member member => member.Path.Count == 1 && member.Path[0] == binderName,
        PredicateNode.Binary binary => IndexExpressionReadsBinder(binary.Left, binderName) ||
                                       IndexExpressionReadsBinder(binary.Right, binderName),
        PredicateNode.Not notNode => IndexExpressionReadsBinder(notNode.Operand, binderName),
        PredicateNode.MemberAccess access => IndexExpressionReadsBinder(access.Target, binderName),
        PredicateNode.Index index => IndexExpressionReadsBinder(index.IndexExpression, binderName),
        PredicateNode.Membership membership => IndexExpressionReadsBinder(membership.Element, binderName) ||
                                               IndexExpressionReadsBinder(membership.Collection, binderName),
        PredicateNode.Call call => call.Arguments.Any(argument => IndexExpressionReadsBinder(argument, binderName)) ||
                                   (call.Receiver is not null && IndexExpressionReadsBinder(call.Receiver, binderName)),
        _ => false,
    };

    private static void ReportBoundVariableProblem(
        SyntaxNodeAnalysisContext context,
        AttributeSyntax attributeSyntax,
        AttributeArgumentSyntax argSyntax,
        string exprText,
        int position,
        string name,
        string suffix)
    {
        var loc = GetLocation(attributeSyntax, argSyntax, exprText, position, name.Length);
        context.ReportDiagnostic(Diagnostic.Create(Diagnostics.FMV0006, loc, name, suffix));
    }

    private static void ReportIndexerProblem(
        SyntaxNodeAnalysisContext context,
        AttributeSyntax attributeSyntax,
        AttributeArgumentSyntax argSyntax,
        string exprText,
        int position,
        string shown,
        string suffix)
    {
        var length = Math.Max(1, Math.Min(shown.Length, exprText.Length - Math.Max(0, position)));
        var loc = GetLocation(attributeSyntax, argSyntax, exprText, position, length);
        context.ReportDiagnostic(Diagnostic.Create(Diagnostics.FMV0007, loc, shown, suffix));
    }

    /// <summary>The name an indexer problem shows: the target as written, so the reader can find
    /// it in the annotation.</summary>
    private static string ShownOfNode(PredicateNode node) => node switch
    {
        PredicateNode.Member member => member.Shown,
        PredicateNode.MemberAccess access => $"{ShownOfNode(access.Target)}.{access.Name}",
        PredicateNode.Index index => $"{ShownOfNode(index.Target)}[...]",
        _ => "the target",
    };

    /// <summary>One frame of the binder scope a quantifier pushes while its body is checked: the
    /// name bound, and where the binding came from. Same shape as the runtime's scope chain.</summary>
    private sealed class BinderScope
    {
        public BinderScope(string name, BinderScope? parent)
        {
            Name = name;
            Parent = parent;
        }

        public string Name { get; }
        public BinderScope? Parent { get; }

        public bool Contains(string sought)
        {
            var frame = this;
            while (frame != null)
            {
                if (frame.Name == sought) return true;
                frame = frame.Parent;
            }

            return false;
        }
    }

    private static ITypeSymbol? EvaluateType(
        PredicateNode node,
        ITypeSymbol receiverType,
        SyntaxNodeAnalysisContext context,
        AttributeSyntax attributeSyntax,
        AttributeArgumentSyntax argSyntax,
        string exprText,
        string attrShortName,
        BinderScope? scope)
    {
        var compilation = context.SemanticModel.Compilation;

        switch (node)
        {
            case PredicateNode.IntLiteral:
                return compilation.GetSpecialType(SpecialType.System_Int32);

            case PredicateNode.BoolLiteral:
                return compilation.GetSpecialType(SpecialType.System_Boolean);

            case PredicateNode.Member member:
                // A bound variable wins before any member or enum lookup — it is the closest
                // binding, and env-first resolution is what lets a body name the binder at all.
                // Only the first segment consults the scope; the rest resolve against the binder's
                // type, which is int, so `i.Foo` reports the honest "Foo is not a member of Int32".
                if (scope != null && scope.Contains(member.Path[0]))
                {
                    if (member.Path.Count == 1)
                        return compilation.GetSpecialType(SpecialType.System_Int32);

                    var rest = new PredicateNode.Member(
                        member.Path.Skip(1).ToList(), member.Position);
                    return ResolveMemberPath(
                        rest, compilation.GetSpecialType(SpecialType.System_Int32),
                        context, attributeSyntax, argSyntax, exprText);
                }

                return ResolveMemberPath(member, receiverType, context, attributeSyntax, argSyntax, exprText);

            case PredicateNode.MemberAccess access:
            {
                var targetType = EvaluateType(
                    access.Target, receiverType, context, attributeSyntax, argSyntax, exprText,
                    attrShortName, scope);
                if (targetType == null) return null;

                var member = ResolveMember(targetType, access.Name);
                if (member == null)
                {
                    ReportUnknownMember(
                        context, attributeSyntax, argSyntax, exprText, access.Position,
                        access.Name, targetType);
                    return null;
                }

                return GetMemberType(member);
            }

            case PredicateNode.Call call:
                return EvaluateCallType(
                    call, receiverType, context, attributeSyntax, argSyntax, exprText, attrShortName, scope);

            case PredicateNode.Membership membership:
            {
                var elementType = EvaluateType(
                    membership.Element, receiverType, context, attributeSyntax, argSyntax, exprText,
                    attrShortName, scope);
                if (elementType == null) return null;

                var collectionType = EvaluateType(
                    membership.Collection, receiverType, context, attributeSyntax, argSyntax, exprText,
                    attrShortName, scope);
                if (collectionType == null) return null;

                var collectionElement = GetCollectionElementType(collectionType, compilation);
                if (collectionElement == null)
                {
                    ReportTypeMismatch(
                        context, attributeSyntax, argSyntax, exprText,
                        GetNodePosition(membership.Collection), collectionType, attrShortName,
                        "collection");
                    return null;
                }

                // The runtime reads the element as a leaf, so only a bool or an int is a value it
                // can compare; an enum member already arrives here as int.
                if (elementType.SpecialType is not
                    (SpecialType.System_Boolean or SpecialType.System_Int32))
                {
                    ReportTypeMismatch(
                        context, attributeSyntax, argSyntax, exprText,
                        GetNodePosition(membership.Element), elementType, attrShortName,
                        "bool or int");
                    return null;
                }

                if (!IsMembershipComparable(collectionElement, elementType))
                {
                    ReportTypeMismatch(
                        context, attributeSyntax, argSyntax, exprText,
                        GetNodePosition(membership.Element), elementType, attrShortName,
                        GetTypeName(collectionElement));
                    return null;
                }

                return compilation.GetSpecialType(SpecialType.System_Boolean);
            }

            case PredicateNode.Not notNode:
            {
                var opType = EvaluateType(notNode.Operand, receiverType, context, attributeSyntax, argSyntax, exprText, attrShortName, scope);
                if (opType == null) return null;
                if (opType.SpecialType != SpecialType.System_Boolean)
                {
                    ReportTypeMismatch(context, attributeSyntax, argSyntax, exprText, GetNodePosition(notNode.Operand), opType, attrShortName, "bool");
                    return null;
                }
                return compilation.GetSpecialType(SpecialType.System_Boolean);
            }

            case PredicateNode.Binary binary:
            {
                var leftType = EvaluateType(binary.Left, receiverType, context, attributeSyntax, argSyntax, exprText, attrShortName, scope);
                var rightType = EvaluateType(binary.Right, receiverType, context, attributeSyntax, argSyntax, exprText, attrShortName, scope);

                if (leftType == null || rightType == null)
                    return null;

                switch (binary.Op)
                {
                    case "&&":
                    case "||":
                    case "==>":
                    {
                        var hasError = false;
                        if (leftType.SpecialType != SpecialType.System_Boolean)
                        {
                            ReportTypeMismatch(context, attributeSyntax, argSyntax, exprText, GetNodePosition(binary.Left), leftType, attrShortName, "bool");
                            hasError = true;
                        }
                        if (rightType.SpecialType != SpecialType.System_Boolean)
                        {
                            ReportTypeMismatch(context, attributeSyntax, argSyntax, exprText, GetNodePosition(binary.Right), rightType, attrShortName, "bool");
                            hasError = true;
                        }
                        return hasError ? null : compilation.GetSpecialType(SpecialType.System_Boolean);
                    }

                    case "+":
                    case "-":
                    {
                        var hasError = false;
                        if (!IsIntOrEnum(leftType))
                        {
                            ReportTypeMismatch(context, attributeSyntax, argSyntax, exprText, GetNodePosition(binary.Left), leftType, attrShortName, "int");
                            hasError = true;
                        }
                        if (!IsIntOrEnum(rightType))
                        {
                            ReportTypeMismatch(context, attributeSyntax, argSyntax, exprText, GetNodePosition(binary.Right), rightType, attrShortName, "int");
                            hasError = true;
                        }
                        return hasError ? null : compilation.GetSpecialType(SpecialType.System_Int32);
                    }

                    case "<":
                    case "<=":
                    case ">":
                    case ">=":
                    {
                        var hasError = false;
                        if (!IsIntOrEnum(leftType))
                        {
                            ReportTypeMismatch(context, attributeSyntax, argSyntax, exprText, GetNodePosition(binary.Left), leftType, attrShortName, "int");
                            hasError = true;
                        }
                        if (!IsIntOrEnum(rightType))
                        {
                            ReportTypeMismatch(context, attributeSyntax, argSyntax, exprText, GetNodePosition(binary.Right), rightType, attrShortName, "int");
                            hasError = true;
                        }
                        return hasError ? null : compilation.GetSpecialType(SpecialType.System_Boolean);
                    }

                    case "==":
                    case "!=":
                    {
                        var leftIsInt = IsIntOrEnum(leftType);
                        var rightIsInt = IsIntOrEnum(rightType);
                        var leftIsBool = leftType.SpecialType == SpecialType.System_Boolean;
                        var rightIsBool = rightType.SpecialType == SpecialType.System_Boolean;

                        if ((leftIsInt && rightIsInt) || (leftIsBool && rightIsBool))
                        {
                            return compilation.GetSpecialType(SpecialType.System_Boolean);
                        }

                        var expected = leftIsInt ? "int" : leftIsBool ? "bool" : GetTypeName(leftType);
                        ReportTypeMismatch(context, attributeSyntax, argSyntax, exprText, GetNodePosition(binary.Right), rightType, attrShortName, expected);
                        return null;
                    }

                    default:
                        return null;
                }
            }

            case PredicateNode.Quantifier quantifier:
            {
                var sourceType = ResolveMemberPath(quantifier.Source, receiverType, context, attributeSyntax, argSyntax, exprText);
                if (sourceType == null)
                    return null;

                var elementType = GetCollectionElementType(sourceType, compilation);
                if (elementType == null)
                {
                    ReportTypeMismatch(context, attributeSyntax, argSyntax, exprText, GetNodePosition(quantifier.Source), sourceType, attrShortName, "collection");
                    return null;
                }

                var bodyType = EvaluateType(quantifier.Body, elementType, context, attributeSyntax, argSyntax, exprText, attrShortName, scope);
                if (bodyType == null)
                    return null;

                if (bodyType.SpecialType != SpecialType.System_Boolean)
                {
                    ReportTypeMismatch(context, attributeSyntax, argSyntax, exprText, GetNodePosition(quantifier.Body), bodyType, attrShortName, "bool");
                    return null;
                }

                return compilation.GetSpecialType(SpecialType.System_Boolean);
            }

            case PredicateNode.Index indexNode:
            {
                var targetType = EvaluateType(
                    indexNode.Target, receiverType, context, attributeSyntax, argSyntax, exprText,
                    attrShortName, scope);
                if (targetType == null) return null;

                if (!IsIndexable(targetType, out var namedRefusal, out var elementType))
                {
                    ReportIndexerProblem(
                        context, attributeSyntax, argSyntax, exprText,
                        GetNodePosition(indexNode.Target), ShownOfNode(indexNode.Target),
                        namedRefusal ?? "is not an indexable collection");
                    return null;
                }

                var indexType = EvaluateType(
                    indexNode.IndexExpression, receiverType, context, attributeSyntax, argSyntax,
                    exprText, attrShortName, scope);
                if (indexType == null) return null;

                if (!IsIntOrEnum(indexType))
                {
                    ReportIndexerProblem(
                        context, attributeSyntax, argSyntax, exprText,
                        GetNodePosition(indexNode.IndexExpression), ShownOfNode(indexNode.Target),
                        $"needs an int index, and '{GetTypeName(indexType)}' is not one");
                    return null;
                }

                return elementType ?? compilation.GetSpecialType(SpecialType.System_Object);
            }

            case PredicateNode.BinderQuantifier binder:
            {
                // The admission rule is textual, never resolved: the validator and the runtime
                // gate must never disagree about a string the adopter can see (design decision
                // D13). `System.Int32` is refused with the same message as `bool`.
                if (binder.DeclaredType is { } declared && declared != "int")
                {
                    ReportBoundVariableProblem(
                        context, attributeSyntax, argSyntax, exprText,
                        FindSegmentPosition(exprText, binder.NamePosition, binder.Name), binder.Name,
                        $"has declared type '{declared}', and only int binders are admitted in this slice");
                    return null;
                }

                // An untyped binder takes its type from use: the index reads pin it as int. A
                // binder no index read mentions cannot be typed — that is a typing failure, not a
                // meaning ruling. A typed binder with no index read is admitted here; whether the
                // gate can enumerate it is gate-time information (D6).
                if (binder.DeclaredType is null && !IndexReadsBinder(binder.Body, binder.Name))
                {
                    ReportBoundVariableProblem(
                        context, attributeSyntax, argSyntax, exprText,
                        FindSegmentPosition(exprText, binder.NamePosition, binder.Name), binder.Name,
                        "cannot have its type inferred from an index read; write the binder's type, like 'i: int'");
                    return null;
                }

                var binderScope = new BinderScope(binder.Name, scope);
                var binderBodyType = EvaluateType(
                    binder.Body, receiverType, context, attributeSyntax, argSyntax, exprText,
                    attrShortName, binderScope);
                if (binderBodyType == null) return null;

                if (binderBodyType.SpecialType != SpecialType.System_Boolean)
                {
                    ReportTypeMismatch(context, attributeSyntax, argSyntax, exprText, GetNodePosition(binder.Body), binderBodyType, attrShortName, "bool");
                    return null;
                }

                return compilation.GetSpecialType(SpecialType.System_Boolean);
            }

            case PredicateNode.Old oldNode:
            {
                var sym = ResolveMember(receiverType, oldNode.Name);
                if (sym == null)
                {
                    var pos = FindSegmentPosition(exprText, oldNode.Position, oldNode.Name);
                    var loc = GetLocation(attributeSyntax, argSyntax, exprText, pos, oldNode.Name.Length);
                    context.ReportDiagnostic(Diagnostic.Create(
                        Diagnostics.FMV0003,
                        loc,
                        oldNode.Name,
                        receiverType.Name));
                    return null;
                }
                var t = GetMemberType(sym);
                return t != null && t.TypeKind == TypeKind.Enum ? compilation.GetSpecialType(SpecialType.System_Int32) : t;
            }

            default:
                return null;
        }
    }

    private static ITypeSymbol? ResolveMemberPath(
        PredicateNode.Member member,
        ITypeSymbol receiverType,
        SyntaxNodeAnalysisContext context,
        AttributeSyntax attributeSyntax,
        AttributeArgumentSyntax argSyntax,
        string exprText)
    {
        var compilation = context.SemanticModel.Compilation;
        var path = member.Path;

        // Check if the path is an enum member (e.g. TaskState.Done)
        // Only if receiver does NOT have path[0] as a member
        if (path.Count >= 2 && ResolveMember(receiverType, path[0]) == null)
        {
            var enumTypeName = string.Join(".", path.Take(path.Count - 1));
            var enumType = FindEnumType(compilation, enumTypeName);
            if (enumType != null)
            {
                var memberName = path[path.Count - 1];
                var enumField = enumType.GetMembers(memberName).OfType<IFieldSymbol>().FirstOrDefault();
                if (enumField != null)
                {
                    return compilation.GetSpecialType(SpecialType.System_Int32);
                }

                var pos = FindSegmentPosition(exprText, member.Position, memberName);
                var loc = GetLocation(attributeSyntax, argSyntax, exprText, pos, memberName.Length);
                context.ReportDiagnostic(Diagnostic.Create(
                    Diagnostics.FMV0003,
                    loc,
                    memberName,
                    enumType.Name));
                return null;
            }
        }

        var currentType = receiverType;
        for (var i = 0; i < path.Count; i++)
        {
            var segment = path[i];
            var sym = ResolveMember(currentType, segment);
            if (sym == null)
            {
                var pos = FindSegmentPosition(exprText, member.Position, segment);
                var loc = GetLocation(attributeSyntax, argSyntax, exprText, pos, segment.Length);
                context.ReportDiagnostic(Diagnostic.Create(
                    Diagnostics.FMV0003,
                    loc,
                    segment,
                    currentType.Name));
                return null;
            }

            var memberType = GetMemberType(sym);
            if (memberType == null)
                return null;

            if (i == path.Count - 1)
            {
                if (memberType.TypeKind == TypeKind.Enum)
                {
                    return compilation.GetSpecialType(SpecialType.System_Int32);
                }
                return memberType;
            }

            currentType = memberType;
        }

        return currentType;
    }

    /// <summary>
    /// The type of <c>Receiver.Method(args)</c>.
    ///
    /// <para>The receiver is resolved as a member first and as a type name second, the same rule
    /// enum members use: <c>Count()</c> is a call on the annotation receiver, <c>x.Validate()</c> is
    /// a call on the <c>x</c> member, and <c>Validator.Validate()</c> is a static call on the type
    /// <c>Validator</c> because the receiver has no member by that name.</para>
    ///
    /// <para>The callee must return a value and carry a <see cref="Diagnostics.FMV0005"/>-worthy
    /// contract: a call the analyzer admits but cannot reason about would be a predicate that
    /// passes by doing nothing.</para>
    /// </summary>
    private static ITypeSymbol? EvaluateCallType(
        PredicateNode.Call call,
        ITypeSymbol receiverType,
        SyntaxNodeAnalysisContext context,
        AttributeSyntax attributeSyntax,
        AttributeArgumentSyntax argSyntax,
        string exprText,
        string attrShortName,
        BinderScope? scope)
    {
        var compilation = context.SemanticModel.Compilation;

        var argumentTypes = new List<ITypeSymbol>();
        foreach (var argument in call.Arguments)
        {
            var argumentType = EvaluateType(
                argument, receiverType, context, attributeSyntax, argSyntax, exprText, attrShortName, scope);
            if (argumentType == null) return null;
            argumentTypes.Add(argumentType);
        }

        ITypeSymbol? targetType;
        var isStatic = false;

        if (call.Receiver == null)
        {
            targetType = receiverType;
        }
        else if (call.Receiver is PredicateNode.Member { Path.Count: 1 } name)
        {
            var member = ResolveMember(receiverType, name.Path[0]);
            if (member == null)
            {
                targetType = FindType(compilation, name.Path[0]);
                if (targetType == null)
                {
                    ReportUnknownMember(
                        context, attributeSyntax, argSyntax, exprText, name.Position,
                        name.Path[0], receiverType);
                    return null;
                }

                isStatic = true;
            }
            else
            {
                targetType = GetMemberType(member);
                if (targetType == null)
                {
                    ReportUnknownMember(
                        context, attributeSyntax, argSyntax, exprText, name.Position,
                        call.Method, receiverType);
                    return null;
                }
            }
        }
        else
        {
            targetType = EvaluateType(
                call.Receiver, receiverType, context, attributeSyntax, argSyntax, exprText, attrShortName, scope);
            if (targetType == null) return null;
        }

        var candidates = Methods(compilation, targetType, call.Method, argumentTypes, isStatic).ToList();
        if (candidates.Count == 0)
        {
            // The method name ends where the '(' begins, so search back from there rather than
            // forward from the call position (which would find a later, unrelated occurrence).
            ReportUnknownMember(
                context, attributeSyntax, argSyntax, exprText,
                Math.Max(0, call.Position - call.Method.Length), call.Method, targetType);
            return null;
        }

        var contracted = candidates.FirstOrDefault(HasContract);
        if (contracted == null)
        {
            ReportNoContract(
                context, attributeSyntax, argSyntax, exprText, call);
            return null;
        }

        return contracted.ReturnType.TypeKind == TypeKind.Enum
            ? compilation.GetSpecialType(SpecialType.System_Int32)
            : contracted.ReturnType;
    }

    /// <summary>
    /// Walks a <c>[Requires]</c>/<c>[Ensures]</c> expression looking only for calls whose receiver
    /// names a type. Nothing else is resolved: a bare member path means the receiver the shape
    /// supplies, which is not the annotated type, so a diagnostic there would be about the wrong
    /// object. A type-qualified call names its own type and is safe.
    /// </summary>
    private static void ValidateStaticCalls(
        PredicateNode node,
        SyntaxNodeAnalysisContext context,
        AttributeSyntax attributeSyntax,
        AttributeArgumentSyntax argSyntax,
        string exprText)
    {
        switch (node)
        {
            case PredicateNode.Call call:
                ValidateStaticCall(call, context, attributeSyntax, argSyntax, exprText);
                if (call.Receiver is PredicateNode.MemberAccess or PredicateNode.Call)
                {
                    ValidateStaticCalls(
                        call.Receiver, context, attributeSyntax, argSyntax, exprText);
                }
                foreach (var argument in call.Arguments)
                    ValidateStaticCalls(argument, context, attributeSyntax, argSyntax, exprText);
                break;

            case PredicateNode.MemberAccess access:
                ValidateStaticCalls(access.Target, context, attributeSyntax, argSyntax, exprText);
                break;

            case PredicateNode.Not not:
                ValidateStaticCalls(not.Operand, context, attributeSyntax, argSyntax, exprText);
                break;

            case PredicateNode.Binary binary:
                ValidateStaticCalls(binary.Left, context, attributeSyntax, argSyntax, exprText);
                ValidateStaticCalls(binary.Right, context, attributeSyntax, argSyntax, exprText);
                break;

            case PredicateNode.Membership membership:
                ValidateStaticCalls(
                    membership.Element, context, attributeSyntax, argSyntax, exprText);
                ValidateStaticCalls(
                    membership.Collection, context, attributeSyntax, argSyntax, exprText);
                break;

            case PredicateNode.Quantifier quantifier:
                ValidateStaticCalls(quantifier.Body, context, attributeSyntax, argSyntax, exprText);
                break;

            case PredicateNode.Index index:
                ValidateStaticCalls(index.Target, context, attributeSyntax, argSyntax, exprText);
                ValidateStaticCalls(index.IndexExpression, context, attributeSyntax, argSyntax, exprText);
                break;

            case PredicateNode.BinderQuantifier binder:
                ValidateStaticCalls(binder.Body, context, attributeSyntax, argSyntax, exprText);
                break;
        }
    }

    private static void ValidateStaticCall(
        PredicateNode.Call call,
        SyntaxNodeAnalysisContext context,
        AttributeSyntax attributeSyntax,
        AttributeArgumentSyntax argSyntax,
        string exprText)
    {
        if (call.Receiver is not PredicateNode.Member { Path.Count: 1 } name) return;

        var compilation = context.SemanticModel.Compilation;
        var type = FindType(compilation, name.Path[0]);
        if (type == null) return;

        var candidates = Methods(type, call.Method, call.Arguments.Count, isStatic: true).ToList();

        // No matching static method means the name is more likely a member of the runtime receiver,
        // which this pass must not resolve. That is the false positive the guard avoids.
        if (candidates.Count == 0) return;
        if (candidates.Any(HasContract)) return;

        ReportNoContract(context, attributeSyntax, argSyntax, exprText, call);
    }

    private static IEnumerable<IMethodSymbol> Methods(
        ITypeSymbol type, string name, int arity, bool isStatic)
    {
        var current = type;
        while (current != null)
        {
            foreach (var method in current.GetMembers(name).OfType<IMethodSymbol>())
            {
                if (Matches(method, arity, isStatic)) yield return method;
            }

            current = current.BaseType;
        }

        foreach (var iface in type.AllInterfaces)
        {
            foreach (var method in iface.GetMembers(name).OfType<IMethodSymbol>())
            {
                if (Matches(method, arity, isStatic)) yield return method;
            }
        }
    }

    private static bool Matches(IMethodSymbol method, int arity, bool isStatic) =>
        method.MethodKind == MethodKind.Ordinary &&
        !method.ReturnsVoid &&
        method.IsStatic == isStatic &&
        method.Parameters.Length == arity &&
        method.Parameters.All(parameter => parameter.RefKind == RefKind.None);

    private static void ReportNoContract(
        SyntaxNodeAnalysisContext context,
        AttributeSyntax attributeSyntax,
        AttributeArgumentSyntax argSyntax,
        string exprText,
        PredicateNode.Call call)
    {
        // Underline the callee and its parentheses, which is the part a reader has to change.
        var start = Math.Max(0, call.Position - call.Shown.Length);
        var length = Math.Max(1, Math.Min(call.Shown.Length + 2, exprText.Length - start));
        var loc = GetLocation(attributeSyntax, argSyntax, exprText, start, length);
        context.ReportDiagnostic(Diagnostic.Create(Diagnostics.FMV0005, loc, call.Shown));
    }

    private static IEnumerable<IMethodSymbol> Methods(
        Compilation compilation,
        ITypeSymbol type,
        string name,
        IReadOnlyList<ITypeSymbol> argumentTypes,
        bool isStatic)
    {
        var current = type;
        while (current != null)
        {
            foreach (var method in current.GetMembers(name).OfType<IMethodSymbol>())
            {
                if (Matches(compilation, method, argumentTypes, isStatic))
                    yield return method;
            }

            current = current.BaseType;
        }

        foreach (var iface in type.AllInterfaces)
        {
            foreach (var method in iface.GetMembers(name).OfType<IMethodSymbol>())
            {
                if (Matches(compilation, method, argumentTypes, isStatic))
                    yield return method;
            }
        }
    }

    private static bool Matches(
        Compilation compilation,
        IMethodSymbol method,
        IReadOnlyList<ITypeSymbol> argumentTypes,
        bool isStatic)
    {
        if (method.MethodKind != MethodKind.Ordinary) return false;
        if (method.ReturnsVoid) return false;
        if (method.IsStatic != isStatic) return false;
        if (method.Parameters.Length != argumentTypes.Count) return false;

        for (var i = 0; i < argumentTypes.Count; i++)
        {
            var parameter = method.Parameters[i];
            if (parameter.RefKind != RefKind.None) return false;
            if (!IsCallArgumentCompatible(compilation, parameter.Type, argumentTypes[i])) return false;
        }

        return true;
    }

    /// <summary>
    /// Whether a call argument can be bound at run time to a parameter.
    ///
    /// <para>The runtime selects a method by testing the boxed value's type
    /// (<c>IsInstanceOfType</c>), so identity, reference and boxing conversions reproduce there
    /// while numeric widening does not: <c>Foo(1)</c> does not bind to <c>Foo(long)</c> at run time
    /// even though C# widens it. Admitting it here would be a call the gate can never make.</para>
    ///
    /// <para>An enum is the exception the grammar created: it models one as its underlying int, so
    /// the runtime converts that int back to the enum before invoking.</para>
    /// </summary>
    private static bool IsCallArgumentCompatible(
        Compilation compilation, ITypeSymbol parameterType, ITypeSymbol argumentType)
    {
        if (SymbolEqualityComparer.Default.Equals(parameterType, argumentType)) return true;

        if (parameterType.TypeKind == TypeKind.Enum &&
            argumentType.SpecialType == SpecialType.System_Int32) return true;
        if (argumentType.TypeKind == TypeKind.Enum &&
            parameterType.SpecialType == SpecialType.System_Int32) return true;

        var conversion = compilation.ClassifyCommonConversion(argumentType, parameterType);
        return conversion.IsImplicit &&
               !conversion.IsNumeric &&
               !conversion.IsNullable &&
               !conversion.IsUserDefined;
    }

    /// <summary>
    /// Whether membership can compare these two at run time.
    ///
    /// <para>The runtime compares the boxed values with <c>Equals</c>, normalising an enum to its
    /// int on either side. Anything else — a numeric widening in particular — would be a silent
    /// false rather than the named failure this language promises.</para>
    /// </summary>
    private static bool IsMembershipComparable(
        ITypeSymbol collectionElement, ITypeSymbol elementType)
    {
        if (SymbolEqualityComparer.Default.Equals(collectionElement, elementType)) return true;
        if (collectionElement.TypeKind == TypeKind.Enum &&
            elementType.SpecialType == SpecialType.System_Int32) return true;
        if (elementType.TypeKind == TypeKind.Enum &&
            collectionElement.SpecialType == SpecialType.System_Int32) return true;
        return false;
    }

    /// <summary>
    /// A callee is admitted when it says what it guarantees. Without a contract the call is a hole
    /// in the reasoning, so it is reported rather than assumed pure.
    /// </summary>
    private static bool HasContract(IMethodSymbol method) =>
        method.GetAttributes().Any(attribute =>
            attribute.AttributeClass is { } type &&
            type.ContainingNamespace?.ToDisplayString() == "Fmverify.Annotations" &&
            type.Name is "RequiresAttribute" or "EnsuresAttribute");

    /// <summary>
    /// A named, non-generic type in the compilation or one of its references. Ambiguity is refused
    /// rather than resolved by assembly order: two same-named types are a reader's problem, not a
    /// coin flip.
    /// </summary>
    private static ITypeSymbol? FindType(Compilation compilation, string name)
    {
        var candidates = new List<INamedTypeSymbol>();
        FindTypesInNamespace(compilation.Assembly.GlobalNamespace, name, candidates);

        // The annotated type's own assembly, and only that one — the runtime resolves a static type
        // in the receiver's assembly, and searching referenced assemblies here would accept a call
        // the gate can never make. A name from a referenced library is refused by name instead.
        return candidates.Count == 1 ? candidates[0] : null;
    }

    private static void FindTypesInNamespace(
        INamespaceSymbol space, string name, List<INamedTypeSymbol> candidates)
    {
        foreach (var type in space.GetTypeMembers())
        {
            if (type.Arity == 0 &&
                type.TypeKind is TypeKind.Class or TypeKind.Struct or TypeKind.Interface &&
                (type.Name == name || type.ToDisplayString() == name))
            {
                candidates.Add(type);
            }

            FindNestedTypes(type, name, candidates);
        }

        foreach (var member in space.GetNamespaceMembers())
            FindTypesInNamespace(member, name, candidates);
    }

    private static void FindNestedTypes(
        INamedTypeSymbol parent, string name, List<INamedTypeSymbol> candidates)
    {
        foreach (var nested in parent.GetTypeMembers())
        {
            if (nested.Arity == 0 &&
                nested.TypeKind is TypeKind.Class or TypeKind.Struct or TypeKind.Interface &&
                (nested.Name == name || nested.ToDisplayString() == name))
            {
                candidates.Add(nested);
            }

            FindNestedTypes(nested, name, candidates);
        }
    }

    private static void ReportUnknownMember(
        SyntaxNodeAnalysisContext context,
        AttributeSyntax attributeSyntax,
        AttributeArgumentSyntax argSyntax,
        string exprText,
        int position,
        string name,
        ITypeSymbol type)
    {
        var pos = FindSegmentPosition(exprText, position, name);
        var loc = GetLocation(attributeSyntax, argSyntax, exprText, pos, Math.Max(1, name.Length));
        context.ReportDiagnostic(Diagnostic.Create(
            Diagnostics.FMV0003, loc, name, type.Name));
    }

    private static ISymbol? ResolveMember(ITypeSymbol type, string name)
    {
        var current = type;
        while (current != null)
        {
            foreach (var member in current.GetMembers(name))
            {
                if (member is IPropertySymbol prop && prop.GetMethod != null)
                    return prop;
                if (member is IFieldSymbol field)
                    return field;
                if (member is IMethodSymbol method && method.Parameters.Length == 0 && !method.ReturnsVoid)
                    return method;
            }
            current = current.BaseType;
        }

        if (type.TypeKind == TypeKind.Interface || type.AllInterfaces.Length > 0)
        {
            foreach (var iface in type.AllInterfaces)
            {
                foreach (var member in iface.GetMembers(name))
                {
                    if (member is IPropertySymbol prop && prop.GetMethod != null)
                        return prop;
                    if (member is IMethodSymbol method && method.Parameters.Length == 0 && !method.ReturnsVoid)
                        return method;
                }
            }
        }

        return null;
    }

    private static ITypeSymbol? GetMemberType(ISymbol member) => member switch
    {
        IPropertySymbol p => p.Type,
        IFieldSymbol f => f.Type,
        IMethodSymbol m => m.ReturnType,
        _ => null,
    };

    private static bool IsIntOrEnum(ITypeSymbol type)
    {
        return type.SpecialType == SpecialType.System_Int32 || type.TypeKind == TypeKind.Enum;
    }

    private static string GetTypeName(ITypeSymbol type)
    {
        return type.SpecialType switch
        {
            SpecialType.System_Int32 => "int",
            SpecialType.System_Boolean => "bool",
            SpecialType.System_String => "string",
            SpecialType.System_Int64 => "long",
            SpecialType.System_Double => "double",
            _ => type.Name,
        };
    }

    private static ITypeSymbol? GetCollectionElementType(ITypeSymbol sourceType, Compilation compilation)
    {
        if (sourceType.SpecialType == SpecialType.System_String)
            return null;

        if (sourceType is IArrayTypeSymbol arrayType)
            return arrayType.ElementType;

        var ienumerableT = compilation.GetSpecialType(SpecialType.System_Collections_Generic_IEnumerable_T);

        if (sourceType is INamedTypeSymbol named && SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, ienumerableT))
            return named.TypeArguments[0];

        foreach (var iface in sourceType.AllInterfaces)
        {
            if (SymbolEqualityComparer.Default.Equals(iface.OriginalDefinition, ienumerableT))
            {
                return iface.TypeArguments[0];
            }
        }

        return null;
    }

    private static INamedTypeSymbol? FindEnumType(Compilation compilation, string typeName)
    {
        var candidates = new List<INamedTypeSymbol>();
        FindEnumTypesInNamespace(compilation.Assembly.GlobalNamespace, typeName, candidates);

        // The same assembly rule as a static type: the runtime looks in the receiver's assembly,
        // and an enum from a referenced assembly would resolve here but not at gate time.
        return candidates.FirstOrDefault();
    }

    private static void FindEnumTypesInNamespace(INamespaceSymbol ns, string typeName, List<INamedTypeSymbol> candidates)
    {
        foreach (var type in ns.GetTypeMembers())
        {
            if (type.TypeKind == TypeKind.Enum && (type.Name == typeName || type.ToDisplayString() == typeName))
            {
                candidates.Add(type);
            }
            FindNestedEnumTypes(type, typeName, candidates);
        }

        foreach (var subNs in ns.GetNamespaceMembers())
        {
            FindEnumTypesInNamespace(subNs, typeName, candidates);
        }
    }

    private static void FindNestedEnumTypes(INamedTypeSymbol parent, string typeName, List<INamedTypeSymbol> candidates)
    {
        foreach (var nested in parent.GetTypeMembers())
        {
            if (nested.TypeKind == TypeKind.Enum && (nested.Name == typeName || nested.ToDisplayString() == typeName))
            {
                candidates.Add(nested);
            }
            FindNestedEnumTypes(nested, typeName, candidates);
        }
    }

    private static int FindSegmentPosition(string text, int startFrom, string segment)
    {
        var idx = text.IndexOf(segment, Math.Max(0, startFrom), StringComparison.Ordinal);
        return idx >= 0 ? idx : startFrom;
    }

    private static int GetNodePosition(PredicateNode node) => node switch
    {
        PredicateNode.IntLiteral i => i.Position,
        PredicateNode.BoolLiteral b => b.Position,
        PredicateNode.Member m => m.Position,
        PredicateNode.MemberAccess a => a.Position,
        PredicateNode.Membership m => m.Position,
        PredicateNode.Call c => c.Position,
        PredicateNode.Old o => o.Position,
        PredicateNode.Not n => n.Position,
        PredicateNode.Binary bin => bin.Position,
        PredicateNode.Quantifier q => q.Position,
        PredicateNode.Index i => i.Position,
        PredicateNode.BinderQuantifier q => q.Position,
        _ => 0,
    };

    private static void ReportTypeMismatch(
        SyntaxNodeAnalysisContext context,
        AttributeSyntax attributeSyntax,
        AttributeArgumentSyntax argSyntax,
        string exprText,
        int position,
        ITypeSymbol actualType,
        string attrShortName,
        string expectedType)
    {
        var loc = GetLocation(attributeSyntax, argSyntax, exprText, position, Math.Max(1, exprText.Length - position));
        context.ReportDiagnostic(Diagnostic.Create(
            Diagnostics.FMV0004,
            loc,
            GetTypeName(actualType),
            attrShortName,
            expectedType));
    }

    private static Location GetLocation(
        AttributeSyntax attributeSyntax,
        AttributeArgumentSyntax argSyntax,
        string exprText,
        int position,
        int length)
    {
        if (argSyntax.Expression is LiteralExpressionSyntax literal && literal.Token.IsKind(SyntaxKind.StringLiteralToken))
        {
            var token = literal.Token;
            var rawText = token.Text;
            var contentStart = rawText.StartsWith("@\"", StringComparison.Ordinal)
                ? token.SpanStart + 2
                : rawText.StartsWith("\"", StringComparison.Ordinal)
                    ? token.SpanStart + 1
                    : token.SpanStart;

            var start = contentStart + Math.Max(0, position);
            if (start < token.Span.End)
            {
                var len = Math.Max(1, Math.Min(length, token.Span.End - start));
                return Location.Create(attributeSyntax.SyntaxTree, new TextSpan(start, len));
            }
        }

        return argSyntax.GetLocation();
    }
}
