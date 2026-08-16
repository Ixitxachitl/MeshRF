<#
.SYNOPSIS
    Builds a self-contained MeshRF release for the host platform and packages it
    under dist/.

.DESCRIPTION
    Runs on Windows, Linux and macOS under PowerShell 7 (pwsh).

    1. Builds the native core with CMake, using the preset for the host OS.
    2. Publishes the managed app self-contained for the host RID (no .NET
       install needed on the target machine).
    3. Copies the native runtime libraries next to the published executable.
    4. Packages the staged folder: .zip on Windows/macOS, .tar.gz on Linux.

    The version defaults to <VersionPrefix> in Directory.Build.props. Pass -Tag
    to also create an annotated git tag (v<version>).

    NOTE: native libraries cannot be cross-compiled, so each platform's artifact
    must be built on that platform (or in CI — see .github/workflows/release.yml).
    The macOS path has never been run on real hardware; see the macos-arm64
    preset in CMakePresets.json.

.EXAMPLE
    pwsh scripts/build-release.ps1
    pwsh scripts/build-release.ps1 -Version 1.1.0 -Tag
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$NativeConfig,
    [switch]$Tag
)

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
Set-Location $repoRoot

# --- Host platform -------------------------------------------------------
# Single-file publish needs an explicit RID, and the CMake preset, library
# extension and archive format all vary by OS. Resolve them once.
if ($IsWindows -or $PSVersionTable.PSVersion.Major -le 5) {
    $platform     = 'windows'
    $rid          = 'win-x64'
    $cmakePreset  = 'windows-x64'
    # Multi-config generator (Visual Studio): binaries land in bin/<config>.
    $nativeConfig = if ($NativeConfig) { $NativeConfig } else { 'RelWithDebInfo' }
    $nativeBinDir = Join-Path $repoRoot "build/$cmakePreset/bin/$nativeConfig"
    $bridgeLib    = 'MeshRF.Native.dll'
    # Radio backends ship as DLLs on Windows; elsewhere they're system packages
    # resolved at runtime via dlopen.
    $extraLibs    = @('hackrf.dll', 'libusb-1.0.dll', 'pthreadVC2.dll', 'rtlsdr.dll')
    $archive      = 'zip'
}
elseif ($IsLinux) {
    $platform     = 'linux'
    $rid          = 'linux-x64'
    $cmakePreset  = 'linux-x64'
    $nativeConfig = if ($NativeConfig) { $NativeConfig } else { 'Release' }
    # Ninja is single-config: no per-config subdirectory.
    $nativeBinDir = Join-Path $repoRoot "build/$cmakePreset/bin"
    $bridgeLib    = 'libMeshRF.Native.so'
    $extraLibs    = @()
    $archive      = 'tar.gz'
}
elseif ($IsMacOS) {
    $platform     = 'macos'
    $rid          = if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'osx-arm64' } else { 'osx-x64' }
    $cmakePreset  = if ($rid -eq 'osx-arm64') { 'macos-arm64' } else { 'macos-x64' }
    $nativeConfig = if ($NativeConfig) { $NativeConfig } else { 'Release' }
    $nativeBinDir = Join-Path $repoRoot "build/$cmakePreset/bin"
    $bridgeLib    = 'libMeshRF.Native.dylib'
    $extraLibs    = @()
    $archive      = 'zip'
    Write-Warning 'The macOS build path is untested — no one has run it on real hardware yet.'
}
else {
    throw "Unsupported host platform."
}

# --- Preflight: linked protobuf schemas ----------------------------------
$protoSentinel = Join-Path $repoRoot 'third_party/meshtastic_protobufs/meshtastic/mesh.proto'
if (-not (Test-Path $protoSentinel)) {
    throw 'Missing Meshtastic protobuf schema files in third_party/meshtastic_protobufs. Initialize submodules with: git submodule update --init --recursive'
}

function Resolve-Cmake {
    $cmd = Get-Command cmake -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $fallback = 'C:\Program Files\CMake\bin\cmake.exe'
    if (Test-Path $fallback) { return $fallback }
    throw 'cmake not found on PATH.'
}
$cmake = Resolve-Cmake

# --- Resolve version -----------------------------------------------------
# The app's own VersionPrefix if it declares one, otherwise the repo default in
# Directory.Build.props.
function Resolve-AppVersion {
    param([string]$ProjectPath)

    if ($Version) { return $Version }   # explicit -Version overrides everything

    foreach ($file in @($ProjectPath, (Join-Path $repoRoot 'Directory.Build.props'))) {
        if (-not (Test-Path $file)) { continue }
        $xml = [xml](Get-Content $file)
        $v = ($xml.Project.PropertyGroup.VersionPrefix | Where-Object { $_ }) | Select-Object -First 1
        if ($v) { return $v }
    }
    throw "Could not resolve a version for $ProjectPath (no VersionPrefix in it or Directory.Build.props)."
}

Write-Host "Building MeshRF — $platform/$rid ($nativeConfig)" -ForegroundColor Cyan

# --- Don't fight a running instance for the shared library ---------------
# Warned about rather than done silently: this runs before any build step has
# proven it will succeed, so ending the user's session on every invocation is a
# bad trade for a build that might fail moments later.
$runningMeshRf = Get-Process -Name MeshRF -ErrorAction SilentlyContinue
if ($runningMeshRf) {
    Write-Host "==> Stopping running MeshRF instance(s) (PID $($runningMeshRf.Id -join ', ')) to release the native library" -ForegroundColor Yellow
    $runningMeshRf | Stop-Process -Force
}

