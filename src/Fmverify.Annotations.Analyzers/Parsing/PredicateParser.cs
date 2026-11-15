using System;
using System.Collections.Generic;
using System.Linq;

namespace Fmverify.Annotations.Analyzers.Parsing;

public sealed class PredicateParseError
{
    public PredicateParseError(string message, int position)
    {
        Message = message;
        Position = position;
    }

    public string Message { get; }
    public int Position { get; }
}

public sealed class PredicateParser
{
    private readonly List<PredicateToken> _tokens;
    private int _at;

    private PredicateParser(List<PredicateToken> tokens)
    {
        _tokens = tokens;
        _at = 0;
    }

    public static bool TryParse(string text, out PredicateNode? root, out PredicateParseError? error)
    {
        try
        {
            var tokens = Tokenize(text);
            var parser = new PredicateParser(tokens);
            root = parser.ParseAll();
            error = null;
            return true;
        }
        catch (PredicateSyntaxException ex)
        {
            root = null;
            error = new PredicateParseError(ex.Message, ex.Position);
            return false;
        }
    }

    public static bool UsesOldIn(PredicateNode node) => node switch
    {
        PredicateNode.Old => true,
        PredicateNode.Not n => UsesOldIn(n.Operand),
        PredicateNode.Binary b => UsesOldIn(b.Left) || UsesOldIn(b.Right),
        PredicateNode.Call c => (c.Receiver is not null && UsesOldIn(c.Receiver)) ||
                                c.Arguments.Any(UsesOldIn),
        PredicateNode.MemberAccess m => UsesOldIn(m.Target),
        PredicateNode.Membership m => UsesOldIn(m.Element) || UsesOldIn(m.Collection),
        PredicateNode.Index m => UsesOldIn(m.Target) || UsesOldIn(m.IndexExpression),
        PredicateNode.Quantifier => false,
        PredicateNode.BinderQuantifier => false,
        _ => false,
    };

    public static IEnumerable<PredicateNode.Old> FindOldNodes(PredicateNode node)
    {
        switch (node)
        {
            case PredicateNode.Old o:
                yield return o;
                break;
            case PredicateNode.Not n:
                foreach (var o in FindOldNodes(n.Operand))
                    yield return o;
                break;
            case PredicateNode.Binary b:
                foreach (var o in FindOldNodes(b.Left))
                    yield return o;
                foreach (var o in FindOldNodes(b.Right))
                    yield return o;
                break;
            case PredicateNode.Quantifier q:
                foreach (var o in FindOldNodes(q.Body))
                    yield return o;
                break;
            case PredicateNode.Call c:
                if (c.Receiver is not null)
                {
                    foreach (var o in FindOldNodes(c.Receiver))
                        yield return o;
                }
                foreach (var argument in c.Arguments)
                {
                    foreach (var o in FindOldNodes(argument))
                        yield return o;
                }
                break;
            case PredicateNode.MemberAccess m:
                foreach (var o in FindOldNodes(m.Target))
                    yield return o;
                break;
            case PredicateNode.Membership m:
                foreach (var o in FindOldNodes(m.Element))
                    yield return o;
                foreach (var o in FindOldNodes(m.Collection))
                    yield return o;
                break;
            case PredicateNode.Index m:
                foreach (var o in FindOldNodes(m.Target))
                    yield return o;
                foreach (var o in FindOldNodes(m.IndexExpression))
                    yield return o;
                break;

            // A binder body cannot contain old(...) — the parser refuses it — so there is nothing
            // to recurse into. Listed rather than left to an implicit fall-through so a reader sees
            // the decision, the same way the all/any case is listed.
            case PredicateNode.BinderQuantifier:
                break;
        }
    }

