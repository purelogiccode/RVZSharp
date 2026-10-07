using BenchmarkDotNet.Running;

// Run every benchmark:
//   dotnet run -c Release --project RVZSharp.Benchmarks
// Run one class or method (BenchmarkDotNet filter syntax):
//   dotnet run -c Release --project RVZSharp.Benchmarks -- --filter *Encode*
// List what is available without running anything:
//   dotnet run -c Release --project RVZSharp.Benchmarks -- --list flat
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
