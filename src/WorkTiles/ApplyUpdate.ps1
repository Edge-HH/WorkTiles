param([Parameter(Mandatory = $true)][string]$Configuration, [switch]$SkipRestart)
$ErrorActionPreference = 'Stop'
$configPath = [IO.Path]::GetFullPath($Configuration)
$stage = Split-Path -Parent $configPath
$log = Join-Path $stage 'update.log'
$changed = [Collections.Generic.List[object]]::new()
$createdDirs = [Collections.Generic.List[string]]::new()
$installed = $false
$canRestart = $false
try {
    $config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
    $target = [IO.Path]::GetFullPath($config.Target).TrimEnd('\')
    $payload = [IO.Path]::GetFullPath($config.Payload).TrimEnd('\')
    if ($payload -ne (Join-Path $stage 'payload')) { throw 'Invalid payload directory.' }
    $parent = Get-Process -Id $config.ParentProcessId -ErrorAction SilentlyContinue
    if ($parent -and $config.ParentStartTimeUtc) {
        $expected = [datetime]::Parse([string]$config.ParentStartTimeUtc, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime()
        if ([math]::Abs(($parent.StartTime.ToUniversalTime() - $expected).TotalSeconds) -gt 2) { throw 'Parent process id was reused; no files were replaced.' }
    }
    # 身份校验通过后才给应用接管承诺；应用等不到这个标记就不会退出。
    'STARTED' | Set-Content -LiteralPath (Join-Path $stage 'update.started') -Encoding UTF8
    if ($parent -and -not $parent.WaitForExit(60000)) { throw 'The application did not exit; no files were replaced.' }
    $canRestart = $true
    $files = @(Get-ChildItem -LiteralPath $payload -Recurse -File)
    foreach ($file in $files) {
        $relative = $file.FullName.Substring($payload.Length + 1)
        $destination = [IO.Path]::GetFullPath((Join-Path $target $relative))
        if (-not $destination.StartsWith($target + '\', [StringComparison]::OrdinalIgnoreCase) -or $relative -match '^data([\/]|$)') { throw 'Invalid update path.' }
        # 不追踪安装目录内的目录联接，也不覆盖用户数据。
        $ancestor = $destination
        while ($ancestor.Length -ge $target.Length) {
            if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Update target contains a link.' }
            $ancestor = [IO.Path]::GetDirectoryName($ancestor)
        }
        $backup = Join-Path (Join-Path $stage 'backup') $relative
        $existed = Test-Path -LiteralPath $destination -PathType Leaf
        if ($existed) {
            $null = New-Item -ItemType Directory -Path (Split-Path -Parent $backup) -Force
            Copy-Item -LiteralPath $destination -Destination $backup
        }
        $parentDirectory = Split-Path -Parent $destination
        $missing = $parentDirectory
        while ($missing.Length -gt $target.Length -and -not (Test-Path -LiteralPath $missing)) {
            $createdDirs.Add($missing)
            $missing = [IO.Path]::GetDirectoryName($missing)
        }
        $null = New-Item -ItemType Directory -Path $parentDirectory -Force
        $changed.Add([pscustomobject]@{ Destination = $destination; Backup = $backup; Existed = $existed })
        Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
    }
    $installed = $true
    'UPDATE_OK' | Set-Content -LiteralPath $log -Encoding UTF8
}
catch {
    $failure = $_.ToString()
    # 只恢复本次已触及的文件；保留备份和日志供恢复，不递归删除安装目录。
    for ($index = $changed.Count - 1; $index -ge 0; $index--) {
        $item = $changed[$index]
        try {
            if ($item.Existed) { Copy-Item -LiteralPath $item.Backup -Destination $item.Destination -Force }
            elseif (Test-Path -LiteralPath $item.Destination -PathType Leaf) { Remove-Item -LiteralPath $item.Destination }
        }
        catch { $failure += "`nRollback: $_" }
    }
    # 按路径长度降序删除，保证子目录先于父目录；同一个父下的分支互不影响。
    foreach ($directory in ($createdDirs | Sort-Object Length -Descending)) {
        try {
            if ((Test-Path -LiteralPath $directory) -and -not (Get-ChildItem -LiteralPath $directory -Force)) {
                Remove-Item -LiteralPath $directory -Force
            }
        }
        catch { }
    }
    ("UPDATE_FAILED`n" + $failure) | Set-Content -LiteralPath $log -Encoding UTF8
}
if (-not $SkipRestart -and $canRestart -and $target) {
    try {
        # 按 Windows 命令行规则为每个原始参数加引号，--windowed / --silent / --project 等启动方式不丢失。
        function ConvertTo-QuotedArgument([string]$value) {
            if ($value -notmatch '[\s"]') { return $value }
            $escaped = [regex]::Replace($value, '(\\*)"', '$1$1\"')
            $escaped = [regex]::Replace($escaped, '(\\+)$', '$1$1')
            return '"' + $escaped + '"'
        }
        $arguments = (@($config.Arguments) | Where-Object { $null -ne $_ } | ForEach-Object { ConvertTo-QuotedArgument ([string]$_) }) -join ' '
        if ($arguments) {
            Start-Process -FilePath (Join-Path $target 'WorkTiles.exe') -WorkingDirectory $target -WindowStyle Hidden -ArgumentList $arguments
        }
        else {
            Start-Process -FilePath (Join-Path $target 'WorkTiles.exe') -WorkingDirectory $target -WindowStyle Hidden
        }
    }
    catch { ("Restart: " + $_) | Add-Content -LiteralPath $log -Encoding UTF8 }
}
if (-not $installed) { exit 1 }
