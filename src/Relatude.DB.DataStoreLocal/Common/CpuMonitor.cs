using Relatude.DB.Common;
using System;
using System.Diagnostics;
using System.Threading.Tasks;

public class CpuMonitor {
    DateTime _lastMeasurementTime;
    TimeSpan _lastProcessorTime;
    Queue<Tuple<DateTime, double>> _cpuUsageHistory = [];
    int maxHistoryLength = 200;
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
        _cpuUsageHistory.Enqueue(Tuple.Create(now, cpuUsagePerTimePerCore));
        while (_cpuUsageHistory.Count > maxHistoryLength) _cpuUsageHistory.Dequeue();
        //Console.WriteLine("CPU Usage: " + Math.Round(cpuUsagePerTimePerCore * 100, 2) + "%");
        //Console.WriteLine("Delta CPU Time: "    + deltaCpuTime.To1000N() + " ms");
        //Console.WriteLine("Delta Time: "     + deltaTime.To1000N() + " ms");
        //Console.WriteLine("Delta Time: " + (100d*deltaCpuTime/deltaTime).To1000N() + " %");
        return cpuUsagePerTimePerCore;
    }
    public double Estimate(TimeSpan timeSpan) {
        var cutoffTime = DateTime.UtcNow - timeSpan;
        var relevantUsages = _cpuUsageHistory.Where(entry => entry.Item1 >= cutoffTime).Select(entry => entry.Item2).ToList();
        if (relevantUsages.Count == 0) return 0.0;
        return Math.Round(relevantUsages.Average(), 2);
    }
}