# --- 1. Native build -----------------------------------------------------
Write-Host '==> Configuring + building native core' -ForegroundColor Yellow
# Visual Studio is a multi-config generator and picks the configuration at
# build time via --config. Ninja (Linux/macOS) is single-config: it ignores
# --config entirely, and with no CMAKE_BUILD_TYPE set it produces an
# unoptimized build — so the type has to be chosen here, at configure time.
if ($platform -eq 'windows') {
    & $cmake --preset $cmakePreset
} else {
    & $cmake --preset $cmakePreset -D "CMAKE_BUILD_TYPE=$nativeConfig"
}
if ($LASTEXITCODE) { throw "cmake configure failed ($LASTEXITCODE)" }
& $cmake --build "build/$cmakePreset" --config $nativeConfig --target mrf_bridge -j
if ($LASTEXITCODE) { throw "native build failed ($LASTEXITCODE)" }

# --- 2/3/4. Publish, bundle, package -------------------------------------
function New-Package {
    param(
        [string]$ProjectPath,
        [string]$ArtifactName,   # archive name stem, e.g. MeshRF
        [string]$PublishRid
    )

    $stage = Join-Path $repoRoot "dist/stage-$ArtifactName"
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    New-Item -ItemType Directory -Path $stage -Force | Out-Null

    $appVersion = Resolve-AppVersion -ProjectPath (Join-Path $repoRoot $ProjectPath)
    Write-Host "==> Publishing $ArtifactName v$appVersion ($PublishRid)" -ForegroundColor Yellow
    dotnet publish $ProjectPath `
        -c Release -r $PublishRid --self-contained true `
        -p:PublishSingleFile=true `
        -p:NativeConfig=$nativeConfig `
        -p:NativeBinDir=$nativeBinDir `
        -p:Version=$appVersion `
        --nologo -o $stage
    if ($LASTEXITCODE) { throw "dotnet publish failed ($LASTEXITCODE)" }

    Write-Host '==> Bundling native runtime libraries' -ForegroundColor Yellow
    $src = Join-Path $nativeBinDir $bridgeLib
    if (-not (Test-Path $src)) { throw "Required native library missing: $src" }
    Copy-Item $src -Destination $stage -Force
    foreach ($lib in $extraLibs) {
        $p = Join-Path $nativeBinDir $lib
        if (Test-Path $p) { Copy-Item $p -Destination $stage -Force }
        else { Write-Warning "Optional native library missing (skipped): $lib" }
    }

    Copy-Item (Join-Path $repoRoot 'LICENSE') -Destination $stage -Force
    Copy-Item (Join-Path $repoRoot 'README.md') -Destination $stage -Force

    # Linux has no equivalent of the PE icon resource, so the launcher entry is
    # what gives the app an icon in the desktop environment.
    if ($platform -eq 'linux') {
        Copy-Item (Join-Path $repoRoot 'icon.png') -Destination (Join-Path $stage 'MeshRF.png') -Force
        @(
            '[Desktop Entry]'
            'Type=Application'
            'Name=MeshRF'
            'Comment=Meshtastic over software-defined radio'
            'Exec=MeshRF'
            'Icon=MeshRF'
            'Terminal=false'
            'Categories=HamRadio;Network;'
        ) -join "`n" | Set-Content (Join-Path $stage 'MeshRF.desktop') -Encoding utf8
    }

    $base = "$ArtifactName-v$appVersion-$PublishRid"
    if ($archive -eq 'zip') {
        $out = Join-Path $repoRoot "dist/$base.zip"
        if (Test-Path $out) { Remove-Item $out -Force }
        Write-Host "==> Packaging $base.zip" -ForegroundColor Yellow
        Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $out -Force
    }
    else {
        $out = Join-Path $repoRoot "dist/$base.tar.gz"
        if (Test-Path $out) { Remove-Item $out -Force }
        Write-Host "==> Packaging $base.tar.gz" -ForegroundColor Yellow
        # Set the executable bit before archiving: tar preserves the mode, and
        # publish does not always mark the apphost runnable.
        chmod +x (Join-Path $stage 'MeshRF')
        # -C so the archive holds plain file names, not the stage path.
        tar -czf $out -C $stage .
        if ($LASTEXITCODE) { throw "tar failed ($LASTEXITCODE)" }
    }
    Remove-Item $stage -Recurse -Force

    $size = '{0:N1} MB' -f ((Get-Item $out).Length / 1MB)
    Write-Host "Release ready: $(Split-Path $out -Leaf) ($size)" -ForegroundColor Green
}

New-Package -ProjectPath 'app/MeshRF.App.Avalonia/MeshRF.App.Avalonia.csproj' `
            -ArtifactName 'MeshRF' -PublishRid $rid

# --- Optional git tag ----------------------------------------------------
if ($Tag) {
    $tagVersion = Resolve-AppVersion -ProjectPath (Join-Path $repoRoot 'app/MeshRF.App.Avalonia/MeshRF.App.Avalonia.csproj')
    $tagName = "v$tagVersion"
    if (git tag --list $tagName) {
        Write-Warning "Tag $tagName already exists; skipping."
    } else {
        git tag -a $tagName -m "MeshRF $tagName"
        Write-Host "Created git tag $tagName (push with: git push origin $tagName)" -ForegroundColor Green
    }
}
