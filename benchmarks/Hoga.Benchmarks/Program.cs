using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;

var config = ManualConfig.Create(DefaultConfig.Instance).AddColumn(StatisticColumn.OperationsPerSecond);
var summaries = BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, config).ToArray();
return summaries.All(summary => !summary.HasCriticalValidationErrors &&
    summary.Reports.All(report => report.Success)) ? 0 : 1;
