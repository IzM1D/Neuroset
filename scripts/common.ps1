$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$script:RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

function Use-NeurosetDotnet {
    $profileDirectory = [Environment]::GetFolderPath('UserProfile')
    $dotnetName = if ($IsWindows -or $env:OS -eq 'Windows_NT') { 'dotnet.exe' } else { 'dotnet' }
    $localDotnet = Join-Path $profileDirectory ".dotnet/$dotnetName"
    if (Test-Path -LiteralPath $localDotnet) {
        $env:DOTNET_ROOT = Split-Path $localDotnet
        $env:PATH = "$env:DOTNET_ROOT$([IO.Path]::PathSeparator)$env:PATH"
    }
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw '.NET SDK 10.0.400 or newer is required. Install it from https://dotnet.microsoft.com/download.'
    }
}

function Get-NeurosetAndroidArguments {
    $arguments = @()
    $androidSdk = if ($env:ANDROID_HOME) { $env:ANDROID_HOME } elseif ($env:ANDROID_SDK_ROOT) { $env:ANDROID_SDK_ROOT } elseif ($env:LOCALAPPDATA) { Join-Path $env:LOCALAPPDATA 'Android/Sdk' }
    if ($androidSdk -and (Test-Path -LiteralPath $androidSdk)) { $arguments += "-p:AndroidSdkDirectory=$androidSdk" }
    $javaSdk = $env:JAVA_HOME
    if ($env:OS -eq 'Windows_NT') {
        $jdk = Get-ChildItem 'C:/Program Files/Eclipse Adoptium' -Directory -Filter 'jdk-17*' -ErrorAction SilentlyContinue | Sort-Object Name -Descending | Select-Object -First 1
        if ($jdk) { $javaSdk = $jdk.FullName }
    }
    if ($javaSdk) { $arguments += "-p:JavaSdkDirectory=$javaSdk" }
    return $arguments
}

function Get-WindowsSdkTool([string]$Name) {
    $sdkRoot = 'C:\Program Files (x86)\Windows Kits\10\bin'
    $tool = Get-ChildItem -LiteralPath $sdkRoot -Recurse -Filter $Name -ErrorAction SilentlyContinue |
        Where-Object { $_.Directory.Name -eq 'x64' } |
        Sort-Object { [version]$_.Directory.Parent.Name } -Descending |
        Select-Object -First 1
    if (-not $tool) { throw "$Name was not found. Install the Windows 10/11 SDK." }
    return $tool.FullName
}
