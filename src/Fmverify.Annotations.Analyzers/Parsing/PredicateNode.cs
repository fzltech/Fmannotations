using System.Collections.Generic;

namespace Fmverify.Annotations.Analyzers.Parsing;

public abstract class PredicateNode
{
    public sealed class IntLiteral : PredicateNode
    {
        public IntLiteral(int value, int position)
        {
            Value = value;
            Position = position;
        }

        public int Value { get; }
        public int Position { get; }
    }

    public sealed class BoolLiteral : PredicateNode
    {
        public BoolLiteral(bool value, int position)
        {
            Value = value;
            Position = position;
        }

        public bool Value { get; }
        public int Position { get; }
    }

    public sealed class Member : PredicateNode
    {
        public Member(IReadOnlyList<string> path, int position)
        {
            Path = path;
            Position = position;
        }

        public IReadOnlyList<string> Path { get; }
        public int Position { get; }
        public string Shown => string.Join(".", Path);
    }

    public sealed class MemberAccess : PredicateNode
    {
        public MemberAccess(PredicateNode target, string name, int position)
        {
            Target = target;
            Name = name;
            Position = position;
        }

        public PredicateNode Target { get; }
        public string Name { get; }
        public int Position { get; }
    }

    /// <summary>
    /// A call with arguments. <see cref="Receiver"/> is null for a call on the annotation receiver;
    /// it is a <see cref="Member"/> for a static type or member path, and any expression for a call
    /// chained on a previous call/member access.
    /// </summary>
    public sealed class Call : PredicateNode
    {
        public Call(
            PredicateNode? receiver,
            string method,
            IReadOnlyList<PredicateNode> arguments,
            int position)
        {
            Receiver = receiver;
            Method = method;
            Arguments = arguments;
            Position = position;
        }

        public PredicateNode? Receiver { get; }
        public string Method { get; }
        public IReadOnlyList<PredicateNode> Arguments { get; }
        public int Position { get; }
        public string Shown =>
            (Receiver is Member m ? m.Shown + "." : "") + Method;
    }

    /// <summary>
    /// <c>Element in Collection</c>. The element is any value expression; the collection is a
    /// member path or a call result that yields an array, list or set.
    /// </summary>
    public sealed class Membership : PredicateNode
    {
        public Membership(PredicateNode element, PredicateNode collection, int position)
        {
            Element = element;
            Collection = collection;
            Position = position;
        }

        public PredicateNode Element { get; }
        public PredicateNode Collection { get; }
        public int Position { get; }
    }

    public sealed class Old : PredicateNode
    {
        public Old(string name, int position)
        {
            Name = name;
            Position = position;
        }

        public string Name { get; }
        public int Position { get; }
    }

    public sealed class Not : PredicateNode
    {
        public Not(PredicateNode operand, int position)
        {
            Operand = operand;
            Position = position;
        }

        public PredicateNode Operand { get; }
        public int Position { get; }
    }

    public sealed class Binary : PredicateNode
    {
        public Binary(string op, PredicateNode left, PredicateNode right, int position)
        {
            Op = op;
            Left = left;
            Right = right;
            Position = position;
        }

        public string Op { get; }
        public PredicateNode Left { get; }
        public PredicateNode Right { get; }
        public int Position { get; }
    }

    public sealed class Quantifier : PredicateNode
    {
        public Quantifier(bool requiresAll, Member source, PredicateNode body, int position)
        {
            RequiresAll = requiresAll;
            Source = source;
            Body = body;
            Position = position;
        }

        public bool RequiresAll { get; }
        public Member Source { get; }
        public PredicateNode Body { get; }
        public int Position { get; }
    }

    /// <summary>
    /// An element read in predicate position: <c>xs[i]</c>. The target is a dotted path or a
    /// member-access chain (the parser refuses anything else); the index is a full expression the
    /// semantic checker holds to be an int.
    /// </summary>
    public sealed class Index : PredicateNode
    {
        public Index(PredicateNode target, PredicateNode indexExpression, int position)
        {
            Target = target;
            IndexExpression = indexExpression;
            Position = position;
        }

        public PredicateNode Target { get; }
        public PredicateNode IndexExpression { get; }
        public int Position { get; }
    }

    /// <summary>
    /// A binder quantifier: <c>forall i: int :: body</c> or <c>exists i :: body</c>. The declared
    /// type is the dotted name exactly as written — the parser admits any type name generically,
    /// and admission of particular types is a semantic question — and is null when the binder is
    /// untyped. <see cref="NamePosition"/> is where the bound variable's name starts, so a
    /// diagnostic underlines the name the reader has to change rather than the keyword it
    /// accidentally matches inside (<c>exists</c> contains an <c>i</c>).
    /// </summary>
    public sealed class BinderQuantifier : PredicateNode
    {
        public BinderQuantifier(
            bool isForall, string name, int namePosition, string? declaredType, PredicateNode body, int position)
        {
            IsForall = isForall;
            Name = name;
            NamePosition = namePosition;
            DeclaredType = declaredType;
            Body = body;
            Position = position;
        }

        public bool IsForall { get; }
        public string Name { get; }
        public int NamePosition { get; }
        public string? DeclaredType { get; }
        public PredicateNode Body { get; }
        public int Position { get; }
    }
}
