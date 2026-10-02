param(
    [string]$Serial,
    [string]$OutputDirectory = 'artifacts/android-diagnostics'
)

. (Join-Path $PSScriptRoot 'common.ps1')
$androidSdk = if ($env:ANDROID_HOME) { $env:ANDROID_HOME } elseif ($env:ANDROID_SDK_ROOT) { $env:ANDROID_SDK_ROOT } elseif ($env:LOCALAPPDATA) { Join-Path $env:LOCALAPPDATA 'Android/Sdk' }
$adbName = if ($env:OS -eq 'Windows_NT') { 'adb.exe' } else { 'adb' }
$adb = if ($androidSdk -and (Test-Path (Join-Path $androidSdk "platform-tools/$adbName"))) { Join-Path $androidSdk "platform-tools/$adbName" } else { (Get-Command $adbName -ErrorAction Stop).Source }
$devices = @(& $adb devices | Select-String '^([^\s]+)\s+device$' | ForEach-Object { $_.Matches[0].Groups[1].Value })
if (-not $Serial) {
    if ($devices.Count -ne 1) { throw 'Connect one Android device with USB debugging enabled, or pass -Serial. Confirm the USB debugging prompt on the device.' }
    $Serial = $devices[0]
}
if ($Serial -notin $devices) { throw "Device $Serial is not connected or authorized for USB debugging." }
$output = [IO.Path]::GetFullPath((Join-Path $script:RepositoryRoot $OutputDirectory))
New-Item -ItemType Directory -Path $output -Force | Out-Null

# Keep device logs intact. Capture recent crash context and a bounded live window.
& $adb -s $Serial logcat -b crash -d -v threadtime > (Join-Path $output 'previous-crash.txt')
& $adb -s $Serial shell getprop ro.product.model > (Join-Path $output 'model.txt')
& $adb -s $Serial shell getprop ro.build.version.release > (Join-Path $output 'android-version.txt')
& $adb -s $Serial shell dumpsys package app.neuroset.client > (Join-Path $output 'package.txt')
$capturePath = Join-Path $output 'startup.txt'
$captureArguments = @('-s', $Serial, 'logcat', '-v', 'threadtime', '-T', '1', 'AndroidRuntime:E', 'NeurosetStartup:V', 'DOTNET:E', 'mono-rt:E', 'libc:F', '*:S')
$processOptions = @{ FilePath=$adb; ArgumentList=$captureArguments; RedirectStandardOutput=$capturePath; RedirectStandardError=(Join-Path $output 'capture-error.txt'); PassThru=$true }
if ($env:OS -eq 'Windows_NT') { $processOptions.WindowStyle = 'Hidden' }
$capture = Start-Process @processOptions
try {
    & $adb -s $Serial shell monkey -p app.neuroset.client -c android.intent.category.LAUNCHER 1 > (Join-Path $output 'launch.txt')
    Write-Host 'Recording startup for 15 seconds. If a diagnostic screen appears, use Copy error on the phone.'
    Start-Sleep -Seconds 15
}
finally {
    if (-not $capture.HasExited) { $capture | Stop-Process }
}
Write-Host "Diagnostic files: $output"
