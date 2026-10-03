# FolderAccessMonitor

Windows 10/11 x64 desktop app built with C#, .NET 10 and Windows Forms. Edit FolderAccessMonitor.sln in Visual Studio Community 2026 with the .NET desktop development workload installed.

## Run

Build the solution and run FolderAccessMonitor. Click **Restart as administrator**, accept the Windows UAC prompt, choose a folder, and click **Start monitoring**. Subfolders are included by default. **Stop** releases the ETW session. Start begins a fresh capture; Clear results resets the displayed capture without stopping monitoring.

The process table shows process name, app description when available, PID, recent activity, operation counts, requested read/write bytes, last operation, file path and executable path. The event log keeps the newest 5,000 events. Export saves that retained log to CSV. The status line reports dropped queued events and events lost by Windows.

## Meaning and limits

This uses Windows kernel ETW file events through Microsoft's TraceEvent library, rather than FileSystemWatcher. Reads, writes, open/create requests, close, directory enumeration, metadata queries/updates, rename/delete requests and flush operations are captured after monitoring starts. Active means observed within the last five seconds; Idle means no recent activity. It is an activity monitor, not a snapshot of every existing open handle or a complete audit system. Requests do not prove successful completion. Read/write totals represent requested bytes.

Unresolved paths are skipped. Events for pre-existing handles, memory-mapped access, network shares, alternate drive aliases/junctions, protected processes and system-attributed I/O may be incomplete or have unavailable attribution. Match by the path Windows reports; use a local drive path for the most predictable results. High traffic can lose events; the UI shows loss counters. Only run monitoring while needed because ETW captures file activity system-wide before the app filters the selected folder. No files are modified by monitoring.

Administrator rights are required for kernel tracing. The executable normally starts without elevation; Visual Studio itself does not need to run as administrator to edit or build. Windows permits a limited number of kernel tracing sessions, so another tracing tool may prevent startup. Errors are shown in the UI.

## Build

    dotnet build FolderAccessMonitor.sln -c Release

Requires the .NET 10 SDK; running the framework-dependent executable requires the .NET 10 Desktop Runtime (x64). NuGet restores Microsoft.Diagnostics.Tracing.TraceEvent 3.1.21, pinned to the version available on this workstation. The newer package could not be downloaded here due to local TLS authentication failure.

## Validation

    .\FolderAccessMonitor\bin\Release\net10.0-windows\FolderAccessMonitor.exe --self-test filter-test-result.txt
    .\FolderAccessMonitor\bin\Release\net10.0-windows\FolderAccessMonitor.exe --gui-smoke gui-smoke-result.txt

Both commands write a PASS file on completion. For an administrator capture check, create a temporary folder, monitor it, and use a separate PowerShell window to write, read, rename, and delete a file in it. Confirm powershell or pwsh and those paths appear. Access an adjacent similarly named folder to confirm it is excluded; toggle subfolders and test a child directory. Stop and close the app to confirm the trace session is released. Live elevated capture has not been validated by the unelevated build environment.

## Implementation

MainForm.cs owns the GUI and CSV export. MonitorService.cs owns the ETW session, bounded queue and folder filtering. Program.cs provides startup, elevation and smoke checks. Each monitor uses a unique session name and disposes its own session. Microsoft ETW and TraceEvent references:

- https://learn.microsoft.com/windows/win32/etw/fileio
- https://github.com/microsoft/perfview/blob/main/documentation/TraceEvent/TraceEventProgrammersGuide.md
