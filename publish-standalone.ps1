<#
.SYNOPSIS
Builds the standalone single-file executable of AIOptimizeSql.

.DESCRIPTION
Publishes the WebUI (which hosts the optimize engine in-process) as one
self-contained executable. The result needs no installed .NET runtime, stores
its data in a local SQLite database and opens a browser when started.

.PARAMETER Runtime
Target runtime identifier: win-x64 (default), linux-x64 or osx-arm64.

.EXAMPLE
./publish-standalone.ps1
./publish-standalone.ps1 -Runtime linux-x64
#>
param(
    [ValidateSet('win-x64', 'linux-x64', 'osx-arm64')]
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'

$project = Join-Path $PSScriptRoot 'src/Tedd.AIOptimizeSql.WebUI/Tedd.AIOptimizeSql.WebUI.csproj'
dotnet publish $project -p:PublishProfile="standalone-$Runtime"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$output = Join-Path $PSScriptRoot "publish/standalone-$Runtime"
Write-Host ""
Write-Host "Done. Standalone executable published to: $output"
