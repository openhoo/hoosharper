using System.Text;

namespace HooSharper.Performance;

internal static class BenchmarkSource
{
    public static string CreateAnalyzerSource(int groups)
    {
        var source = new StringBuilder(groups * 2600);
        source.AppendLine("using System;");
        source.AppendLine("using System.Collections.Generic;");
        source.AppendLine("using System.IO;");
        source.AppendLine("using System.Linq;");
        source.AppendLine("sealed class Workload");
        source.AppendLine("{");
        source.AppendLine("    private readonly Dictionary<string, int> _dictionary = new();");
        source.AppendLine("    private readonly HashSet<string> _set = new();");
        source.AppendLine("    private string? _text;");
        source.AppendLine("    private static void Consume(object? value) { }");

        for (var index = 0; index < groups; index++)
        {
            source.Append("    int Candidate").Append(index).AppendLine("(string key, bool flag, int[] values)");
            source.AppendLine("    {");
            source.AppendLine("        if (_dictionary.ContainsKey(\"key\"))");
            source.AppendLine("        {");
            source.AppendLine("            Consume(_dictionary[\"key\"]);");
            source.AppendLine("        }");
            source.AppendLine("        if (!_dictionary.ContainsKey(\"new-key\"))");
            source.AppendLine("        {");
            source.AppendLine("            _dictionary.Add(\"new-key\", 1);");
            source.AppendLine("        }");
            source.AppendLine("        if (!_set.Contains(\"set-key\"))");
            source.AppendLine("        {");
            source.AppendLine("            _set.Add(\"set-key\");");
            source.AppendLine("        }");
            source.AppendLine("        if (_text is null)");
            source.AppendLine("        {");
            source.AppendLine("            _text = key;");
            source.AppendLine("        }");
            source.AppendLine("        if (flag == true) Consume(key);");
            source.AppendLine("        if (key.IndexOf(\"x\", StringComparison.Ordinal) >= 0) Consume(key);");
            source.AppendLine("        return values.Where(value => value > 0).Select(value => value * 2).Where(value => value < 100).Select(value => value + 1).Where(value => value != 42).Count();");
            source.AppendLine("    }");

            source.Append("    void EarlyReturnCandidate").Append(index).AppendLine("(bool enabled)");
            source.AppendLine("    {");
            source.AppendLine("        if (enabled)");
            source.AppendLine("        {");
            source.AppendLine("            Consume(enabled);");
            source.AppendLine("            Consume(!enabled);");
            source.AppendLine("        }");
            source.AppendLine("    }");

            source.Append("    int RedundantElseCandidate").Append(index).AppendLine("(bool enabled)");
            source.AppendLine("    {");
            source.AppendLine("        if (enabled)");
            source.AppendLine("        {");
            source.AppendLine("            return 1;");
            source.AppendLine("        }");
            source.AppendLine("        else");
            source.AppendLine("        {");
            source.AppendLine("            return 0;");
            source.AppendLine("        }");
            source.AppendLine("    }");

            source.Append("    void LoopCandidate").Append(index).AppendLine("(int[] values)");
            source.AppendLine("    {");
            source.AppendLine("        foreach (var value in values)");
            source.AppendLine("        {");
            source.AppendLine("            if (value > 0)");
            source.AppendLine("            {");
            source.AppendLine("                Consume(value);");
            source.AppendLine("                Consume(value + 1);");
            source.AppendLine("            }");
            source.AppendLine("        }");
            source.AppendLine("    }");

            source.Append("    void TypePatternCandidate").Append(index).AppendLine("(object value)");
            source.AppendLine("    {");
            source.AppendLine("        var text = value as string;");
            source.AppendLine("        if (text is not null)");
            source.AppendLine("        {");
            source.AppendLine("            Consume(text.Length);");
            source.AppendLine("        }");
            source.AppendLine("    }");

            source.Append("    void ThrowIfNullCandidate").Append(index).AppendLine("(object? value)");
            source.AppendLine("    {");
            source.AppendLine("        if (value is null)");
            source.AppendLine("        {");
            source.AppendLine("            throw new ArgumentNullException(nameof(value));");
            source.AppendLine("        }");
            source.AppendLine("        Consume(value);");
            source.AppendLine("    }");

            source.Append("    void NestedIfCandidate").Append(index).AppendLine("(bool first, bool second)");
            source.AppendLine("    {");
            source.AppendLine("        if (first)");
            source.AppendLine("        {");
            source.AppendLine("            if (second)");
            source.AppendLine("            {");
            source.AppendLine("                Consume(second);");
            source.AppendLine("            }");
            source.AppendLine("        }");
            source.AppendLine("    }");

            source.Append("    string CoalescingCandidate").Append(index).AppendLine("(string? value, string fallback) => value is null ? fallback : value;");
            source.Append("    int? NullConditionalCandidate").Append(index).AppendLine("(string? value) => value is null ? null : value.Length;");
            source.Append("    bool BooleanReturnCandidate").Append(index).AppendLine("(bool value)");
            source.AppendLine("    {");
            source.AppendLine("        if (value)");
            source.AppendLine("        {");
            source.AppendLine("            return true;");
            source.AppendLine("        }");
            source.AppendLine("        return false;");
            source.AppendLine("    }");

            source.Append("    void NullGuardCandidate").Append(index).AppendLine("(string? value)");
            source.AppendLine("    {");
            source.AppendLine("        if (value is not null)");
            source.AppendLine("        {");
            source.AppendLine("            Consume(value?.Trim());");
            source.AppendLine("        }");
            source.AppendLine("    }");
            source.Append("    bool NotPatternCandidate").Append(index).AppendLine("(object value) => !(value is string);");

            source.Append("    void UsingCandidate").Append(index).AppendLine("()");
            source.AppendLine("    {");
            source.AppendLine("        using (var stream = new MemoryStream())");
            source.AppendLine("        {");
            source.AppendLine("            stream.WriteByte(1);");
            source.AppendLine("        }");
            source.AppendLine("    }");
        }

        source.AppendLine("}");
        return source.ToString();
    }

