using HooSharper.CodeFixes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Testing;

namespace HooSharper.Analyzers.Tests;

public sealed class GuardBlankLineTests
{
    [Fact]
    public Task SeparatesAnEarlyReturnFromTheLiftedComment() =>
        Verify<PreferEarlyReturnAnalyzer, PreferEarlyReturnCodeFixProvider>("HOO1001", false);

    [Fact]
    public Task SeparatesAContinueFromTheLiftedComment() =>
        Verify<PreferLoopContinueAnalyzer, PreferLoopContinueCodeFixProvider>("HOO1004", true);

    private static Task Verify<TAnalyzer, TFix>(string id, bool loop)
        where TAnalyzer : DiagnosticAnalyzer, new()
        where TFix : CodeFixProvider, new()
    {
        string indent = loop ? "            " : "        ";
        string open = "class C\n{\n    void Run(bool enabled)\n    {\n" +
            (loop ? "        foreach (var value in new[] { 1 })\n        {\n" : "");
        string close = (loop ? "        }\n" : "") + "    }\n}\n";
        var before = open + indent + "{|#0:if|} (enabled)\n" + indent + "{\n" +
            indent + "    // log once\n" + indent + "    System.Console.WriteLine(1);\n" +
            indent + "    System.Console.WriteLine(2);\n" + indent + "}\n" + close;
        var after = open + indent + "if (!enabled)\n" + indent + (loop ? "    continue;\n\n" : "    return;\n\n") +
            indent + "// log once\n" + indent + "System.Console.WriteLine(1);\n" +
            indent + "System.Console.WriteLine(2);\n" + close;
        var test = new CSharpCodeFixTest<TAnalyzer, TFix, DefaultVerifier>
        {
            ReferenceAssemblies = ReferenceAssemblies.Net.Net100, TestCode = before, FixedCode = after,
        };
        test.ExpectedDiagnostics.Add(new DiagnosticResult(id, DiagnosticSeverity.Info).WithLocation(0));
        test.TestState.AnalyzerConfigFiles.Add(("/.editorconfig", "root = true\n[*.cs]\ndotnet_diagnostic.SA1515.severity = warning\n"));
        return test.RunAsync(TestContext.Current.CancellationToken);
    }
}
