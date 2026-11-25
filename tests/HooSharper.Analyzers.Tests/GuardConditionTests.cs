using HooSharper.CodeFixes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using VerifyCS = HooSharper.Analyzers.Tests.AnalyzerVerifier<
    HooSharper.Analyzers.PreferEarlyReturnAnalyzer, HooSharper.CodeFixes.PreferEarlyReturnCodeFixProvider>;

namespace HooSharper.Analyzers.Tests;

public sealed class GuardConditionTests
{
    [Theory]
    [InlineData("op_Inequality", "==")]
    [InlineData("op_Equality", "!=")]
    public async Task PreservesEqualityWhenOnlyTheInverseIsOverloadedInMetadata(string method, string equality)
    {
        // C# operator declarations require matching pairs, but CLR metadata does not.
        var dependency = CSharpCompilation.Create("AsymmetricEquality",
            [CSharpSyntaxTree.ParseText($$"""
                public class Value
                {
                    [System.Runtime.CompilerServices.SpecialName]
                    public static bool {{method}}(Value a, Value b) => false;
                }
                """, cancellationToken: TestContext.Current.CancellationToken)],
            await ReferenceAssemblies.Net.Net100.ResolveAsync(LanguageNames.CSharp, TestContext.Current.CancellationToken),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var bytes = new MemoryStream();
        var emit = dependency.Emit(bytes, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(emit.Success, string.Join("\n", emit.Diagnostics));

        var test = new CSharpCodeFixTest<PreferEarlyReturnAnalyzer, PreferEarlyReturnCodeFixProvider, DefaultVerifier>
        {
            ReferenceAssemblies = ReferenceAssemblies.Net.Net100,
            TestCode = $$"""
                class C
                {
                    void Run(Value a, Value b)
                    {
                        {|#0:if|} (a {{equality}} b)
                        {
                            System.Console.WriteLine();
                        }
                    }
                }
                """,
            FixedCode = $$"""
                class C
                {
                    void Run(Value a, Value b)
                    {
                        if (!(a {{equality}} b))
                            return;
                        System.Console.WriteLine();
                    }
                }
                """,
            ExpectedDiagnostics = { VerifyCS.Diagnostic(PreferEarlyReturnAnalyzer.DiagnosticId).WithLocation(0) },
        };
        test.TestState.AdditionalReferences.Add(MetadataReference.CreateFromImage(bytes.ToArray()));
        await test.RunAsync(TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("object", "a == b", "a != b")]
    [InlineData("int?", "a != b", "a == b")]
    [InlineData("System.TimeSpan", "a /* ticks */ >= b", "a /* ticks */ < b")]
    [InlineData("int", "a < b", "a >= b")]
    [InlineData("int", "a <= b", "a > b")]
    [InlineData("double", "a >= b", "!(a >= b)")]
    [InlineData("int?", "a >= b", "!(a >= b)")]
    [InlineData("Convertible", "a >= b", "!(a >= b)")]
    [InlineData("Custom", "a >= b", "!(a >= b)")]
    public Task ComplementsOnlyTotallyOrderedComparisons(string type, string condition, string inverse)
    {
        const string custom = """
            struct Convertible
            {
                public static implicit operator int(Convertible value) => 1;
                public static bool operator <(Convertible a, Convertible b) => true;
                public static bool operator >(Convertible a, Convertible b) => true;
            }
            struct Custom
            {
                public static bool operator >=(Custom a, Custom b) => false;
                public static bool operator <=(Custom a, Custom b) => false;
            }
            """;
        var before = $$"""
            class C
            {
                void Run({{type}} a, {{type}} b)
                {
                    {|#0:if|} ({{condition}})
                    {
                        System.Console.WriteLine();
                    }
                }
            }
            """ + "\n" + custom;
        var after = $$"""
            class C
            {
                void Run({{type}} a, {{type}} b)
                {
                    if ({{inverse}})
                        return;
                    System.Console.WriteLine();
                }
            }
            """ + "\n" + custom;
        return VerifyCS.VerifyCodeFixAsync(before,
            VerifyCS.Diagnostic(PreferEarlyReturnAnalyzer.DiagnosticId).WithLocation(0), after);
    }
}
