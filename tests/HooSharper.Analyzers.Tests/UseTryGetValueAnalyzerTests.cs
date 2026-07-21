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

}
