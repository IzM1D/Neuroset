# Windows and Android monorepo

Both application hosts compile the same XAML, view models, Markdown parser, themes,
conversation flows and controls from `src/App/Neuroset.App.Shared.projitems`.
Windows uses native WinUI 3. Android uses Uno Platform with the Skia renderer.
The Android application connects directly to the user's configured API; a Windows
computer or a separate Neuroset server is not required.

```text
apps/windows/          Windows entry point, window chrome, MSIX manifest
apps/android/          Android entry point, manifest, Keystore and file adapters
src/App/               Shared XAML, controls, view models, themes and localization
src/Platform.Windows/  Windows notifications and image decoding
src/Application/       Shared contracts, policies and application data paths
src/Domain/            Shared models
src/Agent/             Chat, Coding, Roleplay and evaluation runtimes
src/Providers/         OpenAI-compatible connections and image API
src/Tools/             Agent tools, RAG, presentations and artifacts
src/Storage/           SQLite repositories
src/Security/          Permission policy and Windows DPAPI implementation
src/Infrastructure/    Shared dependency injection and scheduler
python/                Independent evaluation CLI
tests/                 Portable .NET tests and an opt-in Skia layout test host
scripts/               Build, publish and package commands
```

`Neuroset.slnx` contains both applications and every library.
`Neuroset.Core.slnx` builds and tests the shared libraries without Windows or Android
workloads. NuGet versions are pinned centrally in `Directory.Packages.props`;
the Android project pins Uno SDK 6.7.22 and its matching framework dependencies.

## Build

Use the .NET SDK selected by `global.json` (10.0.400). Windows builds also require
the Windows SDK. Android builds require the `android` workload, JDK 17, Android
SDK platform 36 and build-tools 36.0.0. The minimum supported Android version is
8.0 (API 26). Supported Android architectures are arm64 and x86_64.

```powershell
dotnet workload install android
sdkmanager 'platforms;android-36' 'build-tools;36.0.0'

./scripts/build.ps1 -Platform Core
./scripts/build.ps1 -Platform Windows
./scripts/build.ps1 -Platform Android
./scripts/build.ps1 -Platform All

./scripts/publish-unpackaged.ps1 -Runtime win-x64
./scripts/publish-android.ps1 -Runtime android-arm64
./scripts/publish-android.ps1 -Configuration Debug -Runtime android-x64
```

Set `ANDROID_HOME` and `JAVA_HOME` if the SDK/JDK are installed in custom locations.
The PowerShell scripts also detect the usual Windows Android SDK and Temurin JDK
17 installation. Core tests can run directly with `dotnet test Neuroset.Core.slnx`.
Process integration tests require Git and PowerShell (built into Windows; `pwsh`
on Linux/macOS). CI runs core tests on Linux and builds Windows and Android separately.

On Windows, `./scripts/test-android-layout.ps1` runs the Android window and shared
screens in Uno's Skia Win32 host with isolated test data. It simulates fractional
display densities, system bars, keyboard animation and text input in the first-run
form, composer and connection settings. This regression test exercises actual
Skia layout, but substitutes native Android services and is not a device test.
With `-Visuals`, the script instead captures the shared screen in all eight themes
and verifies the orb's shading, translucent glow, animation and pause behavior.
It also checks the centered trajectory of all five rotating layers over full
revolutions and saves eleven frames at fixed times for comparison with WinUI.
Run both modes to check visuals and keyboard layout. PNGs and logs are saved
alongside the isolated test data under `artifacts/qa/android-layout`.

The generated APK uses the local Android development signing key. It is suitable
for testing/sideloading, not a Play Store release. For distribution, provide a
persistent private signing key through MSBuild's `AndroidSigningKeyStore`,
`AndroidSigningKeyAlias`, `AndroidSigningKeyPass`, `AndroidSigningStorePass` and
`AndroidKeyStore=true`. Keep keys/passwords out of the repository; changing the
signing key prevents in-place updates of existing installations.

```powershell
adb install -r ./artifacts/Release/android-arm64/app.neuroset.client-Signed.apk
```

## Platform behavior

| Area | Windows | Android |
| --- | --- | --- |
| Chat, models, streaming, cancellation, history, search, editing, Roleplay | Shared implementation | Shared implementation |
| Themes, accent, density, scale, Markdown, code highlighting | WinUI controls | Shared renderer with Uno text adapter |
| Images, presentations, widgets, HTML, local RAG, memory | Shared tools and native adapters | Shared tools and native adapters |
| API keys | Existing DPAPI-protected `secrets.dat` | AES-GCM ciphertext; non-exportable key in Android Keystore |
| Attachments and export | Windows file pickers | Android document providers, read/written as streams |
| Coding workspace | Selected directory, ZIP export | Private copy of a document-provider folder; ZIP export from chat settings; original folder is not modified |
| Python, Git, MCP subprocesses, executable tools | Require installed executables; Coding may use user-level OS permissions | Android app sandbox applies; desktop executables and unrestricted host access are not supplied by the APK |
| Scheduled tasks | Run while the app is active, resume after restart | Same scheduler; Android may suspend or terminate a background app |
| Notifications | Windows notifications | Android notification channel; Android 13+ requires notification permission |

Phone layouts use an overlay conversation drawer and a section selector for
settings. Dialogs and forms fit narrow viewports, including the initial connection
form and the presentation editor. Tablets retain the wider layout. Android Back
closes open application dialogs, settings or the conversation drawer first.
The Android layout respects status/navigation bars, display cutouts and the on-screen keyboard.

The shared orb and ambient lights use native composition gradients on Windows.
On Android, `CompositionGradients` maps the gradient focus to Uno Skia's coordinate
convention so transparent halos and shaded layers render as intended. The geometry,
colors, independent rotation axes, themes and animation preferences remain shared.

The APK allows HTTP connections so local OpenAI-compatible servers remain usable.
Use HTTPS for remote providers. On an Android emulator, `10.0.2.2` refers to the
development computer; `localhost` refers to the Android device itself.

## Data compatibility and verification

Windows continues to use `%LOCALAPPDATA%\Neuroset`, the same SQLite schema and
the same DPAPI entropy, so moving the source projects does not reset existing
conversations or credentials. Android stores the same database schema inside its
private application directory; automatic backups are disabled because Keystore
keys are tied to the installation. No automatic device synchronization is added.

`NEUROSET_DATA_DIRECTORY` can select an absolute data directory for an isolated
Windows QA launch. It applies to the database, secrets, attachments, artifacts,
models, logs and language preference.

For device acceptance testing, check first-run connection setup, model discovery,
streaming and Stop, restart/history, all eight themes, narrow settings, importing a
document from a provider, image/PPTX/ODP saving and opening, notification permission,
HTML interaction and the presentation editor. Build success alone does not verify
Android device behavior.

The [local verification report](cross-platform-verification.md) records the built
artifacts, completed checks and remaining device acceptance work.

## Android startup diagnostics

Android 1.0.1 resolves storage from the application's private files directory and
uses that directory as the .NET host content root. Configuration file watching is
disabled on Android. Managed startup exceptions are stored locally; reopening the
app displays a native diagnostic screen with **Copy error** and **Retry startup**.
The screen does not require XAML to initialize and does not remove application data.

If Android closes the app before that report can be written, connect the phone
with USB debugging enabled and run `./scripts/diagnose-android.ps1`. This collects
the Android version, package details and startup/crash logs without clearing the
device log or the application's data. Read the report before sharing it: exception
details can contain local file paths or provider addresses.
