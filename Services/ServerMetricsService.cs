using System.Diagnostics;

namespace fyserver.Services;

/// <summary>为管理后台采集当前进程资源占用，并保存最近一段活动趋势。</summary>
public sealed class ServerMetricsService
{
    public sealed record ActivityPoint(long Timestamp, int Online, int Matches, int Queued);
    public sealed record Snapshot(
        double CpuPercent,
        long MemoryUsedBytes,
        long MemoryTotalBytes,
        double MemoryPercent,
        long DatabaseBytes,
        long DiskTotalBytes,
        double DiskPercent,
        IReadOnlyList<ActivityPoint> Activity);

    private const int MaxSamples = 72;
    private readonly object _gate = new();
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly Queue<ActivityPoint> _activity = new();
    private DateTime _lastCpuAt = DateTime.UtcNow;
    private TimeSpan _lastCpuTime;

    public ServerMetricsService()
    {
        _lastCpuTime = _process.TotalProcessorTime;
    }

    public Snapshot Capture(int online, int matches, int queued)
    {
        lock (_gate)
        {
            _process.Refresh();
            var now = DateTime.UtcNow;
            var cpuTime = _process.TotalProcessorTime;
            var elapsedMs = Math.Max(1, (now - _lastCpuAt).TotalMilliseconds);
            var cpuMs = Math.Max(0, (cpuTime - _lastCpuTime).TotalMilliseconds);
            var cpuPercent = Math.Clamp(cpuMs / elapsedMs / Environment.ProcessorCount * 100d, 0d, 100d);
            _lastCpuAt = now;
            _lastCpuTime = cpuTime;

            var memoryUsed = _process.WorkingSet64;
            var memoryTotal = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            if (memoryTotal <= 0) memoryTotal = memoryUsed;
            var memoryPercent = memoryTotal == 0 ? 0 : Math.Clamp(memoryUsed * 100d / memoryTotal, 0d, 100d);

            var databaseBytes = GetDirectorySize(Path.GetFullPath("./faster-log"));
            long diskTotal = 0;
            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath("./faster-log"));
                if (!string.IsNullOrEmpty(root)) diskTotal = new DriveInfo(root).TotalSize;
            }
            catch { }
            var diskPercent = diskTotal == 0 ? 0 : Math.Clamp(databaseBytes * 100d / diskTotal, 0d, 100d);

            var point = new ActivityPoint(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), online, matches, queued);
            if (_activity.Count == 0 || point.Timestamp - _activity.Last().Timestamp >= 4_000)
            {
                _activity.Enqueue(point);
                while (_activity.Count > MaxSamples) _activity.Dequeue();
            }

            return new Snapshot(cpuPercent, memoryUsed, memoryTotal, memoryPercent,
                databaseBytes, diskTotal, diskPercent, _activity.ToArray());
        }
    }

    private static long GetDirectorySize(string path)
    {
        if (!Directory.Exists(path)) return 0;
        try
        {
            return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                .Sum(file =>
                {
                    try { return new FileInfo(file).Length; }
                    catch { return 0L; }
                });
        }
        catch { return 0; }
    }
}
