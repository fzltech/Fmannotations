namespace Fmverify.Annotations.Analyzers.Parsing;

public enum TokenKind
{
    Identifier,
    Number,
    True,
    False,
    Old,
    LParen,
    RParen,
    Bang,
    AndAnd,
    OrOr,
    Lt,
    Le,
    Gt,
    Ge,
    EqEq,
    BangEq,
    Plus,
    Minus,
    Dot,
    Comma,
    All,
    Any,
    In,
    Forall,
    Exists,
    Imply,
    Colon,
    DoubleColon,
    LBracket,
    RBracket,
    End,
}

public readonly struct PredicateToken
{
    public PredicateToken(TokenKind kind, string text, int position)
    {
        Kind = kind;
        Text = text;
        Position = position;
    }

    public TokenKind Kind { get; }
    public string Text { get; }
    public int Position { get; }
}
