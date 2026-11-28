<#
.SYNOPSIS
  Build and publish the apilookup CLI binary (framework-dependent single-file to dotnet-api-lookup/scripts/ by default, or standalone Native AOT with -Aot).

.EXAMPLE
  .\tools\build.ps1
  .\tools\build.ps1 -Runtime win-x64
  .\tools\build.ps1 -Aot -Runtime win-x64
#>
[CmdletBinding()]
param(
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release",
    [switch]$Aot,
    [string]$OutputDir = ""
)

$ErrorActionPreference = "Stop"
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "src\ApiLookup\ApiLookup.csproj"
$exeName = if ($Runtime.StartsWith("win")) { "apilookup.exe" } else { "apilookup" }

$isHostMatch =
    ($Runtime.StartsWith("win") -and [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)) -or
    ($Runtime.StartsWith("linux") -and [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Linux)) -or
    ($Runtime.StartsWith("osx") -and [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::OSX))

if ($Aot) {
    $outDir = if ([string]::IsNullOrWhiteSpace($OutputDir)) { Join-Path $repoRoot "artifacts\apilookup-$Runtime-aot" } else { $OutputDir }
    $tempOut = Join-Path $repoRoot "src\ApiLookup\bin\$Configuration\publish-$Runtime-aot"

    Write-Host "== Publishing apilookup ($Runtime, standalone Native AOT) ==" -ForegroundColor Cyan
    dotnet publish $project -c $Configuration -r $Runtime `
        -p:PublishAot=true `
        -p:StripSymbols=true `
        -p:DebugType=none `
        -p:DebugSymbols=false `
        -o $tempOut
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish (Native AOT) failed" }

    $built = Join-Path $tempOut $exeName
    if (-not (Test-Path -LiteralPath $built)) {
        throw "Published AOT binary not found: $built"
    }

    New-Item -ItemType Directory -Force -Path $outDir | Out-Null
    $dst = Join-Path $outDir $exeName
    Copy-Item -LiteralPath $built -Destination $dst -Force

    $nativeLib = if ($Runtime.StartsWith("win")) { "e_sqlite3.dll" } elseif ($Runtime.StartsWith("osx")) { "libe_sqlite3.dylib" } else { "libe_sqlite3.so" }
    $nativeSrc = Join-Path $tempOut $nativeLib
    if (Test-Path -LiteralPath $nativeSrc) {
        $nativeDst = Join-Path $outDir $nativeLib
        Copy-Item -LiteralPath $nativeSrc -Destination $nativeDst -Force
        $nativeMb = [Math]::Round((Get-Item -LiteralPath $nativeDst).Length / 1MB, 2)
        Write-Host "Copied native library: $nativeDst ($nativeMb MB)" -ForegroundColor Gray
    }

    $sizeMb = [Math]::Round((Get-Item -LiteralPath $dst).Length / 1MB, 2)
    Write-Host "Published AOT: $dst ($sizeMb MB)" -ForegroundColor Green
    if ($isHostMatch) {
        & $dst --version --verbose
        if ($LASTEXITCODE -ne 0) { throw "Published AOT binary verification failed" }
    }
    return
}

$outDir = if ([string]::IsNullOrWhiteSpace($OutputDir)) { Join-Path $repoRoot "dotnet-api-lookup\scripts" } else { $OutputDir }
$tempOut = Join-Path $repoRoot "src\ApiLookup\bin\$Configuration\publish-$Runtime"

Write-Host "== Publishing apilookup ($Runtime, framework-dependent single-file) ==" -ForegroundColor Cyan
dotnet publish $project -c $Configuration -r $Runtime --no-self-contained `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=none `
    -p:DebugSymbols=false `
    -o $tempOut
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

$built = Join-Path $tempOut $exeName
if (-not (Test-Path -LiteralPath $built)) {
    throw "Published binary not found: $built"
}

New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$dst = Join-Path $outDir $exeName
Copy-Item -LiteralPath $built -Destination $dst -Force
$sizeMb = [Math]::Round((Get-Item -LiteralPath $dst).Length / 1MB, 2)
Write-Host "Published: $dst ($sizeMb MB)" -ForegroundColor Green
if ($isHostMatch) {
    & $dst --version --verbose
    if ($LASTEXITCODE -ne 0) { throw "Published binary verification failed" }
}
