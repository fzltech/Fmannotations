namespace Fmannotations.Tests;

using System.Threading.Tasks;
using Xunit;

public class PredicateSyntaxAnalyzerTests
{
    [Fact]
    public async Task Invariant_SyntaxError_Emits_FMV0001()
    {
        var source = """
            using Fmverify.Annotations;

            [Invariant("Count >=")]
            public class TestClass
            {
                public int Count { get; set; }
            }
            """;

        var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync(source);

        var diag = Assert.Single(diagnostics);
        Assert.Equal("FMV0001", diag.Id);
        Assert.Contains("Count >=", diag.GetMessage());
        Assert.Contains("syntax error", diag.GetMessage());
    }

    [Fact]
    public async Task Requires_SyntaxError_Emits_FMV0001()
    {
        var source = """
            using Fmverify.Annotations;

            public class TestClass
            {
                [Requires("Count ==")]
                public void Step() { }
            }
            """;

        var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync(source);

        var diag = Assert.Single(diagnostics);
        Assert.Equal("FMV0001", diag.Id);
        Assert.Contains("Count ==", diag.GetMessage());
    }

    [Fact]
    public async Task Binder_SyntaxError_Emits_FMV0001()
    {
        // A binder-shape mistake is ordinary FMV0001 through the unchanged span plumbing — the
        // new tokens are only new tokens.
        var source = """
            using Fmverify.Annotations;

            [Invariant("forall i: :: p")]
            public class TestClass
            {
                public int Count { get; set; }
            }
            """;

        var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync(source);

        var diag = Assert.Single(diagnostics);
        Assert.Equal("FMV0001", diag.Id);
        Assert.Contains("forall i: :: p", diag.GetMessage());
    }

    [Fact]
    public async Task Requires_WithOld_Emits_FMV0002()
    {
        var source = """
            using Fmverify.Annotations;

            public class TestClass
            {
                [Requires("old(Count) > 0")]
                public void Step() { }
            }
            """;

        var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync(source);

        var diag = Assert.Single(diagnostics);
        Assert.Equal("FMV0002", diag.Id);
        Assert.Equal("old(Count) cannot be used in [Requires]; old(...) is only permitted in [Ensures]", diag.GetMessage());
    }

    [Fact]
    public async Task Invariant_WithOld_Emits_FMV0002()
    {
        var source = """
            using Fmverify.Annotations;

            [Invariant("old(Count) > 0")]
            public class TestClass
            {
                public int Count { get; set; }
            }
            """;

        var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync(source);

        var diag = Assert.Single(diagnostics);
        Assert.Equal("FMV0002", diag.Id);
        Assert.Equal("old(Count) cannot be used in [Invariant]; old(...) is only permitted in [Ensures]", diag.GetMessage());
    }

    [Fact]
    public async Task Measure_WithOld_Emits_FMV0002()
    {
        var source = """
            using Fmverify.Annotations;

            public class TestClass
            {
                [Measure("old(Count)")]
                public int Count { get; set; }
            }
            """;

        var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync(source);

        var diag = Assert.Single(diagnostics);
        Assert.Equal("FMV0002", diag.Id);
        Assert.Equal("old(Count) cannot be used in [Measure]; old(...) is only permitted in [Ensures]", diag.GetMessage());
    }

    [Fact]
    public async Task Ensures_WithOld_Emits_NoDiagnostic()
    {
        var source = """
            using Fmverify.Annotations;

            public class TestClass
            {
                [Ensures("old(Count) - 1 == Count")]
                public void Step() { }
            }
            """;

        var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync(source);

        Assert.Empty(diagnostics);
    }
}
