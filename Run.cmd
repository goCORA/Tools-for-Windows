@echo off
cd /d "%~dp0"
if not exist "FolderAccessMonitor\bin\Release\net10.0-windows\FolderAccessMonitor.exe" (
    dotnet build FolderAccessMonitor.sln -c Release
    if errorlevel 1 (
        pause
        exit /b 1
    )
)
start "" "FolderAccessMonitor\bin\Release\net10.0-windows\FolderAccessMonitor.exe"
