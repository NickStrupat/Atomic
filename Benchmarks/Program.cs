using BenchmarkDotNet.Running;
using Benchmarks;

// `dotnet run -c Release` measures one uncontended operation at a time, per category of T.
// `dotnet run -c Release -- contention` measures throughput with several threads on one cell.
// `dotnet run -c Release -- gc` measures what a cell's allocations cost the rest of the process.
if (args is ["contention"])
	return Contention.Run();
if (args is ["gc"])
	return GcCost.Run();

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
return 0;

public partial class Program;
