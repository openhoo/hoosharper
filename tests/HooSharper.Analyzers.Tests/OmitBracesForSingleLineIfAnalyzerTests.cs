using HooSharper.CodeFixes;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using VerifyCS = HooSharper.Analyzers.Tests.AnalyzerVerifier<
    HooSharper.Analyzers.OmitBracesForSingleLineIfAnalyzer,
    HooSharper.CodeFixes.OmitBracesForSingleLineIfCodeFixProvider>;

namespace HooSharper.Analyzers.Tests;

public sealed class OmitBracesForSingleLineIfAnalyzerTests
{
    [Fact]
    public Task FixAllKeepsIndependentCaseBlockLayouts()
    {
        const string source = """
            class C
            {
                int Read(int n, bool ready)
                {
                    switch (n)
                    {
                        case 1:
                        {
                            if (ready)
                            {|#0:{|}
                                return 10;
                            }
                            break;
                        }
                        case 2:
                            {
                                if (ready)
                                {|#1:{|}
                                    return 20;
                                }
                                break;
                            }
                    }
                    return 0;
                }
            }
            """;
        const string expected = """
            class C
            {
                int Read(int n, bool ready)
                {
                    switch (n)
                    {
                        case 1:
                        {
                            if (ready)
                                return 10;
                            break;
                        }
                        case 2:
                            {
                                if (ready)
                                    return 20;
                                break;
                            }
                    }
                    return 0;
                }
            }
            """;
        return VerifyCS.VerifyCodeFixAsync(source,
            [VerifyCS.Diagnostic(OmitBracesForSingleLineIfAnalyzer.DiagnosticId).WithLocation(0),
             VerifyCS.Diagnostic(OmitBracesForSingleLineIfAnalyzer.DiagnosticId).WithLocation(1)], expected, expected);
    }

