using Relatude.DB.Common;
using System.Text;

namespace Benchmark.Tester;
internal class TestResult(string name, TimeSpan duration, int operations) {
    public string TestName { get; } = name;
    public TimeSpan Duration { get; } = duration;
    public int Operations { get; } = operations;
    public double OperationsPerSecond => Operations / Duration.TotalSeconds;
}
internal class TestReport(string name) {
    public string Name { get; set; } = name;
    public List<TestResult> Results { get; set; } = [];
    public long TotalFileSize { get; set; }
    public override string? ToString() {
        var sb = new StringBuilder();
        foreach (var result in Results) {
            sb.AppendLine(result.TestName);
            sb.AppendLine("Duration: " + result.Duration.TotalMilliseconds.To1000N() + "ms");
            sb.AppendLine("Operations: " + result.Operations.To1000N());
            sb.AppendLine("PerSecond: " + result.OperationsPerSecond.To1000N());
        }
        sb.AppendLine("Total duration: " + Results.Sum(r => r.Duration.TotalMilliseconds).To1000N()+"ms");
        sb.AppendLine("Total file size: " + TotalFileSize.ToByteString());
        return sb.ToString();
    }
}