    private static List<PredicateToken> Tokenize(string text)
    {
        var tokens = new List<PredicateToken>();
        var i = 0;

        while (i < text.Length)
        {
            var c = text[i];

            if (char.IsWhiteSpace(c)) { i++; continue; }

            if (char.IsLetter(c) || c == '_')
            {
                var start = i;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
                var word = text.Substring(start, i - start);
                tokens.Add(new PredicateToken(word switch
                {
                    "true" => TokenKind.True,
                    "false" => TokenKind.False,
                    "old" => TokenKind.Old,
                    "all" => TokenKind.All,
                    "any" => TokenKind.Any,
                    "in" => TokenKind.In,
                    "forall" => TokenKind.Forall,
                    "exists" => TokenKind.Exists,
                    _ => TokenKind.Identifier,
                }, word, start));
                continue;
            }

            if (char.IsDigit(c))
            {
                var start = i;
                while (i < text.Length && char.IsDigit(text[i])) i++;
                var digits = text.Substring(start, i - start);
                if (!int.TryParse(digits, out _))
                    throw new PredicateSyntaxException($"'{digits}' is not an int, at {start}.", start);

                tokens.Add(new PredicateToken(TokenKind.Number, digits, start));
                continue;
            }

            // The three-character pass runs first so `==>` is one token: without it the two-
            // character `==` would split the operator and the parser would see `==` then `>`.
            var three = i + 2 < text.Length ? text.Substring(i, 3) : null;
            if (three == "==>")
            {
                tokens.Add(new PredicateToken(TokenKind.Imply, three, i));
                i += 3;
                continue;
            }

            var two = i + 1 < text.Length ? text.Substring(i, 2) : null;
            switch (two)
            {
                case "&&": tokens.Add(new PredicateToken(TokenKind.AndAnd, two, i)); i += 2; continue;
                case "||": tokens.Add(new PredicateToken(TokenKind.OrOr, two, i)); i += 2; continue;
                case "==": tokens.Add(new PredicateToken(TokenKind.EqEq, two, i)); i += 2; continue;
                case "!=": tokens.Add(new PredicateToken(TokenKind.BangEq, two, i)); i += 2; continue;
                case "<=": tokens.Add(new PredicateToken(TokenKind.Le, two, i)); i += 2; continue;
                case ">=": tokens.Add(new PredicateToken(TokenKind.Ge, two, i)); i += 2; continue;
                case "::": tokens.Add(new PredicateToken(TokenKind.DoubleColon, two, i)); i += 2; continue;
            }

            TokenKind? single = c switch
            {
                '(' => TokenKind.LParen,
                ')' => TokenKind.RParen,
                '!' => TokenKind.Bang,
                '<' => TokenKind.Lt,
                '>' => TokenKind.Gt,
                '+' => TokenKind.Plus,
                '-' => TokenKind.Minus,
                '.' => TokenKind.Dot,
                ',' => TokenKind.Comma,
                ':' => TokenKind.Colon,
                '[' => TokenKind.LBracket,
                ']' => TokenKind.RBracket,
                _ => null,
            };

            if (single is null)
                throw new PredicateSyntaxException($"'{c}' is not part of the expression language, at {i}.", i);

            tokens.Add(new PredicateToken(single.Value, c.ToString(), i));
            i++;
        }

        tokens.Add(new PredicateToken(TokenKind.End, "", text.Length));
        return tokens;
    }

    private PredicateToken Current => _tokens[_at];

    private PredicateNode ParseAll()
    {
        var node = ParseImply();
        if (Current.Kind != TokenKind.End)
            throw new PredicateSyntaxException(
                $"unexpected '{Current.Text}' at {Current.Position}, after the expression ended.", Current.Position);

        return node;
    }

    /// <summary>
    /// Implication, the loosest binding: right-associative, so <c>a ==&gt; b ==&gt; c</c> groups
    /// from the right the way Dafny reads it. Every expression slot — the top level, binder
    /// bodies, all/any bodies, call arguments, parenthesized groups — parses at this level, so
    /// the operator is general rather than a quantifier-only form.
    /// </summary>
    private PredicateNode ParseImply()
    {
        var node = ParseOr();
        while (Current.Kind == TokenKind.Imply)
        {
            var pos = Current.Position;
            _at++;
            node = new PredicateNode.Binary("==>", node, ParseImply(), pos);
        }

        return node;
    }

    private PredicateNode ParseOr()
    {
        var node = ParseAnd();
        while (Current.Kind == TokenKind.OrOr)
        {
            var pos = Current.Position;
            _at++;
            node = new PredicateNode.Binary("||", node, ParseAnd(), pos);
        }

        return node;
    }

    private PredicateNode ParseAnd()
    {
        var node = ParseComparison();
        while (Current.Kind == TokenKind.AndAnd)
        {
            var pos = Current.Position;
            _at++;
            node = new PredicateNode.Binary("&&", node, ParseComparison(), pos);
        }

        return node;
    }

    private PredicateNode ParseComparison()
    {
        var node = ParseAdditive();

        if (Current.Kind == TokenKind.In)
        {
            var position = Current.Position;
            _at++;
            return new PredicateNode.Membership(node, ParseAdditive(), position);
        }

        var op = Current.Kind switch
        {
            TokenKind.Lt => "<",
            TokenKind.Le => "<=",
            TokenKind.Gt => ">",
            TokenKind.Ge => ">=",
            TokenKind.EqEq => "==",
            TokenKind.BangEq => "!=",
            _ => null,
        };

        if (op is null) return node;

        var pos = Current.Position;
        _at++;
        return new PredicateNode.Binary(op, node, ParseAdditive(), pos);
    }

    private PredicateNode ParseAdditive()
    {
        var node = ParseUnary();
        while (Current.Kind is TokenKind.Plus or TokenKind.Minus)
        {
            var op = Current.Kind == TokenKind.Plus ? "+" : "-";
            var pos = Current.Position;
            _at++;
            node = new PredicateNode.Binary(op, node, ParseUnary(), pos);
        }

        return node;
    }

