# Cross-platform verification — 2026-09-09

## Android centered orb rotation: 1.0.4

The remaining drift was reproduced in Uno Skia. The actual APK's
`Uno.UI.Composition.dll` applies `Visual.Orientation` about the local origin,
without the translations around `CenterPoint` that WinUI performs. Consequently,
the ribbons' centers travelled outside the sphere while its clip concealed most
of each layer. This produced the crescent sweeping around the edge on the phone.

`CompositionTransforms.SetCenteredOrientation` now supplies
`Translation(-center) * Rotation(quaternion) * Translation(center)` on Android.
The original Windows quaternion keyframes, axes, periods, phases, gradients and
layer positions are retained. The Android timer uses the centered transform at
initialization and every frame; pointer tilt uses the same correction. Android
cleanup also removes the owned visual from its parent, avoiding Uno 6.7's null
dereference in `SetElementChildVisual(element, null)` when disposing the orb.

The composer is transparent again, matching the user's latest Windows reference.
The earlier gradient compatibility and window-inset fixes remain in place.

- `scripts/test-android-layout.ps1 -Visuals` passed. `OrbRotationProbe` checks the
  renderer's effective matrices for five layers at 33 phases each, including a
  full revolution per layer. The original rotation fails with 200.716339 dp of
  maximum center drift. The corrected maximum is 0.000048 dp; two rotated
  landmarks per layer also follow the expected trajectory within 0.000055 dp.
- Eleven rendered frames at 0, 3, ... 30 seconds were compared with a separate
  native WinUI host seeking the original quaternion animation controllers to the
  same times. Breathing and the moving highlight were held at their rest positions
  to isolate rotation. The band positions and intersections now agree throughout
  the sampled sequence, including the edge-on phases. This comparison checks
  motion as well as the shading checked for 1.0.3.
- The normal visual checks also passed: eight themes, phone/desktop sizes,
  translucent glow, advancing animation and identical frames while paused.
- All 24 keyboard/layout scenarios passed with the final shared XAML.
- The normal Windows Release build passed with zero warnings and errors after
  removing the temporary native capture hook from the ordinary build.
- No physical Android device was connected. Rendering verification uses the Uno
  Skia host, not the Xiaomi GPU or native Android keyboard.

Evidence: `artifacts/qa/android-layout/20260909-221238/` (final visuals),
`artifacts/qa/android-layout/20260909-221344/` (layout),
`artifacts/qa/android-layout/20260909-220745/` (failing rotation),
`artifacts/qa/android-orbits/native-20260909-221109/` (native WinUI frames), and
`artifacts/qa/android-orbits/comparison.png` (Windows / before / after).
The native capture targets, handshake script and source are in that same
`android-orbits` directory; they are excluded from normal builds.

ARM64 Release publish, APK v2/v3 signatures and 16-KB ZIP alignment passed.
The manifest reports 1.0.4 / versionCode 5 and the same application ID and signing
certificate as 1.0.3, allowing an update without uninstalling or clearing data.
The packaged app assembly contains the centered matrix implementation. APK:
`artifacts/Release/android-arm64/Neuroset-1.0.4-android-arm64.apk`, 74,222,318 bytes.
SHA-256: `AD1214F7A204E86B9A467ABE744504F66798152599B780B4FEE8529DE1F8BC54`.
Build log: `artifacts/android-1.0.4-publish.log`; signature and manifest logs:
`artifacts/qa/android-orbits/apk-{signature,manifest}.log`.

## Android visual parity fix: 1.0.3

The user's phone screenshot showed an almost uniformly purple orb and missing
ambient light. The same appearance was reproduced in the Skia test host before
the fix. Uno 6.7 treats `CompositionRadialGradientBrush.GradientOriginOffset` as an
absolute focus point, while WinUI treats it as an offset from `EllipseCenter`.
The default origin selected Uno's conical-gradient path, flattening the visible
orb layers and discarding transparent glow layers. `CompositionGradients` now
sets the coincident focus for Skia's radial shader, including the off-center core
highlight, while retaining WinUI's offset convention.

The shared composer also again uses its existing theme gradient, border and corner
radius resources. No raster replacement was introduced: the orb still consists of
the shared layered geometry with independent 3D axes, highlights and breathing
animation. Version 1.0.3 / versionCode 4 retains the 1.0.2 window-inset fix.

