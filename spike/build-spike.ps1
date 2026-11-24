# Builds every spike program into spike\out (x64, .NET 10) and spike\out-x86 (MXAccess, .NET Framework 4.8).
# Run from the repository root on the test VM:  powershell -ExecutionPolicy Bypass -File spike\build-spike.ps1
param([string]$MxAccessDll = "")

$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

dotnet build "$here\Spike.slnx" -c Release -o "$here\out"
if ($LASTEXITCODE -ne 0) { throw "Spike.slnx build failed" }

$mx = @("build", "$here\Spike.MxAccess\Spike.MxAccess.csproj", "-c", "Release", "-o", "$here\out-x86")
if ($MxAccessDll) { $mx += "-p:MxAccessDll=$MxAccessDll" }
dotnet @mx
if ($LASTEXITCODE -ne 0) { Write-Warning "MXAccess spike did not build. See spike\README.md, step 0." }

Write-Host ""
Write-Host "Built. Programs: spike\out\spike-omi.exe, spike-opcua.exe, spike-historian.exe, spike-sim.exe, spike\out-x86\spike-mxaccess.exe"
