using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.Emit;
using Volt.Benchmarks;

// Run all benchmarks:       dotnet run -c Release
// Run specific benchmark:   dotnet run -c Release -- --filter *JsonLanguage*
// List available:           dotnet run -c Release -- --list flat

// InProcess toolchain avoids the auto-generated project (which fails to
// resolve net10.0-windows), running benchmarks in the host process instead.
var config = ManualConfig.Create(DefaultConfig.Instance)
    .AddJob(Job.MediumRun
        .WithToolchain(InProcessEmitToolchain.Instance));

// WPF requires STA thread for types like DrawingVisual, FormattedText, etc.
var thread = new Thread(() =>
{
    BenchmarkSwitcher
        .FromAssembly(typeof(JsonLanguageBenchmarks).Assembly)
        .Run(args, config);
});
thread.SetApartmentState(ApartmentState.STA);
thread.Start();
thread.Join();
