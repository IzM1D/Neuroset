param(
    [ValidateSet('win-x64', 'win-x86', 'win-arm64')][string]$Runtime = 'win-x64',
    [switch]$Uninstall
)

. (Join-Path $PSScriptRoot 'common.ps1')
$packageName = 'F7E963C5-0AF8-41DC-BEB8-21E4D7DB4255'

if ($Uninstall) {
    $command = "Get-AppxPackage -Name '$packageName' | Remove-AppxPackage"
    & powershell.exe -NoProfile -NonInteractive -Command $command
    Write-Host 'Neuroset package removed. User data under %LOCALAPPDATA%\Neuroset is intentionally preserved.'
    return
}

$packagePath = Join-Path $script:RepositoryRoot "artifacts\Release\package-$Runtime\Neuroset-1.0.0-$Runtime.msix"
$certificatePath = Join-Path $script:RepositoryRoot 'artifacts\certificates\Neuroset.Development.cer'
if (-not (Test-Path -LiteralPath $packagePath)) { throw "Package not found: $packagePath" }

if (Test-Path -LiteralPath $certificatePath) {
    $certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($certificatePath)
    $trusted = Test-Path -LiteralPath "Cert:\CurrentUser\Root\$($certificate.Thumbprint)"
    if (-not $trusted) {
        throw "Trust the development CER interactively in Current User\Trusted Root Certification Authorities first: $certificatePath"
    }
}

$escaped = $packagePath.Replace("'", "''")
& powershell.exe -NoProfile -NonInteractive -Command "Add-AppxPackage -Path '$escaped' -ForceApplicationShutdown"
Write-Host 'Neuroset installed successfully.'

