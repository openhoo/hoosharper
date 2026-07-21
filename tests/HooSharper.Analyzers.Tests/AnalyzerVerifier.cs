using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;

namespace HooSharper.Analyzers.Tests;

internal static class AnalyzerVerifier<TAnalyzer, TCodeFix>
    where TAnalyzer : DiagnosticAnalyzer, new()
    where TCodeFix : CodeFixProvider, new()
{
    public static DiagnosticResult Diagnostic(string diagnosticId) =>
        new DiagnosticResult(diagnosticId, DiagnosticSeverity.Info);

    public static Task VerifyAnalyzerAsync(string source) =>
        new Test
        {
            TestCode = source,
        }.RunAsync(TestContext.Current.CancellationToken);

    public static Task VerifyAnalyzerAsync(string source, DiagnosticResult expected) =>
        new Test
        {
            TestCode = source,
            ExpectedDiagnostics = { expected },
        }.RunAsync(TestContext.Current.CancellationToken);

    public static Task VerifyCodeFixAsync(
        string source,
        DiagnosticResult expected,
        string fixedSource) =>
        new Test
        {
            TestCode = source,
            FixedCode = fixedSource,
            ExpectedDiagnostics = { expected },
        }.RunAsync(TestContext.Current.CancellationToken);

    public static Task VerifyCodeFixAsync(
        string source,
        IEnumerable<DiagnosticResult> expected,
        string fixedSource,
        string? batchFixedSource = null)
    {
        var test = new Test
        {
            TestCode = source,
            FixedCode = fixedSource,
            BatchFixedCode = batchFixedSource ?? fixedSource,
        };
        test.ExpectedDiagnostics.AddRange(expected);
        return test.RunAsync(TestContext.Current.CancellationToken);
    }

    private sealed class Test : CSharpCodeFixTest<TAnalyzer, TCodeFix, DefaultVerifier>
    {
        public Test()
        {
            ReferenceAssemblies = ReferenceAssemblies.Net.Net100;
            SolutionTransforms.Add((solution, projectId) =>
            {
                var project = solution.GetProject(projectId)!;
                return solution.WithProjectParseOptions(
                    projectId,
                    ((Microsoft.CodeAnalysis.CSharp.CSharpParseOptions)project.ParseOptions!)
                        .WithLanguageVersion(Microsoft.CodeAnalysis.CSharp.LanguageVersion.Latest));
            });
        }
    }
}
