using HooSharper.CodeFixes;
using VerifyCS = HooSharper.Analyzers.Tests.AnalyzerVerifier<
    HooSharper.Analyzers.OmitBracesForSingleLineIfAnalyzer,
    HooSharper.CodeFixes.OmitBracesForSingleLineIfCodeFixProvider>;

namespace HooSharper.Analyzers.Tests;

public sealed class OmitBracesForSingleLineIfAnalyzerTests
{
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
}
