$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
$testExe = Join-Path $taskRoot '牛马统计.exe'
$testReport = Join-Path $taskRoot 'artifacts\test-report.txt'
New-Item -ItemType Directory -Path (Split-Path -Parent $testReport) -Force | Out-Null
$testArguments = @('--self-test', '--report', ('"' + $testReport + '"'))
$testProcess = Start-Process -FilePath $testExe -ArgumentList $testArguments -WindowStyle Hidden -PassThru
if (-not $testProcess.WaitForExit(15000)) { throw '测试超过 15 秒，请查看任务管理器和报告。' }
Get-Content -LiteralPath $testReport -Encoding UTF8
if ($testProcess.ExitCode -ne 0) { throw '测试未通过。' }