    [Fact]
    public Task RemovesBracesFromSingleStatementIf()
    {
        const string source = """
            class Example
            {
                void Run(bool enabled)
                {
                    if (enabled)
                    {|#0:{|}
                        Execute();
                    }
                }

                void Execute() { }
            }
            """;
        const string fixedSource = """
            class Example
            {
                void Run(bool enabled)
                {
                    if (enabled)
                        Execute();
                }

                void Execute() { }
            }
            """;

        var expected = VerifyCS.Diagnostic(OmitBracesForSingleLineIfAnalyzer.DiagnosticId)
            .WithLocation(0)
            .WithMessage("Remove braces from this single-statement if");

        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task RemovesBracesFromElseBranch()
    {
        const string source = """
            class Example
            {
                void Run(bool enabled)
                {
                    if (enabled)
                        Execute();
                    else
                    {|#0:{|}
                        Finish();
                    }
                }

                void Execute() { }
                void Finish() { }
            }
            """;
        const string fixedSource = """
            class Example
            {
                void Run(bool enabled)
                {
                    if (enabled)
                        Execute();
                    else
                        Finish();
                }

                void Execute() { }
                void Finish() { }
            }
            """;

        var expected = VerifyCS.Diagnostic(OmitBracesForSingleLineIfAnalyzer.DiagnosticId).WithLocation(0);
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task FixAllRemovesEverySafeBracePair()
    {
        const string source = """
            class Example
            {
                void Run(bool first, bool second)
                {
                    if (first)
                    {|#0:{|}
                        Execute();
                    }

                    if (second)
                    {|#1:{|}
                        Finish();
                    }
                }

                void Execute() { }
                void Finish() { }
            }
            """;
        const string fixedSource = """
            class Example
            {
                void Run(bool first, bool second)
                {
                    if (first)
                        Execute();

                    if (second)
                        Finish();
                }

                void Execute() { }
                void Finish() { }
            }
            """;

        var expected = new[]
        {
            VerifyCS.Diagnostic(OmitBracesForSingleLineIfAnalyzer.DiagnosticId).WithLocation(0),
            VerifyCS.Diagnostic(OmitBracesForSingleLineIfAnalyzer.DiagnosticId).WithLocation(1),
        };

        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource, fixedSource);
    }

    [Fact]
    public Task KeepsBracesWhenElseWouldBindToNestedIf()
    {
        const string source = """
            class Example
            {
                void Run(bool outer, bool inner)
                {
                    if (outer)
                    {
                        if (inner)
                            Execute();
                    }
                    else
                        Finish();
                }

                void Execute() { }
                void Finish() { }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task KeepsBracesForLocalDeclaration()
    {
        const string source = """
            class Example
            {
                void Run(bool enabled)
                {
                    if (enabled)
                    {
                        int value = 1;
                    }
                }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task KeepsBracesWhenOutVariableWouldCollideWithLaterDeclaration()
    {
        const string source = """
            class Example
            {
                void Run(bool enabled, bool other)
                {
                    if (enabled)
                    {
                        TryGet(out var value);
                    }

                    if (other)
                    {
                        int value = 0;
                        Use(value);
                    }
                }

                bool TryGet(out int value) { value = 0; return true; }
                void Use(int value) { }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task KeepsBracesWhenPatternVariableWouldCollideWithLaterDeclaration()
    {
        const string source = """
            class Example
            {
                void Run(bool enabled, bool other, object item)
                {
                    if (enabled)
                    {
                        Use(item is int value);
                    }

                    if (other)
                    {
                        int value = 0;
                        Use(value > 0);
                    }
                }

                void Use(bool value) { }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task PreservesCommentsAttachedToBraceTokens()
    {
        const string source = """
            class Example
            {
                void Run(bool enabled)
                {
                    if (enabled)
                    {|#0:{|} // opening brace
                        Execute();
                    // closing brace
                    } // after closing brace
                }

                void Execute() { }
            }
            """;
        const string fixedSource = """
            class Example
            {
                void Run(bool enabled)
                {
                    if (enabled)
                        // opening brace
                        Execute();
                    // closing brace
                    // after closing brace
                }

                void Execute() { }
            }
            """;

        var expected = VerifyCS.Diagnostic(OmitBracesForSingleLineIfAnalyzer.DiagnosticId).WithLocation(0);
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task KeepsBracesForMultipleStatements()
    {
        const string source = """
            class Example
            {
                void Run(bool enabled)
                {
                    if (enabled)
                    {
                        Execute();
                        Finish();
                    }
                }

                void Execute() { }
                void Finish() { }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task KeepsBracesForLabeledStatement()
    {
        const string source = """
            class Example
            {
                void Run(bool enabled)
                {
                    if (enabled)
                    {
                        Label: Execute();
                    }
                }

                void Execute() { }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task KeepsBracesWhenWhileWrappedIfCouldCaptureElse()
    {
        const string source = """
            class Example
            {
                void Run(bool outer, bool inner)
                {
                    if (outer)
                    {
                        while (inner)
                            if (inner)
                                Execute();
                    }
                    else
                    {|#0:{|}
                        Finish();
                    }
                }

                void Execute() { }
                void Finish() { }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(
            source,
            VerifyCS.Diagnostic(OmitBracesForSingleLineIfAnalyzer.DiagnosticId).WithLocation(0));
    }


    [Fact]
    public Task KeepsBracesWhenElseIfWrapperCouldCaptureElse()
    {
        const string source = """
            class Example
            {
                void Run(bool outer, bool inner, bool third)
                {
                    if (outer)
                    {
                        if (inner)
                            Execute();
                        else if (third)
                            Finish();
                    }
                    else
                    {|#0:{|}
                        Finish();
                    }
                }

                void Execute() { }
                void Finish() { }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(
            source,
            VerifyCS.Diagnostic(OmitBracesForSingleLineIfAnalyzer.DiagnosticId).WithLocation(0));
    }
    [Fact]
    public Task KeepsBracesForEveryRecursiveDanglingElseShape()
    {
        const string source = """
            using System;

            class Example
            {
                void ForLoop(bool outer, bool inner)
                {
                    if (outer)
                    {
                        for (; inner;)
                            if (inner)
                                Execute();
                    }
                    else
                        Finish();
                }

                void ForEach(bool outer, bool inner, bool[] values)
                {
                    if (outer)
                    {
                        foreach (var value in values)
                            if (value)
                                Execute();
                    }
                    else
                        Finish();
                }

                void ForEachVariable(bool outer, (bool First, bool Second)[] values)
                {
                    if (outer)
                    {
                        foreach (var (first, second) in values)
                            if (first && second)
                                Execute();
                    }
                    else
                        Finish();
                }

                void DoLoop(bool outer, bool inner)
                {
                    if (outer)
                    {
                        do
                            if (inner)
                                Execute();
                        while (inner);
                    }
                    else
                        Finish();
                }

                void Using(bool outer, bool inner)
                {
                    if (outer)
                    {
                        using (new Resource())
                            if (inner)
                                Execute();
                    }
                    else
                        Finish();
                }

                void LockAndLabel(bool outer, bool inner)
                {
                    if (outer)
                    {
                        Target:
                        lock (this)
                            if (inner)
                                Execute();
                    }
                    else
                        Finish();
                }

                void Execute() { }
                void Finish() { }

                sealed class Resource : IDisposable
                {
                    public void Dispose() { }
                }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task RemovesBracesWhenDesignationDoesNotCollide()
    {
        const string source = """
            class Example
            {
                void Run(bool enabled, object item)
                {
                    if (enabled)
                    {|#0:{|}
                        Use(item is int value);
                    }
                }

                void Use(bool value) { }
            }
            """;
        const string fixedSource = """
            class Example
            {
                void Run(bool enabled, object item)
                {
                    if (enabled)
                        Use(item is int value);
                }

                void Use(bool value) { }
            }
            """;

        var expected = VerifyCS.Diagnostic(OmitBracesForSingleLineIfAnalyzer.DiagnosticId).WithLocation(0);
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task PreservesBlockCommentAfterClosingBrace()
    {
        const string source = """
            class Example
            {
                void Run(bool enabled)
                {
                    if (enabled)
                    {|#0:{|}
                        Execute();
                    } /* after closing brace */
                }

                void Execute() { }
            }
            """;
        const string fixedSource = """
            class Example
            {
                void Run(bool enabled)
                {
                    if (enabled)
                        Execute();
                    /* after closing brace */
                }

                void Execute() { }
            }
            """;

        var expected = VerifyCS.Diagnostic(OmitBracesForSingleLineIfAnalyzer.DiagnosticId).WithLocation(0);
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task KeepsBracesAroundDirective()
    {
        const string source = """
            class Example
            {
                void Run(bool enabled)
                {
                    if (enabled)
                    {
            #if DEBUG
                        Execute();
            #endif
                    }
                }

                void Execute() { }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }
    [Fact]
    public Task KeepsBracesForEmbeddedIfWhoseParentIsNotABlock()
    {
        const string source = """
            class Example
            {
                void Run(bool enabled, object item)
                {
                    while (enabled)
                        if (enabled)
                        {
                            Use(item is int value);
                        }
                }

                void Use(bool value) { }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task KeepsBracesWhenOutVariableWouldShadowTheIfCondition()
    {
        const string source = """
            class Example
            {
                int value = 1;
                void Run()
                {
                    if (value > 0)
                    {
                        Assign(out int value);
                    }
                }
                void Assign(out int result) => result = 2;
            }
            """;
        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task KeepsNestedElseBlockThatProtectsAnEnclosingElse() =>
        VerifyCS.VerifyAnalyzerAsync("""
            class C
            {
                int Run(bool a, bool b, bool c)
                {
                    if (a)
                        if (b) return 1;
                        else { if (c) return 2; }
                    else return 3;
                    return 0;
                }
            }
            """);

    [Fact]
    public Task FixAllKeepsOneBlockBetweenNestedUnmatchedIfAndOuterElse()
    {
        const string source = """
            class C
            {
                int Run(bool a, bool b, bool c)
                {
                    if (a)
                    {
                        if (b)
                            return 1;
                        else
                        {|#0:{|}
                            if (c) return 2;
                        }
                    }
                    else return 3;
                    return 0;
                }
            }
            """;
        const string fixedSource = """
            class C
            {
                int Run(bool a, bool b, bool c)
                {
                    if (a)
                    {
                        if (b)
                            return 1;
                        else
                            if (c) return 2;
                    }
                    else return 3;
                    return 0;
                }
            }
            """;
        var expected = VerifyCS.Diagnostic(OmitBracesForSingleLineIfAnalyzer.DiagnosticId).WithLocation(0);
        return VerifyCS.VerifyCodeFixAsync(source, new[] { expected }, fixedSource, fixedSource);
    }

    [Fact]
    public async Task FixAllRemovesOverlappingNestedBracesInOnePass()
    {
        const string source = "class C {\r\n\tvoid Run(bool a, bool b) {\r\n"
            + "\t\tif (a) {|#0:{|}\r\n\t\t\tif (b) {|#1:{|}\r\n\t\t\t\tRun(false, false);\r\n\t\t\t}\r\n\t\t}\r\n"
            + "\t\tif (b) {|#2:{|}\r\n\t\t\tRun(false, true);\r\n\t\t}\r\n\t}\r\n}\r\n";
        const string fixedSource = "class C {\r\n\tvoid Run(bool a, bool b) {\r\n"
            + "\t\tif (a) if (b) Run(false, false);\r\n\t\tif (b) Run(false, true);\r\n\t}\r\n}\r\n";
        var test = new CSharpCodeFixTest<OmitBracesForSingleLineIfAnalyzer,
            OmitBracesForSingleLineIfCodeFixProvider, DefaultVerifier>
        {
            ReferenceAssemblies = ReferenceAssemblies.Net.Net100,
            TestCode = source,
            FixedCode = fixedSource,
            BatchFixedCode = fixedSource,
            NumberOfIncrementalIterations = 3,
            NumberOfFixAllIterations = 1,
        };
        test.TestState.AnalyzerConfigFiles.Add(("/.editorconfig",
            "root = true\n[*.cs]\nindent_style = tab\ntab_width = 2\nindent_size = tab\nend_of_line = crlf\ncsharp_new_line_before_open_brace = none\n"));
        for (var i = 0; i < 3; i++)
            test.ExpectedDiagnostics.Add(VerifyCS.Diagnostic(OmitBracesForSingleLineIfAnalyzer.DiagnosticId).WithLocation(i));
        await test.RunAsync(TestContext.Current.CancellationToken);
    }
}
