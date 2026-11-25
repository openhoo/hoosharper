using System;
using System.Runtime.CompilerServices;

internal static class Program
{
    private static bool Flag;

    private static bool MutateThenRead(in bool snapshot)
    {
        Flag = false;
        return snapshot;
    }

    private static string Capture(bool other = false,
        [CallerArgumentExpression("flags")] string text = "", params bool[] flags) => text;

    private static void Main()
    {
        Flag = true;
        Console.WriteLine(MutateThenRead(Flag == true));
        Console.WriteLine(Capture(flags: Flag   ==   true));
        Console.WriteLine(Flag == false);
    }
}
