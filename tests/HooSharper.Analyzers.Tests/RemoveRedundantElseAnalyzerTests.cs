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
using VerifyCS = HooSharper.Analyzers.Tests.AnalyzerVerifier<
    HooSharper.Analyzers.RemoveRedundantElseAnalyzer,
    HooSharper.CodeFixes.RemoveRedundantElseCodeFixProvider>;

namespace HooSharper.Analyzers.Tests;

public sealed class RemoveRedundantElseAnalyzerTests
{
    [Fact]
    public Task RemovesElseAfterReturn()
    {
        const string source = """
            class Example
            {
                int GetValue(bool enabled)
                {
                    if (enabled)
                    {
                        return 1;
                    }
                    {|#0:else|}
                    {
                        return 0;
                    }
                }
            }
            """;
        const string fixedSource = """
            class Example
            {
                int GetValue(bool enabled)
                {
                    if (enabled)
                    {
                        return 1;
                    }
                    return 0;
                }
            }
            """;

        var expected = VerifyCS.Diagnostic(RemoveRedundantElseAnalyzer.DiagnosticId)
            .WithLocation(0)
            .WithMessage("Remove this redundant else");

        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task RemovesElseAfterThrowStatement()
    {
        const string source = """
            using System;

            class Example
            {
                void Run(bool invalid)
                {
                    if (invalid)
                        throw new InvalidOperationException();
                    {|#0:else|}
                        Execute();
                }

                void Execute() { }
            }
            """;
        const string fixedSource = """
            using System;

            class Example
            {
                void Run(bool invalid)
                {
                    if (invalid)
                        throw new InvalidOperationException();
                    Execute();
                }

                void Execute() { }
            }
            """;

        var expected = VerifyCS.Diagnostic(RemoveRedundantElseAnalyzer.DiagnosticId).WithLocation(0);
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task RemovesElseAfterContinueAndBreak()
    {
        const string source = """
            class Example
            {
                void Run(bool skip, bool stop)
                {
                    while (true)
                    {
                        if (skip)
                            continue;
                        {|#0:else|}
                            Execute();

                        if (stop)
                            break;
                        {|#1:else|}
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
                void Run(bool skip, bool stop)
                {
                    while (true)
                    {
                        if (skip)
                            continue;
                        Execute();

                        if (stop)
                            break;
                        Finish();
                    }
                }

                void Execute() { }
                void Finish() { }
            }
            """;

        var expected = new[]
        {
            VerifyCS.Diagnostic(RemoveRedundantElseAnalyzer.DiagnosticId).WithLocation(0),
            VerifyCS.Diagnostic(RemoveRedundantElseAnalyzer.DiagnosticId).WithLocation(1),
        };

        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource, fixedSource);
    }


    [Fact]
    public Task PreservesCommentsAroundElseBody()
    {
        const string source = """
            class Example
            {
                void Run(bool done)
                {
                    if (done)
                    {
                        Finish();
                        return;
                    }
                    // explain fallback
                    {|#0:else|} // keep with moved body
                    {
                        // prepare fallback
                        Execute(); // execute fallback
                    } // end fallback
                }

                void Finish() { }
                void Execute() { }
            }
            """;
        const string fixedSource = """
            class Example
            {
                void Run(bool done)
                {
                    if (done)
                    {
                        Finish();
                        return;
                    }
                    // explain fallback
                    // keep with moved body
                    // prepare fallback
                    Execute(); // execute fallback
                               // end fallback
                }

                void Finish() { }
                void Execute() { }
            }
            """;

        var expected = VerifyCS.Diagnostic(RemoveRedundantElseAnalyzer.DiagnosticId).WithLocation(0);
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task PreservesUsingDeclarationDisposalScope()
    {
        const string source = """
            using System;

            class Example
            {
                void Run(bool done)
                {
                    if (done)
                    {
                        return;
                    }
                    {|#0:else|}
                    {
                        // dispose before Finish
                        using var resource = new Resource();
                        Execute(resource);
                    }

                    Finish();
                }

                void Execute(Resource resource) { }
                void Finish() { }

                sealed class Resource : IDisposable
                {
                    public void Dispose() { }
                }
            }
            """;
        const string fixedSource = """
            using System;

            class Example
            {
                void Run(bool done)
                {
                    if (done)
                    {
                        return;
                    }
                    {
                        // dispose before Finish
                        using var resource = new Resource();
                        Execute(resource);
                    }

                    Finish();
                }

                void Execute(Resource resource) { }
                void Finish() { }

                sealed class Resource : IDisposable
                {
                    public void Dispose() { }
                }
            }
            """;

        var expected = VerifyCS.Diagnostic(RemoveRedundantElseAnalyzer.DiagnosticId).WithLocation(0);
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task PreservesScopeForBranchLocalNameCollision()
    {
        const string source = """
            class Example
            {
                int GetValue(bool done)
                {
                    if (done)
                    {
                        var value = 1;
                        return value;
                    }
                    {|#0:else|}
                    {
                        // separate declaration space
                        var value = 0;
                        return value;
                    }
                }
            }
            """;
        const string fixedSource = """
            class Example
            {
                int GetValue(bool done)
                {
                    if (done)
                    {
                        var value = 1;
                        return value;
                    }
                    {
                        // separate declaration space
                        var value = 0;
                        return value;
                    }
                }
            }
            """;

        var expected = VerifyCS.Diagnostic(RemoveRedundantElseAnalyzer.DiagnosticId).WithLocation(0);
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task PreservesElseLabelScopeAgainstSiblingLabel()
    {
        const string source = """
            class Example
            {
                void Run(bool enabled)
                {
                    {
                    again:
                        Execute();
                    }
                    if (enabled)
                    {
                        return;
                    }
                    {|#0:else|}
                    {
                    again:
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
                    {
                    again:
                        Execute();
                    }
                    if (enabled)
                    {
                        return;
                    }
                    {
                    again:
                        Execute();
                    }
                }

                void Execute() { }
            }
            """;
        return VerifyCS.VerifyCodeFixAsync(source,
            VerifyCS.Diagnostic(RemoveRedundantElseAnalyzer.DiagnosticId).WithLocation(0), fixedSource);
    }

    [Fact]
    public Task DoesNotReportNonterminatingBranch()
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
                    else
                    {
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
    public Task DoesNotReportElseIf()
    {
        const string source = """
            class Example
            {
                int GetValue(bool first, bool second)
                {
                    if (first)
                        return 1;
                    else if (second)
                        return 2;
                    else
                        return 0;
                }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task FixAllRemovesEveryRedundantElse()
    {
        const string source = """
            using System;

            class Example
            {
                int First(bool enabled)
                {
                    if (enabled)
                        return 1;
                    {|#0:else|}
                        return 0;
                }

                void Second(bool invalid)
                {
                    if (invalid)
                    {
                        throw new InvalidOperationException();
                    }
                    {|#1:else|}
                    {
                        Execute();
                    }
                }

                void Execute() { }
            }
            """;
        const string fixedSource = """
            using System;

            class Example
            {
                int First(bool enabled)
                {
                    if (enabled)
                        return 1;
                    return 0;
                }

                void Second(bool invalid)
                {
                    if (invalid)
                    {
                        throw new InvalidOperationException();
                    }
                    Execute();
                }

                void Execute() { }
            }
            """;

        var expected = new[]
        {
            VerifyCS.Diagnostic(RemoveRedundantElseAnalyzer.DiagnosticId).WithLocation(0),
            VerifyCS.Diagnostic(RemoveRedundantElseAnalyzer.DiagnosticId).WithLocation(1),
        };

        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource, fixedSource);
    }

    [Fact]
    public Task DoesNotReportDirectiveOnlyElse()
    {
        const string source = """
            class Example
            {
                void Run(bool enabled)
                {
                    if (enabled)
                        return;
                    else
                    {
            #if DEBUG
            #endif
                    }
                }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }
    [Fact]
    public Task DoesNotReportDirectOrEmptyNonterminatingBranch()
    {
        const string source = """
            class Example
            {
                void Direct(bool enabled)
                {
                    if (enabled)
                        Execute();
                    else
                        Finish();
                }

                void Empty(bool enabled)
                {
                    if (enabled)
                    {
                    }
                    else
                    {
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
    public Task RemovesElseWhenReachableStatementTerminatesBeforeUnreachableTail()
    {
        const string source = """
            class Example
            {
                void Run(bool enabled)
                {
                    if (enabled)
                    {
                        return;
                        Execute();
                    }
                    {|#0:else|}
                    {
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
                    {
                        return;
                        Execute();
                    }
                    Finish();
                }

                void Execute() { }
                void Finish() { }
            }
            """;

        var expected = VerifyCS.Diagnostic(RemoveRedundantElseAnalyzer.DiagnosticId)
            .WithLocation(0)
            .WithMessage("Remove this redundant else");
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task PreservesScopeForDesignationInElseBody()
    {
        const string source = """
            class Example
            {
                void Run(bool enabled, object item)
                {
                    if (enabled)
                    {
                        return;
                    }
                    {|#0:else|}
                    {
                        if (item is int value)
                        {
                            Use(value);
                        }
                    }
                }

                void Use(int value) { }
            }
            """;
        const string fixedSource = """
            class Example
            {
                void Run(bool enabled, object item)
                {
                    if (enabled)
                    {
                        return;
                    }
                    {
                        if (item is int value)
                        {
                            Use(value);
                        }
                    }
                }

                void Use(int value) { }
            }
            """;

        var expected = VerifyCS.Diagnostic(RemoveRedundantElseAnalyzer.DiagnosticId)
            .WithLocation(0)
            .WithMessage("Remove this redundant else");
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }


    [Fact]
    public Task PreservesCommentsInEmptyElseBlock()
    {
        const string source = """
            class Example
            {
                void Run(bool done)
                {
                    if (done)
                        return;
                    {|#0:else|}
                    {
                        // intentionally empty fallback
                    }
                }
            }
            """;
        const string fixedSource = """
            class Example
            {
                void Run(bool done)
                {
                    if (done)
                        return;
                    {
                        // intentionally empty fallback
                    }
                }
            }
            """;
        return VerifyCS.VerifyCodeFixAsync(source,
            VerifyCS.Diagnostic(RemoveRedundantElseAnalyzer.DiagnosticId).WithLocation(0), fixedSource);
    }


    [Fact]
    public Task DoesNotRemoveElseAcrossConditionalTermination()
    {
        const string source = """
            class Example
            {
                void Run(bool enabled)
                {
                    if (enabled)
                    {
            #if FALLTHROUGH
                        Execute();
            #else
                        return;
            #endif
                    }
                    else
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
    public Task FixAllRemovesElseAfterNestedTerminatingIfArms()
    {
        const string source = """
            class Example
            {
                int GetValue(bool enabled, bool inner)
                {
                    if (enabled)
                    {
                        if (inner)
                        {
                            return 1;
                        }
                        {|#0:else|}
                        {
                            return 2;
                        }
                    }
                    {|#1:else|}
                    {
                        return 3;
                    }
                }
            }
            """;
        const string fixedSource = """
            class Example
            {
                int GetValue(bool enabled, bool inner)
                {
                    if (enabled)
                    {
                        if (inner)
                        {
                            return 1;
                        }
                        return 2;
                    }
                    return 3;
                }
            }
            """;
        var expected = new[]
        {
            VerifyCS.Diagnostic(RemoveRedundantElseAnalyzer.DiagnosticId).WithLocation(0),
            VerifyCS.Diagnostic(RemoveRedundantElseAnalyzer.DiagnosticId).WithLocation(1),
        };
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource, fixedSource);
    }

    [Fact]
    public Task RemovesElseAfterTerminatingTryCatchFinally()
    {
        const string source = """
            using System;

            class Example
            {
                void Run(bool enabled)
                {
                    if (enabled)
                    {
                        try
                        {
                            Execute();
                            return;
                        }
                        catch (Exception)
                        {
                            throw;
                        }
                        finally
                        {
                            Cleanup();
                        }
                    }
                    {|#0:else|}
                    {
                        Execute();
                    }
                }

                void Execute() { }
                void Cleanup() { }
            }
            """;
        const string fixedSource = """
            using System;

            class Example
            {
                void Run(bool enabled)
                {
                    if (enabled)
                    {
                        try
                        {
                            Execute();
                            return;
                        }
                        catch (Exception)
                        {
                            throw;
                        }
                        finally
                        {
                            Cleanup();
                        }
                    }
                    Execute();
                }

                void Execute() { }
                void Cleanup() { }
            }
            """;
        return VerifyCS.VerifyCodeFixAsync(source,
            VerifyCS.Diagnostic(RemoveRedundantElseAnalyzer.DiagnosticId).WithLocation(0), fixedSource);
    }

    [Theory]
    [InlineData("try { return; } catch (System.Exception) { throw; }")]
    [InlineData("try { return; } finally { Execute(); }")]
    [InlineData("try { Execute(); } finally { throw new System.Exception(); }")]
    [InlineData("switch (inner) { case true: return; default: throw new System.Exception(); }")]
    public Task ReportsCompoundBranchWithUnreachableEndpoint(string statement)
    {
        var source = "class Example { void Run(bool enabled, bool inner) { if (enabled) { " + statement +
            " } {|#0:else|} { Execute(); } } void Execute() { } }";
        return VerifyCS.VerifyAnalyzerAsync(source,
            VerifyCS.Diagnostic(RemoveRedundantElseAnalyzer.DiagnosticId).WithLocation(0));
    }

    [Fact]
    public Task StillReportsDirectTerminalBranchWhenConditionIsConstantFalse()
    {
        const string source = """
            class Example
            {
                void Run()
                {
                    if (false)
                    {
                        return;
                    }
                    {|#0:else|}
                    {
                        Execute();
                    }
                }

                void Execute() { }
            }
            """;
        return VerifyCS.VerifyAnalyzerAsync(source,
            VerifyCS.Diagnostic(RemoveRedundantElseAnalyzer.DiagnosticId).WithLocation(0));
    }

    [Theory]
    [InlineData("if (inner) return;")]
    [InlineData("try { if (inner) return; } catch (System.Exception) { throw; }")]
    [InlineData("try { return; } catch (System.Exception) { Execute(); }")]
    [InlineData("try { Execute(); } finally { Execute(); }")]
    [InlineData("switch (inner) { case true: return; default: break; }")]
    public Task DoesNotReportCompoundBranchWithReachableEndpoint(string statement)
    {
        var source = "class Example { void Run(bool enabled, bool inner) { if (enabled) { " + statement +
            " } else { Execute(); } } void Execute() { } }";
        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Theory]
    [InlineData("if () return; else throw new System.Exception();")]
    [InlineData("try { return; } catch (System.Exception) { throw new ; }")]
    public async Task MalformedCompoundBranchDoesNotReportOrThrow(string statement)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var source = "class Example { void Run(bool enabled) { if (enabled) { " + statement +
            " } else { Execute(); } } void Execute() { } }";
        var tree = CSharpSyntaxTree.ParseText(source, cancellationToken: cancellationToken);
        var compilation = CSharpCompilation.Create("MalformedElse", [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.Contains(compilation.GetDiagnostics(cancellationToken), diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Error);
        var diagnostics = await compilation.WithAnalyzers(
                ImmutableArray.Create<DiagnosticAnalyzer>(new RemoveRedundantElseAnalyzer()))
            .GetAnalyzerDiagnosticsAsync(cancellationToken);
        Assert.Empty(diagnostics);
    }

    [Theory]
    [InlineData("if (inner) return;")]
    [InlineData("try { return; } catch (System.Exception) { Execute(); }")]
    [InlineData("try { Execute(); } finally { Execute(); }")]
    [InlineData("if () return; else throw new System.Exception();")]
    [InlineData("#if SOMETIMES\nreturn;\n#endif\n")]
    public async Task StaleDiagnosticDoesNotOfferUnsafeElseRemoval(string statement)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("StaleElseDiagnostic", LanguageNames.CSharp)
            .WithParseOptions(new CSharpParseOptions(LanguageVersion.Latest))
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddMetadataReference(MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        var source = "class Example { void Run(bool enabled, bool inner) { if (enabled) {\n" + statement +
            "\n} else { Execute(); } } void Execute() { } }";
        var document = project.AddDocument("Test.cs", SourceText.From(source));
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var ifStatement = root!.DescendantNodes().OfType<IfStatementSyntax>().First();
        var descriptor = new RemoveRedundantElseAnalyzer().SupportedDiagnostics.Single();
        var diagnostic = Diagnostic.Create(descriptor, ifStatement.Else!.ElseKeyword.GetLocation());
        var actions = new List<CodeAction>();
        var context = new CodeFixContext(document, diagnostic,
            (action, _) => actions.Add(action), cancellationToken);

        await new RemoveRedundantElseCodeFixProvider().RegisterCodeFixesAsync(context);

        Assert.Empty(actions);
    }

}
