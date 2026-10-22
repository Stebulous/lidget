<#
.SYNOPSIS
    Publishes Lidget for win-x64 and makes a Velopack release in artifacts/releases.

.DESCRIPTION
    The release workflow and local packaging both use this script.
    The NativeAOT publish of lidget-statusline.exe needs the Visual Studio C++ build tools.
    The script adds the folder of vswhere.exe to PATH, because the AOT linker looks for it there.

.EXAMPLE
    ./scripts/pack.ps1 -Version 0.1.0
#>
param(
    [Parameter(Mandatory)]
    [string] $Version,

    [string] $Output = (Join-Path $PSScriptRoot '..\artifacts')
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$publish = Join-Path $Output 'publish'
$releases = Join-Path $Output 'releases'

$installer = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
if ((Test-Path (Join-Path $installer 'vswhere.exe')) -and -not ($env:PATH -split ';' -contains $installer)) {
    $env:PATH = "$installer;$env:PATH"
}

function Invoke-Step([string] $Name, [scriptblock] $Command) {
    Write-Host "==> $Name"
    & $Command
    if ($LASTEXITCODE -ne 0) {
        throw "$Name failed with exit code $LASTEXITCODE"
    }
}

if (Test-Path $publish) {
    Remove-Item $publish -Recurse -Force
}

Invoke-Step 'Publish Lidget' {
    dotnet publish (Join-Path $root 'src\Lidget.App') -c Release -r win-x64 --self-contained -o $publish -p:Version=$Version
}

Invoke-Step 'Publish lidget-statusline' {
    dotnet publish (Join-Path $root 'src\Lidget.StatusLine') -c Release -r win-x64 -o $publish -p:Version=$Version
}

Invoke-Step 'Restore tools' {
    dotnet tool restore
}

Invoke-Step 'Pack' {
    dotnet vpk pack --packId Lidget --packVersion $Version --packDir $publish --mainExe Lidget.exe `
        --packTitle Lidget --shortcuts StartMenuRoot --packAuthors 'Lidget contributors' --outputDir $releases
}

Write-Host "Release files are in $releases"
