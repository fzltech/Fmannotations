namespace Fmannotations.Tests;

using System.Threading.Tasks;
using Xunit;

public class PredicateSemanticAnalyzerTests
{
    [Fact]
    public async Task Invariant_UnresolvedMember_Emits_FMV0003()
    {
        var source = """
            using Fmverify.Annotations;

            public class MyClass
            {
                [Invariant("Count >= 0")]
                int Total;
            }
            """;

        var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync(source);

        var diag = Assert.Single(diagnostics);
        Assert.Equal("FMV0003", diag.Id);
        Assert.Equal("Member 'Count' is not defined on type 'MyClass'", diag.GetMessage());
    }

    [Fact]
    public async Task Invariant_TypeMismatch_Emits_FMV0004()
    {
        var source = """
            using Fmverify.Annotations;

            public class MyClass
            {
                [Invariant("IsActive")]
                int IsActive;
            }
            """;

        var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync(source);

        var diag = Assert.Single(diagnostics);
        Assert.Equal("FMV0004", diag.Id);
        Assert.Equal("Predicate expression evaluates to 'int', but [Invariant] requires 'bool'", diag.GetMessage());
    }

    [Fact]
    public async Task Measure_TypeMismatch_Emits_FMV0004()
    {
        var source = """
            using Fmverify.Annotations;

            public class MyClass
            {
                [Measure("IsActive")]
                bool IsActive;
            }
            """;

        var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync(source);

        var diag = Assert.Single(diagnostics);
        Assert.Equal("FMV0004", diag.Id);
        Assert.Equal("Predicate expression evaluates to 'bool', but [Measure] requires 'int'", diag.GetMessage());
    }

    [Fact]
    public async Task Measure_ValidInt_Emits_NoDiagnostics()
    {
        var source = """
            using Fmverify.Annotations;

            public class MyClass
            {
                [Measure("Remaining")]
                public int Remaining => 5;
            }
            """;

        var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync(source);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task Invariant_IntBound_Emits_NoDiagnostics()
    {
        var source = """
            using Fmverify.Annotations;

            public class MyClass
            {
                [Invariant("Count <= int.MaxValue")]
                public int Count => 5;
            }
            """;

        var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync(source);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task Invariant_ValidBool_Emits_NoDiagnostics()
    {
        var source = """
            using Fmverify.Annotations;

            public class MyClass
            {
                [Invariant("IsValid")]
                public bool IsValid => true;
            }
            """;

        var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync(source);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task Requires_ReceiverIndependence_Emits_NoDiagnostics()
    {
        var source = """
            using Fmverify.Annotations;

            public class SeamOwner
            {
                [Requires("Unplaced < Capacity")]
                public void Step() { }
            }
            """;

        var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync(source);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task Ensures_ReceiverIndependence_Emits_NoDiagnostics()
    {
        var source = """
            using Fmverify.Annotations;

            public class SeamOwner
            {
                [Ensures("ResultCount == Expected")]
                public void Step() { }
            }
            """;

        var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync(source);

        Assert.Empty(diagnostics);
    }
}
