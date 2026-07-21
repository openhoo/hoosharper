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
                int Run(Dictionary<string, int> dictionary, string key)
                {
                    if (dictionary.{|#0:ContainsKey|}(key))
                    {
                        // Keep this comment.
                        return dictionary[key] + dictionary[key];
                    }

                    return 0;
                }
            }
            """;
        const string fixedSource = """
            using System.Collections.Generic;

            class Example
            {
                int Run(Dictionary<string, int> dictionary, string key)
                {
                    if (dictionary.TryGetValue(key, out var value))
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
                int Run(IDictionary<string, int> dictionary, string key, int value, int value1)
                {
                    if (dictionary.{|#0:ContainsKey|}(key))
                    {
                        return dictionary[key] + value + value1;
                    }

                    return 0;
                }
            }
            """;
        const string fixedSource = """
            using System.Collections.Generic;

            class Example
            {
                int Run(IDictionary<string, int> dictionary, string key, int value, int value1)
                {
                    if (dictionary.TryGetValue(key, out var value2))
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
                int Run(Dictionary<string, int> dictionary, string key)
                {
                    if (dictionary.{|#0:ContainsKey|}(key))
                    {
                        Action later = () => Console.WriteLine(dictionary[key]);
                        later();
                        return dictionary[key];
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
                int Run(Dictionary<string, int> dictionary, string key)
                {
                    if (dictionary.TryGetValue(key, out var value))
                    {
                        Action later = () => Console.WriteLine(dictionary[key]);
                        later();
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
                int Run(Dictionary<string, int> dictionary, string first, string second)
                {
                    var result = 0;
                    if (dictionary.{|#0:ContainsKey|}(first))
                    {
                        result += dictionary[first];
                    }

                    if (dictionary.{|#1:ContainsKey|}(second))
                    {
                        result += dictionary[second];
                    }

                    return result;
                }
            }
            """;
        const string fixedSource = """
            using System.Collections.Generic;

            class Example
            {
                int Run(Dictionary<string, int> dictionary, string first, string second)
                {
                    var result = 0;
                    if (dictionary.TryGetValue(first, out var value))
                    {
                        result += value;
                    }

                    if (dictionary.TryGetValue(second, out var value1))
                    {
                        result += value1;
                    }

                    return result;
                }
            }
            """;
        const string batchFixedSource = """
            using System.Collections.Generic;

            class Example
            {
                int Run(Dictionary<string, int> dictionary, string first, string second)
                {
                    var result = 0;
                    if (dictionary.TryGetValue(first, out var value))
                    {
                        result += value;
                    }

                    if (dictionary.TryGetValue(second, out var value1))
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
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource, batchFixedSource);
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
}
