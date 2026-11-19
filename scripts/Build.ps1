[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$OutputDirectory,

    [switch]$SkipSign
)

$ErrorActionPreference = 'Stop'

$scriptRoot = $PSScriptRoot
if ([string]::IsNullOrEmpty($scriptRoot)) {
    $scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
}

if ([string]::IsNullOrEmpty($OutputDirectory)) {
    $OutputDirectory = Join-Path $scriptRoot '..\artifacts'
}

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $scriptRoot '..'))
$outputRoot = [System.IO.Path]::GetFullPath($OutputDirectory)
$projectPath = Join-Path $repoRoot 'src\WidgetProvider\WidgetProvider.csproj'
$manifestPath = Join-Path $repoRoot 'src\WidgetProvider\Package.appxmanifest'
$publishDir = Join-Path $outputRoot 'publish'
$layoutDir = Join-Path $outputRoot 'layout'
$msixPath = Join-Path $outputRoot 'CustomWidgets.msix'

function Find-BuildTool {
    param([string]$Name)

    $packageRoot = Join-Path $env:USERPROFILE '.nuget\packages\microsoft.windows.sdk.buildtools'
    if (-not (Test-Path $packageRoot)) {
        return $null
    }

    return Get-ChildItem -Path $packageRoot -Recurse -Filter $Name -File |
        Where-Object { $_.FullName -match '\\x64\\' } |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
}

Write-Host '==> Cleaning output' -ForegroundColor Cyan
Remove-Item -Path $publishDir, $layoutDir -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -Path $msixPath -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $publishDir, $layoutDir | Out-Null

Write-Host '==> Publishing self-contained provider (win-x64)' -ForegroundColor Cyan
& dotnet publish $projectPath `
    --configuration $Configuration `
    --runtime win-x64 `
    --self-contained true `
    -p:WindowsAppSDKSelfContained=true `
    --output $publishDir

if ($LASTEXITCODE -ne 0) {
    throw 'dotnet publish failed.'
}

Write-Host '==> Assembling MSIX layout' -ForegroundColor Cyan
Copy-Item -Path (Join-Path $publishDir '*') -Destination $layoutDir -Recurse -Force
Copy-Item -Path $manifestPath -Destination (Join-Path $layoutDir 'AppxManifest.xml') -Force

# Debug symbols are not needed in the shipped package.
Get-ChildItem -Path $layoutDir -Filter '*.pdb' -Recurse -File | Remove-Item -Force

$makeAppx = Find-BuildTool -Name 'makeappx.exe'
if (-not $makeAppx) {
    throw 'makeappx.exe was not found. Restore the Microsoft.Windows.SDK.BuildTools NuGet package first.'
}

Write-Host '==> Packing MSIX' -ForegroundColor Cyan
& $makeAppx.FullName pack /d $layoutDir /p $msixPath /o
if ($LASTEXITCODE -ne 0) {
    throw 'makeappx failed.'
}

if (-not $SkipSign) {
    Write-Host '==> Signing MSIX with a development certificate' -ForegroundColor Cyan

    $signTool = Find-BuildTool -Name 'signtool.exe'
    if (-not $signTool) {
        throw 'signtool.exe was not found. Restore the Microsoft.Windows.SDK.BuildTools NuGet package first.'
    }

    $subject = 'CN=CustomWidgets Development'
    $certificate = Get-ChildItem Cert:\CurrentUser\My |
        Where-Object { $_.Subject -eq $subject -and $_.NotAfter -gt (Get-Date).AddDays(1) } |
        Select-Object -First 1

    if (-not $certificate) {
        $certificate = New-SelfSignedCertificate `
            -Type Custom `
            -Subject $subject `
            -KeyUsage DigitalSignature `
            -KeyExportPolicy Exportable `
            -FriendlyName 'CustomWidgets Widget Provider development' `
            -CertStoreLocation 'Cert:\CurrentUser\My' `
            -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}') `
            -NotAfter (Get-Date).AddYears(3)
    }

    $pfxPath = Join-Path $outputRoot 'dev-cert.pfx'
    $cerPath = Join-Path $outputRoot 'dev-cert.cer'
    $passwordPlain = 'CustomWidgetsDev!'
    $password = ConvertTo-SecureString $passwordPlain -AsPlainText -Force

    Export-PfxCertificate -Cert $certificate -FilePath $pfxPath -Password $password | Out-Null
    Export-Certificate -Cert $certificate -FilePath $cerPath | Out-Null

    & $signTool.FullName sign /fd SHA256 /f $pfxPath /p $passwordPlain $msixPath
    if ($LASTEXITCODE -ne 0) {
        throw 'signtool failed.'
    }
}

$sizeMb = [Math]::Round((Get-Item $msixPath).Length / 1MB, 1)
Write-Host ''
Write-Host "MSIX: $msixPath ($sizeMb MB)" -ForegroundColor Green
if (-not $SkipSign) {
    Write-Host "Development certificate: $(Join-Path $outputRoot 'dev-cert.cer')" -ForegroundColor Green
    Write-Host 'Install with: powershell -ExecutionPolicy Bypass -File .\scripts\Install.ps1 (as administrator)'
}
