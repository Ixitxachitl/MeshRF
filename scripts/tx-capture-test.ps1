<#
.SYNOPSIS
  One-shot TX self-test: capture exactly what the app transmits and decode it
  offline, without any radio hardware.

.DESCRIPTION
  1. Sets MRF_TX_CAPTURE so Core::transmit() writes the final device-rate IQ
     (post resample + offset-mix + normalize) to a .cf32 file.
  2. Launches the WPF app.
  3. You press a Send button (text message OR node info) ONE time, then close
     the app window.
  4. The script runs scripts/analyze_capture.py on the dump and prints whether
     the LoRa preamble/header decodes.

  Interpreting the result:
    * "Found N preamble-like runs" with a stable bin, plus a header line
        -> our DSP/modulation is correct. Any real-device failure is then an
           RF issue (antenna, frequency, the receiver's tuning/preset).
    * No preamble runs / nothing stable
        -> the bug is still in our transmit DSP (symbol mapping / resample),
           and more TX power will not help.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\tx-capture-test.ps1
#>

[CmdletBinding()]
param(
    # Device sample rate the capture is recorded at (Core uses 2.4 MS/s).
    [int]$SrcRate = 2400000,
    # Offset-tuning frequency the TX places the channel at (kLoOffsetHz).
    [int]$FreqOffset = 500000,
    # Where to write the IQ dump.
    [string]$CaptureFile = "tx.cf32"
)

$ErrorActionPreference = 'Stop'

# Resolve repo root (this script lives in <root>\scripts).
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$capturePath = Join-Path $root $CaptureFile
if (Test-Path $capturePath) { Remove-Item $capturePath -Force }

Write-Host ""
Write-Host "=== MeshRF TX capture self-test ===" -ForegroundColor Cyan
Write-Host "Capture file : $capturePath"
Write-Host "Source rate  : $SrcRate Hz"
Write-Host "Freq offset  : $FreqOffset Hz"
Write-Host ""
Write-Host "STEPS:" -ForegroundColor Yellow
Write-Host "  1. The app will open in a moment."
Write-Host "  2. Make sure a HackRF is selected (so transmit is enabled)."
Write-Host "  3. Press 'Send' on a channel (or the 'Node info' button) ONE time."
Write-Host "  4. CLOSE the app window to continue the analysis."
Write-Host ""

# Export the capture trigger for the child process only.
$env:MRF_TX_CAPTURE = $capturePath

Write-Host "Launching app... (close its window when you've sent one packet)" -ForegroundColor Green
dotnet run --project app/MeshtasticRF.App/MeshtasticRF.App.csproj -c Debug --no-build

# Clean up the env var for this shell.
Remove-Item Env:\MRF_TX_CAPTURE -ErrorAction SilentlyContinue

Write-Host ""
if (-not (Test-Path $capturePath)) {
    Write-Host "No capture file was written." -ForegroundColor Red
    Write-Host "That means transmit() never ran. Check that:" -ForegroundColor Red
    Write-Host "  * a HackRF was selected (not the Null/None device), and"
    Write-Host "  * you actually pressed Send / Node info before closing."
    exit 1
}

$size = (Get-Item $capturePath).Length
Write-Host ("Capture written: {0:N0} bytes" -f $size) -ForegroundColor Green
Write-Host ""
Write-Host "=== Analyzing transmitted IQ ===" -ForegroundColor Cyan
python scripts/analyze_capture.py $capturePath $SrcRate $FreqOffset

Write-Host ""
Write-Host "=== How to read this ===" -ForegroundColor Cyan
Write-Host "  Preamble run(s) + a header line  -> DSP is GOOD; remaining issue is RF."
Write-Host "  No stable preamble runs          -> DSP/modulation bug; power won't help."
