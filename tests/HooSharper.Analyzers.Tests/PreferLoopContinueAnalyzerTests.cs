using HooSharper.CodeFixes;
using VerifyCS = HooSharper.Analyzers.Tests.AnalyzerVerifier<
    HooSharper.Analyzers.PreferLoopContinueAnalyzer,
    HooSharper.CodeFixes.PreferLoopContinueCodeFixProvider>;

namespace HooSharper.Analyzers.Tests;

public sealed class PreferLoopContinueAnalyzerTests
{
    [Fact]
    public Task ConvertsFinalForeachIfIntoContinueGuard()
    {
        const string source = """
            using System.Collections.Generic;

            class Example
            {
                void Run(IEnumerable<int> values)
                {
                    foreach (var value in values)
                    {
                        Prepare(value);
                        {|#0:if|} (value > 0)
                        {
                            Execute(value);
                            Finish(value);
                        }
                    }
                }

                void Prepare(int value) { }
                void Execute(int value) { }
                void Finish(int value) { }
            }
            """;
        const string fixedSource = """
            using System.Collections.Generic;

            class Example
            {
                void Run(IEnumerable<int> values)
                {
                    foreach (var value in values)
                    {
                        Prepare(value);
                        if (!(value > 0))
                            continue;
                        Execute(value);
                        Finish(value);
                    }
                }

                void Prepare(int value) { }
                void Execute(int value) { }
                void Finish(int value) { }
            }
            """;

        var expected = VerifyCS.Diagnostic(PreferLoopContinueAnalyzer.DiagnosticId)
            .WithLocation(0)
            .WithMessage("Invert this condition and continue early");

        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task ConvertsFinalWhileIfAndSimplifiesNegation()
    {
        const string source = """
            class Example
            {
                void Run(bool disabled)
                {
                    while (TryNext())
                    {
                        {|#0:if|} (!disabled)
                        {
                            Execute();
                        }
                    }
                }

                bool TryNext() => false;
                void Execute() { }
            }
            """;
        const string fixedSource = """
            class Example
            {
                void Run(bool disabled)
                {
                    while (TryNext())
                    {
                        if (disabled)
                            continue;
                        Execute();
                    }
                }

                bool TryNext() => false;
                void Execute() { }
            }
            """;

        var expected = VerifyCS.Diagnostic(PreferLoopContinueAnalyzer.DiagnosticId).WithLocation(0);
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task PreservesCommentsAroundMovedStatements()
    {
        const string source = """
            class Example
            {
                void Run(bool enabled)
                {
                    for (;;)
                    {
                        // Guarded work follows.
                        {|#0:if|} (enabled)
                        {
                            // Keep this comment.
                            Execute(); // Keep trailing comment.
                            // Keep the closing comment.
                        } // Keep the if comment.
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
                    for (;;)
                    {
                        // Guarded work follows.
                        if (!enabled)
                            continue;
                        // Keep this comment.
                        Execute(); // Keep trailing comment.
                                   // Keep the closing comment.
                                   // Keep the if comment.
                    }
                }

                void Execute() { }
            }
            """;

        var expected = VerifyCS.Diagnostic(PreferLoopContinueAnalyzer.DiagnosticId).WithLocation(0);
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task DoesNotReportNonFinalIf()
    {
        const string source = """
            class Example
            {
                void Run(bool enabled)
                {
                    for (;;)
                    {
                        if (enabled)
                        {
                            Execute();
                        }

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
    public Task DoesNotReportIfOutsideLoop()
    {
        const string source = """
            class Example
            {
                void Run(bool enabled)
                {
                    if (enabled)
                    {
                        Execute();
                    }
                }

                void Execute() { }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task DoesNotReportBlockContainingDirectives()
    {
        const string source = """
            class Example
            {
                void Run(bool enabled)
                {
                    do
                    {
                        if (enabled)
                        {
            #if DEBUG
                            Execute();
            #endif
                        }
                    }
                    while (enabled);
                }

                void Execute() { }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task DoesNotReportWhenEarlierNestedDeclarationWouldCollide()
    {
        const string source = """
            class Example
            {
                void Run(bool enabled)
                {
                    for (;;)
                    {
                        {
                            var value = 1;
                            _ = value;
                        }

                        if (enabled)
                        {
                            var value = 2;
                            _ = value;
                        }
                    }
                }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }


    [Fact]
    public Task DoesNotReportWhenMovedDesignationWouldCollide()
    {
        const string source = """
            class Example
            {
                void Run(bool enabled, object item)
                {
                    while (enabled)
                    {
                        {
                            var value = 1;
                            _ = value;
                        }

                        if (enabled)
                        {
                            if (item is int value)
                            {
                                _ = value;
                            }
                        }
                    }
                }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task DoesNotReportWhenEarlierLocalFunctionWouldCollide()
    {
        const string source = """
            class Example
            {
                void Run(bool enabled)
                {
                    do
                    {
                        {
                            void Work() { }
                            Work();
                        }

                        if (enabled)
                        {
                            void Work() { }
                            Work();
                        }
                    }
                    while (enabled);
                }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task PreservesCommentsInHeaderAndBraceGaps()
    {
        const string source = """
            class Example
            {
                void Run(bool enabled)
                {
                    for (;;)
                    {
                        {|#0:if|} /* before condition */ (enabled) // before brace
                        { // after brace
                            Execute();
                        }
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
                    for (;;)
                    {
                        if /* before condition */ (!enabled) // before brace
                            continue;
                        // after brace
                        Execute();
                    }
                }

                void Execute() { }
            }
            """;

        var expected = VerifyCS.Diagnostic(PreferLoopContinueAnalyzer.DiagnosticId).WithLocation(0);
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task FixAllConvertsEveryEligibleLoop()
    {
        const string source = """
            using System.Collections.Generic;

            class Example
            {
                void Run(IEnumerable<int> values)
                {
                    foreach (var value in values)
                    {
                        {|#0:if|} (value == 0)
                        {
                            Execute(value);
                        }
                    }

                    for (var index = 0; index < 10; index++)
                    {
                        {|#1:if|} (index != 5)
                        {
                            Finish(index);
                        }
                    }
                }

                void Execute(int value) { }
                void Finish(int value) { }
            }
            """;
        const string fixedSource = """
            using System.Collections.Generic;

            class Example
            {
                void Run(IEnumerable<int> values)
                {
                    foreach (var value in values)
                    {
                        if (value != 0)
                            continue;
                        Execute(value);
                    }

                    for (var index = 0; index < 10; index++)
                    {
                        if (index == 5)
                            continue;
                        Finish(index);
                    }
                }

                void Execute(int value) { }
                void Finish(int value) { }
            }
            """;

        var expected = new[]
        {
            VerifyCS.Diagnostic(PreferLoopContinueAnalyzer.DiagnosticId).WithLocation(0),
            VerifyCS.Diagnostic(PreferLoopContinueAnalyzer.DiagnosticId).WithLocation(1),
        };

        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource, fixedSource);
    }
    [Fact]
    public Task PreservesFloatingPointNaNSemantics()
    {
        const string source = """
            class Example
            {
                void Run(double[] values)
                {
                    foreach (var value in values)
                    {
                        {|#0:if|} (value > 0)
                        {
                            Execute(value);
                        }
                    }
                }

                void Execute(double value) { }
            }
            """;
        const string fixedSource = """
            class Example
            {
                void Run(double[] values)
                {
                    foreach (var value in values)
                    {
                        if (!(value > 0))
                            continue;
                        Execute(value);
                    }
                }

                void Execute(double value) { }
            }
            """;

        var expected = VerifyCS.Diagnostic(PreferLoopContinueAnalyzer.DiagnosticId).WithLocation(0);
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task DoesNotReportCustomConditionWithTrueAndFalseOperatorsButNoLogicalNot()
    {
        const string source = """
            readonly struct Truthy
            {
                public static bool operator true(Truthy value) => true;
                public static bool operator false(Truthy value) => false;
            }

            class Example
            {
                void Run(Truthy condition)
                {
                    for (;;)
                    {
                        if (condition)
                        {
                            Execute();
                        }
                    }
                }

                void Execute() { }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task PreservesOverloadedEqualitySemantics()
    {
        const string source = """
            readonly struct Value
            {
                public static bool operator ==(Value left, Value right) => true;
                public static bool operator !=(Value left, Value right) => false;
                public override bool Equals(object? obj) => obj is Value;
                public override int GetHashCode() => 0;
            }

            class Example
            {
                void Run(Value left, Value right)
                {
                    while (true)
                    {
                        {|#0:if|} (left == right)
                        {
                            Execute();
                        }
                    }
                }

                void Execute() { }
            }
            """;
        const string fixedSource = """
            readonly struct Value
            {
                public static bool operator ==(Value left, Value right) => true;
                public static bool operator !=(Value left, Value right) => false;
                public override bool Equals(object? obj) => obj is Value;
                public override int GetHashCode() => 0;
            }

            class Example
            {
                void Run(Value left, Value right)
                {
                    while (true)
                    {
                        if (!(left == right))
                            continue;
                        Execute();
                    }
                }

                void Execute() { }
            }
            """;

        var expected = VerifyCS.Diagnostic(PreferLoopContinueAnalyzer.DiagnosticId).WithLocation(0);
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task PreservesOverloadedLogicalNotSemantics()
    {
        const string source = """
            readonly struct Value
            {
                public static bool operator !(Value value) => true;
            }

            class Example
            {
                void Run(Value value)
                {
                    while (true)
                    {
                        {|#0:if|} (!value)
                        {
                            Execute();
                        }
                    }
                }

                void Execute() { }
            }
            """;
        const string fixedSource = """
            readonly struct Value
            {
                public static bool operator !(Value value) => true;
            }

            class Example
            {
                void Run(Value value)
                {
                    while (true)
                    {
                        if (!(!value))
                            continue;
                        Execute();
                    }
                }

                void Execute() { }
            }
            """;

        var expected = VerifyCS.Diagnostic(PreferLoopContinueAnalyzer.DiagnosticId).WithLocation(0);
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task PreservesLogicalNotOperatorComment()
    {
        const string source = """
            class Example
            {
                void Run(bool enabled)
                {
                    while (true)
                    {
                        {|#0:if|} (! /* keep */ enabled)
                        {
                            Execute();
                        }
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
                    while (true)
                    {
                        if (!(! /* keep */ enabled))
                            continue;
                        Execute();
                    }
                }

                void Execute() { }
            }
            """;

        var expected = VerifyCS.Diagnostic(PreferLoopContinueAnalyzer.DiagnosticId).WithLocation(0);
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task DoesNotReportWhenMovedLabelCollidesWithSiblingLabel()
    {
        const string source = """
            class Example
            {
                void Run(bool enabled)
                {
                    while (enabled)
                    {
                        void Earlier()
                        {
                            Same: Execute();
                        }

                        if (enabled)
                        {
                            Same: Execute();
                        }
                    }
                }

                void Execute() { }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }
    [Fact]
    public Task ConvertsDeconstructionForeachWhenMovedLocalDoesNotCollide()
    {
        const string source = """
            class Example
            {
                void Run((int Value, bool Enabled)[] items)
                {
                    foreach (var (value, enabled) in items)
                    {
                        {|#0:if|} (enabled)
                        {
                            int doubled = value * 2;
                            Use(doubled);
                        }
                    }
                }

                void Use(int value) { }
            }
            """;
        const string fixedSource = """
            class Example
            {
                void Run((int Value, bool Enabled)[] items)
                {
                    foreach (var (value, enabled) in items)
                    {
                        if (!enabled)
                            continue;
                        int doubled = value * 2;
                        Use(doubled);
                    }
                }

                void Use(int value) { }
            }
            """;

        var expected = VerifyCS.Diagnostic(PreferLoopContinueAnalyzer.DiagnosticId)
            .WithLocation(0)
            .WithMessage("Invert this condition and continue early");
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task DoesNotReportWhenMovedLocalCollidesWithEarlierForeachOrCatch()
    {
        const string source = """
            class Example
            {
                void Foreach(bool enabled, int[] items)
                {
                    while (enabled)
                    {
                        {
                            foreach (var value in items)
                            {
                                Use(value);
                            }
                        }

                        if (enabled)
                        {
                            int value = 0;
                            Use(value);
                        }
                    }
                }

                void Catch(bool enabled)
                {
                    while (enabled)
                    {
                        try
                        {
                            Execute();
                        }
                        catch (System.Exception error)
                        {
                            Use(error);
                        }

                        if (enabled)
                        {
                            System.Exception error = new();
                            Use(error);
                        }
                    }
                }

                void Execute() { }
                void Use(object value) { }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }


}
