using Benchmark.Base.Models;
using Benchmark.Base.Operations;
namespace Benchmark;
internal class TestRunner {
    public static TestResult RunAll<T>(TestOptions options, TestData testData) where T : ITester {
        var tester = Activator.CreateInstance<T>();
        var result = new TestResult(typeof(T).Name);
        return result;
    }
}
