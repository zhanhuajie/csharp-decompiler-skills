<#
.SYNOPSIS
  Build and publish the apilookup CLI binary into csharp-definition-lookup/scripts/.

.EXAMPLE
  .\tools\build.ps1
  .\tools\build.ps1 -Runtime win-x64
#>
[CmdletBinding()]
param(
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "src\ApiLookup\ApiLookup.csproj"
$outDir = Join-Path $repoRoot "csharp-definition-lookup\scripts"
$tempOut = Join-Path $repoRoot "src\ApiLookup\bin\$Configuration\publish-$Runtime"

Write-Host "== Publishing apilookup ($Runtime, framework-dependent single-file) ==" -ForegroundColor Cyan
dotnet publish $project -c $Configuration -r $Runtime --no-self-contained `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=none `
    -p:DebugSymbols=false `
    -o $tempOut
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

$exeName = if ($Runtime.StartsWith("win")) { "apilookup.exe" } else { "apilookup" }
$built = Join-Path $tempOut $exeName
if (-not (Test-Path -LiteralPath $built)) {
    throw "Published binary not found: $built"
}

New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$dst = Join-Path $outDir $exeName
Copy-Item -LiteralPath $built -Destination $dst -Force
$sizeMb = [Math]::Round((Get-Item -LiteralPath $dst).Length / 1MB, 2)
Write-Host "Published: $dst ($sizeMb MB)" -ForegroundColor Green
