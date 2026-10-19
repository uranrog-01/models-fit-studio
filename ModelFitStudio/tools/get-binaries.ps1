<#
    For a build from source: fetches the two programs the repository does not store
    (llama.cpp in engine\ and tools\llmfit.exe) from the latest release zip of the app.

    Run from the ModelFitStudio folder:  powershell -ExecutionPolicy Bypass -File tools\get-binaries.ps1
#>
$ErrorActionPreference = 'Stop'
$repo = 'uranrog-01/models-fit-studio'
$here = Split-Path -Parent $PSScriptRoot

$release = Invoke-RestMethod "https://api.github.com/repos/$repo/releases/latest" -Headers @{ 'User-Agent' = 'ModelFitStudio' }
$asset = $release.assets | Where-Object { $_.name -like '*win-x64.zip' } | Select-Object -First 1
if (-not $asset) { throw "The latest release ($($release.tag_name)) has no win-x64 zip." }

$tmp = Join-Path ([IO.Path]::GetTempPath()) ("mfs-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp | Out-Null
try {
    $zip = Join-Path $tmp $asset.name
    Write-Host "Downloading $($asset.name) from $($release.tag_name)..."
    Invoke-WebRequest $asset.browser_download_url -OutFile $zip -UseBasicParsing
    Expand-Archive $zip -DestinationPath (Join-Path $tmp 'x')
    $x = Join-Path $tmp 'x'
    $root = if (Test-Path (Join-Path $x 'engine')) { $x } else { (Get-ChildItem $x -Directory | Select-Object -First 1).FullName }

    New-Item -ItemType Directory -Force -Path (Join-Path $here 'engine') | Out-Null
    Copy-Item (Join-Path $root 'engine\*') (Join-Path $here 'engine') -Recurse -Force
    Copy-Item (Join-Path $root 'llmfit.exe') (Join-Path $here 'tools\llmfit.exe') -Force
    Write-Host "Done: engine\ and tools\llmfit.exe are in place."
}
finally {
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
