param(
    [string]$InnoCompiler
)

$ErrorActionPreference = 'Stop'
$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$publish = [System.IO.Path]::GetFullPath((Join-Path $root 'artifacts\publish'))
$installerOutput = [System.IO.Path]::GetFullPath((Join-Path $root 'artifacts\installer'))

foreach ($path in @($publish, $installerOutput)) {
    if (-not $path.StartsWith($root, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Output path escaped workspace: $path"
    }
}

function Remove-DirectoryWithRetry([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return }
    for ($attempt = 1; $attempt -le 10; $attempt++) {
        try {
            Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop
            return
        }
        catch {
            if ($attempt -eq 10) { throw }
            Start-Sleep -Milliseconds 500
        }
    }
}

if (-not $InnoCompiler) {
    $candidates = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
    )
    $InnoCompiler = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $InnoCompiler) {
        $registryEntry = Get-ItemProperty `
            'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*', `
            'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*', `
            'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*' `
            -ErrorAction SilentlyContinue |
            Where-Object { $_.DisplayName -like 'Inno Setup*' } |
            Sort-Object DisplayVersion -Descending |
            Select-Object -First 1
        if ($registryEntry.InstallLocation) {
            $InnoCompiler = Join-Path $registryEntry.InstallLocation 'ISCC.exe'
        }
    }
}

if (-not $InnoCompiler -or -not (Test-Path -LiteralPath $InnoCompiler)) {
    throw '未找到 Inno Setup 6 编译器 ISCC.exe。'
}

$running = Get-Process MusicBar -ErrorAction SilentlyContinue
if ($running) {
    $running | Stop-Process -Force
    $running | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
}

Remove-DirectoryWithRetry $publish
Remove-DirectoryWithRetry $installerOutput

dotnet test (Join-Path $root 'MusicBar.sln') -c Release
if ($LASTEXITCODE -ne 0) { throw '测试失败。' }

dotnet publish (Join-Path $root 'MusicBar\MusicBar.csproj') `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None `
    -o $publish
if ($LASTEXITCODE -ne 0) { throw '发布失败。' }

& $InnoCompiler (Join-Path $PSScriptRoot 'MusicBar.iss')
if ($LASTEXITCODE -ne 0) { throw '安装包编译失败。' }

Get-ChildItem -LiteralPath $installerOutput -Filter '*.exe'
