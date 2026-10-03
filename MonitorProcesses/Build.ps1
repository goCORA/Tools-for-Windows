param([ValidateSet("Debug","Release")][string]$Configuration="Debug")
$ErrorActionPreference="Stop"
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$framework=Split-Path $compiler
$destination=Join-Path $PSScriptRoot "bin\$Configuration"
New-Item -ItemType Directory -Path $destination -Force | Out-Null
& $compiler /nologo /target:winexe /optimize+ "/out:$destination\MonitorProcesses.exe" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Management.dll /r:Microsoft.CSharp.dll "/r:$framework\WPF\UIAutomationClient.dll" "/r:$framework\WPF\UIAutomationTypes.dll" "/r:$framework\WPF\WindowsBase.dll" "$PSScriptRoot\Program.cs"
if($LASTEXITCODE -ne 0) { throw "Compilation failed ($LASTEXITCODE)." }
Copy-Item "$PSScriptRoot\app.config" "$destination\MonitorProcesses.exe.config"
Write-Host "Built $destination\MonitorProcesses.exe"
