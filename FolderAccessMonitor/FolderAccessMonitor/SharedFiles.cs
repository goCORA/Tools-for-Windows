using System.Text.Json;

namespace FolderAccessMonitor;

internal sealed class AppSettings
{
    public string? LastBrowse { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool Maximized { get; set; }
}

/// <summary>
/// Settings and folder history live in files shared by every running instance. Each update re-reads the
/// file, applies only that instance's change, and writes atomically, all under a machine-wide mutex,
/// so concurrent instances neither lose each other's changes nor expose a half-written file.
/// </summary>
internal static class SharedFiles
{
    private const int MaxHistory = 30;
    private static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FolderAccessMonitor");
    private static readonly string HistoryFile = Path.Combine(Dir, "folders.txt");
    private static readonly string SettingsFile = Path.Combine(Dir, "settings.json");

    private static bool WithLock(Action action)
    {
        using var mutex = new Mutex(false, @"Local\FolderAccessMonitor.Files");
        bool held = false;
        try
        {
            try { held = mutex.WaitOne(3000); }
            catch (AbandonedMutexException) { held = true; }
            if (!held) return false;
            action();
            return true;
        }
        finally { if (held) mutex.ReleaseMutex(); }
    }

    private static void AtomicWrite(string path, string text)
    {
        Directory.CreateDirectory(Dir);
        string temp = $"{path}.{Environment.ProcessId}.tmp";
        File.WriteAllText(temp, text);
        File.Move(temp, path, true);
    }

    public static List<string> ReadHistory()
    {
        try
        {
            if (!File.Exists(HistoryFile)) return [];
            return File.ReadAllLines(HistoryFile).Where(l => l.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxHistory).ToList();
        }
        catch { return []; }
    }

    /// <summary>Applies a change to the latest saved history and returns the merged result.</summary>
    public static List<string> UpdateHistory(Action<List<string>> change)
    {
        List<string> result = ReadHistory();
        try
        {
            WithLock(() =>
            {
                result = ReadHistory();
                change(result);
                result = result.Take(MaxHistory).ToList();
                AtomicWrite(HistoryFile, string.Join(Environment.NewLine, result) + Environment.NewLine);
            });
        }
        catch { }
        return result;
    }

    public static AppSettings ReadSettings()
    {
        try { return File.Exists(SettingsFile) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsFile)) ?? new() : new(); }
        catch { return new(); }
    }

    public static void UpdateSettings(Action<AppSettings> change)
    {
        try
        {
            WithLock(() =>
            {
                var current = ReadSettings();
                change(current);
                AtomicWrite(SettingsFile, JsonSerializer.Serialize(current));
            });
        }
        catch { }
    }
}
