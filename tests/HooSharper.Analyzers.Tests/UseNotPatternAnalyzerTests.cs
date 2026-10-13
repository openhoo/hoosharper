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
    HooSharper.Analyzers.UseNotPatternAnalyzer,
    HooSharper.CodeFixes.UseNotPatternCodeFixProvider>;

namespace HooSharper.Analyzers.Tests;

public sealed class UseNotPatternAnalyzerTests
{
    [Fact]
    public Task ConvertsTypePattern()
    {
        const string source = """
            class Example
            {
                bool Run(object value) => {|#0:!|}(value is string);
            }
            """;
        const string fixedSource = """
            class Example
            {
                bool Run(object value) => value is not string;
            }
            """;

        var expected = VerifyCS.Diagnostic(UseNotPatternAnalyzer.DiagnosticId)
            .WithLocation(0)
            .WithMessage("Use a not pattern");
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task ConvertsNullPattern()
    {
        const string source = """
            class Example
            {
                bool Run(object? value) => {|#0:!|}(value is null);
            }
            """;
        const string fixedSource = """
            class Example
            {
                bool Run(object? value) => value is not null;
            }
            """;

        var expected = VerifyCS.Diagnostic(UseNotPatternAnalyzer.DiagnosticId).WithLocation(0);
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task ParenthesizesCompoundPatternToPreserveMeaning()
    {
        const string source = """
            class Example
            {
                bool Run(object value) => {|#0:!|}(value is string or int);
            }
            """;
        const string fixedSource = """
            class Example
            {
                bool Run(object value) => value is not (string or int);
            }
            """;

        var expected = VerifyCS.Diagnostic(UseNotPatternAnalyzer.DiagnosticId).WithLocation(0);
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task PreservesCommentsAroundPattern()
    {
        const string source = """
            class Example
            {
                bool Run(object value) => {|#0:!|}(value is /* pattern comment */ string);
            }
            """;
        const string fixedSource = """
            class Example
            {
                bool Run(object value) => value is /* pattern comment */ not string;
            }
            """;

        var expected = VerifyCS.Diagnostic(UseNotPatternAnalyzer.DiagnosticId).WithLocation(0);
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task ParenthesizesAlreadyNegatedPattern()
    {
        const string source = """
            class Example
            {
                bool Run(object value) => {|#0:!|}(value is not string);
            }
            """;
        const string fixedSource = """
            class Example
            {
                bool Run(object value) => value is not (not string);
            }
            """;

        var expected = VerifyCS.Diagnostic(UseNotPatternAnalyzer.DiagnosticId).WithLocation(0);
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task PreservesCommentsAttachedAroundTargetAndPattern()
    {
        const string source = """
            class Example
            {
                bool Run(object value) => {|#0:!|}(/* target leading */ value /* target trailing */ is /* pattern leading */ string /* pattern trailing */);
            }
            """;
        const string fixedSource = """
            class Example
            {
                bool Run(object value) => /* target leading */ value /* target trailing */ is /* pattern leading */ not string /* pattern trailing */;
            }
            """;

        var expected = VerifyCS.Diagnostic(UseNotPatternAnalyzer.DiagnosticId).WithLocation(0);
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }
    [Fact]
    public Task PreservesCommentBeforeClosingParenthesis()
    {
        const string source = """
            class Example
            {
                bool Run(object value) => {|#0:!|}(value is string /* reason */);
            }
            """;
        const string fixedSource = """
            class Example
            {
                bool Run(object value) => value is not string /* reason */;
            }
            """;

        var expected = VerifyCS.Diagnostic(UseNotPatternAnalyzer.DiagnosticId).WithLocation(0);
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task ConvertsSimpleDeclarationPattern()
    {
        const string source = """
            class Example
            {
                bool Run(object value) => {|#0:!|}(value is string text);
            }
            """;

        const string fixedSource = """
            class Example
            {
                bool Run(object value) => value is not string text;
            }
            """;
        return VerifyCS.VerifyCodeFixAsync(source,
            VerifyCS.Diagnostic(UseNotPatternAnalyzer.DiagnosticId).WithLocation(0), fixedSource);
    }

    [Fact]
    public Task DoesNotReportAcrossDirective()
    {
        const string source = """
            class Example
            {
                bool Run(object value) => !(
            #if DEBUG
                    value is string
            #else
                    value is int
            #endif
                );
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task DoesNotReportBeforeCSharp9()
    {
        const string source = """
            class Example
            {
                bool Run(object value) => !(value is string);
                bool Declare(object value) => !(value is string text);
            }
            """;

        var test = new CSharpCodeFixTest<UseNotPatternAnalyzer, UseNotPatternCodeFixProvider, DefaultVerifier>
        {
            ReferenceAssemblies = ReferenceAssemblies.Net.Net100,
            TestCode = source,
        };
        test.SolutionTransforms.Add((solution, projectId) =>
        {
            var project = solution.GetProject(projectId)!;
            return solution.WithProjectParseOptions(
                projectId,
                ((CSharpParseOptions)project.ParseOptions!).WithLanguageVersion(LanguageVersion.CSharp8));
        });

        return test.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public Task DoesNotReportInsideExpressionTree()
    {
        const string source = """
            using System;
            using System.Linq.Expressions;

            class Example
            {
                Expression<Func<object, bool>> Build() => value => !(value is string);
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task DoesNotReportImplicitQueryableExpressionTree()
    {
        const string source = """
            using System.Linq;

            class Example
            {
                IQueryable<object> Run(IQueryable<object> query) =>
                    from value in query
                    where !(value is string)
                    select value;
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task FixAllConvertsNestedPatternsInOnePass()
    {
        const string source = """
            class Example
            {
                bool Run(object value) =>
                !(({|#0:!|}(value is string)) is bool);
            }
            """;
        const string fixedSource = """
            class Example
            {
                bool Run(object value) =>
                !((value is not string) is bool);
            }
            """;

        var expected = new[]
        {
            VerifyCS.Diagnostic(UseNotPatternAnalyzer.DiagnosticId).WithLocation(0),
        };
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource, fixedSource);
    }
    [Fact]
    public Task FixAllConvertsEveryEligibleExpression()
    {
        const string source = """
            class Example
            {
                bool Run(object first, object? second) =>
                    {|#0:!|}(first is string) && {|#1:!|}(second is null);
            }
            """;
        const string fixedSource = """
            class Example
            {
                bool Run(object first, object? second) =>
                    first is not string && second is not null;
            }
            """;

        var expected = new[]
        {
            VerifyCS.Diagnostic(UseNotPatternAnalyzer.DiagnosticId).WithLocation(0),
            VerifyCS.Diagnostic(UseNotPatternAnalyzer.DiagnosticId).WithLocation(1),
        };
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource, fixedSource);
    }
    [Fact]
    public Task DoesNotReportRedundantValueTypePattern()
    {
        const string source = """
            class Example
            {
                bool Run(int value) => !(value is int);
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task RewritesNestedEligiblePatternAndPreservesComments()
    {
        const string source = """
            class Example
            {
                bool Run(object value) =>
                    {|#0:!|}((/* inner */ !(value is string) /* nested */) is false);
            }
            """;
        const string fixedSource = """
            class Example
            {
                bool Run(object value) =>
                    (/* inner */ value is not string /* nested */) is not false;
            }
            """;

        return VerifyCS.VerifyCodeFixAsync(
            source,
            VerifyCS.Diagnostic(UseNotPatternAnalyzer.DiagnosticId).WithLocation(0),
            fixedSource);
    }
    [Fact]
    public Task NestedFixDoesNotIntroduceImpossibleValueTypePattern()
    {
        const string source = """
            class Example
            {
                bool Run(int value) => {|#0:!|}(!(value is int) is true);
            }
            """;
        const string fixedSource = """
            class Example
            {
                bool Run(int value) => !(value is int) is not true;
            }
            """;

        return VerifyCS.VerifyCodeFixAsync(
            source,
            VerifyCS.Diagnostic(UseNotPatternAnalyzer.DiagnosticId).WithLocation(0),
            fixedSource);
    }

    [Fact]
    public Task NestedFixDoesNotIntroducePatternInsideExpressionTree()
    {
        const string source = """
            using System;
            using System.Linq.Expressions;

            class Example
            {
                bool Run() => {|#0:!|}(((Expression<Func<object, bool>>)(value => !(value is string))) is null);
            }
            """;
        const string fixedSource = """
            using System;
            using System.Linq.Expressions;

            class Example
            {
                bool Run() => ((Expression<Func<object, bool>>)(value => !(value is string))) is not null;
            }
            """;

        return VerifyCS.VerifyCodeFixAsync(
            source,
            VerifyCS.Diagnostic(UseNotPatternAnalyzer.DiagnosticId).WithLocation(0),
            fixedSource);
    }

    [Fact]
    public Task NestedFixDoesNotIntroducePatternInsideQueryableProjection()
    {
        const string source = """
            using System.Linq;

            class Example
            {
                bool Run(IQueryable<object> values) =>
                    {|#0:!|}((from value in values select !(value is string)) is null);
            }
            """;
        const string fixedSource = """
            using System.Linq;

            class Example
            {
                bool Run(IQueryable<object> values) =>
                    (from value in values select !(value is string)) is not null;
            }
            """;

        return VerifyCS.VerifyCodeFixAsync(
            source,
            VerifyCS.Diagnostic(UseNotPatternAnalyzer.DiagnosticId).WithLocation(0),
            fixedSource);
    }

    [Fact]
    public Task DoesNotReportAlwaysTrueBoxingOrImpossibleTypeTests()
    {
        const string source = """
            using System;

            class Example
            {
                bool Box(int value) => !(value is object);
                bool Interface(int value) => !(value is IComparable);
                bool Numeric(int value) => !(value is double);
                bool Incompatible(string value) => !(value is Uri);
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task FixAllPreservesNestedAlwaysTrueBoxingTests()
    {
        const string source = """
            class Example
            {
                bool First(int value) => {|#0:!|}(!(value is object) is true);
                bool Second<T>(T value) where T : struct => {|#1:!|}(!(value is object) is false);
            }
            """;
        const string fixedSource = """
            class Example
            {
                bool First(int value) => !(value is object) is not true;
                bool Second<T>(T value) where T : struct => !(value is object) is not false;
            }
            """;

        var expected = new[]
        {
            VerifyCS.Diagnostic(UseNotPatternAnalyzer.DiagnosticId).WithLocation(0),
            VerifyCS.Diagnostic(UseNotPatternAnalyzer.DiagnosticId).WithLocation(1),
        };
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource, fixedSource);
    }

    [Fact]
    public Task ConvertsCompatibleNullableAndGenericTypeTests()
    {
        const string source = """
            class Example
            {
                bool Nullable(int? value) => {|#0:!|}(value is int);
                bool NullableBox(int? value) => {|#1:!|}(value is object);
                bool Generic<T>(T value) => {|#2:!|}(value is string);
                bool Dynamic(dynamic value) => {|#3:!|}(value is string);
            }
            """;
        const string fixedSource = """
            class Example
            {
                bool Nullable(int? value) => value is not int;
                bool NullableBox(int? value) => value is not object;
                bool Generic<T>(T value) => value is not string;
                bool Dynamic(dynamic value) => value is not string;
            }
            """;

        var expected = new[]
        {
            VerifyCS.Diagnostic(UseNotPatternAnalyzer.DiagnosticId).WithLocation(0),
            VerifyCS.Diagnostic(UseNotPatternAnalyzer.DiagnosticId).WithLocation(1),
            VerifyCS.Diagnostic(UseNotPatternAnalyzer.DiagnosticId).WithLocation(2),
            VerifyCS.Diagnostic(UseNotPatternAnalyzer.DiagnosticId).WithLocation(3),
        };
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource, fixedSource);
    }

    [Fact]
    public Task DeclarationPatternPreservesDefiniteAssignmentAfterGuard()
    {
        const string source = """
            using System;
            class Example
            {
                string ThrowGuard(object value)
                {
                    if ({|#0:!|}(value is string text)) { throw new ArgumentException(); }
                    return text;
                }
                string AssignGuard(object value)
                {
                    if ({|#1:!|}(value is string text)) { text = "fallback"; }
                    return text;
                }
                string Conditional(object value) => {|#2:!|}(value is string text) ? "fallback" : text;
            }
            """;
        const string fixedSource = """
            using System;
            class Example
            {
                string ThrowGuard(object value)
                {
                    if (value is not string text) { throw new ArgumentException(); }
                    return text;
                }
                string AssignGuard(object value)
                {
                    if (value is not string text) { text = "fallback"; }
                    return text;
                }
                string Conditional(object value) => value is not string text ? "fallback" : text;
            }
            """;
        var expected = Enumerable.Range(0, 3).Select(index =>
            VerifyCS.Diagnostic(UseNotPatternAnalyzer.DiagnosticId).WithLocation(index));
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource, fixedSource);
    }

    [Fact]
    public Task DeclarationPatternPreservesDesignationCommentsAndEscapedName()
    {
        const string source = """
            class Example
            {
                int Run(object value) => {|#0:!|}(value is /* type */ string /* name */ @string /* end */) ? 0 : @string.Length;
            }
            """;
        const string fixedSource = """
            class Example
            {
                int Run(object value) => value is /* type */ not string /* name */ @string /* end */ ? 0 : @string.Length;
            }
            """;
        return VerifyCS.VerifyCodeFixAsync(source,
            VerifyCS.Diagnostic(UseNotPatternAnalyzer.DiagnosticId).WithLocation(0), fixedSource);
    }

    [Fact]
    public Task DeclarationPatternSupportsNullableGenericDynamicAndDiscard()
    {
        const string source = """
            class Example
            {
                bool Nullable(int? value) => {|#0:!|}(value is int number);
                bool Generic<T>(T value) => {|#1:!|}(value is string text);
                bool Dynamic(dynamic value) => {|#2:!|}(value is string text);
                bool Discard(object value) => {|#3:!|}(value is string _);
            }
            """;
        const string fixedSource = """
            class Example
            {
                bool Nullable(int? value) => value is not int number;
                bool Generic<T>(T value) => value is not string text;
                bool Dynamic(dynamic value) => value is not string text;
                bool Discard(object value) => value is not string _;
            }
            """;
        var expected = Enumerable.Range(0, 4).Select(index =>
            VerifyCS.Diagnostic(UseNotPatternAnalyzer.DiagnosticId).WithLocation(index));
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource, fixedSource);
    }

    [Fact]
    public Task FixAllRewritesNestedDeclarationPatternsAndKeepsExcludedPatterns()
    {
        const string source = """
            class Example
            {
                bool Nested(object value) => {|#0:!|}((!(value is string text)) is false);
                bool Recursive(object value) => {|#1:!|}((!(value is string { Length: > 0 } text)) is false);
                bool Always(int value) => {|#2:!|}((!(value is object boxed)) is false);
            }
            """;
        const string fixedSource = """
            class Example
            {
                bool Nested(object value) => (value is not string text) is not false;
                bool Recursive(object value) => (!(value is string { Length: > 0 } text)) is not false;
                bool Always(int value) => (!(value is object boxed)) is not false;
            }
            """;
        var expected = Enumerable.Range(0, 3).Select(index =>
            VerifyCS.Diagnostic(UseNotPatternAnalyzer.DiagnosticId).WithLocation(index));
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource, fixedSource);
    }

    [Fact]
    public Task DoesNotReportComplexDesignationsOrAlwaysMatchingDeclarations()
    {
        const string source = """
            using System;
            class Example
            {
                bool Recursive(object value) => !(value is string { Length: > 0 } text);
                bool Parenthesized(object value) => !(value is (string text));
                bool NestedNot(object value) => !(value is not string text);
                bool Box(int value) => !(value is object boxed);
                bool Same(int value) => !(value is int number);
                bool Interface(int value) => !(value is IComparable comparable);
            }
            """;
        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task DoesNotReportIncompleteDeclarationPattern()
    {
        var test = new CSharpCodeFixTest<UseNotPatternAnalyzer, UseNotPatternCodeFixProvider, DefaultVerifier>
        {
            ReferenceAssemblies = ReferenceAssemblies.Net.Net100,
            CompilerDiagnostics = CompilerDiagnostics.None,
            TestCode = "class Example { bool Run(object value) => !(value is UnknownType text); }",
        };
        return test.RunAsync(TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("class Example { bool Run(int value) => !(value is object boxed); }", LanguageVersion.Latest)]
    [InlineData("class Example { bool Run(int value) => !(value is int number); }", LanguageVersion.Latest)]
    [InlineData("class Example { bool Run(int value) => !(value is double); }", LanguageVersion.Latest)]
    [InlineData("class Example { bool Run(string value) => !(value is System.Uri); }", LanguageVersion.Latest)]
    [InlineData("using System; using System.Linq.Expressions; class Example { Expression<Func<object, bool>> Run() => value => !(value is string); }", LanguageVersion.Latest)]
    [InlineData("using System.Linq; class Example { IQueryable<object> Run(IQueryable<object> query) => from value in query where !(value is string) select value; }", LanguageVersion.Latest)]
    [InlineData("class Example { bool Run(object value) => !(value is string); }", LanguageVersion.CSharp8)]
    [InlineData("class Example { bool Run(object value) => !(value is string text); }", LanguageVersion.CSharp8)]
    public async Task StaleDiagnosticDoesNotOfferUnsafeNotPattern(string source, LanguageVersion languageVersion)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("StaleNotPatternDiagnostic", LanguageNames.CSharp)
            .WithParseOptions(new CSharpParseOptions(languageVersion))
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        foreach (var assembly in ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator))
        {
            project = project.AddMetadataReference(MetadataReference.CreateFromFile(assembly));
        }

        var document = project.AddDocument("Test.cs", SourceText.From(source));
        var compilation = await document.Project.GetCompilationAsync(cancellationToken);
        Assert.DoesNotContain(compilation!.GetDiagnostics(cancellationToken), diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Error);
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var logicalNot = root!.DescendantNodes().OfType<PrefixUnaryExpressionSyntax>().Single();
        var descriptor = new UseNotPatternAnalyzer().SupportedDiagnostics.Single();
        var diagnostic = Diagnostic.Create(descriptor, logicalNot.OperatorToken.GetLocation());
        var actions = new List<CodeAction>();
        var context = new CodeFixContext(document, diagnostic,
            (action, _) => actions.Add(action), cancellationToken);

        await new UseNotPatternCodeFixProvider().RegisterCodeFixesAsync(context);

        Assert.Empty(actions);
    }

}
