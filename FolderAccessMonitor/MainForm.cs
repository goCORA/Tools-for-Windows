using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace FolderAccessMonitor;
public sealed class MainForm : Form
{
    private AppSettings settings = new();
    private readonly ComboBox folder = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown, MaxDropDownItems = 15 };
    private readonly CheckBox recursive = new() { Text = "Include subfolders", Checked = true, AutoSize = true };
    private readonly Button start = new() { Text = "Start monitoring", AutoSize = true };
    private readonly Button stop = new() { Text = "Stop", Enabled = false, AutoSize = true };
    private readonly Label status = new() { AutoSize = true, Text = "Ready. Administrator rights are required to start monitoring." };
    private readonly DataGridView processes = CreateGrid();
    private readonly DataGridView events = CreateGrid();
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 250 };
    private readonly Dictionary<int, DataGridViewRow> rows = new();
    private readonly Dictionary<int, (string Name, string Exe)> metadata = new();
    private readonly CheckBox pause = new() { Text = "Pause", AutoSize = true };
    private readonly TextBox filter = new() { Width = 220, Tag = "Filter (process, operation, path)" };
    private readonly TextBox exclusionFilter = new() { Width = 220, Tag = "Exclude (comma-separated)" };
    private readonly CheckBox linkSelection = new() { Text = "Link process to log", Checked = true, AutoSize = true };
    private int? linkedPid;
    private static readonly Color LinkedColor = Color.FromArgb(222, 236, 255);
    private string[] excludeTerms = [];
    private Mutex? primaryLock; // held by the first running instance only; that instance owns the saved window placement
    private readonly HashSet<string> excluded = new(StringComparer.OrdinalIgnoreCase);
    private readonly CheckBox beep = new() { Text = "Sound on new process", AutoSize = true };
    private readonly Dictionary<int, DateTime> newSince = new();
    private DateTime startedAt;
    private int newCount;
    private static readonly TimeSpan Baseline = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HighlightFor = TimeSpan.FromSeconds(15);
    private readonly Queue<AccessEvent> buffer = new();
    private readonly System.Windows.Forms.Timer filterTimer = new() { Interval = 300 };
    private const int MaxBuffered = 50000;
    private StreamWriter? recorder;
    private MonitorService? monitor;
    private long total;
    private long discarded;
    private const int MaxEvents = 5000;

    public MainForm()
    {
        Text = "FolderAccessMonitor";
        Icon = new Icon(Path.Combine(AppContext.BaseDirectory, "app_icon.ico"));
        Size = new(1250, 850);
        MinimumSize = new(850, 600);
        StartPosition = FormStartPosition.CenterScreen;
        primaryLock = new Mutex(true, @"Local\FolderAccessMonitor.Primary", out bool isPrimary);
        if (!isPrimary) { primaryLock.Dispose(); primaryLock = null; }
        LoadSettings();
        Font = new Font("Segoe UI", 10);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), RowCount = 6, ColumnCount = 1 };
        layout.RowStyles.Add(new(SizeType.Absolute, 40));
        layout.RowStyles.Add(new(SizeType.Absolute, 46));
        layout.RowStyles.Add(new(SizeType.Absolute, 40));
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.Absolute, 40));
        layout.RowStyles.Add(new(SizeType.Absolute, 40));
        var pathBar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3 };
        pathBar.ColumnStyles.Add(new(SizeType.Absolute, 64));
        pathBar.ColumnStyles.Add(new(SizeType.Percent, 100));
        pathBar.ColumnStyles.Add(new(SizeType.Absolute, 100));
        pathBar.Controls.Add(new Label { Text = "Folder", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        pathBar.Controls.Add(folder, 1, 0);
        LoadHistory();
        folder.Validated += (_, _) => CommitTypedFolder();
        folder.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && !folder.DroppedDown) CommitTypedFolder();
            // Delete removes the highlighted entry only while the list is open; otherwise it edits text as usual.
            if (e.KeyCode != Keys.Delete || !folder.DroppedDown || folder.SelectedIndex < 0) return;
            int index = folder.SelectedIndex;
            string removed = (string)folder.Items[index]!;
            folder.Items.RemoveAt(index);
            ApplyHistory(SharedFiles.UpdateHistory(l => l.RemoveAll(h => string.Equals(h, removed, StringComparison.OrdinalIgnoreCase))));
            folder.Text = "";
            if (folder.Items.Count > 0) folder.SelectedIndex = Math.Min(index, folder.Items.Count - 1);
            e.Handled = e.SuppressKeyPress = true;
        };
        var browse = new Button { Text = "Browse…", Dock = DockStyle.Fill };
        browse.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog();
            string current = folder.Text.Trim();
            string? lastBrowse = SharedFiles.ReadSettings().LastBrowse;
            if (Directory.Exists(current)) dialog.InitialDirectory = current;
            else if (Directory.Exists(lastBrowse)) dialog.InitialDirectory = lastBrowse;
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            SharedFiles.UpdateSettings(s => s.LastBrowse = dialog.SelectedPath);
            RememberFolder(dialog.SelectedPath);
        };
        pathBar.Controls.Add(browse, 2, 0);
        layout.Controls.Add(pathBar, 0, 0);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill };
        actions.Controls.AddRange([start, stop, recursive]);
        var elevate = new Button { Text = "Restart as administrator", AutoSize = true, Visible = !Program.IsAdmin };
        elevate.Click += (_, _) => { try { Program.Elevate(); Close(); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "Elevation cancelled or failed"); } };
        actions.Controls.Add(elevate);
        actions.Controls.AddRange([pause, filter, exclusionFilter, beep, linkSelection]);
        foreach (Control c in actions.Controls) c.Anchor = AnchorStyles.Left; // centre every item vertically in the row
        var menu = new ContextMenuStrip();
        var exclude = new ToolStripMenuItem("Hide events from this process");
        exclude.Click += (_, _) => ExcludeSelectedProcess();
        var reset = new ToolStripMenuItem("Show all hidden processes");
        reset.Click += (_, _) => { excluded.Clear(); status.Text = "Hidden processes cleared."; };
        menu.Items.AddRange([exclude, reset]);
        processes.ContextMenuStrip = menu;
        filter.TextChanged += (_, _) => { filterTimer.Stop(); filterTimer.Start(); };
        exclusionFilter.TextChanged += (_, _) =>
        {
            excludeTerms = exclusionFilter.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            filterTimer.Stop(); filterTimer.Start();
        };
        processes.SelectionChanged += (_, _) => { if (processes.Focused) ApplyLink(scroll: true); };
        linkSelection.CheckedChanged += (_, _) => ApplyLink(scroll: linkSelection.Checked);
        events.CellFormatting += (_, e) =>
        {
            if (linkedPid is int pid && e.RowIndex >= 0 && events.Rows[e.RowIndex].Cells[2].Value is int rowPid && rowPid == pid) e.CellStyle!.BackColor = LinkedColor;
        };
        KeepHintVisible(filter);
        KeepHintVisible(exclusionFilter);
        filterTimer.Tick += (_, _) => { filterTimer.Stop(); RebuildViews(); };
        layout.Controls.Add(actions, 0, 1);
        layout.Controls.Add(new Label { Text = "Processes observed since Start • Active = activity within the last 5 seconds", AutoSize = true }, 0, 2);
        var split = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
        split.RowStyles.Add(new(SizeType.Percent, 42));
        split.RowStyles.Add(new(SizeType.Percent, 58));
        split.Controls.Add(processes, 0, 0);
        split.Controls.Add(events, 0, 1);
        layout.Controls.Add(split, 0, 3);
        var tools = new FlowLayoutPanel { Dock = DockStyle.Fill };
        var clear = new Button { Text = "Clear results", AutoSize = true };
        clear.Click += (_, _) => ClearResults();
        var export = new Button { Text = "Export event CSV…", AutoSize = true };
        export.Click += (_, _) => Export();
        var exportProcesses = new Button { Text = "Export process CSV…", AutoSize = true };
        exportProcesses.Click += (_, _) => ExportGrid(processes, "folder-processes-");
        var record = new Button { Text = "Record to CSV…", AutoSize = true };
        record.Click += (_, _) => ToggleRecording(record);
        tools.Controls.AddRange([clear, export, exportProcesses, record]);
        layout.Controls.Add(tools, 0, 4);
        layout.Controls.Add(status, 0, 5);
        Controls.Add(layout);
        AddColumns(processes, "Process", "App / description", "PID", "Activity", "Events", "Read bytes", "Written bytes", "Last seen", "Last operation", "Last path", "Executable");
        AddColumns(events, "Time", "Process", "PID", "Operation", "Path", "Bytes");
        foreach (DataGridViewColumn c in processes.Columns) c.SortMode = DataGridViewColumnSortMode.Automatic;
        processes.Columns[9].Width = 300;
        events.Columns[4].Width = 450;
        start.Click += (_, _) => StartMonitor();
        stop.Click += (_, _) => StopMonitor();
        timer.Tick += (_, _) => Drain();
        timer.Start();
        FormClosing += (_, _) => { filterTimer.Stop(); SaveSettings(); ReleasePrimary(); StopRecording(); timer.Stop(); monitor?.Dispose(); };
    }
    private static DataGridView CreateGrid() => new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None, SelectionMode = DataGridViewSelectionMode.FullRowSelect, BackgroundColor = Color.White, BorderStyle = BorderStyle.FixedSingle };
    private static void AddColumns(DataGridView grid, params string[] names)
    {
        foreach (var name in names) grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = name, Name = name, Width = 140, SortMode = DataGridViewColumnSortMode.NotSortable });
    }
    private void StartMonitor()
    {
        if (!Program.IsAdmin) { MessageBox.Show(this, "Use Restart as administrator, then start monitoring.", "Administrator required"); return; }
        try
        {
            string path = Path.GetFullPath(folder.Text.Trim());
            if (!Directory.Exists(path)) throw new DirectoryNotFoundException("Select an existing folder.");
            folder.Text = path;
            RememberFolder(path);
            ClearResults();
            startedAt = DateTime.Now;
            monitor = new MonitorService();
            monitor.Failed += ex => { if (!IsDisposed && IsHandleCreated) BeginInvoke(() => { StopMonitor(); MessageBox.Show(this, ex.Message, "Trace failed"); }); };
            monitor.Start(path, recursive.Checked);
            start.Enabled = false; stop.Enabled = true; folder.Enabled = recursive.Enabled = false;
            status.Text = "Monitoring " + path;
        }
        catch (Exception ex) { monitor?.Dispose(); monitor = null; MessageBox.Show(this, ex.Message, "Unable to start monitoring"); }
    }
    private void CommitTypedFolder()
    {
        try
        {
            string text = folder.Text.Trim();
            if (text.Length == 0 || !Directory.Exists(text)) return;
            string path = Path.GetFullPath(text);
            if (!folder.Items.Cast<string>().Any(i => string.Equals(i, path, StringComparison.OrdinalIgnoreCase))) RememberFolder(path);
        }
        catch { }
    }
    private void LoadSettings()
    {
        try
        {
            settings = SharedFiles.ReadSettings();
            var bounds = new Rectangle(settings.X, settings.Y, settings.Width, settings.Height);
            if (bounds.Width < MinimumSize.Width || bounds.Height < MinimumSize.Height || !Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(bounds))) return;
            StartPosition = FormStartPosition.Manual;
            Bounds = settings.Maximized ? bounds : CascadeFromOtherInstances(bounds);
            if (settings.Maximized) WindowState = FormWindowState.Maximized;
        }
        catch { }
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr handle, out Rectangle rect);
    // Win32 RECT is (left, top, right, bottom); only left/top are read, so Rectangle's layout is reinterpreted deliberately.
    private static Rectangle CascadeFromOtherInstances(Rectangle bounds)
    {
        try
        {
            var tops = new List<Point>();
            using var self = Process.GetCurrentProcess();
            foreach (var other in Process.GetProcessesByName(self.ProcessName))
            {
                using (other)
                {
                    try
                    {
                        if (other.Id == self.Id || other.MainWindowHandle == IntPtr.Zero || !GetWindowRect(other.MainWindowHandle, out var r)) continue;
                        tops.Add(new Point(r.X, r.Y));
                    }
                    catch { }
                }
            }
            var moved = bounds;
            for (int i = 0; i < 10 && tops.Any(t => Math.Abs(t.X - moved.X) <= 10 && Math.Abs(t.Y - moved.Y) <= 10); i++) moved.Offset(32, 32);
            return Screen.AllScreens.Any(s => s.WorkingArea.Contains(moved.Location)) ? moved : bounds;
        }
        catch { return bounds; }
    }
    private void ReleasePrimary()
    {
        try { primaryLock?.ReleaseMutex(); primaryLock?.Dispose(); } catch { }
        primaryLock = null;
    }
    private void SaveSettings()
    {
        if (primaryLock == null) return;
        var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        bool maximized = WindowState == FormWindowState.Maximized;
        SharedFiles.UpdateSettings(s => { s.X = b.X; s.Y = b.Y; s.Width = b.Width; s.Height = b.Height; s.Maximized = maximized; });
    }
    private void LoadHistory() => folder.Items.AddRange(SharedFiles.ReadHistory().Cast<object>().ToArray());
    // Replaces the list with the merged history only when another instance changed it, keeping the typed text.
    private void ApplyHistory(List<string> merged)
    {
        if (folder.Items.Cast<string>().SequenceEqual(merged)) return;
        string text = folder.Text;
        folder.Items.Clear();
        folder.Items.AddRange(merged.Cast<object>().ToArray());
        folder.Text = text;
    }
    private void RememberFolder(string path)
    {
        ApplyHistory(SharedFiles.UpdateHistory(l =>
        {
            l.RemoveAll(h => string.Equals(h, path, StringComparison.OrdinalIgnoreCase));
            l.Insert(0, path);
        }));
        folder.Text = path;
    }
    private void StopMonitor()
    {
        var old = monitor;
        if (old == null) return;
        old.Dispose();
        Drain();
        monitor = null;
        start.Enabled = true; stop.Enabled = false; folder.Enabled = recursive.Enabled = true;
        status.Text = $"Stopped • {total:N0} events observed • {discarded:N0} older log rows removed";
    }
    private void ExcludeSelectedProcess()
    {
        if (processes.CurrentRow?.Cells[0].Value is not string name) return;
        excluded.Add(name);
        foreach (var pid in rows.Where(r => string.Equals((string)r.Value.Cells[0].Value!, name, StringComparison.OrdinalIgnoreCase)).Select(r => r.Key).ToList())
        {
            processes.Rows.Remove(rows[pid]);
            rows.Remove(pid);
        }
        var kept = buffer.Where(e => !string.Equals(e.Process, name, StringComparison.OrdinalIgnoreCase)).ToList();
        buffer.Clear();
        foreach (var e in kept) buffer.Enqueue(e);
        RebuildViews();
        status.Text = $"Hiding events from {name} (right-click → Show all hidden processes to undo).";
    }
    // Highlights the selected process's events in the log and centres its most recent one.
    private void ApplyLink(bool scroll)
    {
        linkedPid = linkSelection.Checked && processes.CurrentRow?.Cells[2].Value is int pid ? pid : null;
        events.Invalidate();
        if (linkedPid is not int target || !scroll) return;
        for (int i = events.Rows.Count - 1; i >= 0; i--)
        {
            if (events.Rows[i].Cells[2].Value is not int rowPid || rowPid != target) continue;
            events.FirstDisplayedScrollingRowIndex = Math.Max(0, i - Math.Max(1, events.DisplayedRowCount(false)) / 2);
            return;
        }
    }
    private void SetRowVisible(DataGridViewRow row, bool visible)
    {
        if (row.Visible == visible) return;
        if (!visible && processes.CurrentCell?.OwningRow == row) processes.CurrentCell = null;
        row.Visible = visible;
    }
    // Rebuilds both grids from the in-memory buffer so a filter change applies to rows already seen.
    private void RebuildViews()
    {
        var matching = buffer.Where(MatchesFilter).ToList();
        var shown = matching.Count > MaxEvents ? matching.GetRange(matching.Count - MaxEvents, MaxEvents) : matching;
        var newRows = new DataGridViewRow[shown.Count];
        for (int i = 0; i < newRows.Length; i++)
        {
            var e = shown[i];
            var row = new DataGridViewRow();
            row.CreateCells(events, e.Time.ToString("HH:mm:ss.fff"), e.Process, e.ProcessId, e.Operation, e.Path, e.Bytes);
            newRows[i] = row;
        }
        events.SuspendLayout();
        events.Rows.Clear();
        events.Rows.AddRange(newRows);
        if (events.Rows.Count > 0) events.FirstDisplayedScrollingRowIndex = events.Rows.Count - 1;
        events.ResumeLayout();
        bool filtering = FilterActive;
        var pids = filtering ? matching.Select(e => e.ProcessId).ToHashSet() : null;
        foreach (var (pid, row) in rows) SetRowVisible(row, pids == null || pids.Contains(pid));
        if (monitor == null) status.Text = filtering ? $"Filter: {matching.Count:N0} of {buffer.Count:N0} buffered events match." : status.Text;
    }
    private bool MatchesFilter(AccessEvent e)
    {
        string text = filter.Text.Trim();
        if (text.Length > 0 && !Contains(e, text)) return false;
        foreach (var term in excludeTerms) if (Contains(e, term)) return false;
        return true;
    }
    private static bool Contains(AccessEvent e, string term) => e.Process.Contains(term, StringComparison.OrdinalIgnoreCase) || e.Operation.Contains(term, StringComparison.OrdinalIgnoreCase) || e.Path.Contains(term, StringComparison.OrdinalIgnoreCase);
    private bool FilterActive => filter.Text.Trim().Length > 0 || excludeTerms.Length > 0;
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wParam, string text);
    // Uses the native cue banner alone (PlaceholderText would draw a second copy on top) so the hint stays visible while the box is empty, even when focused.
    private static void KeepHintVisible(TextBox box)
    {
        void Apply() => SendMessage(box.Handle, 0x1501 /* EM_SETCUEBANNER */, 1, (string)box.Tag!);
        box.HandleCreated += (_, _) => Apply();
        if (box.IsHandleCreated) Apply();
    }
    private void ClearResults() { newSince.Clear(); newCount = 0; buffer.Clear(); rows.Clear(); processes.Rows.Clear(); events.Rows.Clear(); total = discarded = 0; }
    private void Drain()
    {
        int batch = 0;
        bool ring = false;
        var pending = new List<DataGridViewRow>();
        while (monitor != null && !pause.Checked && batch++ < 2000 && monitor.TryRead(out var item))
        {
            var e = item!;
            if (excluded.Contains(e.Process)) continue;
            total++;
            bool newProcess = false;
            if (!rows.TryGetValue(e.ProcessId, out var row))
            {
                newProcess = true;
                string name = e.Process, exe = "Unavailable";
                try { using var p = Process.GetProcessById(e.ProcessId); name = p.MainModule?.FileVersionInfo.FileDescription ?? p.ProcessName; exe = p.MainModule?.FileName ?? exe; } catch { }
                metadata[e.ProcessId] = (name, exe);
                int index = processes.Rows.Add(e.Process, name, e.ProcessId, "Active", 0L, 0L, 0L, e.Time, e.Operation, e.Path, exe);
                row = processes.Rows[index]; rows.Add(e.ProcessId, row);
                // The first seconds after Start only establish a baseline; later newcomers are highlighted.
                if (DateTime.Now - startedAt > Baseline)
                {
                    row.DefaultCellStyle.BackColor = Color.LemonChiffon;
                    newSince[e.ProcessId] = DateTime.Now;
                    newCount++;
                    ring = true;
                }
            }
            row.Cells[4].Value = (long)row.Cells[4].Value! + 1;
            if (e.Operation == "Read") row.Cells[5].Value = (long)row.Cells[5].Value! + e.Bytes;
            if (e.Operation == "Write") row.Cells[6].Value = (long)row.Cells[6].Value! + e.Bytes;
            row.Cells[7].Value = e.Time; row.Cells[8].Value = e.Operation; row.Cells[9].Value = e.Path;
            recorder?.WriteLine(string.Join(",", Quote(e.Time.ToString("yyyy-MM-dd HH:mm:ss.fff")), Quote(e.Process), Quote(e.ProcessId), Quote(e.Operation), Quote(e.Path), Quote(e.Bytes)));
            buffer.Enqueue(e);
            if (buffer.Count > MaxBuffered) buffer.Dequeue();
            if (MatchesFilter(e))
            {
                var eventRow = new DataGridViewRow();
                eventRow.CreateCells(events, e.Time.ToString("HH:mm:ss.fff"), e.Process, e.ProcessId, e.Operation, e.Path, e.Bytes);
                pending.Add(eventRow);
                SetRowVisible(row, true);
            }
            else if (newProcess && FilterActive) SetRowVisible(row, false);
        }
        if (pending.Count > 0)
        {
            // One bulk add and one trim per tick keeps the UI responsive at thousands of events per second.
            events.SuspendLayout();
            events.Rows.AddRange(pending.ToArray());
            int overflow = events.Rows.Count - MaxEvents;
            if (overflow > 0)
            {
                for (int i = 0; i < overflow; i++) events.Rows.RemoveAt(0);
                discarded += overflow;
            }
            events.ResumeLayout();
        }
        if (ring && beep.Checked) System.Media.SystemSounds.Asterisk.Play();
        try { recorder?.Flush(); } catch (Exception ex) { StopRecording(); status.Text = "Recording stopped: " + ex.Message; }
        foreach (var (pid, started) in newSince.ToList())
        {
            if (DateTime.Now - started < HighlightFor) continue;
            newSince.Remove(pid);
            if (rows.TryGetValue(pid, out var old)) old.DefaultCellStyle.BackColor = Color.Empty;
        }
        foreach (var row in rows.Values) row.Cells[3].Value = monitor != null && DateTime.Now - (DateTime)row.Cells[7].Value! < TimeSpan.FromSeconds(5) ? "Active" : "Idle";
        if (monitor != null) status.Text = (pause.Checked ? "PAUSED (events buffer, up to 20,000) • " : "") + $"Monitoring • {rows.Count:N0} processes (new: {newCount:N0}) • {total:N0} events • Dropped: {monitor.Dropped:N0} • ETW lost: {monitor.Lost:N0} • Log keeps latest {MaxEvents:N0}";
    }
    private static string Quote(object? value) { string s = value?.ToString() ?? ""; if (s.Length > 0 && "=+-@".Contains(s[0])) s = "'" + s; return "\"" + s.Replace("\"", "\"\"") + "\""; }
    private static readonly string[] EventHeaders = ["Time", "Process", "PID", "Operation", "Path", "Bytes"];
    private void Export() => ExportGrid(events, "folder-access-");
    private void ExportGrid(DataGridView grid, string prefix)
    {
        using var dialog = new SaveFileDialog { Filter = "CSV files (*.csv)|*.csv", FileName = prefix + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".csv" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            using var writer = new StreamWriter(dialog.FileName, false, new UTF8Encoding(true));
            writer.WriteLine(string.Join(",", grid.Columns.Cast<DataGridViewColumn>().Select(c => Quote(c.HeaderText))));
            foreach (DataGridViewRow row in grid.Rows) writer.WriteLine(string.Join(",", row.Cells.Cast<DataGridViewCell>().Select(c => Quote(c.Value))));
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Export failed"); }
    }
    private void ToggleRecording(Button button)
    {
        if (recorder != null) { StopRecording(); button.Text = "Record to CSV…"; return; }
        using var dialog = new SaveFileDialog { Filter = "CSV files (*.csv)|*.csv", FileName = "folder-log-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".csv" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            recorder = new StreamWriter(dialog.FileName, false, new UTF8Encoding(true));
            recorder.WriteLine(string.Join(",", EventHeaders.Select(h => Quote(h))));
            button.Text = "Stop recording";
            status.Text = "Recording every event to " + dialog.FileName + " (ignores the log filter and the 5,000-row limit).";
        }
        catch (Exception ex) { recorder = null; MessageBox.Show(this, ex.Message, "Unable to record"); }
    }
    private void StopRecording() { try { recorder?.Dispose(); } catch { } recorder = null; }
}



