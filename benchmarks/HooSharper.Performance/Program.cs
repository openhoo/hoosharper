using BenchmarkDotNet.Running;

#if DEBUG
#error HooSharper.Performance must be built and run in Release configuration.
#endif

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