    public static string CreateTryGetValueSource(int groups) => CreateFixSource(groups, static (builder, index) =>
    {
        builder.Append("    int M").Append(index).AppendLine("()");
        builder.AppendLine("    {");
        builder.AppendLine("        if (_dictionary.ContainsKey(\"key\"))");
        builder.AppendLine("        {");
        builder.AppendLine("            return _dictionary[\"key\"];");
        builder.AppendLine("        }");
        builder.AppendLine("        return 0;");
        builder.AppendLine("    }");
    }, "using System.Collections.Generic;", "    private readonly Dictionary<string, int> _dictionary = new();");

    public static string CreateUsingDeclarationSource(int groups) => CreateFixSource(groups, static (builder, index) =>
    {
        builder.Append("    void M").Append(index).AppendLine("()");
        builder.AppendLine("    {");
        builder.AppendLine("        using (var stream = new MemoryStream())");
        builder.AppendLine("        {");
        builder.AppendLine("            stream.WriteByte(1);");
        builder.AppendLine("        }");
        builder.AppendLine("    }");
    }, "using System.IO;", string.Empty);

    public static string CreateWrapFluentChainSource(int groups) => CreateFixSource(groups, static (builder, index) =>
    {
        builder.Append("    int[] M").Append(index).AppendLine("(int[] source) => source.Where(value => value > 0).Select(value => value * 2).Where(value => value < 100).Select(value => value + 1).Where(value => value != 42).ToArray();");
    }, "using System.Linq;", string.Empty);

    public static string CreatePreferEarlyReturnSource(int groups) => CreateFixSource(groups, static (builder, index) =>
    {
        builder.Append("    void M").Append(index).AppendLine("(bool enabled, int value)");
        builder.AppendLine("    {");
        builder.AppendLine("        if (enabled)");
        builder.AppendLine("        {");
        builder.AppendLine("            Consume(value);");
        builder.AppendLine("            Consume(value + 1);");
        builder.AppendLine("        }");
        builder.AppendLine("    }");
    }, string.Empty, "    private static void Consume(int value) { }");

    public static string CreatePreferLoopContinueSource(int groups) => CreateFixSource(groups, static (builder, index) =>
    {
        builder.Append("    void M").Append(index).AppendLine("(int[] values)");
        builder.AppendLine("    {");
        builder.AppendLine("        foreach (var value in values)");
        builder.AppendLine("        {");
        builder.AppendLine("            if (value > 0)");
        builder.AppendLine("            {");
        builder.AppendLine("                Consume(value);");
        builder.AppendLine("                Consume(value + 1);");
        builder.AppendLine("            }");
        builder.AppendLine("        }");
        builder.AppendLine("    }");
    }, string.Empty, "    private static void Consume(int value) { }");

    public static string CreateMergeNestedIfSource(int groups) => CreateFixSource(groups, static (builder, index) =>
    {
        builder.Append("    void M").Append(index).AppendLine("(bool first, bool second, bool third)");
        builder.AppendLine("    {");
        builder.AppendLine("        if (first)");
        builder.AppendLine("        {");
        builder.AppendLine("            if (second)");
        builder.AppendLine("            {");
        builder.AppendLine("                if (third)");
        builder.AppendLine("                {");
        builder.AppendLine("                    Consume();");
        builder.AppendLine("                }");
        builder.AppendLine("            }");
        builder.AppendLine("        }");
        builder.AppendLine("    }");
    }, string.Empty, "    private static void Consume() { }");

    public static string CreateNullConditionalAccessSource(int groups) => CreateFixSource(groups, static (builder, index) =>
    {
        builder.Append("    string? M").Append(index).AppendLine("(string? value)");
        builder.AppendLine("    {");
        builder.AppendLine("        return value is null ? null : value.Trim();");
        builder.AppendLine("    }");
    }, string.Empty, string.Empty);

    public static string CreateNullCoalescingAssignmentSource(int groups) => CreateFixSource(groups, static (builder, index) =>
    {
        builder.Append("    void M").Append(index).AppendLine("(ref string? value)");
        builder.AppendLine("    {");
        builder.AppendLine("        if (value is null)");
        builder.AppendLine("        {");
        builder.AppendLine("            value = \"fallback\";");
        builder.AppendLine("        }");
        builder.AppendLine("    }");
    }, string.Empty, string.Empty);

    private static string CreateFixSource(
        int groups,
        Action<StringBuilder, int> appendCandidate,
        string usingDirective,
        string member)
    {
        var source = new StringBuilder(groups * 400);
        source.AppendLine(usingDirective);
        source.AppendLine("sealed class Workload");
        source.AppendLine("{");
        if (member.Length != 0)
        {
            source.AppendLine(member);
        }
        for (var index = 0; index < groups; index++)
        {
            appendCandidate(source, index);
        }
        source.AppendLine("}");
        return source.ToString();
    }
}
