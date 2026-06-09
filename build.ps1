#!/usr/bin/env pwsh
# Publish both AOT exes into .\dist alongside browsers.template.json.
# Equivalent to running:
#   dotnet publish src\BrowseRouter.Launcher -c Release -r <rid> -o dist
#   dotnet publish src\BrowseRouter.Host     -c Release -r <rid> -o dist
#
# Usage:
#   .\build.ps1                 # win-x64 (default)
#   .\build.ps1 -Rid win-arm64  # cross-publish to Windows on ARM
#
# AOT linking needs vswhere.exe (which then resolves the MSVC linker) reachable
# on PATH. The standard VS installer directory is prepended below so this script
# works outside a Developer Command Prompt too.
[CmdletBinding()]
param(
    [string]$Rid = 'win-x64',
    [string]$Configuration = 'Release',
    [string]$Output = 'dist'
)

$ErrorActionPreference = 'Stop'

$vswhereDir = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer'
if (Test-Path (Join-Path $vswhereDir 'vswhere.exe')) {
    if (($env:PATH -split ';') -notcontains $vswhereDir) {
        $env:PATH = "$vswhereDir;$env:PATH"
    }
}

Push-Location $PSScriptRoot
try {
    foreach ($proj in 'BrowseRouter.Launcher', 'BrowseRouter.Host') {
        dotnet publish "src\$proj" -c $Configuration -r $Rid -o $Output
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet publish failed for $proj (exit $LASTEXITCODE)"
        }
    }
}
finally {
    Pop-Location
}
