using Benchmark.Tester;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Benchmark.Site.Tester;
public class Result(string testName) {
    public string TestName { get; } = testName;
    public bool Running { get; set; }
    public int Count { get; set; } = -1;
    public double DurationMs { get; set; }
}
public class Tester(string name, Result[] results) {
    public string TesterName { get; } = name;
    public Result[] Results { get; } = results;
}
// thread safe
public class Status {
    public static readonly Status Current = new();
    object _lock = new object();
    readonly Stopwatch _sw = new();
    Tester[] _testers = [];
    public bool _running;
    public bool Running { get { lock (_lock) return _running; } set { lock (_lock) _running = value; } }
    public Tester[] Testers { get { lock (_lock) return _testers; } }
    public void Initialize(string[] testerNames, string[] testNames) {
        lock (_lock) _testers = [.. testerNames.Select(n => new Tester(n, [.. testNames.Select(tn => new Result(tn))]))];

    }
    bool tryFindTest(string testerName, string testName, [MaybeNullWhen(false)] out Result result) {
        result = null;
        var tester = _testers.FirstOrDefault(t => t.TesterName == testerName);
        if (tester == null) return false;
        result = tester.Results.FirstOrDefault(r => r.TestName == testName);
        if (result == null) return false;
        return true;
    }
    public TimeSpan Elapsed() => _sw.Elapsed;
    string _testerName = null!;
    string _testName = null!;
    public void Start(string testerName, string testName) {
        lock (_lock) {
            _testerName = testerName;
            _testName = testName;
            if (!tryFindTest(testerName, testName, out var test)) return;
            test.Running = true;
        }
        _sw.Restart();
    }
    public void Complete(int count) {
        _sw.Stop();
        lock (_lock) {
            if (!tryFindTest(_testerName, _testName, out var test)) return;
            test.Running = false;
            test.Count = count;
            test.DurationMs = _sw.Elapsed.TotalMilliseconds;
        }
    }
}
