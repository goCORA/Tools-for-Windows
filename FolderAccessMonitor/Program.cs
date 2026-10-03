using System.Diagnostics;
using System.Security.Principal;

namespace FolderAccessMonitor;
internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (args.Contains("--self-test"))
        {
            var cases = new (string path, bool recursive, bool expected)[] {
                (@"C:\Test\file.txt", false, true), (@"C:\Test\child\file.txt", false, false),
                (@"C:\Test\child\file.txt", true, true), (@"C:\Testing\file.txt", true, false),
                (@"c:\test\FILE.txt", true, true), (@"C:\Test", false, true), ("", true, false) };
            foreach (var c in cases) if (FolderFilter.Contains(@"C:\Test\", c.path, c.recursive) != c.expected) throw new Exception("Filter failed: " + c.path);
            if (!FolderFilter.Contains(@"C:\", @"C:\file.txt", false)) throw new Exception("Drive root failed");
            File.WriteAllText(args.Length > 1 ? args[1] : "self-test-result.txt", "PASS: 8 folder boundary/filter cases");
            return;
        }
        ApplicationConfiguration.Initialize();
        MainForm form; try { form = new MainForm(); } catch (Exception ex) { if (args.Contains("--gui-smoke")) File.WriteAllText(args.Length > 1 ? args[1] : "gui-smoke-result.txt", ex.ToString()); else MessageBox.Show(ex.ToString(), "Startup failed"); Environment.ExitCode = 1; return; }
        if (args.Contains("--gui-smoke"))
        {
            var timer = new System.Windows.Forms.Timer { Interval = 1200 };
            timer.Tick += (_, _) => { timer.Stop(); form.Close(); };
            form.Shown += (_, _) => timer.Start();
            Application.Run(form);
            File.WriteAllText(args.Length > 1 ? args[1] : "gui-smoke-result.txt", "PASS: main window constructed, shown and closed");
            timer.Dispose();
            return;
        }
        Application.Run(form);
    }
    public static bool IsAdmin => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    public static void Elevate()
    {
        Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" });
    }
}



