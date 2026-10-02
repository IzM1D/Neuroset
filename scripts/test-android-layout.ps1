param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [switch]$Visuals,
    [switch]$ConnectionKeys
)

. (Join-Path $PSScriptRoot 'common.ps1')
Use-NeurosetDotnet
if ($env:OS -ne 'Windows_NT') { throw 'This Skia regression host requires Windows.' }
$results = Join-Path $script:RepositoryRoot ("artifacts/qa/android-layout/" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $results -Force | Out-Null
$project = Join-Path $script:RepositoryRoot 'tests/Neuroset.App.SkiaTests/Neuroset.App.SkiaTests.csproj'
try { dotnet build $project -c $Configuration -v quiet *> (Join-Path $results 'build.log') }
catch { throw "Layout test host build failed. See $results/build.log" }
if ($LASTEXITCODE -ne 0) { throw "Layout test host build failed. See $results/build.log" }
$executable = Join-Path $script:RepositoryRoot "tests/Neuroset.App.SkiaTests/bin/$Configuration/net10.0/Neuroset.App.SkiaTests.exe"
$arguments = '"{0}"' -f $results
if ($Visuals) { $arguments += ' --visuals' }
if ($ConnectionKeys) { $arguments += ' --connection-keys' }
$process = Start-Process -FilePath $executable -ArgumentList $arguments `
    -WorkingDirectory (Split-Path $executable) -WindowStyle Hidden `
    -RedirectStandardOutput (Join-Path $results 'layout.log') `
    -RedirectStandardError (Join-Path $results 'errors.log') -Wait -PassThru
Get-Content -LiteralPath (Join-Path $results 'layout.log') -Tail 4
if ($process.ExitCode -ne 0) {
    Get-Content -LiteralPath (Join-Path $results 'errors.log')
    throw "Layout tests failed (exit $($process.ExitCode)). See $results"
}
if ($ConnectionKeys) { Write-Host "Connection key editor tests passed. Images, logs and isolated data: $results" }
elseif ($Visuals) { Write-Host "Visual tests passed. Images, logs and isolated data: $results" }
else { Write-Host "Layout tests passed. Logs and isolated data: $results" }
