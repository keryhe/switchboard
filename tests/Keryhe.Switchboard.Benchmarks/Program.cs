using System.Reflection;
using BenchmarkDotNet.Running;

// Plan decision D33: microbenchmarks only — per-operation CPU/allocation cost on the hot path, no
// sockets, no concurrent load (that's tests/Keryhe.Switchboard.LoadHarness, Slice 5). Run with:
//   dotnet run -c Release --project tests/Keryhe.Switchboard.Benchmarks
BenchmarkSwitcher.FromAssembly(Assembly.GetExecutingAssembly()).Run(args);
