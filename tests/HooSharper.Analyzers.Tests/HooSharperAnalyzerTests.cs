using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace HooSharper.Analyzers.Tests;

public sealed class HooSharperAnalyzerTests
{
    [Fact]
    public async Task EmptySourceProducesNoDiagnostics()
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(string.Empty, cancellationToken: TestContext.Current.CancellationToken);
        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(assembly => !assembly.IsDynamic && !string.IsNullOrEmpty(assembly.Location))
            .Select(assembly => MetadataReference.CreateFromFile(assembly.Location));
        var compilation = CSharpCompilation.Create(
            "AnalyzerTest",
            [syntaxTree],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var diagnostics = await compilation
            .WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new HooSharperAnalyzer()))
            .GetAnalyzerDiagnosticsAsync(TestContext.Current.CancellationToken);

        Assert.Empty(diagnostics);
    }
}
