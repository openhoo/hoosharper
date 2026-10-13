using HooSharper.CodeFixes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using Microsoft.CodeAnalysis.Text;
using VerifyCS = HooSharper.Analyzers.Tests.AnalyzerVerifier<
    HooSharper.Analyzers.UseNullCoalescingAssignmentAnalyzer,
    HooSharper.CodeFixes.UseNullCoalescingAssignmentCodeFixProvider>;

namespace HooSharper.Analyzers.Tests;

public sealed class UseNullCoalescingAssignmentAnalyzerTests
{
    [Fact]
    public Task ReplacesIdentifierIsNullCheck()
    {
        const string source = """
            class Example
            {
                string Get(string? value, string fallback)
                {
                    if (value {|#0:is|} null)
                    {
                        value = fallback;
                    }

                    return value;
                }
            }
            """;
        const string fixedSource = """
            class Example
            {
                string Get(string? value, string fallback)
                {
                    value ??= fallback;

                    return value;
                }
            }
            """;

        var expected = VerifyCS.Diagnostic(UseNullCoalescingAssignmentAnalyzer.DiagnosticId)
            .WithLocation(0)
            .WithMessage("Use a null-coalescing assignment");

        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task IgnoresPropertyTargets()
    {
        const string source = """
            class Holder
            {
                public string? Value { get; set; }
            }

            class Example
            {
                private readonly Holder holder = new();

                void Set(string fallback)
                {
                    if (holder.Value == null)
                    {
                        holder.Value = fallback;
                    }
                }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task PreservesComments()
    {
        const string source = """
            class Example
            {
                void Set(string? value, string fallback)
                {
                    // before
                    if (value {|#0:is|} null) // condition
                    {
                        // assignment
                        value = fallback; // value
                    } // after
                }
            }
            """;
        const string fixedSource = """
            class Example
            {
                void Set(string? value, string fallback)
                {
                    // before
                    // condition
                    // assignment
                    // after
                    value ??= fallback; // value
                }
            }
            """;

        var expected = VerifyCS.Diagnostic(UseNullCoalescingAssignmentAnalyzer.DiagnosticId).WithLocation(0);
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task FixAllReplacesEverySafeNullCheck()
    {
        const string source = """
            class Example
            {
                private string? field;

                void Set(string? first, string fallback)
                {
                    if (first {|#0:is|} null)
                    {
                        first = fallback;
                    }

                    if (field {|#1:==|} null)
                    {
                        field = fallback;
                    }
                }
            }
            """;
        const string fixedSource = """
            class Example
            {
                private string? field;

                void Set(string? first, string fallback)
                {
                    first ??= fallback;

                    field ??= fallback;
                }
            }
            """;

        var expected = new[]
        {
            VerifyCS.Diagnostic(UseNullCoalescingAssignmentAnalyzer.DiagnosticId).WithLocation(0),
            VerifyCS.Diagnostic(UseNullCoalescingAssignmentAnalyzer.DiagnosticId).WithLocation(1),
        };

        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource, fixedSource);
    }

    [Fact]
    public Task ReplacesLocalAndNullableValueTargets()
    {
        const string source = """
            class Example
            {
                void Set(string? value, int? number, string fallback)
                {
                    string? local = value;
                    if ((local) {|#0:is|} null)
                    {
                        local = fallback;
                    }

                    int? nullable = number;
                    if (nullable {|#1:is|} null)
                    {
                        nullable = 42;
                    }
                }
            }
            """;
        const string fixedSource = """
            class Example
            {
                void Set(string? value, int? number, string fallback)
                {
                    string? local = value;
                    local ??= fallback;

                    int? nullable = number;
                    nullable ??= 42;
                }
            }
            """;

        var expected = new[]
        {
            VerifyCS.Diagnostic(UseNullCoalescingAssignmentAnalyzer.DiagnosticId).WithLocation(0),
            VerifyCS.Diagnostic(UseNullCoalescingAssignmentAnalyzer.DiagnosticId).WithLocation(1),
        };

        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource, fixedSource);
    }

    [Fact]
    public Task ReplacesStaticFieldAndIgnoresPropertyTargets()
    {
        const string source = """
            class Holder
            {
                public string? Value { get; set; }
            }

            class Example
            {
                private static string? shared;

                void Set(Holder parameter, string fallback)
                {
                    if (shared {|#0:is|} null)
                    {
                        shared = fallback;
                    }

                    if (parameter.Value is null)
                    {
                        parameter.Value = fallback;
                    }
                }
            }
            """;
        const string fixedSource = """
            class Holder
            {
                public string? Value { get; set; }
            }

            class Example
            {
                private static string? shared;

                void Set(Holder parameter, string fallback)
                {
                    shared ??= fallback;

                    if (parameter.Value is null)
                    {
                        parameter.Value = fallback;
                    }
                }
            }
            """;

        return VerifyCS.VerifyCodeFixAsync(
            source,
            VerifyCS.Diagnostic(UseNullCoalescingAssignmentAnalyzer.DiagnosticId).WithLocation(0),
            fixedSource);
    }

    [Fact]
    public Task IgnoresDynamicEqualityAndRefReturnTargets()
    {
        const string source = """
            class Holder
            {
                private string? value;
                public ref string? Value => ref value;
            }

            class Example
            {
                void Set(dynamic value, dynamic fallback, Holder holder)
                {
                    if (value == null)
                    {
                        value = fallback;
                    }

                    if (holder.Value is null)
                    {
                        holder.Value = "fallback";
                    }
                }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task AcceptsDefaultLanguageVersion()
    {
        const string source = """
            class Example
            {
                void Set(string? value, string fallback)
                {
                    if (value {|#0:is|} null)
                    {
                        value = fallback;
                    }
                }
            }
            """;

        var test = new CSharpCodeFixTest<
            UseNullCoalescingAssignmentAnalyzer,
            UseNullCoalescingAssignmentCodeFixProvider,
            DefaultVerifier>
        {
            ReferenceAssemblies = ReferenceAssemblies.Net.Net100,
            TestCode = source,
            FixedCode = """
                class Example
                {
                    void Set(string? value, string fallback)
                    {
                        value ??= fallback;
                    }
                }
                """,
        };
        test.ExpectedDiagnostics.Add(
            VerifyCS.Diagnostic(UseNullCoalescingAssignmentAnalyzer.DiagnosticId).WithLocation(0));
        test.SolutionTransforms.Add((solution, projectId) =>
        {
            var project = solution.GetProject(projectId)!;
            return solution.WithProjectParseOptions(
                projectId,
                ((CSharpParseOptions)project.ParseOptions!).WithLanguageVersion(LanguageVersion.Default));
        });

        return test.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public Task IgnoresElementAccessAndNonNullableTargets()
    {
        const string source = """
            class Example
            {
                void Set(string?[] values, int number)
                {
                    if (values[0] is null)
                    {
                        values[0] = "fallback";
                    }

                    if (number == null)
                    {
                        number = 42;
                    }
                }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task DoesNotReportBeforeCSharp8()
    {
        const string source = """
            class Example
            {
                void Set(string value, string fallback)
                {
                    if (value == null)
                    {
                        value = fallback;
                    }
                }
                void SetNullable(int? value)
                {
                    if (!value.HasValue)
                    {
                        value = 1;
                    }
                }
            }
            """;

        var test = new CSharpCodeFixTest<
            UseNullCoalescingAssignmentAnalyzer,
            UseNullCoalescingAssignmentCodeFixProvider,
            DefaultVerifier>
        {
            ReferenceAssemblies = ReferenceAssemblies.Net.Net100,
            TestCode = source,
        };
        test.SolutionTransforms.Add((solution, projectId) =>
        {
            var project = solution.GetProject(projectId)!;
            return solution.WithProjectParseOptions(
                projectId,
                ((CSharpParseOptions)project.ParseOptions!).WithLanguageVersion(LanguageVersion.CSharp7_3));
        });

        return test.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public Task IgnoresMismatchedTarget()
    {
        const string source = """
            class Example
            {
                void Set(string? first, string? second, string fallback)
                {
                    if (first is null)
                    {
                        second = fallback;
                    }
                }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task IgnoresOverloadedEqualityOperator()
    {
        const string source = """
            class Value
            {
                public static bool operator ==(Value? left, Value? right) => true;
                public static bool operator !=(Value? left, Value? right) => false;
                public override bool Equals(object? obj) => false;
                public override int GetHashCode() => 0;
            }

            class Example
            {
                void Set(Value? value, Value fallback)
                {
                    if (value == null)
                    {
                        value = fallback;
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
            class Holder
            {
                public string? Value { get; set; }
            }

            class Example
            {
                Holder GetHolder() => new();

                void Set(string fallback)
                {
                    if (GetHolder().Value is null)
                    {
                        GetHolder().Value = fallback;
                    }
                }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task IgnoresMultipleStatements()
    {
        const string source = """
            class Example
            {
                void Set(string? value, string fallback)
                {
                    if (value is null)
                    {
                        value = fallback;
                        System.Console.WriteLine(value);
                    }
                }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task IgnoresElseClauseAndDirectives()
    {
        const string source = """
            class Example
            {
                void Set(string? value, string fallback)
                {
                    if (value is null)
                    {
                        value = fallback;
                    }
                    else
                    {
                        System.Console.WriteLine(value);
                    }

                    if (value is null)
                    {
            #if DEBUG
                        value = fallback;
            #endif
                    }
                }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }
    [Fact]
    public Task IgnoresCommentInsideNullCondition()
    {
        const string source = """
            class Example
            {
                void Set(string? value, string fallback)
                {
                    if (value /* keep */ is null)
                    {
                        value = fallback;
                    }
                }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task IgnoresBoundaryDocumentationCommentThatFixerCannotPreserve()
    {
        const string source = """
            class Example
            {
                void Set(string? value, string fallback)
                {
                    if (/** keep */ value is null)
                    {
                        value = fallback;
                    }
                }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task PreservesCommentsAtConditionBoundaries()
    {
        const string source = """
            class Example
            {
                void Set(string? value, string fallback)
                {
                    if (/* before */ value {|#0:is|} null /* after */)
                    {
                        value = fallback;
                    }
                }
            }
            """;
        const string fixedSource = """
            class Example
            {
                void Set(string? value, string fallback)
                {
                    /* before */
                    /* after */
                    value ??= fallback;
                }
            }
            """;

        return VerifyCS.VerifyCodeFixAsync(
            source,
            VerifyCS.Diagnostic(UseNullCoalescingAssignmentAnalyzer.DiagnosticId).WithLocation(0),
            fixedSource);
    }

    [Fact]
    public Task PreservesCommentBeforeClosingBrace()
    {
        const string source = """
            class Example
            {
                void Set(string? value, string fallback)
                {
                    if (value {|#0:is|} null)
                    {
                        value = fallback;
                        // created lazily
                    }
                }
            }
            """;
        const string fixedSource = """
            class Example
            {
                void Set(string? value, string fallback)
                {
                    // created lazily
                    value ??= fallback;
                }
            }
            """;

        var expected = VerifyCS.Diagnostic(UseNullCoalescingAssignmentAnalyzer.DiagnosticId).WithLocation(0);
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task ReplacesFieldOnStableReadonlyReceiver()
    {
        const string source = """
            class Holder
            {
                public string? Value;
            }

            class Example
            {
                private readonly Holder holder = new();

                void Set(string fallback)
                {
                    if (((holder.Value)) {|#0:is|} null)
                    {
                        ((holder.Value)) = fallback;
                    }
                }
            }
            """;
        const string fixedSource = """
            class Holder
            {
                public string? Value;
            }

            class Example
            {
                private readonly Holder holder = new();

                void Set(string fallback)
                {
                    ((holder.Value)) ??= fallback;
                }
            }
            """;

        return VerifyCS.VerifyCodeFixAsync(
            source,
            VerifyCS.Diagnostic(UseNullCoalescingAssignmentAnalyzer.DiagnosticId).WithLocation(0),
            fixedSource);
    }

    [Fact]
    public Task PreservesBuiltInReferenceEqualityForTypesWithImplicitConversions()
    {
        const string source = """
            #nullable enable
            class Wrapper
            {
                public static implicit operator string?(Wrapper? value) => null;
                public int Length => 1;
                public void Call() { }
            }
            class Example
            {
                void M(Wrapper? value, Wrapper fallback)
                {
                    if (value {|#0:==|} null)
                    {
                        value = fallback;
                    }
                }
            }
            """;
        const string fixedSource = """
            #nullable enable
            class Wrapper
            {
                public static implicit operator string?(Wrapper? value) => null;
                public int Length => 1;
                public void Call() { }
            }
            class Example
            {
                void M(Wrapper? value, Wrapper fallback)
                {
                    value ??= fallback;
                }
            }
            """;

        return VerifyCS.VerifyCodeFixAsync(
            source,
            VerifyCS.Diagnostic(UseNullCoalescingAssignmentAnalyzer.DiagnosticId).WithLocation(0),
            fixedSource);
    }

    [Fact]
    public Task ReplacesStableNullableHasValueGuards()
    {
        const string source = """
            class Example
            {
                int? field;
                static int? shared;
                readonly Example receiver;
                Example() { receiver = this; }
                void Run(int? parameter, char c)
                {
                    char? digit29 = null;
                    if ({|#0:!|}digit29.HasValue) { digit29 = c; }
                    if (({|#1:!|}((parameter).HasValue))) { parameter = 1; }
                    if ({|#2:!|}this.field.HasValue) { this.field = 2; }
                    if ({|#3:!|}shared.HasValue) { shared = 3; }
                    if ({|#4:!|}receiver.field.HasValue) { receiver.field = 4; }
                }
            }
            """;
        const string fixedSource = """
            class Example
            {
                int? field;
                static int? shared;
                readonly Example receiver;
                Example() { receiver = this; }
                void Run(int? parameter, char c)
                {
                    char? digit29 = null;
                    digit29 ??= c;
                    parameter ??= 1;
                    this.field ??= 2;
                    shared ??= 3;
                    receiver.field ??= 4;
                }
            }
            """;
        var expected = Enumerable.Range(0, 5).Select(index =>
            VerifyCS.Diagnostic(UseNullCoalescingAssignmentAnalyzer.DiagnosticId).WithLocation(index));
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource, fixedSource);
    }

    [Fact]
    public Task PreservesCommentsAroundNullableHasValueGuard()
    {
        const string source = """
            class Example
            {
                void Run(int? value)
                {
                    // fallback
                    if (/* guard */ {|#0:!|}value.HasValue /* missing */)
                    {
                        // assignment
                        value = 1; // retain
                    }
                }
            }
            """;
        const string fixedSource = """
            class Example
            {
                void Run(int? value)
                {
                    // fallback
                    /* guard */
                    /* missing */
                    // assignment
                    value ??= 1; // retain
                }
            }
            """;
        return VerifyCS.VerifyCodeFixAsync(source,
            VerifyCS.Diagnostic(UseNullCoalescingAssignmentAnalyzer.DiagnosticId).WithLocation(0), fixedSource);
    }

    [Fact]
    public Task IgnoresCustomHasValueAndUnstableNullableTargets()
    {
        const string source = """
            class Custom { public bool HasValue => false; }
            class Box { public int? Value; }
            class Example
            {
                int? storage;
                int? Property { get; set; }
                ref int? Reference => ref storage;
                Box receiver = new Box();
                Box GetBox() => receiver;
                void Run(Custom custom, int? value, int? other, Box first, Box second)
                {
                    if (!custom.HasValue) { custom = new Custom(); }
                    if (!Property.HasValue) { Property = 1; }
                    if (!Reference.HasValue) { Reference = 2; }
                    if (!GetBox().Value.HasValue) { GetBox().Value = 3; }
                    if (!receiver.Value.HasValue) { receiver.Value = 4; }
                    if (!value.HasValue) { other = 5; }
                    if (!first.Value.HasValue) { second.Value = 6; }
                    if (value.HasValue == false) { value = 7; }
                }
            }
            """;
        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task IgnoresNullableHasValueGuardWithInternalCommentsOrDirectives()
    {
        const string source = """
            class Example
            {
                void Run(int? value)
                {
                    if (! /* reason */ value.HasValue) { value = 1; }
                    if (!value.HasValue)
                    {
            #if DEBUG
                        value = 2;
            #else
                        value = 3;
            #endif
                    }
                }
            }
            """;
        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task FixAllCombinesNullableHasValueAndNullGuards()
    {
        const string source = """
            class Example
            {
                int? Property { get; set; }
                void Run(int? number, string? text)
                {
                    if ({|#0:!|}number.HasValue) { number = 7; }
                    if (text {|#1:is|} null) { text = "fallback"; }
                    if (!Property.HasValue) { Property = 8; }
                }
            }
            """;
        const string fixedSource = """
            class Example
            {
                int? Property { get; set; }
                void Run(int? number, string? text)
                {
                    number ??= 7;
                    text ??= "fallback";
                    if (!Property.HasValue) { Property = 8; }
                }
            }
            """;
        var expected = Enumerable.Range(0, 2).Select(index =>
            VerifyCS.Diagnostic(UseNullCoalescingAssignmentAnalyzer.DiagnosticId).WithLocation(index));
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource, fixedSource);
    }

    [Fact]
    public Task IgnoresIncompleteNullableHasValueGuard()
    {
        var test = new CSharpCodeFixTest<UseNullCoalescingAssignmentAnalyzer,
            UseNullCoalescingAssignmentCodeFixProvider, DefaultVerifier>
        {
            ReferenceAssemblies = ReferenceAssemblies.Net.Net100,
            CompilerDiagnostics = CompilerDiagnostics.None,
            TestCode = "class Example { void Run(int? value) { if (!value.HasValue) { value = ; } } }",
        };
        return test.RunAsync(TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("class Example { void Run(int? value, int? other) { if (!value.HasValue) { other = 5; } } }", LanguageVersion.Latest)]
    [InlineData("class Example { int? Value { get; set; } void Run() { if (!Value.HasValue) { Value = 5; } } }", LanguageVersion.Latest)]
    [InlineData("class Box { public int? Value; } class Example { Box receiver = new Box(); void Run() { if (!receiver.Value.HasValue) { receiver.Value = 5; } } }", LanguageVersion.Latest)]
    [InlineData("class Example { void Run(int? value) { if (!value.HasValue) { value = 5; } else { value = 6; } } }", LanguageVersion.Latest)]
    [InlineData("class Example { void Run(int? value) { if (! /* preserve */ value.HasValue) { value = 5; } } }", LanguageVersion.Latest)]
    [InlineData("class Example { void Run(int? value) { if (!value.HasValue) {\n#if DEBUG\nvalue = 5;\n#else\nvalue = 6;\n#endif\n} } }", LanguageVersion.Latest)]
    [InlineData("class Example { void Run(int? value) { if (!value.HasValue) { value = 5; } } }", LanguageVersion.CSharp7_3)]
    [InlineData("class Example { void Run(string value) { if (value == null) { value = \"fallback\"; } } }", LanguageVersion.CSharp7_3)]
    public async Task StaleDiagnosticDoesNotOfferUnsafeNullAssignment(string source, LanguageVersion languageVersion)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("StaleNullAssignmentDiagnostic", LanguageNames.CSharp)
            .WithParseOptions(new CSharpParseOptions(languageVersion))
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddMetadataReference(MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        var document = project.AddDocument("Test.cs", SourceText.From(source));
        var compilation = await document.Project.GetCompilationAsync(cancellationToken);
        Assert.DoesNotContain(compilation!.GetDiagnostics(cancellationToken), diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Error);
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var ifStatement = root!.DescendantNodes().OfType<IfStatementSyntax>().Single();
        var descriptor = new UseNullCoalescingAssignmentAnalyzer().SupportedDiagnostics.Single();
        var diagnostic = Diagnostic.Create(descriptor, ifStatement.Condition.GetLocation());
        var actions = new List<CodeAction>();
        var context = new CodeFixContext(document, diagnostic,
            (action, _) => actions.Add(action), cancellationToken);

        await new UseNullCoalescingAssignmentCodeFixProvider().RegisterCodeFixesAsync(context);

        Assert.Empty(actions);
    }

}
