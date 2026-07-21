using VerifyCS = HooSharper.Analyzers.Tests.AnalyzerVerifier<
    HooSharper.Analyzers.UseDictionaryTryAddAnalyzer,
    HooSharper.CodeFixes.UseDictionaryTryAddCodeFixProvider>;

namespace HooSharper.Analyzers.Tests;

public sealed class UseDictionaryTryAddAnalyzerTests
{
    [Fact]
    public Task ReplacesSoleAddStatement()
    {
        const string source = """
            using System.Collections.Generic;
            class C
            {
                void M(Dictionary<string, int> dictionary, string key, int value)
                {
                    if (!dictionary.{|#0:ContainsKey|}(key))
                    {
                        dictionary.Add(key, value);
                    }
                }
            }
            """;
        const string fixedSource = """
            using System.Collections.Generic;
            class C
            {
                void M(Dictionary<string, int> dictionary, string key, int value)
                {
                    dictionary.TryAdd(key, value);
                }
            }
            """;
        var expected = VerifyCS.Diagnostic(UseDictionaryTryAddAnalyzer.DiagnosticId).WithLocation(0);
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task ReplacesConditionAndRemovesAddPreservingComment()
    {
        const string source = """
            using System.Collections.Generic;
            class C
            {
                void M(Dictionary<string, int> dictionary, string key, int value)
                {
                    if (!dictionary.{|#0:ContainsKey|}(key))
                    {
                        dictionary.Add(key, value);
                        // newly added
                        Consume(value);
                    }
                }
                void Consume(int value) { }
            }
            """;
        const string fixedSource = """
            using System.Collections.Generic;
            class C
            {
                void M(Dictionary<string, int> dictionary, string key, int value)
                {
                    if (dictionary.TryAdd(key, value))
                    {
                        // newly added
                        Consume(value);
                    }
                }
                void Consume(int value) { }
            }
            """;
        var expected = VerifyCS.Diagnostic(UseDictionaryTryAddAnalyzer.DiagnosticId).WithLocation(0);
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task PreservesRemovedTriviaForSoleAdd()
    {
        const string source = """
            using System.Collections.Generic;
            class C
            {
                void M(Dictionary<string, int> dictionary, string key, int value)
                {
                    if (!dictionary.{|#0:ContainsKey|}(key) /* after condition */)
                    { // opening brace
                        // before Add
                        dictionary./* on Add */Add(key, value); // after Add
                        // closing brace
                    }
                }
            }
            """;
        const string fixedSource = """
            using System.Collections.Generic;
            class C
            {
                void M(Dictionary<string, int> dictionary, string key, int value)
                {
                    /* after condition */
                    // before Add
                    // before Add
                    /* on Add */
                    dictionary.TryAdd(key, value); // after Add
                                                   // closing brace
                }
            }
            """;
        var expected = VerifyCS.Diagnostic(UseDictionaryTryAddAnalyzer.DiagnosticId).WithLocation(0);
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task PreservesRemovedAddTriviaBeforeRemainingBody()
    {
        const string source = """
            using System.Collections.Generic;
            class C
            {
                void M(Dictionary<string, int> dictionary, string key, int value)
                {
                    if (!/* after not */dictionary.{|#0:ContainsKey|}(key))
                    { // retained opening brace
                        // before Add
                        dictionary./* on Add */Add(key, value); // after Add
                        // remaining body
                        Consume(value);
                    } // retained closing brace
                }
                void Consume(int value) { }
            }
            """;
        const string fixedSource = """
            using System.Collections.Generic;
            class C
            {
                void M(Dictionary<string, int> dictionary, string key, int value)
                {
                    if ( /* after not */dictionary.TryAdd(key, value))
                    { // retained opening brace
                      // before Add
                        /* on Add */
                        // after Add
                        // remaining body
                        Consume(value);
                    } // retained closing brace
                }
                void Consume(int value) { }
            }
            """;
        var expected = VerifyCS.Diagnostic(UseDictionaryTryAddAnalyzer.DiagnosticId).WithLocation(0);
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task FixAllUpdatesBothForms()
    {
        const string source = """
            using System.Collections.Generic;
            class C
            {
                void M(Dictionary<string, int> dictionary, string first, string second)
                {
                    if (!dictionary.{|#0:ContainsKey|}(first))
                    {
                        dictionary.Add(first, 1);
                    }
                    if (!dictionary.{|#1:ContainsKey|}(second))
                    {
                        dictionary.Add(second, 2);
                        Consume();
                    }
                }
                void Consume() { }
            }
            """;
        const string fixedSource = """
            using System.Collections.Generic;
            class C
            {
                void M(Dictionary<string, int> dictionary, string first, string second)
                {
                    dictionary.TryAdd(first, 1);
                    if (dictionary.TryAdd(second, 2))
                    {
                        Consume();
                    }
                }
                void Consume() { }
            }
            """;
        var expected = new[]
        {
            VerifyCS.Diagnostic(UseDictionaryTryAddAnalyzer.DiagnosticId).WithLocation(0),
            VerifyCS.Diagnostic(UseDictionaryTryAddAnalyzer.DiagnosticId).WithLocation(1),
        };
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource, fixedSource);
    }

    [Fact]
    public Task IgnoresUnsupportedAndUnsafeCases()
    {
        const string source = """
            using System.Collections.Generic;
            class Custom
            {
                public bool ContainsKey(string key) => false;
                public void Add(string key, int value) { }
                public bool TryAdd(string key, int value) => true;
            }
            class C
            {
                int GetValue() => 1;
                void M(Dictionary<string, int> dictionary, IDictionary<string, int> abstraction, Custom custom, string key)
                {
                    if (!dictionary.ContainsKey(key)) { dictionary.Add(key, GetValue()); }
                    if (!abstraction.ContainsKey(key)) { abstraction.Add(key, 1); }
                    if (!custom.ContainsKey(key)) { custom.Add(key, 1); }
                    if (!dictionary.ContainsKey(key)) { dictionary.Add(key, 1); } else { }
                }
            }
            """;
        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task AllowsReadonlyFieldReceiverAndKey()
    {
        const string source = """
            using System.Collections.Generic;

            class C
            {
                private readonly Dictionary<string, int> _dictionary = new();
                private readonly string _key = "key";

                void M()
                {
                    if (!_dictionary.{|#0:ContainsKey|}(_key)) { _dictionary.Add(_key, 1); }
                }
            }
            """;
        const string fixedSource = """
            using System.Collections.Generic;

            class C
            {
                private readonly Dictionary<string, int> _dictionary = new();
                private readonly string _key = "key";

                void M()
                {
                    _dictionary.TryAdd(_key, 1);
                }
            }
            """;

        var expected = VerifyCS.Diagnostic(UseDictionaryTryAddAnalyzer.DiagnosticId).WithLocation(0);
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task IgnoresVolatileReceiverAndKeyRecursively()
    {
        const string source = """
            using System.Collections.Generic;

            class State
            {
                public Dictionary<string, int> Dictionary = new();
                public string Key = "key";
            }

            class C
            {
                private volatile Dictionary<string, int> _dictionary = new();
                private volatile string _key = "key";
                private volatile State _state = new();

                void M()
                {
                    if (!_dictionary.ContainsKey("key")) { _dictionary.Add("key", 1); }
                    if (!_state.Dictionary.ContainsKey("key")) { _state.Dictionary.Add("key", 1); }
                    if (!_state.Dictionary.ContainsKey(_key)) { _state.Dictionary.Add(_key, 1); }
                    if (!_state.Dictionary.ContainsKey(_state.Key)) { _state.Dictionary.Add(_state.Key, 1); }
                }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task IgnoresPropertyGetterReceiverAndKey()
    {
        const string source = """
            using System.Collections.Generic;

            class Holder
            {
                private readonly Dictionary<string, int> _first = new();
                private readonly Dictionary<string, int> _second = new();
                private int _dictionaryGets;
                private int _keyGets;

                public Dictionary<string, int> Dictionary => _dictionaryGets++ == 0 ? _first : _second;
                public string Key => (_keyGets++).ToString();
            }

            class C
            {
                void M(Holder holder, Dictionary<string, int> dictionary)
                {
                    if (!holder.Dictionary.ContainsKey("key")) { holder.Dictionary.Add("key", 1); }
                    if (!dictionary.ContainsKey(holder.Key)) { dictionary.Add(holder.Key, 1); }
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
            class C
            {
                void M(Dictionary<string, int> dictionary, string key)
                {
                    if (!dictionary.ContainsKey(key))
                    {
            #if DEBUG
                        dictionary.Add(key, 1);
            #endif
                    }
                }
            }
            """;
        return VerifyCS.VerifyAnalyzerAsync(source);
    }
    [Fact]
    public Task IgnoresComparerMutatingLocalReceiverKeyAndValue()
    {
        const string source = """
            using System;
            using System.Collections.Generic;

            sealed class CallbackComparer : IEqualityComparer<string>
            {
                private readonly Action _callback;
                public CallbackComparer(Action callback) => _callback = callback;
                public bool Equals(string? x, string? y) { _callback(); return StringComparer.Ordinal.Equals(x, y); }
                public int GetHashCode(string value) { _callback(); return StringComparer.Ordinal.GetHashCode(value); }
            }

            class C
            {
                void M()
                {
                    Dictionary<string, int> dictionary = null!;
                    var replacement = new Dictionary<string, int>();
                    var key = "before";
                    var value = 1;
                    dictionary = new Dictionary<string, int>(new CallbackComparer(() =>
                    {
                        dictionary = replacement;
                        key = "after";
                        value = 2;
                    }));

                    if (!dictionary.ContainsKey(key))
                    {
                        dictionary.Add(key, value);
                    }
                }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task IgnoresComparerMutatingKeyAndValueStorage()
    {
        const string source = """
            using System;
            using System.Collections.Generic;

            sealed class CallbackComparer : IEqualityComparer<string>
            {
                public Action Callback = () => { };
                public bool Equals(string? x, string? y) { Callback(); return StringComparer.Ordinal.Equals(x, y); }
                public int GetHashCode(string value) { Callback(); return StringComparer.Ordinal.GetHashCode(value); }
            }

            class C
            {
                private readonly CallbackComparer _comparer = new();
                private readonly Dictionary<string, int> _dictionary;
                private string _key = "before";

                C() => _dictionary = new Dictionary<string, int>(_comparer);

                void M(string stableKey, int stableValue)
                {
                    var value = 1;
                    _comparer.Callback = () => { _key = "after"; value = 2; };

                    if (!_dictionary.ContainsKey(_key)) { _dictionary.Add(_key, stableValue); }
                    if (!_dictionary.ContainsKey(stableKey)) { _dictionary.Add(stableKey, value); }
                }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }


    [Fact]
    public Task AllowsReadonlyValueFieldWithStableReceiver()
    {
        const string source = """
            using System.Collections.Generic;

            class C
            {
                private readonly Dictionary<string, int> _dictionary = new();
                private readonly int _value = 1;

                void M(string key)
                {
                    if (!_dictionary.{|#0:ContainsKey|}(key)) { _dictionary.Add(key, _value); }
                }
            }
            """;
        const string fixedSource = """
            using System.Collections.Generic;

            class C
            {
                private readonly Dictionary<string, int> _dictionary = new();
                private readonly int _value = 1;

                void M(string key)
                {
                    _dictionary.TryAdd(key, _value);
                }
            }
            """;

        var expected = VerifyCS.Diagnostic(UseDictionaryTryAddAnalyzer.DiagnosticId).WithLocation(0);
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

}
