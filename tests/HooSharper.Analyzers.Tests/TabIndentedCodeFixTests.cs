using HooSharper.CodeFixes;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Testing;

namespace HooSharper.Analyzers.Tests;

public sealed class TabIndentedCodeFixTests
{
    [Fact]
    public Task RemovingOuterBracesKeepsTheInnerElseVisiblyAttached() =>
        Verify<OmitBracesForSingleLineIfAnalyzer, OmitBracesForSingleLineIfCodeFixProvider>(
            "HOO1002",
            "\t\tif (enabled) {|#0:{|}\r\n\t\t\tif (args.Length > 0) System.Console.WriteLine(1);\r\n\t\t\telse System.Console.WriteLine(2);\r\n\t\t}",
            "\t\tif (enabled)\r\n\t\t\tif (args.Length > 0) System.Console.WriteLine(1);\r\n\t\t\telse System.Console.WriteLine(2);");

    [Fact]
    public Task RemovingBracesKeepsTheFollowingClosingBraceAtItsOriginalIndent() =>
        Verify<OmitBracesForSingleLineIfAnalyzer, OmitBracesForSingleLineIfCodeFixProvider>(
            "HOO1002",
            "\t\tforeach (var arg in args) {\r\n\t\t\tif (enabled) {|#0:{|}\r\n\t\t\t\tSystem.Console.WriteLine(arg);\r\n\t\t\t}\r\n\t\t}",
            "\t\tforeach (var arg in args) {\r\n\t\t\tif (enabled) System.Console.WriteLine(arg);\r\n\t\t}");

    [Fact]
    public Task MergingNestedIfsKeepsTheFollowingStatementAtItsOriginalIndent() =>
        Verify<MergeNestedIfAnalyzer, MergeNestedIfCodeFixProvider>(
            "HOO1010",
            "\t\t{|#0:if|} (enabled) {\r\n\t\t\tif (args.Length > 0) {\r\n\t\t\t\treturn;\r\n\t\t\t}\r\n\t\t}\r\n\r\n\t\tSystem.Console.WriteLine();",
            "\t\tif (enabled && args.Length > 0) {\r\n\t\t\treturn;\r\n\t\t}\r\n\r\n\t\tSystem.Console.WriteLine();");

    [Fact]
    public Task ReplacingArgumentGuardPreservesSiblingIndentation() =>
        Verify<UseThrowIfNullAnalyzer, UseThrowIfNullCodeFixProvider>(
            "HOO1009",
            "\t\tif (args {|#0:==|} null) {\r\n\t\t\tthrow new System.ArgumentNullException(nameof(args));\r\n\t\t}\r\n\t\tSystem.Console.WriteLine();",
            "\t\tSystem.ArgumentNullException.ThrowIfNull(args);\r\n\t\tSystem.Console.WriteLine();");

    private static Task Verify<TAnalyzer, TFix>(string id, string body, string fixedBody)
        where TAnalyzer : DiagnosticAnalyzer, new()
        where TFix : CodeFixProvider, new()
    {
        static string Wrap(string body) => "class Example {\r\n\tvoid Run(bool enabled, string[] args) {\r\n" + body + "\r\n\t}\r\n}\r\n";
        var test = new CSharpCodeFixTest<TAnalyzer, TFix, DefaultVerifier> {
            ReferenceAssemblies = ReferenceAssemblies.Net.Net100, TestCode = Wrap(body), FixedCode = Wrap(fixedBody),
        };
        test.ExpectedDiagnostics.Add(new DiagnosticResult(id, Microsoft.CodeAnalysis.DiagnosticSeverity.Info).WithLocation(0));
        test.TestState.AnalyzerConfigFiles.Add(("/.editorconfig",
            "root = true\n[*.cs]\nindent_style = tab\ntab_width = 2\nindent_size = tab\nend_of_line = crlf\ncsharp_new_line_before_open_brace = none\n"));
        return test.RunAsync(TestContext.Current.CancellationToken);
    }
}
