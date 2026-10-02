param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [ValidateSet('win-x64', 'win-x86', 'win-arm64')][string]$Runtime = 'win-x64',
    [ValidatePattern('^[a-zA-Z0-9._-]*$')][string]$OutputName = ''
)

. (Join-Path $PSScriptRoot 'common.ps1')
Use-NeurosetDotnet
$platform = switch ($Runtime) { 'win-x86' { 'x86' } 'win-arm64' { 'ARM64' } default { 'x64' } }
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $script:RepositoryRoot "artifacts\$Configuration"))
$directoryName = if ($OutputName) { $OutputName } else { "unpackaged-$Runtime" }
$output = [IO.Path]::GetFullPath((Join-Path $artifactsRoot $directoryName))
if (-not $output.StartsWith($artifactsRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to clean an output path outside $artifactsRoot."
}
if (Test-Path -LiteralPath $output) {
    # Check the entire output before cleaning anything. A running application may
    # hold only a localized DLL open; deleting up to that file leaves a broken build.
    foreach ($file in Get-ChildItem -LiteralPath $output -Recurse -File) {
        try { $probe = [IO.File]::Open($file.FullName, 'Open', 'Read', 'None'); $probe.Dispose() }
        catch { throw "The output is in use: $($file.FullName). Close that build or choose -OutputName." }
    }
    Remove-Item -LiteralPath $output -Recurse -Force
}
Push-Location $script:RepositoryRoot
try {
    dotnet publish apps\windows\Neuroset.App.csproj -c $Configuration -r $Runtime `
        -p:Platform=$platform -p:NeurosetUnpackaged=true -p:PublishTrimmed=false `
        -p:PublishDir="$output\"
    if ($LASTEXITCODE -ne 0) { throw "Unpackaged publish failed with exit code $LASTEXITCODE." }
    $applicationPri = Join-Path $output 'Neuroset.App.pri'
    if (-not (Test-Path -LiteralPath $applicationPri)) {
        throw "Unpackaged publish is incomplete: $applicationPri was not produced."
    }
    Write-Host "Unpackaged application: $output\Neuroset.App.exe"
}
finally {
    Pop-Location
}
