<#
    Packages dist\ into Models-Fit-Studio-<version>-win-x64.zip, everything inside one
    "Models Fit Studio" folder, and refuses to leave behind a zip that only Windows
    Explorer can unpack.

    Why not Compress-Archive, and not [ZipFile]::CreateFromDirectory either: both
    run on Windows PowerShell 5.1 (.NET Framework), and both store nested entries
    with backslash separators - "Web\app.js" (measured on this machine, 5.1.26100).
    Every unpacker except Explorer then writes a single file whose *name* contains
    a backslash, so there is no Web\ folder and the published app opens blank.
    Writing the entries here keeps real forward slashes on every PowerShell.

    Exits 0 when the archive is good, 1 when there was nothing to package, 2 when
    the archive would not unpack into a runnable app.
#>
[CmdletBinding()]
param(
    [string]$Source = 'dist',
    [string]$Version = '',
    [string]$Destination = '',
    [string]$Folder = 'Models Fit Studio'
)

$ErrorActionPreference = 'Stop'
# ZipFile lives in ...FileSystem, but ZipArchiveMode (and friends) live in the
# base ...Compression assembly, which a fresh -File session has not loaded yet.
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

if (-not $Version) {
    $proj = Join-Path (Split-Path -Parent $PSScriptRoot) 'ModelFitStudio.csproj'
    $Version = ([xml](Get-Content -LiteralPath $proj -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
if (-not $Destination) { $Destination = "Models-Fit-Studio-$Version-win-x64.zip" }
$exe = 'Models Fit Studio.exe'

if (-not (Test-Path -LiteralPath $Source -PathType Container)) {
    Write-Host "        no $Source folder to package"
    exit 1
}

$root    = (Resolve-Path -LiteralPath $Source).Path
$cut     = $root.Length + 1
$outPath = Join-Path (Get-Location).Path $Destination
# Left out: a WebView2 user profile (an older build kept one next to the exe; browser
# state and locked databases, not app files), debug symbols, the WebView2 SDK's API
# docs, and the exe of the builds before it was renamed.
$files   = @(Get-ChildItem -LiteralPath $root -Recurse -File | Where-Object {
    $rel = $_.FullName.Substring($cut)
    $rel -notmatch '(^|[\\/])[^\\/]*\.WebView2([\\/]|$)' -and
    $rel -notmatch '\.(pdb|xml)$' -and
    $rel -ne 'ModelFitStudio.exe'
})
if ($files.Count -eq 0) {
    Write-Host "        $Source is empty"
    exit 1
}

if (Test-Path -LiteralPath $outPath) { Remove-Item -LiteralPath $outPath -Force }

$level  = [System.IO.Compression.CompressionLevel]::Optimal
$portal = [System.IO.Compression.ZipFile]::Open($outPath, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($f in $files) {
        $name  = "$Folder/" + $f.FullName.Substring($cut).Replace('\', '/')
        $entry = $portal.CreateEntry($name, $level)
        $entry.LastWriteTime = $f.LastWriteTime
        $target = $entry.Open()
        try {
            $input = [System.IO.File]::OpenRead($f.FullName)
            try { $input.CopyTo($target) } finally { $input.Dispose() }
        } finally { $target.Dispose() }
    }
} finally { $portal.Dispose() }

# Read it back the way a foreign unzip would see it: a backslash path here is a
# broken release, and a missing Web/app.js or exe means the app would not start.
$check = [System.IO.Compression.ZipFile]::OpenRead($outPath)
$bad = 0
$web = 0
$app = 0
$profiles = 0
foreach ($e in $check.Entries) {
    if ($e.FullName.Contains('\')) { $bad++ }
    if ($e.FullName -eq "$Folder/Web/app.js") { $web++ }
    if ($e.FullName -eq "$Folder/$exe") { $app++ }
    if ($e.FullName -match '\.WebView2/') { $profiles++ }
}
$count = $check.Entries.Count
$check.Dispose()

Write-Host "        $Destination entries=$count exe=$app Web/app.js=$web backslash-paths=$bad user-profiles=$profiles"
if ($bad -gt 0 -or $web -ne 1 -or $app -ne 1 -or $profiles -gt 0) {
    Write-Host '        the archive would not unpack into a runnable app'
    exit 2
}
exit 0
