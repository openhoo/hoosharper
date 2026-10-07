using System.Reflection;
using BenchmarkDotNet.Running;
using HooSharper.Performance;

var configuration = typeof(Program).Assembly
    .GetCustomAttribute<AssemblyConfigurationAttribute>()?
    .Configuration;
if (!string.Equals(configuration, "Release", StringComparison.Ordinal))
{
    throw new InvalidOperationException(
        $"HooSharper.Performance must be run in Release configuration; current configuration is '{configuration ?? "unknown"}'.");
}

if (args is ["--validate-fixtures"])
{
    await FixtureValidation.RunAsync();
    return;
}

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
