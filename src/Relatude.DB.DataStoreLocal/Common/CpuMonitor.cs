using System.Diagnostics;

public class CpuMonitor {
    DateTime _lastMeasurementTime;
    TimeSpan _lastProcessorTime;
    struct Reading(DateTime timestamp, double usage) {
        public DateTime Time { get; set; } = timestamp;
        public double Usage { get; set; } = usage;
    }
    Queue<Reading> _cpuUsageHistory = [];
    int _maxHistoryLength = 200;
    public CpuMonitor() {
        _lastMeasurementTime = DateTime.UtcNow;
        _lastProcessorTime = Process.GetCurrentProcess().TotalProcessorTime;
    }
    public double DequeCpuUsage() {
        var now = DateTime.UtcNow;
        var currentProcessorTime = Process.GetCurrentProcess().TotalProcessorTime;
        var deltaCpuTime = (currentProcessorTime - _lastProcessorTime).TotalMilliseconds;
        var deltaTime = (now - _lastMeasurementTime).TotalMilliseconds;
        // Update last measurement
        _lastMeasurementTime = now;
        _lastProcessorTime = currentProcessorTime;
        // Calculate the percentage of CPU usage across all cores
        var cpuUsagePerTimePerCore = deltaCpuTime / deltaTime / Environment.ProcessorCount;
        _cpuUsageHistory.Enqueue(new(now, cpuUsagePerTimePerCore));
        while (_cpuUsageHistory.Count > _maxHistoryLength) _cpuUsageHistory.Dequeue();
        return cpuUsagePerTimePerCore;
    }
    public double Estimate(TimeSpan timeSpan) {
        var cutoffTime = DateTime.UtcNow - timeSpan;
        var relevantUsages = _cpuUsageHistory.Where(e => e.Time >= cutoffTime).Select(e => e.Usage).ToList();
        if (relevantUsages.Count == 0) return 0.0;
        return Math.Round(relevantUsages.Average(), 2);
    }
}
