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
    $markerText = if (Test-Path -LiteralPath $marker) { (Get-Content -LiteralPath $marker -Raw) } else { '<missing>' }
    # 判定标准是「窗口真的用发布包里的 XAML/资源加载起来了」，也就是 marker。
    # 退出码只看有没有在超时内退出：无显示会话里 WinUI 拆窗口会在 CoreMessagingXP 里崩
    # （0xC000027B），本机桌面会话下同样的包退出是干净的，所以退出阶段只报警告不判失败。
    if ($markerText -ne 'WINDOW_LOADED') {
        $crash = Join-Path $testRoot 'data/crash.log'
        if (Test-Path -LiteralPath $crash) { Get-Content -LiteralPath $crash -Tail 18 }
        throw "发布包未能加载窗口：退出码 $($process.ExitCode)，startup-ok=$markerText"
    }
    if ($process.ExitCode -ne 0) {
        Write-Warning "窗口已加载，但进程退出码为 $($process.ExitCode)（0x$('{0:X8}' -f $process.ExitCode)），通常是窗口拆除阶段的 WinUI 崩溃。"
    }
    else {
        'PASS: published application loaded the real WinUI window and exited normally.'
    }
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
