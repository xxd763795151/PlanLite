param(
    [string]$OutputDir = "artifacts\publish",
    [switch]$NoRestore
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try {
    $dotnetRoot = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet"
    $dotnetExe = Join-Path $dotnetRoot "dotnet.exe"
    if (-not (Test-Path $dotnetExe)) {
        throw ".NET 10 SDK not found at $dotnetExe. Run dotnet-install.ps1 -Channel 10.0 first."
    }

    $env:DOTNET_ROOT = $dotnetRoot
    $env:PATH = "$dotnetRoot;$env:PATH"

    $publishArgs = @(
        "publish",
        "src\PlanLite\PlanLite.csproj",
        "-c", "Release",
        "-r", "win-x64",
        "--self-contained", "true",
        "--disable-build-servers",
        "-m:1",
        "-p:UseSharedCompilation=false",
        "-p:PublishSingleFile=true",
        "-p:IncludeNativeLibrariesForSelfExtract=true",
        "-p:EnableCompressionInSingleFile=true",
        "-o", $OutputDir
    )

    if ($NoRestore) {
        $publishArgs += "--no-restore"
    }

    & $dotnetExe @publishArgs
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }

    $exePath = Join-Path $OutputDir "PlanLite.exe"
    if (-not (Test-Path $exePath)) {
        throw "Expected output executable not found: $exePath"
    }

    $info = Get-Item $exePath
    $sizeMb = [Math]::Round($info.Length / 1MB, 2)
    Write-Host "Published portable single-file executable: $($info.FullName) ($sizeMb MB)"
}
finally {
    Pop-Location
}
