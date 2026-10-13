using System.Collections.Immutable;
using HooSharper.Analyzers;
using HooSharper.CodeFixes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace HooSharper.Analyzers.Tests;

public sealed class UsingDeclarationSafetyTests
{
    [Fact]
    public async Task MalformedUsingInitializerDoesNotProduceStyleDiagnosticOrAnalyzerException()
    {
        const string source = "class Example { void Run() { using (var resource = ) { Execute(); } } void Execute() { } }";
        var cancellationToken = TestContext.Current.CancellationToken;
        var tree = CSharpSyntaxTree.ParseText(source, cancellationToken: cancellationToken);
        var compilation = CSharpCompilation.Create("MalformedUsing", [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.Contains(compilation.GetDiagnostics(cancellationToken), diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Error && diagnostic.Id == "CS1525");
        var diagnostics = await compilation.WithAnalyzers(
                ImmutableArray.Create<DiagnosticAnalyzer>(new UseUsingDeclarationAnalyzer()))
            .GetAnalyzerDiagnosticsAsync(cancellationToken);
        Assert.Empty(diagnostics);
    }

    [Theory]
    [InlineData("using (var stream = new System.IO.MemoryStream()) { stream.Flush(); } Execute();")]
    [InlineData("using (var stream = new System.IO.MemoryStream()) { }")]
    [InlineData("using (var stream = ) { Execute(); }")]
    [InlineData("Execute(value); using (var stream = Create(out int value)) { Execute(value); }")]
    [InlineData("restart: Execute(); using (var stream = new System.IO.MemoryStream()) { if (retry) goto restart; stream.Flush(); }")]
    [InlineData("{ again: Execute(); } using (var stream = new System.IO.MemoryStream()) { again: stream.Flush(); }")]
    public async Task StaleDiagnosticDoesNotOfferUnsafeUsingConversion(string statements)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("StaleUsingDiagnostic", LanguageNames.CSharp)
            .WithParseOptions(new CSharpParseOptions(LanguageVersion.Latest))
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddMetadataReference(MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        var source = "class Example { int value = 1; void Run(bool retry) { " + statements +
            " } void Execute(int value = 0) { } System.IO.MemoryStream Create(out int result) { result = 1; return new(); } }";
        var document = project.AddDocument("Test.cs", SourceText.From(source));
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var usingStatement = root!.DescendantNodes().OfType<UsingStatementSyntax>().Single();
        var descriptor = new UseUsingDeclarationAnalyzer().SupportedDiagnostics.Single();
        var diagnostic = Diagnostic.Create(descriptor, usingStatement.UsingKeyword.GetLocation());
        var actions = new List<CodeAction>();
        var context = new CodeFixContext(document, diagnostic,
            (action, _) => actions.Add(action), cancellationToken);

        await new UseUsingDeclarationCodeFixProvider().RegisterCodeFixesAsync(context);

        Assert.Empty(actions);
    }
}
