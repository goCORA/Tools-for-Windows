using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace FolderAccessMonitor;

public record AccessEvent(DateTime Time, int ProcessId, string Process, string Operation, string Path, long Bytes);

public static class FolderFilter
{
    public static bool Contains(string folder, string path, bool recursive)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string root = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(folder));
        string candidate = path.Replace('/', '\\');
        if (candidate.Equals(root, StringComparison.OrdinalIgnoreCase)) return true;
        string prefix = root.EndsWith('\\') ? root : root + "\\";
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        return recursive || candidate.AsSpan(prefix.Length).IndexOf('\\') < 0;
    }
}

/// <summary>Converts kernel device paths (\Device\HarddiskVolume3\...) from raw ETW events to drive-letter paths.</summary>
public static class DevicePathMapper
{
    [DllImport("kernel32.dll", EntryPoint = "QueryDosDeviceW", CharSet = CharSet.Unicode)]
    private static extern uint QueryDosDevice(string deviceName, [Out] char[] target, int max);

    public static (string Device, string Drive)[] Build()
    {
        var map = new List<(string, string)>();
        var buffer = new char[2048];
        foreach (var drive in DriveInfo.GetDrives())
        {
            string letter = drive.Name.TrimEnd('\\');
            uint n = QueryDosDevice(letter, buffer, buffer.Length);
            if (n == 0) continue;
            string device = new string(buffer).Split((char)0)[0];
            if (device.Length > 0) map.Add((device, letter));
        }
        map.Add((@"\Device\Mup", @"\"));
        map.Add((@"\Device\LanmanRedirector", @"\"));
        return [.. map];
    }

    public static string ToDrivePath(string path, (string Device, string Drive)[] map)
    {
        foreach (var (device, drive) in map)
        {
            if (path.Length >= device.Length && path.StartsWith(device, StringComparison.OrdinalIgnoreCase) && (path.Length == device.Length || path[device.Length] == '\\'))
                return drive + path.Substring(device.Length);
        }
        return path;
    }
}

public sealed class MonitorService : IDisposable
{
    private TraceEventSession? session;
    private Task? worker;
    private readonly ConcurrentQueue<AccessEvent> queue = new();
    private int queued;
    private long dropped;
    public long Dropped => Interlocked.Read(ref dropped);
    public int Lost => session?.Source.EventsLost ?? 0;
    public event Action<Exception>? Failed;

    public void Start(string folder, bool recursive)
    {
        StopOrphanedSessions();
        var deviceMap = DevicePathMapper.Build();
        session = new TraceEventSession($"{SessionPrefix}{Environment.ProcessId}-{Guid.NewGuid():N}");
        session.StopOnDispose = true;
        void Record(int pid, string process, string operation, string path, long bytes = 0)
        {
            path = DevicePathMapper.ToDrivePath(path, deviceMap);
            if (!FolderFilter.Contains(folder, path, recursive)) return;
            if (Interlocked.Increment(ref queued) > 20000)
            {
                Interlocked.Decrement(ref queued);
                Interlocked.Increment(ref dropped);
                return;
            }
            queue.Enqueue(new(DateTime.Now, pid, string.IsNullOrEmpty(process) ? "PID " + pid : process, operation, path, bytes));
        }
        try
        {
            // The kernel provider must be enabled before touching session.Source, which otherwise opens the session.
            session.EnableKernelProvider(KernelTraceEventParser.Keywords.DiskFileIO | KernelTraceEventParser.Keywords.FileIO | KernelTraceEventParser.Keywords.FileIOInit | KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.Thread);
            var source = session.Source;
            var kernel = source.Kernel;
            kernel.FileIOCreate += e => Record(e.ProcessID, e.ProcessName, "Open / create", e.FileName);
            kernel.FileIORead += e => Record(e.ProcessID, e.ProcessName, "Read", e.FileName, e.IoSize);
            kernel.FileIOWrite += e => Record(e.ProcessID, e.ProcessName, "Write", e.FileName, e.IoSize);
            kernel.FileIOClose += e => Record(e.ProcessID, e.ProcessName, "Close", e.FileName);
            kernel.FileIODelete += e => Record(e.ProcessID, e.ProcessName, "Delete request", e.FileName);
            kernel.FileIORename += e => Record(e.ProcessID, e.ProcessName, "Rename request", e.FileName);
            kernel.FileIOQueryInfo += e => Record(e.ProcessID, e.ProcessName, "Query information", e.FileName);
            kernel.FileIOSetInfo += e => Record(e.ProcessID, e.ProcessName, "Set information", e.FileName);
            kernel.FileIODirEnum += e => Record(e.ProcessID, e.ProcessName, "Directory enumeration", e.FileName);
            kernel.FileIOFlush += e => Record(e.ProcessID, e.ProcessName, "Flush", e.FileName);
            worker = Task.Run(() => { try { source.Process(); } catch (Exception ex) { Failed?.Invoke(ex); } });
        }
        catch { Dispose(); throw; }
    }
    private const string SessionPrefix = "FolderAccessMonitor-";
    // A session is orphaned when the instance that created it (the PID in its name) is no longer running.
    private static bool IsOrphaned(string name)
    {
        string[] parts = name.Substring(SessionPrefix.Length).Split('-');
        if (parts.Length < 2 || !int.TryParse(parts[0], out int pid)) return true;
        if (pid == Environment.ProcessId) return false;
        try
        {
            using var owner = System.Diagnostics.Process.GetProcessById(pid);
            using var self = System.Diagnostics.Process.GetCurrentProcess();
            return !string.Equals(owner.ProcessName, self.ProcessName, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return true; }
        catch { return false; }
    }
    // A killed or crashed instance leaves its system-wide ETW session running; stop any such leftovers.
    private static void StopOrphanedSessions()
    {
        try
        {
            foreach (var name in TraceEventSession.GetActiveSessionNames().Where(n => n.StartsWith(SessionPrefix, StringComparison.OrdinalIgnoreCase) && IsOrphaned(n)))
            {
                try { using var old = TraceEventSession.GetActiveSession(name); old?.Stop(); } catch { }
            }
        }
        catch { }
    }
    public bool TryRead(out AccessEvent? item)
    {
        if (!queue.TryDequeue(out item)) return false;
        Interlocked.Decrement(ref queued);
        return true;
    }
    public void Dispose()
    {
        var old = session;
        session = null;
        old?.Dispose();
        if (worker != null && Task.CurrentId != worker.Id) worker.Wait(TimeSpan.FromSeconds(3));
    }
}

