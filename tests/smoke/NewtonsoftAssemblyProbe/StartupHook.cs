using System.Reflection;
using System.Security.Cryptography;

// The vstest launcher uses its own Newtonsoft.Json. Record only the assembly
// inside the process that actually executes the upstream tests.
internal static class StartupHook
{
    public static void Initialize()
    {
        if (!string.Equals(Path.GetFileNameWithoutExtension(Environment.GetCommandLineArgs()[0]),
                "testhost", StringComparison.OrdinalIgnoreCase))
            return;

        var output = Environment.GetEnvironmentVariable("HOOSHARPER_NEWTONSOFT_LOADS")
            ?? throw new InvalidOperationException("Missing testhost assembly log directory.");
        void Record(Assembly assembly)
        {
            if (assembly.IsDynamic || assembly.GetName().Name != "Newtonsoft.Json" || assembly.Location.Length == 0)
                return;
            using var dll = File.OpenRead(assembly.Location);
            using var algorithm = SHA256.Create();
            var hash = Convert.ToHexString(algorithm.ComputeHash(dll)).ToLowerInvariant();
            File.AppendAllText(Path.Combine(output, Environment.ProcessId + ".tsv"),
                string.Join("\t", hash, assembly.Location) + Environment.NewLine);
        }

        AppDomain.CurrentDomain.AssemblyLoad += (_, args) => Record(args.LoadedAssembly);
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            Record(assembly);
    }
}
