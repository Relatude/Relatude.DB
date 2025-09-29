namespace Benchmark;
internal class TestResult (string name) {
    public string TestName { get; } = name;
    public TimeSpan Duration { get; set; }
    public int Operations { get; set; }
    public double OperationsPerSecond => Operations / Duration.TotalSeconds;
}
internal class TestReport {
    public string Name { get; set; } = string.Empty;
    public List<TestResult> Results { get; set; } = [];
    public long TotalFileSize{ get; set; }
}

