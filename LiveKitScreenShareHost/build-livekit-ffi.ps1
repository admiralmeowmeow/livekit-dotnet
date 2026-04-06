param(
    [string]$RustSdkPath = (Join-Path $PSScriptRoot '..\rust-sdks'),
    [string]$Configuration = 'release'
)

$ErrorActionPreference = 'Stop'
$projectRoot = Join-Path $PSScriptRoot 'src'
$nativeOutput = Join-Path $projectRoot 'native'

if (-not (Get-Command cargo -ErrorAction SilentlyContinue)) {
    throw 'cargo was not found on PATH. Install Rust with rustup first.'
}

Push-Location $RustSdkPath
try {
    if ($Configuration -ieq 'release') {
        cargo build -p livekit-ffi --release
        $outputDir = Join-Path $RustSdkPath 'target\release'
    }
    else {
        cargo build -p livekit-ffi --profile $Configuration
        $outputDir = Join-Path $RustSdkPath "target\$Configuration"
    }
}
finally {
    Pop-Location
}

New-Item -ItemType Directory -Force -Path $nativeOutput | Out-Null
Get-ChildItem -Path $outputDir -Filter '*.dll' | Copy-Item -Destination $nativeOutput -Force
Write-Host "Copied native FFI binaries to $nativeOutput"