- `scripts/test-android-layout.ps1 -Visuals` passed: full-screen PNGs in all eight
  themes, phone and desktop sizes, plus light settings; varied orb shading and a
  translucent halo; advancing animation in Full mode and identical frames with
  animation disabled. Images were inspected in dark and light themes and compared
  with an isolated native WinUI window using the same source and stopped animation.
- The pixel check fails on the original orb capture (8 quantized opaque colors,
  358 halo pixels) and passes after correction (over 570 colors, 29,407 halo pixels).
- The 24 keyboard/layout regression scenarios passed after the shared visual
  changes, including fractional densities and keyboard show/hide.
- The normal Windows Debug and Release builds passed with 0 warnings and 0 errors. A separate
  native WinUI QA publish was used only for visual comparison; the user's running
  Windows application was not modified or stopped.
- Native Android GPU/device execution remains unverified here. Skia captures use
  the same Uno renderer but do not establish pixel-identical output on every GPU.

Visual results: `artifacts/qa/android-layout/20260909-200929/`; previous rendering:
`artifacts/qa/android-visuals/before/`; native Windows comparison:
`artifacts/qa/android-visuals/native-phone.png`. Layout results:
`artifacts/qa/android-layout/20260909-201248/`.

ARM64 Release publish, APK v2/v3 signatures and 16-KB ZIP alignment passed.
The manifest reports 1.0.3 / versionCode 4; the signing certificate is unchanged.
Install over the existing application to retain data. APK:
`artifacts/Release/android-arm64/Neuroset-1.0.3-android-arm64.apk`, 74,222,318 bytes.
SHA-256: `475AED3ABEC2FBAA5CDFA5B8A5AE543EF72E1B19D490BC2BDCE6C7C04ADB5BCB`.
Build log: `artifacts/android-1.0.3-publish.log`. A side-by-side comparison is saved
as `artifacts/qa/android-visuals/comparison.png` and the theme contact sheet as
`artifacts/qa/android-visuals/themes.png`.

## Android layout-cycle fix: 1.0.2

The report from a Xiaomi 2201116PG on Android 13 identified
`LayoutCycleException` during `TextInputConnection` → `TextBox.ProcessTextInput`
→ `TextBoxView.UpdateDisplayBlockText` → `UpdateLayout`.

The exception was reproduced in Uno Skia Win32 using the same `Uno.UI.dll` as
the APK, a 2.75 display density, fractional viewport dimensions and simulated
native system-bar bounds. The previous `SafeArea` attachment on the root Frame
changed its margin from `LayoutUpdated`, feeding the rounded content bounds back
into the inset calculation. Its bottom margin repeatedly cycled through
17.45453 → 17.38177 → 17.30902 → 17.23627 dp. Text input then exhausted Uno's
layout iteration limit with the same managed call stack as the device report.

The Android window now uses `WindowInsetsController`: it calculates margins from
OS window, visible-area and keyboard bounds, snaps to physical pixels, and updates
on native metric events. It does not calculate insets from the resulting content
size or subscribe to `LayoutUpdated`. Shared screens, themes and business logic
are unchanged; no exception suppression or iteration-limit increase was added.

- The original inset implementation fails the reproduction; the replacement passes.
- `scripts/test-android-layout.ps1` passed 24 viewport/density combinations in
  `tests/Neuroset.App.SkiaTests`: densities 1.25, 2.625, 2.75 and 3; phone/tablet
  and landscape sizes; system bars; keyboard animation/show/hide; typing and
  deletion in first-run fields, chat composer and connection settings. It also
  checks stable margins/sizes, retained text and the composer binding.
- Device keyboard events are simulated through Uno's input entry point. Native
  Android services are replaced in this test host. No physical Android execution
  was available, so confirmation on the user's Xiaomi remains necessary.
- Version is 1.0.2 / versionCode 3, with the existing application ID and signing
  key for an in-place update. If the native launcher still displays the saved
  1.0.1 exception after updating, use **Повторить запуск** to clear that old report.
- ARM64 Release publish passed. The packaged assembly uses the new inset
  controller; manifest inspection confirms versionCode 3, API 26 minimum and
  API 36 target. APK v2/v3 signatures and `zipalign -c -P 16 4` passed. The
  signing certificate SHA-256 remains
  `5b739069b246db8848a3a904900bf344def32004bc7e53d02086d3eba5d58b68`.

