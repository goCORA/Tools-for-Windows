# MonitorProcesses

Windows 10/11 desktop diagnostic utility for tracing background processes and popup windows, including subscription advertisements.

## Open in Visual Studio Community 2026
1. In Visual Studio Installer, install the **.NET desktop development** workload and **.NET Framework 4.8 targeting/development tools**.
2. Open MonitorProcesses.sln.
3. Build and press F5. No NuGet packages are required.

A compiled executable is included at bin\Debug\MonitorProcesses.exe. It needs .NET Framework 4.8. To compile without Visual Studio using the installed Windows Framework compiler, run: powershell -ExecutionPolicy Bypass -File .\Build.ps1

## Finding a Corel popup
Keep this application running until the advertisement appears.
- In **Popup / notification activity**, look for the window title at the matching timestamp. A Window shown entry gives the actual window owner's PID and executable.
- Search for Corel, the product name, executable name or PID in **Processes**. Publisher, command line and parent PID can identify a background updater or promotional component.
- Search the same vendor or executable in **Enabled scheduled tasks**. Actions shows executable paths, arguments and working directories.
- Export activity to CSV before closing. The newest 5,000 events are retained in memory, not across restarts.
- If the popup was visible before monitoring started, use the process tab's Window title column. Activity history does not reconstruct past events.

## Views
- **Processes:** refreshes every five seconds. Lists processes across sessions with details where your Windows account has access.
- **Tray icons:** accessible buttons exposed in taskbar notification areas and an open overflow flyout. Open the hidden-icons arrow and click Refresh. Shell controls can include system controls; not every icon is guaranteed to appear.
- **Popup / notification activity:** WinEvent window-show events and UI Automation window/tooltip-open events, with accessible text where available. Ordinary windows and tooltips are included; this is evidence to inspect, not an automatic advertisement classifier.
- **Enabled scheduled tasks:** recursively enumerates enabled tasks including hidden tasks accessible to your account. Shows state, author, actions, trigger types/start boundaries, last/next runs and hexadecimal results. Reload manually for updates. Errors and partial enumeration are shown explicitly.
- Search filters every tab. Export saves the selected tab's filtered rows.

## Attribution and access limits
Normal popup ownership is directly identified. This does not prove which upstream application launched that process. PIDs can be reused after a process exits.

Tray icons are usually hosted by Explorer. Their UI Automation PID identifies the UI host, not the application registering the icon. This version lists accessible labels and explicitly marks the originating process unresolved; it does not definitively flag every process owning a tray icon.

Windows toast notifications can be hosted by a shell process. UI Automation observation is best effort: some notifications do not expose window/tooltip-open events or text. This utility does not read notification-center history or implement the permission-based Windows UserNotificationListener API, and cannot guarantee identification of every toast sender. Custom-drawn, elevated, other-session, secure-desktop or extremely short-lived windows may be missed. Conventional Corel subscription popup windows are the most useful capture target.

Publisher is file version metadata, not a verified digital signature. Protected processes may hide details. Administrator privileges may improve process/task visibility. Monitoring runs while the app is open, including minimized. It does not install a service/startup entry, stop processes or change tasks.

## Validation on the development host
- Compiled with the installed .NET Framework C# compiler.
- Enumerated 667 active processes and found the test application's PID.
- Captured a separate test popup with its owning process.
- Verified search filtering and CSV quote escaping.
- Task Scheduler service was running but both COM enumeration and schtasks /query failed with path-not-found. Enabled-task enumeration could not be verified here; the app reports a persistent task error.
- Full Windows 10/11 tray/toast coverage and an interactive Visual Studio build have not been verified.

## References
- Visual Studio 2026 components: https://github.com/MicrosoftDocs/visualstudio-docs/blob/main/docs/install/includes/vs-2026/workload-component-id-vs-community.md
- Hidden icons and UI Automation: https://devblogs.microsoft.com/oldnewthing/20250929-00/?p=111637
- Notification listener permissions: https://learn.microsoft.com/en-us/windows/apps/develop/notifications/app-notifications/notification-listener
