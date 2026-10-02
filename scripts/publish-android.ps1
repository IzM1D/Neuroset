param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [ValidateSet('android-arm64', 'android-x64')][string]$Runtime = 'android-arm64'
)

. (Join-Path $PSScriptRoot 'common.ps1')
Use-NeurosetDotnet
$output = Join-Path $script:RepositoryRoot "artifacts/$Configuration/$Runtime"
$androidArguments = Get-NeurosetAndroidArguments
Push-Location $script:RepositoryRoot
try {
    dotnet publish apps/android/Neuroset.Android.csproj -c $Configuration -r $Runtime `
        -p:RuntimeIdentifiers=$Runtime -p:AndroidPackageFormats=apk -p:PublishTrimmed=false -p:RunAOTCompilation=false `
        -o $output @androidArguments
    if ($LASTEXITCODE -ne 0) { throw 'Android publish failed.' }
    $apk = Get-ChildItem -LiteralPath $output -Filter '*-Signed.apk' | Select-Object -First 1
    if (-not $apk) { throw "Signed APK was not produced in $output." }
    Write-Host "APK (development signing key): $($apk.FullName)"
}
finally { Pop-Location }