APK: `artifacts/Release/android-arm64/Neuroset-1.0.2-android-arm64.apk`,
74,222,318 bytes. SHA-256:
`D970F4C859894580905816F7009DCABDC33B5E1910717C1341D0AC52112E02D7`.
Build log: `artifacts/android-1.0.2-publish.log`.

Evidence: `artifacts/qa/android-startup/layout-baseline-native-error.log`,
`layout-baseline-native.log`, `layout-fixed.log`, and
`artifacts/qa/android-layout/20260909-174351/{layout,errors,build}.log`.
The final test-host build after adding explicit null checks is recorded in
`artifacts/qa/android-layout/final-build.log`.

## Android startup follow-up: 1.0.1

The user reported that 1.0.0 fails to start on Android. The precise error and device
log were not available during this session, so the device failure has not been
reproduced or confirmed fixed.

Version 1.0.1 selects the Android private files directory before shared startup,
sets an explicit .NET host content root and disables configuration file watching.
A native launcher shows a saved managed startup exception on the next launch,
with copy/retry actions. `scripts/diagnose-android.ps1` can capture logcat when no
managed report is available. The application ID and signing certificate are
unchanged, and versionCode increased to 2 for installation over 1.0.0.

- ARM64 Release build, APK v2/v3 signatures and ZIP alignment passed.
- Windows Debug build and 280 core tests passed after the shared startup hooks.
- The shared Android-conditional screens were compiled into a temporary Uno Skia
  Win32 probe. Its log reached `Startup: main page initialized successfully` with
  a new isolated SQLite database. Native Android services were substituted in
  this probe; this does not establish successful Android execution.
- API 29 and API 36 emulator attempts could not boot on this machine; there is no
  installed acceleration driver and software mode hung or crashed. No Android
  device is connected.

Candidate APK: `artifacts/Release/android-arm64/Neuroset-1.0.1-android-arm64.apk`,
74,222,318 bytes. SHA-256:
`054365A8BA3948FC7D3B096F4112C0D8936FF2DE8E51F7279F040BFB47437EC9`.

## Initial 1.0.0 verification

Verification ran on Windows x64 with .NET SDK 10.0.400. Android builds used
Uno SDK 6.7.22 / Uno 6.7.103, JDK 17 and Android SDK 36.

| Check | Result |
| --- | --- |
| Shared .NET suite (`dotnet test Neuroset.Core.slnx -c Debug`) | 280 passed, 0 failed |
| Windows x64 Release publish | Passed; self-contained output with application PRI and assets |
| Windows launch and saved history after restart | Passed with an isolated QA database |
| First-run setup, model discovery, streaming response | Passed against a local mock OpenAI-compatible provider |
| Markdown headings, emphasis, links, lists, code colors/font and math | Visually checked in the Windows application |
| Narrow chat and settings | Visually checked at 480 px window width; settings selector clears the window caption buttons |
| Android ARM64 Release publish | Passed; 73,807,211-byte APK |
| Android package inspection | API 26 minimum / API 36 target; launcher, permissions, SQLite, ONNX, Skia, bundled code font and license present |
| APK signature / ZIP alignment | v2 and v3 signatures valid; `zipalign -c -P 16 4` passed |
| Android device execution | Not verified: no connected device; the API 36 emulator could not boot without an available acceleration driver |

The standard test suite contains opt-in model/Python integration checks. The test
count above does not establish a real MiniLM download/inference run or a connection
to a paid model provider. Linux CI is configured but was not executed in this local
Windows session.

Android device acceptance remains necessary, especially for first launch, text
selection, keyboard/system insets, Back, provider file pickers, Keystore,
notifications, interactive HTML and the presentation editor. Uno reports two
unimplemented UI properties: `ItemsUpdatingScrollMode` and
`MenuFlyoutPresenter.SystemBackdrop`; scroll behavior during updates and popup
appearance should be included in that check.

See [platform behavior and build instructions](cross-platform.md) for Android
workspace copies, executable tools, background scheduling and signing limits.

Local outputs:

- `artifacts/Release/cross-platform-win-x64/Neuroset.App.exe` (keep the entire folder).
- `artifacts/Release/android-arm64/app.neuroset.client-Signed.apk` (development key).
- `artifacts/qa/cross-platform/tests/core.trx` and screenshots in the same QA folder.
- Build logs: `artifacts/windows-publish.log`, `artifacts/android-publish.log`.

APK SHA-256:
`ED64A4BE53AB332E01A46C0F0733895104D2292434E55893A336F70F972680E7`.
