using System.Collections.Immutable;
using HooSharper.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace HooSharper.Analyzers.Tests;

public sealed class ControlFlowMalformedCodeTests
{
    [Theory]
    [InlineData("void Run(bool ready) { if (ready) { Execute() } }", "CS1002")]
    [InlineData("void Run(bool ready) { while (true) { if (ready) { Execute() } } }", "CS1002")]
    [InlineData("void Run(bool ready) { if (ready) return; else { Execute() } }", "CS1002")]
    [InlineData("void Run(bool ready) { if (ready) { if (ready) { Execute() } } }", "CS1002")]
    [InlineData("bool Run(bool ready) { if (ready) return true return false; }", "CS1002")]
    [InlineData("bool Run(bool ready) => true == ;", "CS1525")]
    [InlineData("void Run(bool ready) { if (ready && ) { Execute(); } }", "CS1525")]
    public async Task IncompleteEditorCodeHasExpectedCompilerErrorsAndNoStyleDiagnostics(
        string method,
        string expectedCompilerError)
    {
        var source = "class Example { " + method + " void Execute() { } }";
        var cancellationToken = TestContext.Current.CancellationToken;
        var tree = CSharpSyntaxTree.ParseText(
            source,
            new CSharpParseOptions(LanguageVersion.Latest),
            cancellationToken: cancellationToken);
        var compilation = CSharpCompilation.Create(
            "IncompleteEditorCode",
            [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        // Compiler errors are deliberate: these snapshots represent code while
        // its author is still typing. They must never produce an unsafe style
        // suggestion or an analyzer exception (AD0001).
        Assert.Contains(compilation.GetDiagnostics(cancellationToken), diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Error && diagnostic.Id == expectedCompilerError);

        var analyzers = ImmutableArray.Create<DiagnosticAnalyzer>(
            new PreferEarlyReturnAnalyzer(),
            new PreferLoopContinueAnalyzer(),
            new RemoveRedundantElseAnalyzer(),
            new MergeNestedIfAnalyzer(),
            new OmitBracesForSingleLineIfAnalyzer(),
            new SimplifyBooleanReturnAnalyzer(),
            new SimplifyBooleanComparisonAnalyzer());
        var diagnostics = await compilation.WithAnalyzers(analyzers)
            .GetAnalyzerDiagnosticsAsync(cancellationToken);

        Assert.Empty(diagnostics);
    }
}
