param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [ValidateSet('Windows', 'Android', 'Core', 'All')][string]$Platform = 'Windows',
    [switch]$RunModelTests
)

. (Join-Path $PSScriptRoot 'common.ps1')
Use-NeurosetDotnet
Push-Location $script:RepositoryRoot
try {
    if ($Platform -in @('Windows', 'All')) {
        dotnet build apps/windows/Neuroset.App.csproj -c $Configuration
        if ($LASTEXITCODE -ne 0) { throw 'Windows build failed.' }
    }
    if ($Platform -in @('Android', 'All')) {
        $androidArguments = Get-NeurosetAndroidArguments
        dotnet build apps/android/Neuroset.Android.csproj -c $Configuration @androidArguments
        if ($LASTEXITCODE -ne 0) { throw 'Android build failed.' }
    }
    if ($RunModelTests) { $env:NEUROSET_RUN_MODEL_TESTS = '1' }
    dotnet test tests/Neuroset.Tests/Neuroset.Tests.csproj -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw 'Core tests failed.' }
}
finally {
    Pop-Location
}
