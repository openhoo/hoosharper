using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;

using HooSharper.CodeFixes;
using VerifyCS = HooSharper.Analyzers.Tests.AnalyzerVerifier<
    HooSharper.Analyzers.UseTryGetValueAnalyzer,
    HooSharper.CodeFixes.UseTryGetValueCodeFixProvider>;

namespace HooSharper.Analyzers.Tests;

public sealed class UseTryGetValueAnalyzerTests
{
    [Fact]
    public Task FixesDictionaryAndRepeatedAccesses()
    {
        const string source = """
            using System.Collections.Generic;

            class Example
            {
                private readonly Dictionary<string, int> dictionary = new();
                private const string Key = "key";

                int Run()
                {
                    if (dictionary.{|#0:ContainsKey|}(Key))
                    {
                        // Keep this comment.
                        return dictionary[Key] + dictionary[Key];
                    }

                    return 0;
                }
            }
            """;
        const string fixedSource = """
            using System.Collections.Generic;

            class Example
            {
                private readonly Dictionary<string, int> dictionary = new();
                private const string Key = "key";

                int Run()
                {
                    if (dictionary.TryGetValue(Key, out var value))
                    {
                        // Keep this comment.
                        return value + value;
                    }

                    return 0;
                }
            }
            """;

        var expected = VerifyCS.Diagnostic(UseTryGetValueAnalyzer.DiagnosticId)
            .WithLocation(0)
            .WithMessage("Use TryGetValue instead of ContainsKey followed by an index access");
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task FixesIDictionaryAndAvoidsNameCollision()
    {
        const string source = """
            using System.Collections.Generic;

            class Example
            {
                private readonly IDictionary<string, int> dictionary = new Dictionary<string, int>();
                private const string Key = "key";

                int Run(int value, int value1)
                {
                    if (dictionary.{|#0:ContainsKey|}(Key))
                    {
                        return dictionary[Key] + value + value1;
                    }

                    return 0;
                }
            }
            """;
        const string fixedSource = """
            using System.Collections.Generic;

            class Example
            {
                private readonly IDictionary<string, int> dictionary = new Dictionary<string, int>();
                private const string Key = "key";

                int Run(int value, int value1)
                {
                    if (dictionary.TryGetValue(Key, out var value2))
                    {
                        return value2 + value + value1;
                    }

                    return 0;
                }
            }
            """;

        var expected = VerifyCS.Diagnostic(UseTryGetValueAnalyzer.DiagnosticId).WithLocation(0);
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task DoesNotReplaceNestedScopeAccess()
    {
        const string source = """
            using System;
            using System.Collections.Generic;

            class Example
            {
                private readonly Dictionary<string, int> dictionary = new();
                private const string Key = "key";

                int Run()
                {
                    if (dictionary.{|#0:ContainsKey|}(Key))
                    {
                        Action later = () => Console.WriteLine(dictionary[Key]);
                        return dictionary[Key];
                    }

                    return 0;
                }
            }
            """;
        const string fixedSource = """
            using System;
            using System.Collections.Generic;

            class Example
            {
                private readonly Dictionary<string, int> dictionary = new();
                private const string Key = "key";

                int Run()
                {
                    if (dictionary.TryGetValue(Key, out var value))
                    {
                        Action later = () => Console.WriteLine(dictionary[Key]);
                        return value;
                    }

                    return 0;
                }
            }
            """;

        var expected = VerifyCS.Diagnostic(UseTryGetValueAnalyzer.DiagnosticId).WithLocation(0);
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task FixAllUpdatesEveryDictionaryLookup()
    {
        const string source = """
            using System.Collections.Generic;

            class Example
            {
                private readonly Dictionary<string, int> dictionary = new();
                private const string First = "first";
                private const string Second = "second";

                int Run()
                {
                    var result = 0;
                    if (dictionary.{|#0:ContainsKey|}(First))
                    {
                        result += dictionary[First];
                    }

                    if (dictionary.{|#1:ContainsKey|}(Second))
                    {
                        result += dictionary[Second];
                    }

                    return result;
                }
            }
            """;
        const string fixedSource = """
            using System.Collections.Generic;

            class Example
            {
                private readonly Dictionary<string, int> dictionary = new();
                private const string First = "first";
                private const string Second = "second";

                int Run()
                {
                    var result = 0;
                    if (dictionary.TryGetValue(First, out var value))
                    {
                        result += value;
                    }

                    if (dictionary.TryGetValue(Second, out var value1))
                    {
                        result += value1;
                    }

                    return result;
                }
            }
            """;

        var expected = new[]
        {
            VerifyCS.Diagnostic(UseTryGetValueAnalyzer.DiagnosticId).WithLocation(0),
            VerifyCS.Diagnostic(UseTryGetValueAnalyzer.DiagnosticId).WithLocation(1),
        };
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource, fixedSource);
    }

    [Fact]
    public Task IgnoresSideEffectingReceiver()
    {
        const string source = """
            using System.Collections.Generic;

            class Example
            {
                Dictionary<string, int> GetDictionary() => new();

                int Run(string key)
                {
                    if (GetDictionary().ContainsKey(key))
                    {
                        return GetDictionary()[key];
                    }

                    return 0;
                }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task IgnoresSideEffectingKey()
    {
        const string source = """
            using System.Collections.Generic;

            class Example
            {
                string GetKey() => "key";

                int Run(Dictionary<string, int> dictionary)
                {
                    if (dictionary.ContainsKey(GetKey()))
                    {
                        return dictionary[GetKey()];
                    }

                    return 0;
                }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task IgnoresCustomContainsKey()
    {
        const string source = """
            class CustomDictionary
            {
                public bool ContainsKey(string key) => true;
                public int this[string key] => 1;
            }

            class Example
            {
                int Run(CustomDictionary dictionary, string key)
                {
                    if (dictionary.ContainsKey(key))
                    {
                        return dictionary[key];
                    }

                    return 0;
                }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task IgnoresIfWithElse()
    {
        const string source = """
            using System.Collections.Generic;

            class Example
            {
                int Run(Dictionary<string, int> dictionary, string key)
                {
                    if (dictionary.ContainsKey(key))
                    {
                        return dictionary[key];
                    }
                    else
                    {
                        return 0;
                    }
                }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task IgnoresDirectives()
    {
        const string source = """
            using System.Collections.Generic;

            class Example
            {
                int Run(Dictionary<string, int> dictionary, string key)
                {
                    if (dictionary.ContainsKey(key))
                    {
            #if DEBUG
                        return dictionary[key];
            #else
                        return 0;
            #endif
                    }

                    return 0;
                }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }
    [Theory]
    [InlineData("dictionary[Key] = 1;")]
    [InlineData("dictionary[Key] += 1;")]
    [InlineData("dictionary[Key]++;")]
    [InlineData("dictionary[\"other\"] = 1;")]
    [InlineData("dictionary.Add(\"other\", 1);")]
    [InlineData("dictionary.Remove(Key);")]
    [InlineData("dictionary.Clear();")]
    [InlineData("dictionary.EnsureCapacity(10);")]
    [InlineData("dictionary.TrimExcess();")]
    public Task IgnoresMutationBeforeRead(string mutation)
    {
        var source = $$"""
            using System.Collections.Generic;

            class Example
            {
                private readonly Dictionary<string, int> dictionary = new();
                private const string Key = "key";

                int Run()
                {
                    if (dictionary.ContainsKey(Key))
                    {
                        {{mutation}}
                        return dictionary[Key];
                    }

                    return 0;
                }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task IgnoresUnknownMethodReceivingDictionaryBeforeRead()
    {
        const string source = """
            using System.Collections.Generic;

            class Example
            {
                private readonly Dictionary<string, int> dictionary = new();
                private const string Key = "key";

                static void Mutate(Dictionary<string, int> value) => value.Clear();

                int Run()
                {
                    if (dictionary.ContainsKey(Key))
                    {
                        Mutate(dictionary);
                        return dictionary[Key];
                    }

                    return 0;
                }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task IgnoresComparerMutatingReceiverAndKeyParameters()
    {
        const string source = """
            using System;
            using System.Collections.Generic;

            sealed class CallbackComparer : IEqualityComparer<string>
            {
                private readonly Action callback;
                public CallbackComparer(Action callback) => this.callback = callback;
                public bool Equals(string? x, string? y) { callback(); return StringComparer.Ordinal.Equals(x, y); }
                public int GetHashCode(string value) { callback(); return StringComparer.Ordinal.GetHashCode(value); }
            }

            class Example
            {
                int Run(Dictionary<string, int> dictionary, string key)
                {
                    var replacement = new Dictionary<string, int> { ["after"] = 2 };
                    dictionary = new Dictionary<string, int>(new CallbackComparer(() =>
                    {
                        dictionary = replacement;
                        key = "after";
                    })) { ["before"] = 1 };

                    if (dictionary.ContainsKey(key))
                    {
                        return dictionary[key];
                    }

                    return 0;
                }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task FixesReadonlyFieldReceiverAndConstantKey()
    {
        const string source = """
            using System.Collections.Generic;

            class Example
            {
                private readonly Dictionary<string, int> dictionary = new();
                private const string Key = "key";

                int Run()
                {
                    if (dictionary.{|#0:ContainsKey|}(Key))
                    {
                        return dictionary[Key];
                    }

                    return 0;
                }
            }
            """;
        const string fixedSource = """
            using System.Collections.Generic;

            class Example
            {
                private readonly Dictionary<string, int> dictionary = new();
                private const string Key = "key";

                int Run()
                {
                    if (dictionary.TryGetValue(Key, out var value))
                    {
                        return value;
                    }

                    return 0;
                }
            }
            """;

        var expected = VerifyCS.Diagnostic(UseTryGetValueAnalyzer.DiagnosticId).WithLocation(0);
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Theory]
    [InlineData("Dictionary<string, int> Dictionary => field;", "Dictionary", "key")]
    [InlineData("Dictionary<string, int> Dictionary = new();", "Dictionary", "key")]
    [InlineData("string Key => key;", "dictionary", "Key")]
    [InlineData("string Key = \"key\";", "dictionary", "Key")]
    public Task IgnoresUnstableReceiverOrKey(string member, string receiver, string lookupKey)
    {
        var source = $$"""
            using System.Collections.Generic;

            class Example
            {
                private readonly Dictionary<string, int> field = new();
                private readonly string key = "key";
                private readonly Dictionary<string, int> dictionary = new();
                {{member}}

                int Run()
                {
                    if ({{receiver}}.ContainsKey({{lookupKey}}))
                    {
                        return {{receiver}}[{{lookupKey}}];
                    }

                    return 0;
                }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task IgnoresRefReturn()
    {
        const string source = """
            using System.Collections.Generic;

            class Example
            {
                ref int Run(Dictionary<string, int> dictionary, string key)
                {
                    if (dictionary.ContainsKey(key))
                    {
                        return ref {|CS8156:dictionary[key]|};
                    }

                    throw new System.Exception();
                }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }
    [Fact]
    public Task IgnoresCustomComparerWithObservableCallbacks()
    {
        const string source = """
            using System.Collections.Generic;

            sealed class CallbackComparer : IEqualityComparer<string>
            {
                public bool Equals(string? x, string? y) => true;
                public int GetHashCode(string value) => 0;
            }

            class Example
            {
                private readonly Dictionary<string, int> dictionary =
                    new Dictionary<string, int>(new CallbackComparer());
                private const string Key = "key";

                int Run()
                {
                    if (dictionary.ContainsKey(Key))
                    {
                        return dictionary[Key];
                    }

                    return 0;
                }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task IgnoresAliasIndexerWrite()
    {
        const string source = """
            using System.Collections.Generic;

            class Example
            {
                private readonly Dictionary<string, int> dictionary = new();
                private const string Key = "key";

                int Run()
                {
                    var alias = dictionary;
                    if (dictionary.ContainsKey(Key))
                    {
                        alias[Key] = 1;
                        return dictionary[Key];
                    }

                    return 0;
                }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task IgnoresTupleAssignmentTarget()
    {
        const string source = """
            using System.Collections.Generic;

            class Example
            {
                private readonly Dictionary<string, int> dictionary = new();
                private const string Key = "key";

                int Run()
                {
                    if (dictionary.ContainsKey(Key))
                    {
                        (dictionary[Key], _) = (1, 1);
                        return dictionary[Key];
                    }

                    return 0;
                }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task IgnoresUserDefinedImplicitConversion()
    {
        const string source = """
            using System.Collections.Generic;

            struct KeyType
            {
                public static implicit operator string(KeyType value) => "key";
            }

            class Example
            {
                private readonly Dictionary<string, int> dictionary = new();
                private readonly KeyType key = new KeyType();

                int Run()
                {
                    if (dictionary.ContainsKey(key))
                    {
                        return dictionary[key];
                    }

                    return 0;
                }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task IgnoresExplicitCSharp6()
    {
        const string source = """
            using System.Collections.Generic;

            class Example
            {
                private readonly Dictionary<string, int> dictionary =
                    new Dictionary<string, int>();
                private const string Key = "key";

                int Run()
                {
                    if (dictionary.ContainsKey(Key))
                    {
                        return dictionary[Key];
                    }

                    return 0;
                }
            }
            """;
        var test = new CSharpCodeFixTest<
            UseTryGetValueAnalyzer,
            UseTryGetValueCodeFixProvider,
            DefaultVerifier>
        {
            ReferenceAssemblies = ReferenceAssemblies.Net.Net100,
            TestCode = source,
        };
        test.SolutionTransforms.Add((solution, projectId) =>
            solution.WithProjectParseOptions(
                projectId,
                ((CSharpParseOptions)solution.GetProject(projectId)!.ParseOptions!)
                    .WithLanguageVersion(LanguageVersion.CSharp6)));

        return test.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public Task AcceptsDefaultLanguageVersion()
    {
        const string source = """
            using System.Collections.Generic;

            class Example
            {
                private readonly Dictionary<string, int> dictionary =
                    new Dictionary<string, int>();
                private const string Key = "key";

                int Run()
                {
                    if (dictionary.{|#0:ContainsKey|}(Key))
                    {
                        return dictionary[Key];
                    }

                    return 0;
                }
            }
            """;
        const string fixedSource = """
            using System.Collections.Generic;

            class Example
            {
                private readonly Dictionary<string, int> dictionary =
                    new Dictionary<string, int>();
                private const string Key = "key";

                int Run()
                {
                    if (dictionary.TryGetValue(Key, out var value))
                    {
                        return value;
                    }

                    return 0;
                }
            }
            """;
        var test = new CSharpCodeFixTest<
            UseTryGetValueAnalyzer,
            UseTryGetValueCodeFixProvider,
            DefaultVerifier>
        {
            ReferenceAssemblies = ReferenceAssemblies.Net.Net100,
            TestCode = source,
            FixedCode = fixedSource,
        };
        test.ExpectedDiagnostics.Add(
            new DiagnosticResult(UseTryGetValueAnalyzer.DiagnosticId, DiagnosticSeverity.Info)
                .WithLocation(0));
        test.SolutionTransforms.Add((solution, projectId) =>
            solution.WithProjectParseOptions(
                projectId,
                ((CSharpParseOptions)solution.GetProject(projectId)!.ParseOptions!)
                    .WithLanguageVersion(LanguageVersion.Default)));

        return test.RunAsync(TestContext.Current.CancellationToken);
    }


    [Fact]
    public Task IgnoresDefaultComparerForUserDefinedKey()
    {
        const string source = """
            using System.Collections.Generic;

            struct KeyType
            {
                private static int Calls;

                public override bool Equals(object? obj)
                {
                    Calls++;
                    return true;
                }

                public override int GetHashCode()
                {
                    Calls++;
                    return 0;
                }
            }

            class Example
            {
                private readonly Dictionary<KeyType, int> dictionary =
                    new Dictionary<KeyType, int>();
                private readonly KeyType key = new KeyType();

                int Run()
                {
                    if (dictionary.ContainsKey(key))
                    {
                        return dictionary[key];
                    }

                    return 0;
                }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }
    [Fact]
    public Task ReplacesEnumAndNullableEnumDictionaryLookups()
    {
        const string source = """
            using System.Collections.Generic;

            enum Kind { First }

            class Example
            {
                private readonly Dictionary<Kind, int> kinds = new();
                private readonly Dictionary<Kind?, int> nullableKinds = new();
                private const Kind Key = Kind.First;
                private readonly Kind? nullableKey = Kind.First;

                int Run()
                {
                    var result = 0;
                    if (kinds.{|#0:ContainsKey|}(Key))
                    {
                        result += kinds[Key];
                    }

                    if (nullableKinds.{|#1:ContainsKey|}(nullableKey))
                    {
                        result += nullableKinds[nullableKey];
                    }

                    return result;
                }
            }
            """;
        const string fixedSource = """
            using System.Collections.Generic;

            enum Kind { First }

            class Example
            {
                private readonly Dictionary<Kind, int> kinds = new();
                private readonly Dictionary<Kind?, int> nullableKinds = new();
                private const Kind Key = Kind.First;
                private readonly Kind? nullableKey = Kind.First;

                int Run()
                {
                    var result = 0;
                    if (kinds.TryGetValue(Key, out var value))
                    {
                        result += value;
                    }

                    if (nullableKinds.TryGetValue(nullableKey, out var value1))
                    {
                        result += value1;
                    }

                    return result;
                }
            }
            """;
        var expected = new[]
        {
            VerifyCS.Diagnostic(UseTryGetValueAnalyzer.DiagnosticId).WithLocation(0),
            VerifyCS.Diagnostic(UseTryGetValueAnalyzer.DiagnosticId).WithLocation(1),
        };
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource, fixedSource);
    }

    [Fact]
    public Task IgnoresObjectCreationBetweenLookups()
    {
        const string source = """
            using System.Collections.Generic;

            class Example
            {
                private sealed class CountListener
                {
                    public CountListener(Example owner)
                    {
                        owner.counts[Key] = 1;
                    }

                    public int Observed => 1;
                }

                private readonly Dictionary<string, int> counts = new();
                private const string Key = "key";

                int Run()
                {
                    if (counts.ContainsKey(Key))
                    {
                        var listener = new CountListener(this);
                        return counts[Key] + listener.Observed;
                    }

                    return 0;
                }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task IgnoresTargetTypedNewBetweenLookups()
    {
        const string source = """
            using System.Collections.Generic;

            class Example
            {
                private sealed class CountListener
                {
                    public CountListener(Example owner)
                    {
                        owner.counts[Key] = 1;
                    }

                    public int Observed => 1;
                }

                private readonly Dictionary<string, int> counts = new();
                private const string Key = "key";

                int Run()
                {
                    if (counts.ContainsKey(Key))
                    {
                        CountListener listener = new(this);
                        return counts[Key] + listener.Observed;
                    }

                    return 0;
                }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task IgnoresBareAwaitBetweenLookups()
    {
        const string source = """
            using System.Collections.Generic;
            using System.Threading.Tasks;

            class Example
            {
                private readonly Dictionary<string, int> counts = new();
                private const string Key = "key";
                private readonly Task pending = Task.CompletedTask;

                async Task<int> RunAsync()
                {
                    if (counts.ContainsKey(Key))
                    {
                        await pending;
                        return counts[Key];
                    }

                    return 0;
                }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task FixAllLeavesSuppressedMutationShapesUnchanged()
    {
        const string source = """
            using System.Collections.Generic;
            using System.Threading.Tasks;

            class Example
            {
                private sealed class CountListener
                {
                    public CountListener(Example owner)
                    {
                        owner.counts[Key] = 1;
                    }

                    public int Observed => 1;
                }

                private readonly Dictionary<string, int> counts = new();
                private const string Key = "key";
                private readonly Task pending = Task.CompletedTask;

                int RunCreation()
                {
                    if (counts.ContainsKey(Key))
                    {
                        var listener = new CountListener(this);
                        return counts[Key] + listener.Observed;
                    }

                    return 0;
                }

                int RunTargetTyped()
                {
                    if (counts.ContainsKey(Key))
                    {
                        CountListener listener = new(this);
                        return counts[Key] + listener.Observed;
                    }

                    return 0;
                }

                async Task<int> RunAwaitAsync()
                {
                    if (counts.ContainsKey(Key))
                    {
                        await pending;
                        return counts[Key];
                    }

                    return 0;
                }
            }
            """;

        return VerifyCS.VerifyCodeFixAsync(source, [], source, source);
    }

    [Fact]
    public Task IgnoresConstructorKeyReassignmentThroughThis()
    {
        const string source = """
            using System;
            using System.Collections.Generic;

            class Example
            {
                private readonly Dictionary<string, int> map = new();
                private readonly string name;

                public Example(string other)
                {
                    name = other;
                    if (map.ContainsKey(name))
                    {
                        this.name = other + "?";
                        Console.WriteLine(map[name]);
                    }
                }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task IgnoresParenthesizedConstructorKeyReassignment()
    {
        const string source = """
            using System;
            using System.Collections.Generic;

            class Example
            {
                private readonly Dictionary<string, int> map = new();
                private readonly string name;

                public Example(string other)
                {
                    name = other;
                    if (map.ContainsKey(name))
                    {
                        (name) = other + "?";
                        Console.WriteLine(map[name]);
                    }
                }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task FixesConstLocalKeyDespiteSameNamedFieldWrite()
    {
        const string source = """
            using System.Collections.Generic;

            class Example
            {
                private readonly Dictionary<string, int> map = new();
                private string name = "initial";

                public void Run()
                {
                    const string name = "key";
                    if (map.{|#0:ContainsKey|}(name))
                    {
                        this.name = name + "?";
                        _ = map[name];
                    }
                }
            }
            """;
        const string fixedSource = """
            using System.Collections.Generic;

            class Example
            {
                private readonly Dictionary<string, int> map = new();
                private string name = "initial";

                public void Run()
                {
                    const string name = "key";
                    if (map.TryGetValue(name, out var value))
                    {
                        this.name = name + "?";
                        _ = value;
                    }
                }
            }
            """;

        var expected = VerifyCS.Diagnostic(UseTryGetValueAnalyzer.DiagnosticId).WithLocation(0);
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task FixesConstructorWithoutKeyReassignment()
    {
        const string source = """
            using System.Collections.Generic;

            class Example
            {
                private readonly Dictionary<string, int> map = new();
                private readonly string name;

                public Example(string other)
                {
                    name = other;
                    if (map.{|#0:ContainsKey|}(name))
                    {
                        _ = map[name];
                    }
                }
            }
            """;
        const string fixedSource = """
            using System.Collections.Generic;

            class Example
            {
                private readonly Dictionary<string, int> map = new();
                private readonly string name;

                public Example(string other)
                {
                    name = other;
                    if (map.TryGetValue(name, out var value))
                    {
                        _ = value;
                    }
                }
            }
            """;

        var expected = VerifyCS.Diagnostic(UseTryGetValueAnalyzer.DiagnosticId).WithLocation(0);
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task FixesDifferentInstanceKeyWrite()
    {
        const string source = """
            using System.Collections.Generic;

            class Example
            {
                public sealed class Peer
                {
                    public string Name = "initial";
                }

                private readonly Dictionary<string, int> map = new();
                private readonly string name = "initial";

                public Example(Peer peer)
                {
                    if (map.{|#0:ContainsKey|}(name))
                    {
                        peer.Name = name + "?";
                        _ = map[name];
                    }
                }
            }
            """;
        const string fixedSource = """
            using System.Collections.Generic;

            class Example
            {
                public sealed class Peer
                {
                    public string Name = "initial";
                }

                private readonly Dictionary<string, int> map = new();
                private readonly string name = "initial";

                public Example(Peer peer)
                {
                    if (map.TryGetValue(name, out var value))
                    {
                        peer.Name = name + "?";
                        _ = value;
                    }
                }
            }
            """;

        var expected = VerifyCS.Diagnostic(UseTryGetValueAnalyzer.DiagnosticId).WithLocation(0);
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task KeepsCommentsInsideReplacedIndexerReads()
    {
        const string source = """
            using System.Collections.Generic;

            class Example
            {
                private readonly Dictionary<string, int> weights = new();
                private const string Item = "item";

                int Run()
                {
                    var total = 0;
                    if (weights.{|#0:ContainsKey|}(Item))
                    {
                        total += weights[Item /* a */] - weights[ /* b */ Item];
                    }

                    return total;
                }
            }
            """;
        const string fixedSource = """
            using System.Collections.Generic;

            class Example
            {
                private readonly Dictionary<string, int> weights = new();
                private const string Item = "item";

                int Run()
                {
                    var total = 0;
                    if (weights.TryGetValue(Item, out var value))
                    {
                        total += /* a */ value - /* b */ value;
                    }

                    return total;
                }
            }
            """;

        var expected = VerifyCS.Diagnostic(UseTryGetValueAnalyzer.DiagnosticId).WithLocation(0);
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task KeepsCommentFromLastSeenIndexerRead()
    {
        const string source = """
            using System.Collections.Generic;

            class Example
            {
                private readonly Dictionary<string, int> weights = new();
                private const string Item = "item";

                int Run()
                {
                    var total = 0;
                    if (weights.{|#0:ContainsKey|}(Item))
                    {
                        total += weights[Item /* last seen */];
                    }

                    return total;
                }
            }
            """;
        const string fixedSource = """
            using System.Collections.Generic;

            class Example
            {
                private readonly Dictionary<string, int> weights = new();
                private const string Item = "item";

                int Run()
                {
                    var total = 0;
                    if (weights.TryGetValue(Item, out var value))
                    {
                        total += /* last seen */ value;
                    }

                    return total;
                }
            }
            """;

        var expected = VerifyCS.Diagnostic(UseTryGetValueAnalyzer.DiagnosticId).WithLocation(0);
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }
}
