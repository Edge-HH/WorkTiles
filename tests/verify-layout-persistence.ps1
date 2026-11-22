param(
    [string]$PublishDirectory = (Join-Path $PSScriptRoot '..\src\WorkTiles\bin\Release\net8.0-windows10.0.19041.0\win-x64'),
    [ValidateSet('Dark', 'Light')] [string]$Theme = 'Dark',
    [ValidateSet('display', 'settings')] [string]$View = 'display'
)
$ErrorActionPreference = 'Stop'

$outputDirectory = [System.IO.Path]::GetFullPath($PublishDirectory)
Write-Output "Using publish directory: $outputDirectory"
$executablePath = Join-Path $outputDirectory 'WorkTiles.exe'

if (-not (Test-Path -LiteralPath $executablePath)) {
    throw 'Build Release x64 before running the layout persistence test.'
}

$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('WorkTiles-layout-persistence-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
$process = $null

try {
    # 不复制真实 data：夹具必须自己决定加载什么，测试也不能带着真实集控凭据启动。
    Get-ChildItem -LiteralPath $outputDirectory |
        Where-Object { $_.Name -notin @('data', 'verification', 'startup-ok.txt') } |
        Copy-Item -Destination $testRoot -Recurse -Force
    $dataDirectory = Join-Path $testRoot 'data'
    New-Item -ItemType Directory -Path $dataDirectory -Force | Out-Null
    $statePath = Join-Path $dataDirectory 'pancake.json'
    $fixture = @'
{
  "SchemaVersion": 1,
  "Settings": {
    "Theme": "Dark",
    "LaunchAtStartup": false,
    "SilentStartup": false,
    "MicrophoneSampleRate": 16000,
    "MicrophoneCalibrationDb": 0,
    "WeatherCityName": "Beijing",
    "WeatherCityCode": "101010100",
    "GridSnappingEnabled": true,
    "AutoUpdateEnabled": false,
    "UpdateRepository": "Edge-HH/WorkTiles"
  },
  "Subjects": [
    { "Name": "Math", "AccentHex": "#65D46E", "X": 48, "Y": 48, "Width": 384, "Height": 240, "Entries": [], "InkStrokes": [] },
    { "Name": "English", "AccentHex": "#7567FF", "X": 528, "Y": 48, "Width": 384, "Height": 240, "Entries": [], "InkStrokes": [] },
    { "Name": "Physics", "AccentHex": "#60A5FA", "X": 528, "Y": 336, "Width": 480, "Height": 288, "Entries": [], "InkStrokes": [] }
  ]
}
'@
    $fixture = $fixture.Replace('"Theme": "Dark"', ('"Theme": "' + $Theme + '"'))
    [System.IO.File]::WriteAllText($statePath, $fixture)
    $before = [System.IO.File]::ReadAllText($statePath) | ConvertFrom-Json

    # --layout-probe：允许正常关闭，并跳过麦克风、天气、自动备份、开机自启与托盘。
    $process = Start-Process `
        -FilePath (Join-Path $testRoot 'WorkTiles.exe') `
        -ArgumentList '--windowed', "--view=$View", '--layout-probe' `
        -WorkingDirectory $testRoot `
        -WindowStyle Hidden `
        -PassThru

    Start-Sleep -Seconds 4
    if ($process.HasExited) { throw 'The test process exited before normal shutdown.' }
    $null = $process.CloseMainWindow()
    if (-not $process.WaitForExit(5000)) {
        throw 'The test process did not close normally within five seconds.'
    }

    $library = [System.IO.File]::ReadAllText((Join-Path $dataDirectory 'projects.json')) | ConvertFrom-Json
    $after = [PSCustomObject]@{ Settings = $library.Settings; Subjects = $library.Projects[0].Subjects }
    if ($after.Settings.Theme -ne $Theme) { throw 'The selected theme was not preserved.' }
    $beforeLayout = $before.Subjects | ForEach-Object { "$($_.Name):$($_.X),$($_.Y),$($_.Width),$($_.Height)" }
    $afterLayout = $after.Subjects | ForEach-Object { "$($_.Name):$($_.X),$($_.Y),$($_.Width),$($_.Height)" }

    if (Compare-Object -ReferenceObject $beforeLayout -DifferenceObject $afterLayout) {
        Write-Error "Layout changed after startup and normal shutdown. Before: $($beforeLayout -join '; '); After: $($afterLayout -join '; ')"
        exit 1
    }

    Write-Output "PASS: $Theme / $View startup and normal shutdown preserve theme and tile layout."
}
finally {
    if ($process -and -not $process.HasExited) {
        $null = $process.CloseMainWindow()
        if (-not $process.WaitForExit(3000)) {
            try { Stop-Process -Id $process.Id -Force -ErrorAction Stop } catch { }
        }
    }

    $resolvedTestRoot = [System.IO.Path]::GetFullPath($testRoot)
    $resolvedTempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
    if ($resolvedTestRoot.StartsWith($resolvedTempRoot, [System.StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $resolvedTestRoot).StartsWith('WorkTiles-layout-persistence-')) {
        try { Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force -ErrorAction Stop }
        catch { Write-Warning "临时测试目录未能删除：$resolvedTestRoot（$($_.Exception.Message)）" }
    }
}
