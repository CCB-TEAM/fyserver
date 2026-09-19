using System.Diagnostics;
using System.Runtime.InteropServices;

namespace fyserver.Services;

/// <summary>采集 Windows 整机与 FYServer 进程资源占用，并保存最近一段活动趋势。</summary>
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
        double? SystemCpuPercent,
        long? SystemMemoryUsedBytes,
        long? SystemMemoryTotalBytes,
        double? SystemMemoryPercent,
        long? DiskUsedBytes,
        double? DiskUsedPercent,
        IReadOnlyList<ActivityPoint> Activity);

    private const int MaxSamples = 72;
    private readonly object _gate = new();
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly Queue<ActivityPoint> _activity = new();
    private DateTime _lastCpuAt = DateTime.UtcNow;
    private TimeSpan _lastCpuTime;
    private ulong _idle, _kernel, _user;
    private bool _systemCpuReady;
    private double? _systemCpuPercent;
    private long _lastSystemSample;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile;
        public ulong TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    private void SampleSystemCpu()
    {
        if (!OperatingSystem.IsWindows()) return;
        var now = Stopwatch.GetTimestamp();
        if (_lastSystemSample != 0 && Stopwatch.GetElapsedTime(_lastSystemSample, now).TotalSeconds < 1)
            return;
        _lastSystemSample = now;
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
        {
            _systemCpuPercent = null;
            _systemCpuReady = false;
            return;
        }
        if (_systemCpuReady && kernel >= _kernel && user >= _user && idle >= _idle)
        {
            var total = (kernel - _kernel) + (user - _user);
            _systemCpuPercent = total == 0 ? null :
                Math.Clamp(100d * (1d - (double)(idle - _idle) / total), 0, 100);
        }
        _idle = idle; _kernel = kernel; _user = user;
        _systemCpuReady = true;
    }

    public ServerMetricsService()
    {
        _lastCpuTime = _process.TotalProcessorTime;
        SampleSystemCpu();
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
            SampleSystemCpu();
            long? systemMemoryUsed = null, systemMemoryTotal = null;
            double? systemMemoryPercent = null;
            var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
            if (OperatingSystem.IsWindows() && GlobalMemoryStatusEx(ref memory) && memory.TotalPhysical > 0)
            {
                systemMemoryTotal = (long)memory.TotalPhysical;
                systemMemoryUsed = (long)(memory.TotalPhysical - memory.AvailablePhysical);
                systemMemoryPercent = systemMemoryUsed * 100d / systemMemoryTotal;
            }

            var databaseBytes = GetDirectorySize(Path.GetFullPath("./faster-log"));
            long diskTotal = 0;
            long? diskUsed = null;
            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath("./faster-log"));
                if (!string.IsNullOrEmpty(root))
                {
                    var drive = new DriveInfo(root);
                    diskTotal = drive.TotalSize;
                    diskUsed = diskTotal - drive.TotalFreeSpace;
                }
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
                databaseBytes, diskTotal, diskPercent,
                _systemCpuPercent, systemMemoryUsed, systemMemoryTotal, systemMemoryPercent,
                diskUsed, diskTotal > 0 ? diskUsed * 100d / diskTotal : null, _activity.ToArray());
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
