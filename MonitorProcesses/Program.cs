using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Forms;
namespace MonitorProcesses {
 static class Program {
  [STAThread] static void Main() { Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false); Application.Run(new MainForm()); }
 }
 public class MainForm : Form {
  readonly TabControl tabs = new TabControl { Dock = DockStyle.Fill };
  readonly TextBox filter = new TextBox { Width = 240 };
  readonly Label status = new Label { AutoSize = true }; readonly Label taskStatus = new Label { Dock=DockStyle.Bottom,Height=30,Text="Loading enabled tasks...",Padding=new Padding(5) };
  readonly DataGridView processes = Grid(), tray = Grid(), events = Grid(), tasks = Grid();
  readonly Timer timer = new Timer { Interval = 5000 };
  readonly Queue<string[]> history = new Queue<string[]>();
  List<string[]> processRows = new List<string[]>(), trayRows = new List<string[]>(), taskRows = new List<string[]>();
  bool refreshing, closing;
  IntPtr hook; Native.WinEvent callback; AutomationEventHandler notificationHandler;
  public MainForm() {
   Text = "MonitorProcesses"; Width = 1400; Height = 850; MinimumSize = new Size(900,550);
   var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(6) };
   var refresh = new Button { Text = "Refresh", AutoSize = true };
   var reload = new Button { Text = "Reload tasks", AutoSize = true };
   var export = new Button { Text = "Export current CSV", AutoSize = true };
   bar.Controls.AddRange(new Control[] { new Label { Text = "Search (try Corel):", AutoSize = true, Padding = new Padding(0,6,0,0) }, filter, refresh, reload, export, status });
   AddTab("Processes", processes); AddTab("Tray icons", tray); AddTab("Popup / notification activity", events); AddTab("Enabled scheduled tasks", tasks); tabs.TabPages[3].Controls.Add(taskStatus);
   var note = new Label { Dock = DockStyle.Bottom, Height = 52, Padding = new Padding(8), Text = "Monitoring starts when this app opens. Window activity includes ordinary windows. Tray/notification UI host is not the originating app. Expand hidden tray icons before refreshing. Search applies to every tab; export saves visible rows." };
   Controls.Add(tabs); Controls.Add(bar); Controls.Add(note);
   Columns(processes,"PID","Process","Publisher","Executable","Command line","Parent PID","Session","Window title","Memory MB");
   Columns(tray,"Icon / accessible label","UI host PID","UI host executable","Attribution");
   Columns(events,"Time","Kind","PID / UI host","Process / UI host","Executable","Title / accessible text","Attribution");
   Columns(tasks,"Task path","State","Author","Actions","Triggers","Last run","Next run","Last result");
   filter.TextChanged += delegate { Render(); };
   refresh.Click += async delegate { await RefreshProcesses(); await RefreshTray(); };
   reload.Click += async delegate { await RefreshTasks(); };
   export.Click += delegate { Export(); };
   timer.Tick += async delegate { await RefreshProcesses(); };
   Shown += async delegate {
    callback = WindowEvent; hook = Native.SetWinEventHook(0x8002,0x8002,IntPtr.Zero,callback,0,0,2);
    if(hook == IntPtr.Zero) Log("Monitor error",0,"","Window hook installation failed","Popup capture unavailable");
    notificationHandler = NotificationEvent;
    try { Automation.AddAutomationEventHandler(AutomationElement.ToolTipOpenedEvent,AutomationElement.RootElement,TreeScope.Subtree,notificationHandler); Automation.AddAutomationEventHandler(WindowPattern.WindowOpenedEvent,AutomationElement.RootElement,TreeScope.Subtree,notificationHandler); }
    catch(Exception ex) { Log("Monitor error",0,"",ex.Message,"UI Automation listener unavailable"); }
    timer.Start(); await RefreshProcesses(); await RefreshTasks(); await RefreshTray();
   };
   FormClosing += delegate {
    closing = true; timer.Stop(); if(hook != IntPtr.Zero) Native.UnhookWinEvent(hook);
    try { if(notificationHandler != null) { Automation.RemoveAutomationEventHandler(AutomationElement.ToolTipOpenedEvent,AutomationElement.RootElement,notificationHandler); Automation.RemoveAutomationEventHandler(WindowPattern.WindowOpenedEvent,AutomationElement.RootElement,notificationHandler); } } catch {}
   };
  }
  static DataGridView Grid() { return new DataGridView { Dock=DockStyle.Fill,ReadOnly=true,AllowUserToAddRows=false,AllowUserToDeleteRows=false,AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.DisplayedCells,SelectionMode=DataGridViewSelectionMode.FullRowSelect,RowHeadersVisible=false,MultiSelect=false }; }
  void AddTab(string name, Control grid) { var page = new TabPage(name); page.Controls.Add(grid); tabs.TabPages.Add(page); }
  static void Columns(DataGridView grid, params string[] names) { foreach(string name in names) grid.Columns.Add(name,name); }
  void Fill(DataGridView grid, IEnumerable<string[]> rows) {
   string query=filter.Text.Trim(); grid.Rows.Clear();
   foreach(var row in rows.Where(r=>query.Length==0 || r.Any(s=>(s??"").IndexOf(query,StringComparison.OrdinalIgnoreCase)>=0))) grid.Rows.Add(row);
  }
  void Render() { Fill(processes,processRows); Fill(tray,trayRows); Fill(tasks,taskRows); Fill(events,history); }
  static string PathFor(int pid) { try { using(var p=Process.GetProcessById(pid)) return p.MainModule.FileName; } catch { return "Unavailable / access denied"; } }
  async Task RefreshProcesses() {
   if(refreshing || closing) return; refreshing=true;
   try {
    var result=await Task.Run(()=>{
     var metadata=new Dictionary<int,string[]>();
     try { using(var search=new ManagementObjectSearcher("SELECT ProcessId, ParentProcessId, ExecutablePath, CommandLine FROM Win32_Process"))
     using(var collection=search.Get()) foreach(ManagementObject item in collection) using(item)
      metadata[Convert.ToInt32(item["ProcessId"])]=new[]{Convert.ToString(item["ExecutablePath"]),Convert.ToString(item["CommandLine"]),Convert.ToString(item["ParentProcessId"])};
     } catch {}
     var rows=new List<string[]>();
     foreach(var p in Process.GetProcesses()) using(p) {
      try {
       string[] m; metadata.TryGetValue(p.Id,out m);
       string path=m==null || string.IsNullOrEmpty(m[0]) ? PathFor(p.Id) : m[0];
       string publisher=""; try { if(File.Exists(path)) publisher=FileVersionInfo.GetVersionInfo(path).CompanyName; } catch {}
       rows.Add(new[]{p.Id.ToString(),p.ProcessName,publisher,path,m==null?"Unavailable":m[1],m==null?"":m[2],p.SessionId.ToString(),p.MainWindowTitle,(p.WorkingSet64/1048576.0).ToString("F1")});
      } catch {}
     } return rows.OrderBy(r=>r[1]).ToList();
    });
    if(closing) return; processRows=result; Fill(processes,result); status.Text=result.Count+" processes • "+DateTime.Now.ToLongTimeString();
   } catch(Exception ex) { if(!closing) status.Text=ex.Message; } finally { refreshing=false; }
  }
  async Task RefreshTray() {
   try {
    var rows=await Task.Run(()=>{
     var result=new List<string[]>();
     Native.EnumWindows((hwnd,unused)=>{
      var cls=new StringBuilder(256); Native.GetClassName(hwnd,cls,cls.Capacity);
      if(cls.ToString()!="Shell_TrayWnd" && cls.ToString()!="Shell_SecondaryTrayWnd" && cls.ToString()!="NotifyIconOverflowWindow" && cls.ToString()!="TopLevelWindowForOverflowXamlIsland") return true;
      try {
       var root=AutomationElement.FromHandle(hwnd);
       var elements=root.FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Button));
       foreach(AutomationElement el in elements) {
        var current=el.Current; bool inTray=cls.ToString().IndexOf("Overflow",StringComparison.Ordinal)>=0; var ancestor=el;
        for(int depth=0;depth<12 && ancestor!=null;depth++,ancestor=TreeWalker.ControlViewWalker.GetParent(ancestor)) {
         var a=ancestor.Current;
         if(a.ClassName=="TrayNotifyWnd" || a.ClassName=="ToolbarWindow32" && a.Name.IndexOf("notification",StringComparison.OrdinalIgnoreCase)>=0 || a.AutomationId.IndexOf("SystemTray",StringComparison.OrdinalIgnoreCase)>=0) inTray=true;
         if(ancestor.Equals(root)) break;
        }
        if(inTray && !current.IsOffscreen && !string.IsNullOrWhiteSpace(current.Name)) result.Add(new[]{current.Name,current.ProcessId.ToString(),PathFor(current.ProcessId),"Accessible tray control; originating process unresolved"});
       }
      } catch {} return true;
     },IntPtr.Zero);
     return result.GroupBy(r=>r[0]+"|"+r[1]).Select(g=>g.First()).ToList();
    });
    if(closing) return; trayRows=rows; Fill(tray,rows);
    if(rows.Count==0) status.Text="No exposed tray controls. Expand hidden icons and refresh.";
   } catch(Exception ex) { if(!closing) status.Text=ex.Message; }
  }
  void WindowEvent(IntPtr h,uint ev,IntPtr hwnd,int objectId,int child,uint thread,uint time) {
   if(closing || objectId!=0 || child!=0 || hwnd==IntPtr.Zero || !Native.IsWindowVisible(hwnd) || Native.GetAncestor(hwnd,2)!=hwnd) return;
   uint pid; Native.GetWindowThreadProcessId(hwnd,out pid); if(pid==Process.GetCurrentProcess().Id) return;
   var title=new StringBuilder(2048); Native.GetWindowText(hwnd,title,title.Capacity);
   Log("Window shown",(int)pid,PathFor((int)pid),title.ToString(),"Confirmed window owner; may be an ordinary window");
  }
  void NotificationEvent(object sender,AutomationEventArgs args) {
   try {
    var element=sender as AutomationElement; if(element==null) return;
    var c=element.Current; if(c.ProcessId==Process.GetCurrentProcess().Id) return;
    string text=c.Name;
    var children=element.FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Text));
    foreach(AutomationElement child in children) if(!string.IsNullOrEmpty(child.Current.Name)) text+=" | "+child.Current.Name;
    if(text.Length>4000) text=text.Substring(0,4000);
    Log("Accessible tooltip / notification",c.ProcessId,PathFor(c.ProcessId),text,"UI host only; sender unconfirmed; may be unrelated UI");
   } catch {}
  }
  void Log(string kind,int pid,string path,string text,string attribution) {
   string name=""; try { using(var p=Process.GetProcessById(pid)) name=p.ProcessName; } catch {}
   var row=new[]{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),kind,pid.ToString(),name,path,text,attribution};
   if(closing || !IsHandleCreated) return;
   try { BeginInvoke(new Action(()=>{ if(closing) return; history.Enqueue(row); while(history.Count>5000) history.Dequeue(); Fill(events,history); })); } catch(InvalidOperationException) {}
  }
  async Task RefreshTasks() {
   try {
    var result=await Task.Run(()=>{
     var rows=new List<string[]>(); var warnings=new List<string>(); dynamic service=null;
     try { service=Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")); service.Connect(); ReadFolder(service.GetFolder("\\"),rows,warnings); }
     finally { if(service!=null) Marshal.ReleaseComObject(service); }
     return Tuple.Create(rows,warnings);
    });
    if(closing) return; taskRows=result.Item1; Fill(tasks,taskRows); taskStatus.Text=result.Item1.Count+" enabled tasks. "+(result.Item2.Count>0 ? "Partial results: "+string.Join("; ",result.Item2) : "Loaded "+DateTime.Now.ToLongTimeString());
    if(result.Item2.Count>0) Log("Task enumeration warning",0,"",string.Join("; ",result.Item2),"Task list is partial");
   } catch(Exception ex) { if(!closing) { taskStatus.Text="Task Scheduler unavailable: "+ex.Message; Log("Task enumeration error",0,"",ex.Message,"No task inventory available"); } }
  }
  static string TriggerName(int value) { string[] names={"Event","One time","Daily","Weekly","Monthly","Monthly day-of-week","Idle","Registration","Boot","Logon","Unused","Session state change"}; return value>=0 && value<names.Length ? names[value] : "Type "+value; }
  static void ReadFolder(dynamic folder,List<string[]> rows,List<string> warnings) {
   dynamic taskCollection=null,folders=null;
   try {
    taskCollection=folder.GetTasks(1);
    for(int i=1;i<=taskCollection.Count;i++) {
     dynamic task=null,definition=null,actions=null,triggers=null,registration=null;
     try {
      task=taskCollection[i]; if(!task.Enabled) continue;
      definition=task.Definition; actions=definition.Actions; triggers=definition.Triggers; registration=definition.RegistrationInfo;
      var actionText=new List<string>(); var triggerText=new List<string>();
      for(int a=1;a<=actions.Count;a++) { dynamic action=actions[a]; try { if(action.Type==0) actionText.Add(action.Path+" "+action.Arguments+" [working directory: "+action.WorkingDirectory+"]"); else actionText.Add("Action type "+action.Type); } finally { Marshal.ReleaseComObject(action); } }
      for(int t=1;t<=triggers.Count;t++) { dynamic trigger=triggers[t]; try { triggerText.Add(TriggerName((int)trigger.Type)+" | enabled="+trigger.Enabled+" | start="+trigger.StartBoundary); } finally { Marshal.ReleaseComObject(trigger); } }
      string[] states={"Unknown","Disabled","Queued","Ready","Running"}; int state=task.State;
      rows.Add(new string[]{(string)task.Path,state>=0 && state<states.Length?states[state]:state.ToString(),(string)registration.Author,string.Join("; ",actionText),string.Join("; ",triggerText),(string)Convert.ToString(task.LastRunTime),(string)Convert.ToString(task.NextRunTime),"0x"+((uint)(int)task.LastTaskResult).ToString("X8")});
     } catch(Exception ex) { warnings.Add("Task entry: "+ex.Message); }
     finally { foreach(object obj in new object[]{registration,triggers,actions,definition,task}) if(obj!=null) Marshal.ReleaseComObject(obj); }
    }
    folders=folder.GetFolders(0); for(int f=1;f<=folders.Count;f++) ReadFolder(folders[f],rows,warnings);
   } catch(Exception ex) { warnings.Add("Folder: "+ex.Message); }
   finally { if(folders!=null) Marshal.ReleaseComObject(folders); if(taskCollection!=null) Marshal.ReleaseComObject(taskCollection); Marshal.ReleaseComObject(folder); }
  }
  void Export() {
   var grid=tabs.SelectedTab.Controls[0] as DataGridView;
   using(var dialog=new SaveFileDialog { Filter="CSV files|*.csv",FileName="MonitorProcesses-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".csv" })
    if(dialog.ShowDialog(this)==DialogResult.OK) try {
     using(var writer=new StreamWriter(dialog.FileName,false,new UTF8Encoding(true))) {
      writer.WriteLine(string.Join(",",grid.Columns.Cast<DataGridViewColumn>().Select(c=>Csv(c.HeaderText))));
      foreach(DataGridViewRow row in grid.Rows) writer.WriteLine(string.Join(",",row.Cells.Cast<DataGridViewCell>().Select(c=>Csv(Convert.ToString(c.Value)))));
     }
    } catch(Exception ex) { MessageBox.Show(this,ex.Message,"Export failed"); }
  }
  static string Csv(string value) { return "\"" +(value??"").Replace("\"","\"\"")+ "\""; }
 }
 static class Native {
  internal delegate void WinEvent(IntPtr hook,uint ev,IntPtr window,int obj,int child,uint thread,uint time);
  internal delegate bool EnumProc(IntPtr window,IntPtr data);
  [DllImport("user32.dll")] internal static extern IntPtr SetWinEventHook(uint min,uint max,IntPtr module,WinEvent callback,uint process,uint thread,uint flags);
  [DllImport("user32.dll")] internal static extern bool UnhookWinEvent(IntPtr hook);
  [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumProc callback,IntPtr data);
  [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr window);
  [DllImport("user32.dll")] internal static extern IntPtr GetAncestor(IntPtr window,uint flags);
  [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr window,out uint process);
  [DllImport("user32.dll",CharSet=CharSet.Unicode)] internal static extern int GetWindowText(IntPtr window,StringBuilder text,int size);
  [DllImport("user32.dll",CharSet=CharSet.Unicode)] internal static extern int GetClassName(IntPtr window,StringBuilder text,int size);
 }
}



