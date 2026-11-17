// These values are compared after per-project Fix Alls have been saved and rebuilt.
internal static class RewriteControls
{
    private readonly struct Flag
    {
        public static implicit operator bool(Flag value) => true;
    }

    private readonly struct Number
    {
        public static implicit operator int?(Number value) => 42;
        public static implicit operator int(Number value) => 13;
        public static implicit operator int(Number? value) => 24;
    }

    private sealed class OtherGuard
    {
        public void ThrowIfNull(object? value, string name) { }
    }

    private static readonly Dictionary<int, int> Map = new() { [1] = 10 };
    private static readonly int value = 42;

    private static class LookupInitializer
    {
        public static readonly int Offset;

        static LookupInitializer()
        {
            Map.Remove(1);
            Map.Add(1, 50);
            Offset = 1;
        }
    }

    private static int Lookup()
    {
        if (Map.ContainsKey(1))
        {
            var offset = LookupInitializer.Offset;
            return Map[1] + offset;
        }

        return 0;
    }

    private static int Branch(bool first, bool second, bool third)
    {
        if (first)
            if (second) return 1;
            else { if (third) return 2; }
        else return 3;
        return 0;
    }

    private static int EmbeddedElse(bool stop)
    {
        if (stop)
            return 0;
        else
            int.TryParse("7", out var value);
        return value;
    }

    private static int? NullableDefault(bool choose)
    {
        int? number = null;
        if (number is null)
        {
            number = choose ? default : default;
        }
        return number;
    }

    private static int? NullableConversion(Number source)
    {
        int? number = null;
        if (number is null)
        {
            number = source;
        }
        return number;
    }

    private static int NullableExpression(Number? source) => source is null ? 2 : source;

    private static string Guard(object? arg)
    {
        var ArgumentNullException = new OtherGuard();
        try
        {
            if (arg is null)
                throw new ArgumentNullException(nameof(arg));
            return "no exception";
        }
        catch (ArgumentNullException ex)
        {
            return ex.ParamName!;
        }
    }

    private static string Pick(Flag value) => "flag";
    private static string Pick(bool value) => "bool";
    private static string Pick(object value) => "object";
    private static string BoxedPattern(object input) => Pick((object)!(input is string));

    public static string Run()
    {
        Flag flag = default;
        return string.Join("|", Branch(false, false, false), Branch(true, false, false),
            EmbeddedElse(false), NullableDefault(true) ?? -1, NullableConversion(default),
            NullableExpression(new Number()), Lookup(), Guard(null), Pick(!flag == false), BoxedPattern("text"));
    }
}
