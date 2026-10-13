using System.Linq.Expressions;

var example = new Example();
example.EarlyReturn(true);
example.Braces(true);
example.Loop(new[] { 1, -1, 2 });
example.Nested(true, true);
example.TryAdd(1, 2);
example.SetAdd(3);
example.Using();
example.Guard(new Service());
Console.WriteLine(string.Join("|", example.Sink, example.RedundantElse(false),
    example.TypePattern("abc"), example.Boolean(true), example.Lookup(),
    example.Assign(null), example.Coalesce(null), example.Conditional(new Service()),
    example.Contains("abc"), example.ContainsCharacter("abc"),
    example.BooleanReturn(true), example.NotPattern(1),
    string.Join(",", example.Chain(new[] { 1, 2, 50 })), example.ExpressionContains().Compile()("abc"),
    example.NullableGuard(null), example.NullableGuard(9), example.DeclaredPattern("abc"),
    example.DeclaredPattern(1), example.NestedTermination(true, false), example.NestedTermination(false, true)));
Console.WriteLine(SafetyControls.Run());

public sealed class Service
{
    public string Name => "service";
    public void Run() { }
}

public sealed class Example
{
    private readonly Dictionary<int, int> dictionary = new() { [1] = 7 };
    public int Sink { get; private set; }

    public void EarlyReturn(bool ready)
    {
        if (ready)
        {
            Sink += 1;
            Sink += 2;
        }
    }

    public void Braces(bool ready)
    {
        if (ready)
        {
            Sink++;
        }
    }

    public int RedundantElse(bool ready)
    {
        if (ready)
            return 1;
        else
            return 2;
    }

    public void Loop(int[] values)
    {
        foreach (var value in values)
        {
            if (value > 0)
            {
                Sink += value;
                Sink++;
            }
        }
    }

    public int TypePattern(object value)
    {
        var text = value as string;
        if (text is not null)
            return text.Length;
        return 0;
    }

    public bool Boolean(bool value) => value == true;

    public int Lookup()
    {
        if (dictionary.ContainsKey(key: 1))
            return dictionary[1];
        return 0;
    }

    public string Assign(string? value)
    {
        if (value is null)
        {
            value = "fallback";
        }
        return value;
    }

    public void NullGuard(object value)
    {
        if (value is null)
            throw new ArgumentNullException(nameof(value));
        Sink++;
    }

    public void Nested(bool first, bool second)
    {
        if (first)
        {
            if (second)
                Sink++;
        }
    }

    public void TryAdd(int key, int value)
    {
        var values = new Dictionary<int, int>();
        if (!values.ContainsKey(key))
        {
            values.Add(value: value, key: key);
        }
    }

    public void SetAdd(int value)
    {
        var values = new HashSet<int>();
        if (!values.Contains(value) /* keep once */)
        {
            values.Add(value);
            Sink++;
        }
    }

    public void Using()
    {
        using (var stream = new MemoryStream())
        {
            Sink += (int)stream.Length;
        }
    }

    public string Coalesce(string? value) => value is null ? "fallback" : value;
    public string? Conditional(Service? value) => value is null ? null : value.Name;
    public bool Contains(string value) => value.IndexOf("a", StringComparison.Ordinal) >= 0;
    public bool ContainsCharacter(string value) => value.IndexOf('a', StringComparison.Ordinal) >= 0;

    public bool BooleanReturn(bool ready)
    {
        if (ready)
            return true;
        return false;
    }

    public void Guard(Service? value)
    {
        if (value is not null)
        {
            value?.Run();
        }
    }

    public bool NotPattern(object value) => !(value is string);

    public int NullableGuard(int? value)
    {
        if (!value.HasValue)
        {
            value = 4;
        }
        return value.Value;
    }

    public int DeclaredPattern(object value)
    {
        if (!(value is string text))
            return 0;
        return text.Length;
    }

    public int NestedTermination(bool ready, bool flag)
    {
        if (ready)
        {
            if (flag)
                return 10;
            else
                return 11;
        }
        else
            return 20;
    }

    public int[] Chain(int[] source) => source.Where(value => value > 0).Select(value => value * 2).Where(value => value < 100).Select(value => value + 1).Where(value => value != 42).ToArray();

    public Expression<Func<string, bool>> ExpressionContains() => value => value.IndexOf("a", StringComparison.Ordinal) >= 0;
}
