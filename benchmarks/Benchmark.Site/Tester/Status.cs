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
    HashSet<string>? _selectedTests = null;
    Tester[] _testers = [];
    public bool _running;
    public bool Running { get { lock (_lock) return _running; } set { lock (_lock) _running = value; } }
    public Tester[] Testers { get { lock (_lock) return _testers; } }
    public void Initialize(string[] testerNames, string[] testNames, TestOptions options) {
        lock (_lock) {
            _testers = [.. testerNames.Select(n => new Tester(n, [.. testNames.Select(tn => new Result(tn))]))];
            _duration = options.Duration;
            if (options.SelectedTests != null) {
                _selectedTests = [..options.SelectedTests];
            }
        }
    }
    bool tryFindTest(string testerName, string testName, [MaybeNullWhen(false)] out Result result) {
        result = null;
        var tester = _testers.FirstOrDefault(t => t.TesterName == testerName);
        if (tester == null) return false;
        result = tester.Results.FirstOrDefault(r => r.TestName == testName);
        if (result == null) return false;
        return true;
    }
    public bool KeepRunning() => _sw.Elapsed < _duration && !_excluded;
    string _testerName = null!;
    string _testName = null!;
    bool _excluded = false;
    TimeSpan _duration;
    public void Start(string testerName, string testName) {
        lock (_lock) {
            _testerName = testerName;
            _testName = testName;
            if (!tryFindTest(testerName, testName, out var test)) return;
            test.Running = true;
            _excluded = _selectedTests != null && !_selectedTests.Contains(_testName);
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
