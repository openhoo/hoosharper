using HooSharper.CodeFixes;
using VerifyCS = HooSharper.Analyzers.Tests.AnalyzerVerifier<
    HooSharper.Analyzers.RemoveRedundantNullConditionalGuardAnalyzer,
    HooSharper.CodeFixes.RemoveRedundantNullConditionalGuardCodeFixProvider>;

namespace HooSharper.Analyzers.Tests;

public sealed class RemoveRedundantNullConditionalGuardAnalyzerTests
{
    [Fact]
    public Task RemovesIsNotNullGuardAroundInvocation()
    {
        const string source = """
            class Service
            {
                public void Run() { }
            }

            class Example
            {
                void Invoke(Service? service)
                {
                    if (service {|#0:is|} not null)
                    {
                        service?.Run();
                    }
                }
            }
            """;
        const string fixedSource = """
            class Service
            {
                public void Run() { }
            }

            class Example
            {
                void Invoke(Service? service)
                {
                    service?.Run();
                }
            }
            """;

        var expected = VerifyCS.Diagnostic(RemoveRedundantNullConditionalGuardAnalyzer.DiagnosticId)
            .WithLocation(0)
            .WithMessage("Remove the redundant null-conditional guard");
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task RemovesBuiltInInequalityGuardAroundLocalDelegateInvocation()
    {
        const string source = """
            using System;

            class Example
            {
                void Raise(Action? changed)
                {
                    if (changed {|#0:!=|} null)
                    {
                        changed?.Invoke();
                    }
                }
            }
            """;
        const string fixedSource = """
            using System;

            class Example
            {
                void Raise(Action? changed)
                {
                    changed?.Invoke();
                }
            }
            """;

        var expected = VerifyCS.Diagnostic(RemoveRedundantNullConditionalGuardAnalyzer.DiagnosticId).WithLocation(0);
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task RemovesGuardAroundParenthesizedLocalReceiver()
    {
        const string source = """
            class Service
            {
                public void Run() { }
            }

            class Example
            {
                void Invoke(Service? input)
                {
                    var service = input;
                    if (((service)) {|#0:is|} not null)
                    {
                        ((service))?.Run();
                    }
                }
            }
            """;
        const string fixedSource = """
            class Service
            {
                public void Run() { }
            }

            class Example
            {
                void Invoke(Service? input)
                {
                    var service = input;
                    ((service))?.Run();
                }
            }
            """;

        var expected = VerifyCS.Diagnostic(RemoveRedundantNullConditionalGuardAnalyzer.DiagnosticId).WithLocation(0);
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task RemovesGuardAroundReadonlyFieldChain()
    {
        const string source = """
            class Service
            {
                public void Run() { }
            }

            class Holder
            {
                public readonly Service? Service;
            }

            class Example
            {
                private readonly Holder holder = new();

                void Invoke()
                {
                    if (holder.Service {|#0:is|} not null)
                    {
                        holder.Service?.Run();
                    }
                }
            }
            """;
        const string fixedSource = """
            class Service
            {
                public void Run() { }
            }

            class Holder
            {
                public readonly Service? Service;
            }

            class Example
            {
                private readonly Holder holder = new();

                void Invoke()
                {
                    holder.Service?.Run();
                }
            }
            """;

        var expected = VerifyCS.Diagnostic(RemoveRedundantNullConditionalGuardAnalyzer.DiagnosticId).WithLocation(0);
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task PreservesComments()
    {
        const string source = """
            class Service
            {
                public void Run() { }
            }

            class Example
            {
                void Invoke(Service? service)
                {
                    // before
                    if (service {|#0:is|} not null)
                    {
                        // invocation
                        service?.Run();
                    }
                }
            }
            """;
        const string fixedSource = """
            class Service
            {
                public void Run() { }
            }

            class Example
            {
                void Invoke(Service? service)
                {
                    // before
                    // invocation
                    service?.Run();
                }
            }
            """;

        var expected = VerifyCS.Diagnostic(RemoveRedundantNullConditionalGuardAnalyzer.DiagnosticId).WithLocation(0);
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task PreservesTrailingCommentAfterClosingBrace()
    {
        const string source = """
            class Service
            {
                public void Run() { }
            }

            class Example
            {
                void Invoke(Service? service)
                {
                    if (service {|#0:is|} not null)
                    {
                        service?.Run();
                    } // keep
                }
            }
            """;
        const string fixedSource = """
            class Service
            {
                public void Run() { }
            }

            class Example
            {
                void Invoke(Service? service)
                {
                    service?.Run(); // keep
                }
            }
            """;

        var expected = VerifyCS.Diagnostic(RemoveRedundantNullConditionalGuardAnalyzer.DiagnosticId).WithLocation(0);
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task PreservesTrailingBlockCommentAfterClosingBrace()
    {
        const string source = """
            class Service
            {
                public void Run() { }
            }

            class Example
            {
                void Invoke(Service? service)
                {
                    if (service {|#0:is|} not null)
                    {
                        service?.Run();
                    } /* keep */
                }
            }
            """;
        const string fixedSource = """
            class Service
            {
                public void Run() { }
            }

            class Example
            {
                void Invoke(Service? service)
                {
                    service?.Run(); /* keep */
                }
            }
            """;

        var expected = VerifyCS.Diagnostic(RemoveRedundantNullConditionalGuardAnalyzer.DiagnosticId).WithLocation(0);
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task FixAllRemovesEveryRedundantGuard()
    {
        const string source = """
            class Service
            {
                public void Run() { }
            }

            class Example
            {
                void Invoke(Service? first, Service? second)
                {
                    if (first {|#0:is|} not null)
                    {
                        first?.Run();
                    }

                    if (null {|#1:!=|} second)
                    {
                        second?.Run();
                    }
                }
            }
            """;
        const string fixedSource = """
            class Service
            {
                public void Run() { }
            }

            class Example
            {
                void Invoke(Service? first, Service? second)
                {
                    first?.Run();

                    second?.Run();
                }
            }
            """;

        var expected = new[]
        {
            VerifyCS.Diagnostic(RemoveRedundantNullConditionalGuardAnalyzer.DiagnosticId).WithLocation(0),
            VerifyCS.Diagnostic(RemoveRedundantNullConditionalGuardAnalyzer.DiagnosticId).WithLocation(1),
        };
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource, fixedSource);
    }

    [Fact]
    public Task IgnoresMismatchedReceiver()
    {
        const string source = """
            class Service
            {
                public void Run() { }
            }

            class Example
            {
                void Invoke(Service? first, Service? second)
                {
                    if (first is not null)
                    {
                        second?.Run();
                    }
                }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task IgnoresOverloadedEquality()
    {
        const string source = """
            class Service
            {
                public void Run() { }
                public static bool operator ==(Service? left, Service? right) => true;
                public static bool operator !=(Service? left, Service? right) => false;
                public override bool Equals(object? obj) => false;
                public override int GetHashCode() => 0;
            }

            class Example
            {
                void Invoke(Service? service)
                {
                    if (service != null)
                    {
                        service?.Run();
                    }
                }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task IgnoresUnstableReceiver()
    {
        const string source = """
            class Service
            {
                public void Run() { }
            }

            class Example
            {
                Service? GetService() => null;

                void Invoke()
                {
                    if (GetService() is not null)
                    {
                        GetService()?.Run();
                    }
                }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task IgnoresPropertiesEvenWhenTheyMatchSyntactically()
    {
        const string source = """
            class Service
            {
                public void Run() { }
            }

            class Example
            {
                Service? Service => null;

                void Invoke()
                {
                    if (Service is not null)
                    {
                        Service?.Run();
                    }
                }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task IgnoresMutableFieldsAndEvents()
    {
        const string source = """
            using System;

            class Service
            {
                public void Run() { }
            }

            class Example
            {
                private Service? service;
                private volatile Service? volatileService;
                private event Action? Changed;

                void Invoke()
                {
                    if (service is not null)
                    {
                        service?.Run();
                    }

                    if (volatileService is not null)
                    {
                        volatileService?.Run();
                    }

                    if (Changed is not null)
                    {
                        Changed?.Invoke();
                    }
                }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task IgnoresElseMultipleStatementsAndDirectives()
    {
        const string source = """
            class Service
            {
                public void Run() { }
            }

            class Example
            {
                void Invoke(Service? service)
                {
                    if (service is not null)
                    {
                        service?.Run();
                    }
                    else
                    {
                        System.Console.WriteLine("missing");
                    }

                    if (service is not null)
                    {
                        service?.Run();
                        service?.Run();
                    }

                    if (service is not null)
                    {
            #if DEBUG
                        service?.Run();
            #endif
                    }
                }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task IgnoresOrdinaryMemberAccess()
    {
        const string source = """
            class Service
            {
                public void Run() { }
            }

            class Example
            {
                void Invoke(Service? service)
                {
                    if (service is not null)
                    {
                        service.Run();
                    }
                }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }
}
