param([string]$OutputPath = '')
$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputPath)) { $OutputPath = Join-Path $taskRoot '牛马统计.exe' }
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
New-Item -ItemType Directory -Path (Split-Path -Parent $OutputPath) -Force | Out-Null
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
    $compiler = Join-Path $framework 'csc.exe'
}
if (-not (Test-Path -LiteralPath $compiler)) { throw '需要 Windows 自带的 .NET Framework 4.8。' }
$references = @('System.dll', 'System.Core.dll', 'System.Runtime.Serialization.dll', 'System.IO.Compression.dll', 'System.Windows.Forms.dll', 'System.Drawing.dll', 'System.Xaml.dll')
$arguments = @('/nologo', '/target:winexe', '/platform:anycpu', '/optimize+', '/utf8output', '/codepage:65001', '/warn:4')
foreach ($reference in $references) { $arguments += ('/reference:' + (Join-Path $framework $reference)) }
foreach ($reference in @('PresentationCore.dll', 'PresentationFramework.dll', 'WindowsBase.dll')) { $arguments += ('/reference:' + (Join-Path $framework ('WPF\' + $reference))) }
$arguments += ('/out:' + $OutputPath)
$arguments += ('/win32manifest:' + (Join-Path $taskRoot 'src\app.manifest'))
$arguments += ('/win32icon:' + (Join-Path $taskRoot 'assets\app.ico'))
$arguments += ('/resource:' + (Join-Path $taskRoot 'src\MainWindow.xaml') + ',NiuMa.MainWindow.xaml')
$arguments += ('/resource:' + (Join-Path $taskRoot 'assets\app.ico') + ',NiuMa.app.ico')
$arguments += (Get-ChildItem -LiteralPath (Join-Path $taskRoot 'src') -Filter '*.cs' | ForEach-Object FullName)
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw '编译失败。' }
Copy-Item -LiteralPath (Join-Path $taskRoot 'src\app.config') -Destination ($OutputPath + '.config') -Force
Write-Host ('已生成 ' + $OutputPath)
