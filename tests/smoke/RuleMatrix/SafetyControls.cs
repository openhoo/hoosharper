// Executable negative controls for transformations that previously changed
// values, bindings, or compiler validity. Other safe style rules may still run.
public static class SafetyControls
{
    private readonly struct Flag
    {
        public static implicit operator bool(Flag value) => true;
        public static bool operator !(Flag value) => false;
    }

    private readonly struct State
    {
        public int Value => 999;
    }

    private readonly struct Handle
    {
        public static implicit operator string?(Handle value) => null;
    }

    private const int Key = 1;
    private static readonly Dictionary<int, int> values = new() { [1] = 10, [2] = 20 };

    public static string Run() => string.Join("|", ConvertedBoolean(new Flag()).GetType().Name,
        NullableMember(new State())!.GetType().Name, WrongNamedKey(), ShadowedKey(),
        ConvertedNullGuard(default), ImpossibleLegacyType(1), InitializerScope(), BackwardGoto());

    private static object ConvertedBoolean(Flag value) => value == true;
    private static object? NullableMember(State? value) => value is null ? null : value.Value;

    private static bool WrongNamedKey()
    {
        var dictionary = new Dictionary<int, int> { [1] = 7 };
        if (!dictionary.ContainsKey(1))
        {
            dictionary.Add(value: 1, key: 2);
        }
        return dictionary.ContainsKey(2);
    }

    private static int ShadowedKey()
    {
        if (values.ContainsKey(Key))
        {
            const int Key = 2;
            return values[Key];
        }
        return 0;
    }

    private static bool ConvertedNullGuard(Handle argument)
    {
        try
        {
            if (argument == null)
                throw new ArgumentNullException(nameof(argument));
            return false;
        }
        catch (ArgumentNullException)
        {
            return true;
        }
    }

    private static bool ImpossibleLegacyType(int value) => !(value is long);

    private static readonly int value = 8;

    private static MemoryStream Open(out int result)
    {
        result = 3;
        return new MemoryStream();
    }

    private static int InitializerScope()
    {
        var before = value;
        using (var resource = Open(out int value))
        {
            return before + value;
        }
    }

    private static int BackwardGoto()
    {
        var count = 0;
    again:
        count++;
        using (var resource = new MemoryStream())
        {
            if (count < 2)
                goto again;
            return count;
        }
    }
}
