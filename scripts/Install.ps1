[CmdletBinding()]
param(
    [string]$MsixPath,
    [string]$CertificatePath
)

$ErrorActionPreference = 'Stop'

$scriptRoot = $PSScriptRoot
if ([string]::IsNullOrEmpty($scriptRoot)) {
    $scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
}

if ([string]::IsNullOrEmpty($MsixPath)) {
    $MsixPath = Join-Path $scriptRoot '..\artifacts\CustomWidgets.msix'
}

if ([string]::IsNullOrEmpty($CertificatePath)) {
    $CertificatePath = Join-Path $scriptRoot '..\artifacts\dev-cert.cer'
}

$msix = [System.IO.Path]::GetFullPath($MsixPath)
$certificate = [System.IO.Path]::GetFullPath($CertificatePath)

if (-not (Test-Path $msix)) {
    throw "MSIX not found at '$msix'. Run .\scripts\Build.ps1 first."
}

if (-not (Test-Path $certificate)) {
    throw "Development certificate not found at '$certificate'. Run .\scripts\Build.ps1 first."
}

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Please run this script from an elevated PowerShell window (certificate trust requires administrator rights).'
}

$developerMode = (Get-ItemProperty `
    -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock' `
    -Name AllowDevelopmentWithoutDevLicense `
    -ErrorAction SilentlyContinue).AllowDevelopmentWithoutDevLicense

if ($developerMode -ne 1) {
    Write-Warning 'Developer Mode is not enabled. Enable it under Settings > System > For developers, then run this script again.'
}

Write-Host '==> Trusting the development certificate (LocalMachine\TrustedPeople)' -ForegroundColor Cyan
try {
    Import-Certificate -FilePath $certificate -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' -Confirm:$false | Out-Null
}
catch {
    Write-Warning "The certificate could not be imported again (it may already be trusted): $($_.Exception.Message)"
}

Write-Host '==> Installing the MSIX package' -ForegroundColor Cyan
Add-AppxPackage -Path $msix -ForceUpdateFromAnyVersion -ForceApplicationShutdown

$package = Get-AppxPackage -Name 'CustomWidgets.WidgetProvider'
if (-not $package) {
    throw 'The package was not registered. Check the MSIX signature and the Event Viewer (AppxDeployment-Server).'
}

Write-Host ''
Write-Host "Installed: $($package.PackageFullName)" -ForegroundColor Green
Write-Host ''
Write-Host 'Open the Widgets Board with Win+W, choose "Widgets hinzufügen" and add "Jetzt läuft" or "System".'
Write-Host 'Lock screen: Einstellungen > Personalisierung > Sperrbildschirm > Widgets.'
