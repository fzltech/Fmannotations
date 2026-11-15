using Fmverify.Annotations.Analyzers.Parsing;
using Xunit;

namespace Fmannotations.Tests;

public class PredicateParserTests
{
    [Theory]
    [InlineData("Count >= 0")]
    [InlineData("!Done || Success")]
    [InlineData("old(Count) - 1 == Count")]
    [InlineData("all(Items, Active)")]
    [InlineData("any(Values, Pending == 1)")]
    [InlineData("Target.Details.Status == 2")]
    [InlineData("Count <= int.MaxValue")]
    [InlineData("Count >= int.MinValue")]
    public void Valid_expressions_parse_successfully(string expr)
    {
        var ok = PredicateParser.TryParse(expr, out var root, out var error);
        Assert.True(ok, error?.Message);
        Assert.NotNull(root);
    }

    [Theory]
    [InlineData("Count >=", 8)]
    [InlineData("old()", 4)]
    [InlineData("all(Items)", 9)]
    public void Invalid_expressions_return_error_and_position(string expr, int expectedPos)
    {
        var ok = PredicateParser.TryParse(expr, out var root, out var error);
        Assert.False(ok);
        Assert.Null(root);
        Assert.NotNull(error);
        Assert.Equal(expectedPos, error.Position);
    }

    [Theory]
    [InlineData("forall i: int :: xs[i] >= 0")]
    [InlineData("forall i :: xs[i] >= 0")]
    [InlineData("exists i :: xs[i] == 0")]
    [InlineData("forall i: Foo.Bar :: true")]
    [InlineData("forall i :: forall j :: xs[i] == xs[j]")]
    [InlineData("xs[i + 1] > xs[i]")]
    [InlineData("xs[i][j] >= 0")]
    [InlineData("!xs[i]")]
    [InlineData("a ==> b")]
    [InlineData("a ==> b ==> c")]
    [InlineData("!a ==> b")]
    [InlineData("!(a ==> b)")]
    [InlineData("(a ==> b)")]
    [InlineData("all(Items, forall j :: Self[j] > 0 ==> Flag)")]
    [InlineData("forall i :: xs[i] in ys")]
    [InlineData("(x)[i] >= 0")]
    public void Binder_grammar_parses(string expr)
    {
        var ok = PredicateParser.TryParse(expr, out var root, out var error);
        Assert.True(ok, error?.Message);
        Assert.NotNull(root);
    }

    [Fact]
    public void Binder_node_carries_its_parts()
    {
        PredicateParser.TryParse("forall i: int :: xs[i] >= 0", out var root, out _);
        var binder = Assert.IsType<PredicateNode.BinderQuantifier>(root);
        Assert.True(binder.IsForall);
        Assert.Equal("i", binder.Name);
        Assert.Equal("int", binder.DeclaredType);
        var comparison = Assert.IsType<PredicateNode.Binary>(binder.Body);
        Assert.Equal(">=", comparison.Op);
        Assert.IsType<PredicateNode.Index>(comparison.Left);
    }

    [Fact]
    public void Untyped_binder_carries_null_type_and_exists_carries_isForall_false()
    {
        PredicateParser.TryParse("exists i :: xs[i] == 0", out var root, out _);
        var binder = Assert.IsType<PredicateNode.BinderQuantifier>(root);
        Assert.False(binder.IsForall);
        Assert.Null(binder.DeclaredType);
    }

    [Fact]
    public void Dotted_type_name_is_kept_as_written()
    {
        PredicateParser.TryParse("forall i: Foo.Bar :: true", out var root, out _);
        Assert.Equal("Foo.Bar", Assert.IsType<PredicateNode.BinderQuantifier>(root).DeclaredType);
    }

    [Fact]
    public void Implication_is_right_associative()
    {
        PredicateParser.TryParse("a ==> b ==> c", out var root, out _);
        var outer = Assert.IsType<PredicateNode.Binary>(root);
        Assert.Equal("==>", outer.Op);
        Assert.IsType<PredicateNode.Member>(outer.Left);
        Assert.Equal("==>", Assert.IsType<PredicateNode.Binary>(outer.Right).Op);
    }

    [Fact]
    public void The_binder_body_extends_over_an_implication()
    {
        PredicateParser.TryParse("forall i :: p ==> q", out var root, out _);
        var binder = Assert.IsType<PredicateNode.BinderQuantifier>(root);
        Assert.Equal("==>", Assert.IsType<PredicateNode.Binary>(binder.Body).Op);
    }

    [Fact]
    public void An_index_read_binds_tighter_than_a_comparison()
    {
        PredicateParser.TryParse("xs[i] >= 0", out var root, out _);
        var comparison = Assert.IsType<PredicateNode.Binary>(root);
        Assert.Equal(">=", comparison.Op);
        var index = Assert.IsType<PredicateNode.Index>(comparison.Left);
        Assert.IsType<PredicateNode.Member>(index.Target);
        Assert.IsType<PredicateNode.Member>(index.IndexExpression);
    }

    [Fact]
    public void Negation_applies_after_indexing()
    {
        PredicateParser.TryParse("!xs[i]", out var root, out _);
        var not = Assert.IsType<PredicateNode.Not>(root);
        Assert.IsType<PredicateNode.Index>(not.Operand);
    }

    [Theory]
    [InlineData("f(x)[i]", 4)]
    [InlineData("(1)[i]", 3)]
    [InlineData("forall", 6)]
    [InlineData("forall i", 8)]
    [InlineData("forall i ::", 11)]
    [InlineData("forall :: p", 7)]
    [InlineData("forall i: :: p", 10)]
    [InlineData("forall i int :: p", 9)]
    [InlineData("forall i: int p", 14)]
    [InlineData("xs[]", 3)]
    [InlineData("xs[i", 4)]
    [InlineData("forall.Count > 0", 6)]
    [InlineData("exists.Count > 0", 6)]
    [InlineData("forall i: int :: old(x) == 0", 17)]
    public void Binder_and_index_mistakes_are_refused(string expr, int expectedPos)
    {
        var ok = PredicateParser.TryParse(expr, out var root, out var error);
        Assert.False(ok);
        Assert.Null(root);
        Assert.NotNull(error);
        Assert.Equal(expectedPos, error.Position);
    }
}
