using System.Collections.Immutable;
using HooSharper.CodeFixes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Testing;
using Microsoft.CodeAnalysis.Text;

namespace HooSharper.Analyzers.Tests;

public sealed class LineEndingCodeFixTests
{
    [Fact]
    public Task MovingAnInlineGuardCommentUsesTheSurroundingLineEnding()
    {
        const string prefix = "class C\r\n{\r\n    void M(string value)\r\n    {\r\n        ";
        const string suffix = " System.Console.WriteLine(value);\r\n    }\r\n}\r\n";
        return AnalyzerVerifier<UseNullCoalescingAssignmentAnalyzer, UseNullCoalescingAssignmentCodeFixProvider>.VerifyCodeFixAsync(
            prefix + "if (value {|#0:is|} null) /* default */ value = \"x\";" + suffix,
            new DiagnosticResult("HOO1008", DiagnosticSeverity.Info).WithLocation(0),
            prefix + "/* default */\r\n        value ??= \"x\";" + suffix);
    }

    [Fact]
    public async Task IndependentElseFixesComposeAcrossAnUnchangedSwitch()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var workspace = new AdhocWorkspace();
        var solution = workspace.CurrentSolution.WithOptions(workspace.Options.WithChangedOption(FormattingOptions.NewLine, LanguageNames.CSharp, "\n"));
        var project = solution.AddProject("ElseFixAll", "ElseFixAll", LanguageNames.CSharp)
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddMetadataReference(MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        const string switchMethod = "    int Untouched(int n) { switch (n) { case 11: return 1; default: return 0; } }";
        static string Guard(string name, bool fixedCode) =>
            $"    int {name}(bool b)\r\n    {{\r\n        if (b) return 1;\r\n        " +
            (fixedCode ? "" : "else ") + "return 2;\r\n    }";
        static string Source(bool fixedCode) => string.Join("\r\n", "class C", "{", Guard("First", fixedCode), switchMethod, Guard("Last", fixedCode), "}", "");
        var original = SourceText.From(Source(false));
        var document = project.AddDocument("C.cs", original);
        var compilation = (await document.Project.GetCompilationAsync(cancellationToken))!;
        var diagnostics = await compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new RemoveRedundantElseAnalyzer()))
            .GetAnalyzerDiagnosticsAsync(cancellationToken);
        Assert.Equal(2, diagnostics.Length);
        var provider = new RemoveRedundantElseCodeFixProvider();
        var changes = new List<TextChange>();
        foreach (var diagnostic in diagnostics.OrderBy(d => d.Location.SourceSpan.Start))
        {
            var actions = new List<CodeAction>();
            await provider.RegisterCodeFixesAsync(new CodeFixContext(document, diagnostic, (action, _) => actions.Add(action), cancellationToken));
            var operations = await Assert.Single(actions).GetOperationsAsync(cancellationToken);
            var updated = Assert.Single(operations.OfType<ApplyChangesOperation>()).ChangedSolution.GetDocument(document.Id)!;
            var updatedText = await updated.GetTextAsync(cancellationToken);
            var method = diagnostic.Location.SourceTree!.GetRoot(cancellationToken).FindToken(diagnostic.Location.SourceSpan.Start)
                .Parent!.AncestorsAndSelf().OfType<MethodDeclarationSyntax>().First();
            var edits = updatedText.GetTextChanges(original);
            Assert.All(edits, edit => Assert.True(method.FullSpan.Contains(edit.Span), $"Edit {edit.Span} escapes {method.Identifier}"));
            changes.AddRange(edits);
        }
        Assert.Equal(Source(true), original.WithChanges(changes).ToString());
        Assert.Equal(Source(true), original.WithChanges(changes.AsEnumerable().Reverse()).ToString());
    }

    [Fact]
    public async Task WrappingChainUsesConfiguredCrLfInAnLfDocument()
    {
        const string prefix = "class C\n{\n    string M(string value)\n    {\n        return ";
        const string suffix = ";\n    }\n}\n";
        var test = new CSharpCodeFixTest<WrapFluentChainAnalyzer, WrapFluentChainCodeFixProvider, DefaultVerifier>
        {
            ReferenceAssemblies = ReferenceAssemblies.Net.Net100,
            TestCode = prefix + "{|#0:value.Trim().ToUpperInvariant().PadRight(200)|}" + suffix,
            FixedCode = prefix + "value\r\n            .Trim()\r\n            .ToUpperInvariant()\r\n            .PadRight(200)" + suffix,
        };
        test.TestState.AnalyzerConfigFiles.Add(("/.editorconfig", "root = true\n[*.cs]\nhoosharper_max_line_length = 35\nend_of_line = crlf\n"));
        test.ExpectedDiagnostics.Add(new DiagnosticResult("HOO1020", DiagnosticSeverity.Info).WithLocation(0));
        await test.RunAsync(TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(null, "\r\n")]
    [InlineData("end_of_line = lf", "\n")]
    [InlineData("end_of_line = crlf", "\r\n")]
    public async Task MergingConditionsUsesLocalOrConfiguredBreaks(string? setting, string inserted)
    {
        // Editing the guard must leave mixed separators in adjacent source,
        // including raw/verbatim strings, a comment, and a captured expression.
        const string prefix = "class C\n{\r\n    const string Raw = \"\"\"\nfirst\nlast\n\"\"\";\r\n"
            + "    static void Log(bool flag, [System.Runtime.CompilerServices.CallerArgumentExpression(\"flag\")] string expr = \"\") { }\r\n"
            + "    const string Text = @\"start\nend\"; /*keep\ncomment*/\r\n    void M(bool a, bool b)\r\n    {\r\n        Log(a &&\n            b);\r\n";
        const string suffix = "    }\r\n}\r\n";
        const string body = "        {|#0:if|} (a)\r\n        {\r\n            if (b)\r\n            {\r\n                M(false, false);\r\n            }\r\n        }\r\n";
        var test = new CSharpCodeFixTest<MergeNestedIfAnalyzer, MergeNestedIfCodeFixProvider, DefaultVerifier>
        {
            ReferenceAssemblies = ReferenceAssemblies.Net.Net100,
            TestCode = prefix + body + suffix,
            FixedCode = prefix + "        if (a && b)" + inserted + "        {" + inserted + "            M(false, false);" + inserted + "        }\r\n" + suffix,
        };
        if (setting is not null)
            test.TestState.AnalyzerConfigFiles.Add(("/.editorconfig", "root = true\n[*.cs]\n" + setting + "\n"));
        test.ExpectedDiagnostics.Add(new DiagnosticResult("HOO1010", DiagnosticSeverity.Info).WithLocation(0));
        await test.RunAsync(TestContext.Current.CancellationToken);
    }
}
