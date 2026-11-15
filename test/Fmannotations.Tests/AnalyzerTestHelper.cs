namespace Fmannotations.Tests;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Fmverify.Annotations.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

public static class AnalyzerTestHelper
{
    private static readonly SyntaxTree AnnotationSyntaxTree = CSharpSyntaxTree.ParseText("""
        namespace Fmverify.Annotations
        {
            using System;

            [AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
            public sealed class InvariantAttribute : Attribute
            {
                public InvariantAttribute(string name) { }
            }

            [AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
            public sealed class MeasureAttribute : Attribute
            {
                public MeasureAttribute(string name) { }
            }

            [AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
            public sealed class RequiresAttribute : Attribute
            {
                public RequiresAttribute(string name) { }
            }

            [AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
            public sealed class EnsuresAttribute : Attribute
            {
                public EnsuresAttribute(string name) { }
            }
        }
        """);

    private static readonly MetadataReference[] DefaultReferences = CreateDefaultReferences();

    private static MetadataReference[] CreateDefaultReferences()
    {
        var refs = new List<MetadataReference>();

        var trustedAssemblies = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))?.Split(Path.PathSeparator);
        if (trustedAssemblies != null)
        {
            foreach (var path in trustedAssemblies)
            {
                refs.Add(MetadataReference.CreateFromFile(path));
            }
        }
        else
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!asm.IsDynamic && !string.IsNullOrWhiteSpace(asm.Location))
                {
                    refs.Add(MetadataReference.CreateFromFile(asm.Location));
                }
            }
        }

        return refs.ToArray();
    }

    public static async Task<ImmutableArray<Diagnostic>> GetDiagnosticsAsync(string source, params MetadataReference[] additionalReferences)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source);
        var allRefs = DefaultReferences.Concat(additionalReferences).ToArray();

        var compilation = CSharpCompilation.Create(
            "TestCompilation_" + Guid.NewGuid().ToString("N"),
            new[] { syntaxTree, AnnotationSyntaxTree },
            allRefs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var analyzer = new PredicateExpressionAnalyzer();
        var compilationWithAnalyzers = compilation.WithAnalyzers(
            ImmutableArray.Create<DiagnosticAnalyzer>(analyzer));

        return await compilationWithAnalyzers.GetAnalyzerDiagnosticsAsync();
    }

    /// <summary>
    /// A referenced assembly built from source, for cases where the analyzer must treat a symbol as
    /// coming from outside the annotated compilation.
    /// </summary>
    public static MetadataReference CompileReference(string source)
    {
        var compilation = CSharpCompilation.Create(
            "ExternalReference_" + Guid.NewGuid().ToString("N"),
            new[] { CSharpSyntaxTree.ParseText(source) },
            DefaultReferences,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        if (!result.Success)
        {
            throw new InvalidOperationException(
                "the referenced fixture did not compile: " +
                string.Join(Environment.NewLine, result.Diagnostics));
        }

        return MetadataReference.CreateFromImage(stream.ToArray());
    }
}