    private PredicateNode ParseUnary()
    {
        if (Current.Kind != TokenKind.Bang) return ParsePrimary();

        var pos = Current.Position;
        _at++;
        return new PredicateNode.Not(ParseUnary(), pos);
    }

    private PredicateNode ParsePrimary() => ParsePostfix(ParseAtom());

    private PredicateNode ParseAtom()
    {
        var token = Current;
        switch (token.Kind)
        {
            case TokenKind.Number:
                _at++;
                return new PredicateNode.IntLiteral(int.Parse(token.Text), token.Position);

            case TokenKind.True:
            case TokenKind.False:
                _at++;
                return new PredicateNode.BoolLiteral(token.Kind == TokenKind.True, token.Position);

            case TokenKind.Identifier when TryIntBound(out var bound):
                return new PredicateNode.IntLiteral(bound, token.Position);

            case TokenKind.Identifier:
                return new PredicateNode.Member(ParsePath(), token.Position);

            case TokenKind.Old:
                _at++;
                Expect(TokenKind.LParen, "(");
                var name = Current;
                if (name.Kind != TokenKind.Identifier)
                    throw new PredicateSyntaxException(
                        $"old(...) takes one member name, and got '{name.Text}' at {name.Position}.", name.Position);
                _at++;
                Expect(TokenKind.RParen, ")");
                return new PredicateNode.Old(name.Text, token.Position);

            case TokenKind.All:
            case TokenKind.Any:
            {
                var requiresAll = token.Kind == TokenKind.All;
                var shown = requiresAll ? "all" : "any";
                _at++;
                Expect(TokenKind.LParen, "(");

                if (Current.Kind != TokenKind.Identifier)
                    throw new PredicateSyntaxException(
                        $"{shown}(...) takes a member path as its first argument, and got " +
                        $"'{Current.Text}' at {Current.Position}.", Current.Position);

                var source = new PredicateNode.Member(ParsePath(), Current.Position);
                Expect(TokenKind.Comma, ",");
                var body = ParseImply();
                Expect(TokenKind.RParen, ")");

                if (UsesOldIn(body))
                    throw new PredicateSyntaxException(
                        $"old(...) cannot be used inside {shown}(...): there is no earlier value " +
                        "of an element the run produced for it to name. Move it outside the " +
                        "quantifier.", token.Position);

                return new PredicateNode.Quantifier(requiresAll, source, body, token.Position);
            }

            case TokenKind.Forall:
            case TokenKind.Exists:
            {
                var isForall = token.Kind == TokenKind.Forall;
                var shown = isForall ? "forall" : "exists";
                _at++;

                if (Current.Kind != TokenKind.Identifier)
                    throw new PredicateSyntaxException(
                        $"{shown} takes a bound variable name, and got " +
                        (Current.Kind == TokenKind.End ? "the end of the expression." : $"'{Current.Text}'."),
                        Current.Position);

                var binderName = Current.Text;
                var binderNamePosition = Current.Position;
                _at++;

                // The declared type is parsed generically — any dotted name is syntax, and which
                // types are admitted is the semantic validator's question, not the grammar's.
                string? declaredType = null;
                if (Current.Kind == TokenKind.Colon)
                {
                    _at++;
                    if (Current.Kind != TokenKind.Identifier)
                        throw new PredicateSyntaxException(
                            $"a ':' after '{binderName}' must be followed by the bound variable's type, and got " +
                            (Current.Kind == TokenKind.End ? "the end of the expression." : $"'{Current.Text}'."),
                            Current.Position);

                    declaredType = string.Join(".", ParsePath());
                }

                Expect(TokenKind.DoubleColon, "::");
                var body = ParseImply();

                // The refusal points at the old(...) itself rather than at the keyword, so the
                // span names the thing to move. all/any points at its keyword instead; nothing
                // pins that choice, and the more precise span is the better one to hand forward.
                var oldNode = FindOldNodes(body).FirstOrDefault();
                if (oldNode != null)
                    throw new PredicateSyntaxException(
                        $"old(...) cannot be used inside {shown}: there is no earlier value " +
                        "of an element the run produced for it to name. Move it outside the " +
                        "quantifier.", oldNode.Position);

                return new PredicateNode.BinderQuantifier(
                    isForall, binderName, binderNamePosition, declaredType, body, token.Position);
            }

            case TokenKind.LParen:
                _at++;
                var inner = ParseImply();
                Expect(TokenKind.RParen, ")");
                return inner;

            case TokenKind.End:
                throw new PredicateSyntaxException(
                    $"the expression ended at {token.Position}, where a value was expected.", token.Position);

            default:
                throw new PredicateSyntaxException(
                    $"unexpected '{token.Text}' at {token.Position}, where a value was expected.", token.Position);
        }
    }

