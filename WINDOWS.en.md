<!-- Language switch: one badge per line (they render side by side). Keep the same list in every WINDOWS*.md; the current language uses the darker color. -->
[![日本語](https://img.shields.io/badge/lang-%E6%97%A5%E6%9C%AC%E8%AA%9E-e05d44)](WINDOWS.md)
[![English](https://img.shields.io/badge/lang-English-003d7a)](WINDOWS.en.md)
[![한국어](https://img.shields.io/badge/lang-%ED%95%9C%EA%B5%AD%EC%96%B4-2ea44f)](WINDOWS.ko.md)

# FlugelKranzWin: installation and development guide

This repository targets Windows x64 / SteamVR. For the Linux / Monado / WiVRn version, see the [parent repository](https://github.com/ReinaS-64892/FlugelKranz). The controls and inertia behavior are described in the [README](README.en.md).

## Requirements

- Windows x64, SteamVR, and an HMD and left / right controllers that SteamVR can track.
- The dedicated SteamVR driver from this repository.
- For building: .NET 10 SDK, MSVC x64 Build Tools, Windows SDK, CMake.

The distributed app is self-contained, so you do not need to install the .NET SDK separately just to run it. The setup currently tested on real hardware is Quest 2 + Touch / Virtual Desktop.

## Install from the release zip

To use it without building, download `FlugelKranzWin-<version>-win-x64.zip` (for example `FlugelKranzWin-v0.9.0-win-x64.zip`) from [Releases](https://github.com/Qwerty0525/FlugelKranzWin/releases). It contains a single `FlugelKranzWin` folder: a release build with the same layout as the build output described below (`FlugelKranz.exe`, `driver/flugelkranz`, `install-driver.ps1`), including the licenses and documents. You do not need the .NET SDK or CMake. The driver settings file `default.vrsettings` already has the tested [frame pose correction](#frame-pose-correction-settings) settings below, so you can use it without editing it.

1. Quit SteamVR.
2. Extract the zip (Extract All) and put the `FlugelKranzWin` folder where it will stay. The driver is registered by this folder's location, so do not run it from inside the zip.
3. If you have already registered a FlugelKranz driver from another folder, such as an older version or an upstream build, run `./install-driver.ps1 -Uninstall` in that folder so that the driver is not registered in two places.
4. Open PowerShell in the `FlugelKranzWin` folder (right-click the folder → "Open in Terminal") and register the driver.
5. Start SteamVR, check that the HMD and both hands are tracked, then run the diagnostics and launch the app. For what to do next, see [First steps](README.en.md#first-steps) in the README.

```powershell
powershell -ExecutionPolicy Bypass -File ./install-driver.ps1
# After starting SteamVR:
./FlugelKranz.exe --diagnose | Out-Host
./FlugelKranz.exe
```

`-ExecutionPolicy Bypass` allows the script for this one run only and does not change any Windows setting. Where scripts are allowed to run, `./install-driver.ps1` does the same. If Windows SmartScreen warns you when you open `FlugelKranz.exe` from File Explorer, choose "More info" → "Run anyway". Head pilot in F mode (enabled by default) needs the SteamVR developer setting **Experimental overlay input overrides** (see the [README](README.en.md#head-pilot-prototype-windows--f-mode)).

To update, quit FlugelKranz and SteamVR, then copy the contents of the new `FlugelKranzWin` folder over your current folder (the existing registration keeps working). If you edited `default.vrsettings`, it is replaced, so check it again. To move to a different folder, move the registration as described in [Updating and unregistering](#updating-and-unregistering).

## Building and first install

Run this from the repository root.

```powershell
./build-windows.ps1
# If they are not on PATH:
# ./build-windows.ps1 -Dotnet 'C:/path/to/dotnet.exe' -CMake 'C:/path/to/cmake.exe'
# Test build for flight / compass testing (driverAudit = true, compass on by default):
# ./build-windows.ps1 -TestBuild
# Output somewhere other than the deployed folder (artifacts/windows-x64), e.g. to make a release zip:
# ./build-windows.ps1 -OutputDir artifacts/release-v0.9.0
```

A normal (release) build writes `driverAudit: false` into the deployed driver settings `default.vrsettings`, and a build with `-TestBuild` writes `driverAudit: true` (a `DriverAudit` line every second in `vrserver.txt`). Both overwrite the deployed value on every build, so a value you changed with "Detailed diagnostics" (「詳細診断」) in the app is also reset by a build.
`-TestBuild` also builds the app with the test profile (`FlugelKranzBuildProfile=test`, in FlugelKranz.dll's assembly metadata), and settings that have not been saved yet start with the gravity compass on. In release builds it is off. Values already saved in `config.json` are used as they are in either build.

The output goes to `artifacts/windows-x64`. Deploy the whole folder, and decide where it will live before registering it. The app name is `FlugelKranz` and the executable is `FlugelKranz.exe`.

1. Quit SteamVR.
2. Apply the frame pose correction settings below to the deployed driver's settings file.
3. Run the registration script in the distribution folder.
4. Start SteamVR and check that the HMD and both hands are tracked.
5. After running the diagnostics, launch the app.

```powershell
cd artifacts/windows-x64
./install-driver.ps1
# After the settings and registration are done and SteamVR is running:
./FlugelKranz.exe --diagnose | Out-Host
./FlugelKranz.exe
```

The registration script uses SteamVR's `vrpathreg`. It does not quit or restart SteamVR, and it does not add any auto-start setting. If you have disabled the driver in SteamVR's add-on management, enable it.

## Frame pose correction settings

Set `driver/flugelkranz/resources/settings/default.vrsettings` in the deployed folder before starting SteamVR. The configuration with which black edges and darkening were confirmed to improve on real hardware is as follows.

```json
{
  "driver_flugelkranz": {
    "loadPriority": 100,
    "observeFrames": true,
    "correctFramePose": true,
    "timeBasedFramePose": true
  }
}
```

**In the source and in the distribution, the defaults have all three of the boolean settings above, tested on real hardware, set to true.** The build script copies the settings from the source to the deployed folder, so check them again after rebuilding or updating. If SteamVR has a user setting with the same name, that one takes precedence.

`correctFramePose` enables correction of the HMD pose passed to rendering and streaming. `timeBasedFramePose` is a method that interpolates / predicts the physical pose for the frame's predicted time, and is used together with `correctFramePose=true`. `observeFrames` enables the observation hook. If both `observeFrames` and `correctFramePose` are false, no frame hook is added.

The flown head pose passed to the game differs from the physical head pose used for display correction. The pose-update hook alone could not keep this display path consistent. For identity transforms, the original frame pose is passed through unchanged, and while a transform is held, the correction keeps the original frame prediction. After these fixes, the known wobble has been reported as resolved on real hardware.

`timeBasedFramePose=false` goes back to the older history-matching method. That method had a problem where the correction came off when the head and the flight pose moved at the same time. For details, see the [driver documentation](native/driver/README.md) (Japanese).

## Updating and unregistering

To update, quit FlugelKranz and SteamVR, and build and deploy while the DLL is not in use. Check the whole distribution folder and the correction settings, then start SteamVR, the game and FlugelKranz. If you moved the deployed folder, unregister the old location and register the new one.

To unregister, run this in the distribution folder.

```powershell
./install-driver.ps1 -Uninstall
```

Restart SteamVR after unregistering. The script itself does not restart it.

## Controls, settings and diagnostics

The app starts OFF. `I` is infinite walking and `F` is free flight. On Touch, pressing the grip is Drag and pressing left X / right A is Turn. OFF stops flight and inertia. In F mode with Drag enabled, you can still grab and move while OFF (no inertia on release). If Drag is disabled too, the current transform is held. `↻` resets the transform. For one-hand / two-hand controls and the inertia settings in detail, see the [README](README.en.md#controls-and-behavior).

```powershell
./FlugelKranz.exe --help
./FlugelKranz.exe --bindings
./FlugelKranz.exe --diagnose
```

`FlugelKranz.exe` is a GUI app (WinExe), so launching it without arguments does not open a console window. With options, it writes to the terminal it was started from (if it was not started from a terminal, it opens its own console, which closes with Enter). PowerShell does not wait for GUI apps to exit, so to wait for the result and the exit code, pipe it, as in `./FlugelKranz.exe --diagnose | Out-Host` (you can then check the exit code with `$LASTEXITCODE`).

`--bindings` opens SteamVR's controller bindings. `--diagnose` checks the connection, the physical poses and the logical input without opening the UI. When diagnosing, quit any other FlugelKranz and make sure the HMD and both controllers can be tracked. If the inputs are not all available, it exits with a non-zero code.

"Gravity compass" (「重力コンパス」) in the settings panel shows a see-through ball (a navball) as a SteamVR overlay above the back of the chosen hand (the right hand by default, about 4.5 cm across, so that it does not overlap a watch-style overlay on the left wrist; you can switch hands in the settings at any time, and the change applies immediately). It draws only the half facing you, tints the part above the real horizon like the sky and the part below like the ground, and draws a bright horizon line where they meet. Real down (gravity) is shown as a glowing dot (a faint ring when it is on the far side of the ball), and a small glowing triangle outside the edge always points toward real down. The small white dot is the direction of the flown world's down (a faint ring on the far side); before you fly it overlaps the glowing dot (toggle it with "Show world down" (「ワールドの下を表示」)). Even when 3-axis rotation tilts the world, the ball shows the orientation of the real floor. For new settings, the compass is off in release builds and on in `-TestBuild` builds (saved values are kept). When it is shown, by default it appears only while flight is ON, and it takes no input. The image is updated as a D3D11 texture through `SetOverlayTexture`, and the settings section shows images per second, draw time and SteamVR call time. If the overlay cannot be created, or SteamVR rejects an image, the settings section shows the reason, keeps the last accepted image and retries, and flight carries on.

Settings are saved to `%USERPROFILE%/.config/FlugelKranz/config.json`. You can specify the file with `FLUGELKRANZ_CONFIG` and the directory with `XDG_CONFIG_HOME`. Existing saved values are restored separately from the app's defaults.

## Verification

On 2026-09-13, this was tested on Quest 2 + Touch / Virtual Desktop / SteamVR with the time-based correction of `5b41cab` enabled.

- Confirmed reading the physical poses of the HMD and both Touch controllers, and the flight transform being applied on the SteamVR side.
- Reported: the earlier black edges, darkening and jitter improved, both for head movement while a rotation is held and for head movement during rotation input.
- For the wobble in the view reported afterwards, `9932816` added pass-through for identity transforms, and `c743e66` keeps the original frame prediction while a transform is held. On 2026-09-14 the user confirmed that the wobble was gone.

SteamVR MirrorView sometimes looked normal even when it did not match what was shown in the HMD. When testing, check what you see in the HMD as well. Per-game testing, systematic testing of all axes, other HMDs, FBT, coexistence with other pose-hook tools, and long sessions have not been tested.

There are also requests to tune the current inertia and movement speed. A change toward a feel closer to OVRAS's 10% inertia brake and 1.4× drag multiplier has not been made, and the defaults in the README are the values of the current implementation.

## Troubleshooting

| Symptom | What to check |
| --- | --- |
| Cannot connect to SteamVR | SteamVR is running, the driver is registered, the add-on is enabled, another client is connected |
| Cannot get both hands | The HMD is worn and both controllers are tracked by SteamVR |
| Input does not respond | FlugelKranz's SteamVR bindings, the ON state, whether you released the button and pressed it again |
| Black edges, darkening or wobble when rotating | The correction settings and logs, whether a rotation is being held or updated, whether the head is moving, differences between the HMD and MirrorView |
| Stops because the reference space changed | A change of the Standing origin after connecting, or another space-manipulation tool at work. Check the state before reconnecting |

Hook and `FrameAudit` logs appear in SteamVR's `logs/vrserver.txt`. In a default installation it is `C:/Program Files (x86)/Steam/logs/vrserver.txt`. `history-matched pose correction installed` means the correction hook was installed, and `timed=1` means time-based correction was attempted for that layer. While a transform is held, only a constant inverse transform is applied, so you see `timed=0` and `corrected=1`, and the `held` count goes up. The startup log `XYZ body-pose transforms v2` alone does not tell you whether the correction is enabled.

## Development and automated tests

```powershell
dotnet restore FlugelKranz.slnx
dotnet build FlugelKranz.slnx
dotnet test FlugelKranz.slnx
cmake -S native/driver -B artifacts/driver-build -A x64
cmake --build artifacts/driver-build --config Release
ctest --test-dir artifacts/driver-build -C Release --output-on-failure
```

The C# tests cover input, coordinate composition, inertia, mode switching, tracking loss, restoring, settings and the UI. The native tests cover pose and velocity transforms, pass-through for identity transforms, preservation of layer information, and interpolation / prediction of frame poses. Keep passing automated tests separate from display quality on real hardware.

The Windows UI is handled by `UseWin32()`, input and connection by `FlugelKranz.OpenVR`, control calculations by `FlugelKranz.Core`, and pose application and frame correction by `native/driver`. The OpenXR / Monado projects inherited from upstream remain for dependencies and reference, but Linux installation and development are not covered here.

## Running the head pilot prototype

Head pilot and the separate Drag setting have been merged into main. To publish only the app to a separate folder and connect it to the existing registered driver, use the following command. This does not update the driver DLL, so if you also need the render correction fixes, use the build and driver installation steps above.

```powershell
dotnet publish src/FlugelKranz -c Release -r win-x64 --self-contained true -o artifacts/head-pilot-windows-x64
```

Quit the normal FlugelKranz, then launch the app in this folder. Because it uses the same app key, SteamVR's action manifest registration points to the path of whichever version was launched last. To go back to the normal version, launch the normal version again.

Enable `Experimental overlay input overrides` in the SteamVR developer settings, and enable head pilot in the app's F mode settings. The program does not change this global setting automatically. If input stays on standby, check the assignments for left-stick thrust and the X / A toggle under `/actions/pilot`. For the controls and what is still untested, see the [README](README.en.md#head-pilot-prototype-windows--f-mode).

### Input diagnostics with VRChat running

There is a report that you can move in SteamVR's waiting space but not inside VRChat, and the cause is under investigation. The input diagnostics in the settings screen show `requested`, `priority`, `dashboard`, `suspended`, `neutral`, `stickActive`, `y`, whether X/A is enabled and pressed, and `scenePid`. Compare them between SteamVR's waiting space and inside VRChat, and check the offset being sent as well. The diagnostics display does not change the pose transform or input priority.
