using HooSharper.CodeFixes;
using VerifyCS = HooSharper.Analyzers.Tests.AnalyzerVerifier<
    HooSharper.Analyzers.UseHashSetAddResultAnalyzer,
    HooSharper.CodeFixes.UseHashSetAddResultCodeFixProvider>;

namespace HooSharper.Analyzers.Tests;

public sealed class UseHashSetAddResultAnalyzerTests
{
    [Fact]
    public Task ReplacesSoleAddWithExpressionStatement()
    {
        const string source = """
            using System.Collections.Generic;

            class Example
            {
                void Add(string value)
                {
                    var set = new HashSet<string>();
                    if (!set.{|#0:Contains|}(value))
                    {
                        set.Add(value);
                    }
                }
            }
            """;
        const string fixedSource = """
            using System.Collections.Generic;

            class Example
            {
                void Add(string value)
                {
                    var set = new HashSet<string>();
                    set.Add(value);
                }
            }
            """;

        var expected = VerifyCS.Diagnostic(UseHashSetAddResultAnalyzer.DiagnosticId)
            .WithLocation(0)
            .WithMessage("Use the result of HashSet.Add instead of calling Contains first");
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task UsesAddAsConditionAndRemovesFirstStatement()
    {
        const string source = """
            using System.Collections.Generic;

            class Example
            {
                void Add(int value)
                {
                    var set = new HashSet<int>();
                    if (!set.{|#0:Contains|}(value))
                    {
                        set.Add(value);
                        System.Console.WriteLine(value);
                    }
                }
            }
            """;
        const string fixedSource = """
            using System.Collections.Generic;

            class Example
            {
                void Add(int value)
                {
                    var set = new HashSet<int>();
                    if (set.Add(value))
                    {
                        System.Console.WriteLine(value);
                    }
                }
            }
            """;

        var expected = VerifyCS.Diagnostic(UseHashSetAddResultAnalyzer.DiagnosticId).WithLocation(0);
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task PreservesCommentsFromConditionAndRemovedAdd()
    {
        const string source = """
            using System.Collections.Generic;

            class Example
            {
                void Add(int value)
                {
                    var set = new HashSet<int>();
                    // Before the if.
                    if (!set.Contains(/* value */ value))
                    {
                        // Add comment.
                        set.Add(value); // Added.
                        System.Console.WriteLine(value);
                    }
                }
            }
            """;
        const string fixedSource = """
            using System.Collections.Generic;

            class Example
            {
                void Add(int value)
                {
                    var set = new HashSet<int>();
                    // Before the if.
                    if (/* value */
            set.Add(value))
                    {
                        // Add comment.
                        // Added.
                        System.Console.WriteLine(value);
                    }
                }
            }
            """;

        var expected = VerifyCS.Diagnostic(UseHashSetAddResultAnalyzer.DiagnosticId)
            .WithSpan(9, 18, 9, 26);
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task FixAllUpdatesBothForms()
    {
        const string source = """
            using System.Collections.Generic;

            class Example
            {
                void Add(int first, int second)
                {
                    var set = new HashSet<int>();
                    if (!set.{|#0:Contains|}(first))
                    {
                        set.Add(first);
                    }

                    if (!set.{|#1:Contains|}(second))
                    {
                        set.Add(second);
                        System.Console.WriteLine(second);
                    }
                }
            }
            """;
        const string fixedSource = """
            using System.Collections.Generic;

            class Example
            {
                void Add(int first, int second)
                {
                    var set = new HashSet<int>();
                    set.Add(first);

                    if (set.Add(second))
                    {
                        System.Console.WriteLine(second);
                    }
                }
            }
            """;

        var expected = new[]
        {
            VerifyCS.Diagnostic(UseHashSetAddResultAnalyzer.DiagnosticId).WithLocation(0),
            VerifyCS.Diagnostic(UseHashSetAddResultAnalyzer.DiagnosticId).WithLocation(1),
        };
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource, fixedSource);
    }

    [Fact]
    public Task IgnoresCustomSetAndISet()
    {
        const string source = """
            using System.Collections.Generic;

            class CustomSet
            {
                public bool Contains(int value) => false;
                public bool Add(int value) => true;
            }

            class Example
            {
                void Custom(CustomSet set, int value)
                {
                    if (!set.Contains(value))
                    {
                        set.Add(value);
                    }
                }

                void Interface(ISet<int> set, int value)
                {
                    if (!set.Contains(value))
                    {
                        set.Add(value);
                    }
                }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task IgnoresDifferentOrUnstableReceiverAndValue()
    {
        const string source = """
            using System.Collections.Generic;

            class Example
            {
                HashSet<int> GetSet() => new();
                int GetValue() => 1;

                void Run(HashSet<int> first, HashSet<int> second, int value)
                {
                    if (!first.Contains(value))
                    {
                        second.Add(value);
                    }

                    if (!first.Contains(value))
                    {
                        first.Add(value + 1);
                    }

                    if (!GetSet().Contains(value))
                    {
                        GetSet().Add(value);
                    }

                    if (!first.Contains(GetValue()))
                    {
                        first.Add(GetValue());
                    }
                }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task IgnoresSideEffectingUserDefinedImplicitConversion()
    {
        const string source = """
            using System.Collections.Generic;

            readonly struct Value
            {
                public static int ConversionCount;

                public static implicit operator int(Value value)
                {
                    ConversionCount++;
                    return ConversionCount;
                }
            }

            class Example
            {
                void Run(HashSet<int> set, Value value)
                {
                    if (!set.Contains((value)))
                    {
                        set.Add((value));
                    }
                }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task IgnoresVolatileReceiverAndValueRecursively()
    {
        const string source = """
            using System.Collections.Generic;

            class State
            {
                public HashSet<int> Set = new();
                public int Value;
            }

            class Example
            {
                private volatile HashSet<int> _set = new();
                private volatile int _value;
                private volatile State _state = new();

                void Run()
                {
                    if (!_set.Contains(1)) { _set.Add(1); }
                    if (!_state.Set.Contains(1)) { _state.Set.Add(1); }
                    if (!_state.Set.Contains(_value)) { _state.Set.Add(_value); }
                    if (!_state.Set.Contains(_state.Value)) { _state.Set.Add(_state.Value); }
                }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task IgnoresElseNonFirstAddAndDirectives()
    {
        const string source = """
            using System.Collections.Generic;

            class Example
            {
                void Run(HashSet<int> set, int value)
                {
                    if (!set.Contains(value))
                    {
                        set.Add(value);
                    }
                    else
                    {
                        System.Console.WriteLine(value);
                    }

                    if (!set.Contains(value))
                    {
                        System.Console.WriteLine(value);
                        set.Add(value);
                    }

                    if (!set.Contains(value))
                    {
            #if DEBUG
                        set.Add(value);
            #endif
                    }
                }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }
    [Fact]
    public Task IgnoresComparerMutatingLocalReceiverAndValue()
    {
        const string source = """
            using System;
            using System.Collections.Generic;

            sealed class CallbackComparer : IEqualityComparer<int>
            {
                private readonly Action _callback;
                public CallbackComparer(Action callback) => _callback = callback;
                public bool Equals(int x, int y) { _callback(); return x == y; }
                public int GetHashCode(int value) { _callback(); return value; }
            }

            class Example
            {
                void Run()
                {
                    HashSet<int> set = null!;
                    var replacement = new HashSet<int>();
                    var value = 1;
                    set = new HashSet<int>(new CallbackComparer(() =>
                    {
                        set = replacement;
                        value = 2;
                    }));

                    if (!set.Contains(value))
                    {
                        set.Add(value);
                    }
                }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task IgnoresComparerMutatingValueStorage()
    {
        const string source = """
            using System;
            using System.Collections.Generic;

            sealed class CallbackComparer : IEqualityComparer<int>
            {
                public Action Callback = () => { };
                public bool Equals(int x, int y) { Callback(); return x == y; }
                public int GetHashCode(int value) { Callback(); return value; }
            }

            class Example
            {
                private readonly CallbackComparer _comparer = new();
                private readonly HashSet<int> _set;
                private int _value = 1;

                Example() => _set = new HashSet<int>(_comparer);

                void Run()
                {
                    var localValue = 1;
                    _comparer.Callback = () => { _value = 2; localValue = 2; };

                    if (!_set.Contains(_value)) { _set.Add(_value); }
                    if (!_set.Contains(localValue)) { _set.Add(localValue); }
                }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }


    [Fact]
    public Task AllowsReadonlyReceiverAndValueFields()
    {
        const string source = """
            using System.Collections.Generic;

            class Example
            {
                private readonly HashSet<int> _set = new();
                private readonly int _value = 1;

                void Run()
                {
                    if (!_set.{|#0:Contains|}(_value)) { _set.Add(_value); }
                }
            }
            """;
        const string fixedSource = """
            using System.Collections.Generic;

            class Example
            {
                private readonly HashSet<int> _set = new();
                private readonly int _value = 1;

                void Run()
                {
                    _set.Add(_value);
                }
            }
            """;

        var expected = VerifyCS.Diagnostic(UseHashSetAddResultAnalyzer.DiagnosticId).WithLocation(0);
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource);
    }

    [Fact]
    public Task IgnoresCountingComparerForLiteralAndLocalValue()
    {
        const string source = """
            using System;
            using System.Collections.Generic;

            sealed class CountingComparer : IEqualityComparer<int>
            {
                public int Count;
                public bool Equals(int x, int y) { Count++; return x == y; }
                public int GetHashCode(int value) { Count++; return value; }
            }

            class Example
            {
                void Run()
                {
                    var comparer = new CountingComparer();
                    var set = new HashSet<int>(comparer);
                    var value = 1;
                    if (!set.Contains(value))
                    {
                        set.Add(value);
                    }
                }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }
    [Fact]
    public Task ReplacesEnumAndNullableEnumValues()
    {
        const string source = """
            using System.Collections.Generic;

            enum Kind { First }

            class Example
            {
                void Run(Kind value, Kind? nullableValue)
                {
                    var kinds = new HashSet<Kind>();
                    var nullableKinds = new HashSet<Kind?>();
                    if (!kinds.{|#0:Contains|}(value)) { kinds.Add(value); }
                    if (!nullableKinds.{|#1:Contains|}(nullableValue)) { nullableKinds.Add(nullableValue); }
                }
            }
            """;
        const string fixedSource = """
            using System.Collections.Generic;

            enum Kind { First }

            class Example
            {
                void Run(Kind value, Kind? nullableValue)
                {
                    var kinds = new HashSet<Kind>();
                    var nullableKinds = new HashSet<Kind?>();
                    kinds.Add(value);
                    nullableKinds.Add(nullableValue);
                }
            }
            """;

        var expected = new[]
        {
            VerifyCS.Diagnostic(UseHashSetAddResultAnalyzer.DiagnosticId).WithLocation(0),
            VerifyCS.Diagnostic(UseHashSetAddResultAnalyzer.DiagnosticId).WithLocation(1),
        };
        return VerifyCS.VerifyCodeFixAsync(source, expected, fixedSource, fixedSource);
    }

    [Fact]
    public Task IgnoresLocalReassignedToCustomComparer()
    {
        const string source = """
            using System.Collections.Generic;

            sealed class CountingComparer : IEqualityComparer<int>
            {
                public bool Equals(int x, int y) => x == y;
                public int GetHashCode(int value) => value;
            }

            class Example
            {
                void Run(int value)
                {
                    var set = new HashSet<int>();
                    set = new HashSet<int>(new CountingComparer());
                    if (!set.Contains(value)) { set.Add(value); }
                }
            }
            """;

        return VerifyCS.VerifyAnalyzerAsync(source);
    }

}