    /// <summary>
    /// A member access, a call, or an index read after a primary expression. Calls carry their
    /// receiver explicitly: a one-segment path is a call on the annotation receiver, a longer path
    /// splits into a receiver path and the method name, and a call/member chain continues on the
    /// previous expression.
    ///
    /// <para>An index read's target must be path-shaped — a dotted path, a member-access chain, or
    /// a chained read (<c>xs[i][j]</c>). A call result or a literal cannot be indexed, so
    /// <c>f(x)[i]</c> and <c>(1)[i]</c> are refused at parse; a parenthesized path is still a path
    /// and parses.</para>
    /// </summary>
    private PredicateNode ParsePostfix(PredicateNode node)
    {
        while (true)
        {
            if (Current.Kind == TokenKind.Dot)
            {
                var dot = Current.Position;
                _at++;
                if (Current.Kind != TokenKind.Identifier)
                    throw new PredicateSyntaxException(
                        $"a '.' at {dot} must be followed by a member name.", Current.Position);

                node = new PredicateNode.MemberAccess(node, Current.Text, dot);
                _at++;
                continue;
            }

            if (Current.Kind == TokenKind.LBracket)
            {
                var bracket = Current.Position;
                if (node is not (PredicateNode.Member or PredicateNode.MemberAccess or PredicateNode.Index))
                    throw new PredicateSyntaxException(
                        $"an index read at {bracket} needs a dotted path target.", bracket);

                _at++;
                var index = ParseImply();
                Expect(TokenKind.RBracket, "]");
                node = new PredicateNode.Index(node, index, bracket);
                continue;
            }

            if (Current.Kind == TokenKind.LParen)
            {
                var position = Current.Position;
                var (receiver, method) = node switch
                {
                    PredicateNode.Member { Path.Count: 1 } member =>
                        ((PredicateNode?)null, member.Path[0]),
                    PredicateNode.Member member =>
                        ((PredicateNode?)new PredicateNode.Member(
                            member.Path.Take(member.Path.Count - 1).ToList(), member.Position),
                         member.Path[member.Path.Count - 1]),
                    PredicateNode.MemberAccess access =>
                        ((PredicateNode?)access.Target, access.Name),
                    _ => throw new PredicateSyntaxException(
                        $"a call at {position} needs a method name.", position),
                };

                var arguments = ParseArguments();
                node = new PredicateNode.Call(receiver, method, arguments, position);
                continue;
            }

            return node;
        }
    }

    private List<PredicateNode> ParseArguments()
    {
        Expect(TokenKind.LParen, "(");
        var arguments = new List<PredicateNode>();
        if (Current.Kind != TokenKind.RParen)
        {
            arguments.Add(ParseImply());
            while (Current.Kind == TokenKind.Comma)
            {
                _at++;
                arguments.Add(ParseImply());
            }
        }

        Expect(TokenKind.RParen, ")");
        return arguments;
    }

    /// <summary>
    /// The two C# int bounds are constants, not member paths on the receiver. Without this,
    /// <c>int.MaxValue</c> would parse as a dotted path and fail at evaluation.
    /// </summary>
    private bool TryIntBound(out int value)
    {
        value = 0;
        if (Current.Kind != TokenKind.Identifier || Current.Text != "int") return false;
        if (_at + 2 >= _tokens.Count) return false;
        if (_tokens[_at + 1].Kind != TokenKind.Dot) return false;
        if (_tokens[_at + 2].Kind != TokenKind.Identifier) return false;

        var name = _tokens[_at + 2].Text;
        if (name is not ("MaxValue" or "MinValue")) return false;

        _at += 3;
        value = name == "MaxValue" ? int.MaxValue : int.MinValue;
        return true;
    }

    private List<string> ParsePath()
    {
        var path = new List<string> { Current.Text };
        _at++;

        while (Current.Kind == TokenKind.Dot)
        {
            _at++;
            if (Current.Kind != TokenKind.Identifier)
                throw new PredicateSyntaxException(
                    $"a '.' at {Current.Position - 1} must be followed by a member name, and got " +
                    (Current.Kind == TokenKind.End ? "the end of the expression." : $"'{Current.Text}'."),
                    Current.Position);

            path.Add(Current.Text);
            _at++;
        }

        return path;
    }

    private void Expect(TokenKind kind, string shown)
    {
        if (Current.Kind != kind)
            throw new PredicateSyntaxException(
                $"expected '{shown}' at {Current.Position}, and found " +
                (Current.Kind == TokenKind.End ? "the end of the expression." : $"'{Current.Text}'."),
                Current.Position);

        _at++;
    }

    private sealed class PredicateSyntaxException : Exception
    {
        public PredicateSyntaxException(string message, int position) : base(message)
        {
            Position = position;
        }

        public int Position { get; }
    }
}
