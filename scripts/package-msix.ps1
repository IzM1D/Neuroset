param(
    [ValidateSet('win-x64', 'win-x86', 'win-arm64')][string]$Runtime = 'win-x64',
    [string]$CertificatePath,
    [securestring]$CertificatePassword,
    [switch]$CreateDeveloperCertificate,
    [switch]$TrustDeveloperCertificate
)

. (Join-Path $PSScriptRoot 'common.ps1')
Use-NeurosetDotnet
$platform = switch ($Runtime) { 'win-x86' { 'x86' } 'win-arm64' { 'ARM64' } default { 'x64' } }
$packageRoot = Join-Path $script:RepositoryRoot "artifacts\Release\package-$Runtime"
$publishRoot = Join-Path $packageRoot 'payload'
$packagePath = Join-Path $packageRoot "Neuroset-1.0.0-$Runtime.msix"
$certificateDirectory = Join-Path $script:RepositoryRoot 'artifacts\certificates'

if ($CreateDeveloperCertificate) {
    New-Item -ItemType Directory -Path $certificateDirectory -Force | Out-Null
    if (-not $CertificatePassword) { $CertificatePassword = ConvertTo-SecureString 'Neuroset-Local-Development' -AsPlainText -Force }
    $certificate = New-SelfSignedCertificate -Type Custom -Subject 'CN=Neuroset' -FriendlyName 'Neuroset local development' `
        -KeyUsage DigitalSignature -CertStoreLocation 'Cert:\CurrentUser\My' `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3')
    $CertificatePath = Join-Path $certificateDirectory 'Neuroset.Development.pfx'
    $publicPath = Join-Path $certificateDirectory 'Neuroset.Development.cer'
    Export-PfxCertificate -Cert $certificate -FilePath $CertificatePath -Password $CertificatePassword | Out-Null
    Export-Certificate -Cert $certificate -FilePath $publicPath | Out-Null
    if ($TrustDeveloperCertificate) {
        Import-Certificate -FilePath $publicPath -CertStoreLocation 'Cert:\CurrentUser\TrustedPeople' | Out-Null
        Write-Warning 'Windows also requires an interactive import of the CER into Current User\Trusted Root Certification Authorities before local MSIX installation.'
    }
}

if (-not $CertificatePath -or -not (Test-Path -LiteralPath $CertificatePath)) {
    throw 'Provide -CertificatePath or use -CreateDeveloperCertificate. The certificate subject must be CN=Neuroset.'
}
if (-not $CertificatePassword) { $CertificatePassword = Read-Host 'PFX password' -AsSecureString }

if (Test-Path -LiteralPath $publishRoot) { Remove-Item -LiteralPath $publishRoot -Recurse -Force }
New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null
Push-Location $script:RepositoryRoot
try {
    dotnet build apps\windows\Neuroset.App.csproj -c Release -p:PublishReadyToRun=false -p:PublishTrimmed=false
    dotnet publish apps\windows\Neuroset.App.csproj -c Release -r $Runtime `
        -p:Platform=$platform -p:NeurosetUnpackaged=true -p:PublishTrimmed=false `
        -p:PublishDir="$publishRoot\"
    if ($LASTEXITCODE -ne 0) { throw "MSIX payload publish failed with exit code $LASTEXITCODE." }
    $applicationPri = Join-Path $publishRoot 'Neuroset.App.pri'
    if (-not (Test-Path -LiteralPath $applicationPri)) {
        throw "MSIX payload is incomplete: $applicationPri was not produced."
    }

    Copy-Item apps\windows\bin\Release\net10.0-windows10.0.26100.0\$Runtime\AppxManifest.xml `
        (Join-Path $publishRoot 'AppxManifest.xml') -Force
    Copy-Item apps\windows\bin\Release\net10.0-windows10.0.26100.0\$Runtime\resources.pri `
        (Join-Path $publishRoot 'resources.pri') -Force
    $manifestPath = Join-Path $publishRoot 'AppxManifest.xml'
    [xml]$manifest = Get-Content -LiteralPath $manifestPath -Raw
    $namespace = [System.Xml.XmlNamespaceManager]::new($manifest.NameTable)
    $namespace.AddNamespace('f', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
    $runtimeDependency = $manifest.SelectSingleNode('/f:Package/f:Dependencies/f:PackageDependency', $namespace)
    if ($runtimeDependency) { [void]$runtimeDependency.ParentNode.RemoveChild($runtimeDependency) }
    $manifest.Save($manifestPath)
    Get-ChildItem -LiteralPath $publishRoot -Recurse -File |
        Where-Object { $_.Extension -in '.pdb', '.lib' } |
        Remove-Item -Force
    if (Test-Path -LiteralPath $packagePath) { Remove-Item -LiteralPath $packagePath -Force }
    $makeAppx = Get-WindowsSdkTool 'makeappx.exe'
    & $makeAppx pack /o /d $publishRoot /p $packagePath

    $plainPassword = [System.Net.NetworkCredential]::new('', $CertificatePassword).Password
    try {
        $signTool = Get-WindowsSdkTool 'signtool.exe'
        & $signTool sign /fd SHA256 /f $CertificatePath /p $plainPassword $packagePath
        $signature = Get-AuthenticodeSignature -FilePath $packagePath
        if (-not $signature.SignerCertificate -or $signature.SignerCertificate.Subject -ne 'CN=Neuroset') {
            throw 'The MSIX signature could not be read back.'
        }
        if ($signature.SignerCertificate.Subject -ne $signature.SignerCertificate.Issuer) {
            & $signTool verify /pa /v $packagePath
        }
        else {
            Write-Warning 'The MSIX has a valid self-signed development signature, but its chain remains untrusted until the CER is imported interactively.'
        }
    }
    finally {
        $plainPassword = $null
    }
    Write-Host "Signed MSIX: $packagePath"
    if ($CreateDeveloperCertificate) {
        Write-Host "Development certificate: $publicPath"
        Write-Host 'Before installation, import the CER interactively into Current User\Trusted Root Certification Authorities.'
    }
}
finally {
    Pop-Location
}
