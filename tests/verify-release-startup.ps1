param([Parameter(Mandatory = $true)][string]$PublishDirectory)
$ErrorActionPreference = 'Stop'
$source = (Resolve-Path -LiteralPath $PublishDirectory).Path
foreach ($required in @('WorkTiles.exe', 'WorkTiles.dll', 'WorkTiles.pri', 'WorkTiles.runtimeconfig.json', 'ApplyUpdate.ps1')) {
    if (-not (Test-Path -LiteralPath (Join-Path $source $required))) { throw "发布包缺少 $required" }
}
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('WorkTiles-release-test-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $testRoot
try {
    Get-ChildItem -LiteralPath $source | Where-Object { $_.Name -notin @('data', 'verification', 'startup-ok.txt') } | Copy-Item -Destination $testRoot -Recurse
    $process = Start-Process -FilePath (Join-Path $testRoot 'WorkTiles.exe') -ArgumentList '--smoke-test' -WorkingDirectory $testRoot -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(30000)) {
        $crash = Join-Path $testRoot 'data/crash.log'
        if (Test-Path -LiteralPath $crash) { Get-Content -LiteralPath $crash -Tail 18 }
        throw '发布包未在 30 秒内完成窗口加载验证。'
    }
    $marker = Join-Path $testRoot 'startup-ok.txt'
    if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $marker) -or (Get-Content -LiteralPath $marker -Raw) -ne 'WINDOW_LOADED') {
        $crash = Join-Path $testRoot 'data/crash.log'
        if (Test-Path -LiteralPath $crash) { Get-Content -LiteralPath $crash -Tail 18 }
        throw "发布包启动失败，退出码 $($process.ExitCode)"
    }
    'PASS: published application loaded the real WinUI window and exited normally.'
}
finally {
    if ($process -and -not $process.HasExited) {
        try { Stop-Process -Id $process.Id -Force -ErrorAction Stop } catch { }
    }
    $resolved = [IO.Path]::GetFullPath($testRoot)
    if ($resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase) -and (Split-Path -Leaf $resolved) -like 'WorkTiles-release-test-*') {
        # 进程刚退出时 DLL 可能仍被杀毒/系统短暂占用；清理失败不能把成功结果变成失败。
        try { Remove-Item -LiteralPath $resolved -Recurse -Force -ErrorAction Stop }
        catch { Write-Warning "临时测试目录未能删除：$resolved（$($_.Exception.Message)）" }
    }
}
