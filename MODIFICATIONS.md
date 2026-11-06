# Modifications

This build is a modified version of
[FlugelKranzWin](https://github.com/kqnade/FlugelKranzWin) by kqnade, which is
itself a Windows fork of
[FlugelKranz](https://github.com/ReinaS-64892/FlugelKranz) by Reina_Sakiria.
Both are licensed under the GNU General Public License v3.0, and so is this
modified version. See `LICENSE`.

Modified by Hiyomi ([@Qwerty0525](https://github.com/Qwerty0525)).
Base revision: `6dfcb65` (2026-09-14).

The complete corresponding source for any binary built from these changes
is the `slimevr-hmd-anchored` branch of
<https://github.com/Qwerty0525/FlugelKranzWin>. Each release is also tagged
at the exact commit it was built from (for example `slimevr-20261025`), and
those tags are kept.

## 2026-09-14 — SlimeVR and other HMD-anchored solvers

Affected files: `native/driver/Driver.cpp`, `native/driver/Transform.h`,
`native/driver/FrameHistory.h`, `native/driver/TransformTests.cpp`,
`native/driver/resources/settings/default.vrsettings`,
`native/driver/README.md`.

SlimeVR's SteamVR driver reads HMD and controller poses back through
`IVRServerDriverHost::GetRawTrackedDevicePoses` and builds its skeleton around
them. Those poses already carry the flight transform, so the skeleton was
assembled around the flown head and the flight then landed on SlimeVR's
trackers a second time. Measured on Quest 2 + Touch / Virtual Desktop /
SlimeVR with the flight held at 33.6 m of translation, the poses SlimeVR's six
trackers submitted sat 0.4–1.0 m from the flown head and about 33 m from the physical head, while
Virtual Desktop's arm trackers and the controllers were the reverse.

The driver now hooks `GetRawTrackedDevicePoses` and removes the transform last
applied to each device, but only for callers whose module file name appears in
the new `physicalPoseDrivers` setting (default `driver_slimevr.dll`). Such a
solver then sees the physical body, and its trackers take the flight exactly
once like every other device, for both translation and rotation.

A second setting, `hmdAnchoredTrackingSystems`, applies rotation about the
flown HMD to devices whose `Prop_TrackingSystemName_String` matches. It exists
for solvers the readback hook cannot reach and defaults to empty: it stops the
doubled translation but mixes flown head orientation with physical body
orientation, which visibly twisted the legs once rotation was involved.

Both settings live in `driver/flugelkranz/resources/settings/default.vrsettings`
and are documented in `native/driver/README.md`.

### Testing

`transform_tests` covers the pivoted rotation, the physical readback including
velocities, and the identity pass-through. All three native test suites and
186 of 189 C# tests pass; the three failures are pre-existing
`Avalonia.Headless.XUnit` discovery errors introduced upstream by the xunit v4
dependency bump in `6dfcb65`, unrelated to these changes.

Verified on hardware with Quest 2 + Touch, Virtual Desktop over SteamVR, and
SlimeVR full-body tracking. Flight with the Virtual Desktop virtual elbow
trackers unassigned in SlimeVR behaved correctly over several sessions.
Rotation-heavy flight with those virtual trackers reassigned has not been
re-tested since the readback hook replaced the rotation-only approach.

## 2026-10-25 — Keep the raw-pose readback off the pose-update lock

Affected file: `native/driver/Driver.cpp`.

The readback hook above took the same mutex that pose updates hold across
their IPC mutex waits. SlimeVR polls `GetRawTrackedDevicePoses` about every
2 ms, so the HMD's own pose updates queued behind it and the headset view
juddered while moving with FlugelKranz running. Measured from the driver's
FrameAudit log on the same hardware, the render correction used HMD samples
47 ms or older in 72% of flying frames, against 0.3% before the hook existed.

The transforms last applied to each device now have their own mutex, held only
for one assignment by a pose update or one array copy by the readback, and the
readback processes its copy unlocked. Each call site is classified once and
cached in lock-free atomics, so the readback no longer takes the loader lock
either.

### Testing

All three native test suites pass. Not yet confirmed on hardware.

## 2026-10-28 — rebocap in the default readback list

Affected files: `native/driver/Driver.cpp`,
`native/driver/resources/settings/default.vrsettings`,
`native/driver/README.md`.

`physicalPoseDrivers` now defaults to `driver_slimevr.dll,driver_rebocap.dll`.
rebocap's SteamVR driver also reads the HMD pose inside vrserver and its
desktop app builds the body around that head, so with the old default its
trackers took the flight twice: they travelled about twice as far as the HMD,
and in rotation the chest and arms drifted from the head.

### Testing

All three native test suites pass. On 2026-10-27 a Quest 3S + rebocap user
added `driver_rebocap.dll` to the setting by hand on the `slimevr-20261025`
build and reported that translation and rotation then behaved correctly. Their
`vrserver.txt` from a later flight session (SteamVR loading the HMD through its
`oculus` driver) shows `raw poses for driver_rebocap.dll: physical (flight
removed)`, so rebocap's readback comes from `driver_rebocap.dll` itself and is
served physical poses. This exact build has not been flown.

## 2026-11-01 — Pose updates no longer wait for the app; readback pairing

Affected files: `native/driver/Driver.cpp`, `native/driver/FrameHistory.h`,
`native/driver/TransformTests.cpp`.

Pose updates and `RunFrame` hold the driver's pose-update lock, and they used
to wait up to 2 ms, twice per update, for the IPC mutex the app also takes. If
the app paused while holding it (a .NET garbage collection, for example), every
device's pose update stalled. They now only try the mutex: a miss keeps the
previous flight command and skips one physical sample for the app.

The readback hook could also remove the wrong transform. The new transform was
published before the pose carrying it reached vrserver, so a solver reading in
between got the previous pose with the new transform removed, a one-sample
jump of up to the flight's per-update movement. While a pose is being
forwarded, the readback now removes whichever of the previous and the new
transform lands nearer that update's physical position, and the committed
transform is recorded only after the pose has been forwarded.

### Testing

All three native test suites pass, including a new check that a readback
holding either the old or the new pose has the matching transform removed.
Not built into a release and not flown on hardware.

## 2026-11-06 — Driver test harness and a flags line in the log

Affected files: `native/driver/Driver.cpp`, `native/driver/DriverHarness.h`
(new), `native/driver/DriverPathTests.cpp`, `native/driver/README.md`.

Settings are now read by one function, `loadSettings()`, called from `Init`
as before. It also writes one `FlugelKranz flags:` line to `vrserver.txt`
with the value of every behaviour flag, so a log always says which paths
were live. A flag missing from every settings file keeps the behaviour the
driver had before that flag existed. Each change below adds its own flag.
Nothing else changes for the user.

### Testing

`DriverHarness.h` stands in for the driver log, settings, device properties
and the app's shared memory and IPC mutex, so `driver_path_tests` now runs
`loadSettings()`, `classify()`, `refresh()` (command taken, app pausing
while it holds the mutex, invalid command, dead client) and the readback
caller check through the production code. All four native test suites pass.

## 2026-11-06 — DriverAudit counters

Affected files: `native/driver/Driver.cpp`, `native/driver/FrameHistory.h`,
`native/driver/resources/settings/default.vrsettings`,
`native/driver/DriverPathTests.cpp`, `native/driver/ObserverTests.cpp`,
`native/driver/README.md`.

A new flag, `driverAudit` (default `true` in the shipped settings, `false`
when missing), adds one `DriverAudit` line per second to `vrserver.txt`.
It only has values the existing `FrameAudit` line lacks: IPC misses, owner
revocations and invalid commands counted separately, command changes and
their intervals, the HMD's virtual step per update, how the readback picked
a transform while a pose was in flight, each layer's correction path with
the pose jump at path changes, and the HMD sample age measured on the
steady clock. Ten seconds after start it also reports once if the
SubmitLayer hook was never installed, which means every layer goes out
flown. The line is formatted and logged from `RunFrame` after the
pose-update lock is released; the hot paths only add to counters under a
small lock of their own. Pose and frame output do not change. Set
`driverAudit` to `false` to remove the line and the counting.

### Testing

`driver_path_tests` checks each counter through the real `refresh()`,
`updated()` and readback code, the line's contents, the per-second reset,
and the one-time Direct Mode report; `observer_tests` checks that each
correction path is counted. All four native test suites pass. Not run on
hardware.

## 2026-11-06 — Fallback render correction (opt-in)

Affected files: `native/driver/Driver.cpp`, `native/driver/FrameHistory.h`,
`native/driver/resources/settings/default.vrsettings`,
`native/driver/ObserverTests.cpp`, `native/driver/FrameTests.cpp`,
`native/driver/README.md`.

When no correction path can place a layer (the newest HMD sample is older
than 50 ms, the prediction is out of range, and so on), the layer is
forwarded with the flown HMD pose, which on Quest 2 / Virtual Desktop has
shown up as black borders. With the new flag `fallbackFramePose=true`
(default `false`), such a layer instead has a transform removed: of
identity and every transform in the HMD history, the one whose removal
lands the layer nearest the newest physical head. Identity means the layer
is forwarded unchanged, so a layer rendered before flight started is left
alone. With an empty history the HMD's last committed transform is used.
It requires `correctFramePose=true`.

In the common failure (a stale newest sample) the newest transform is the
one vrserver holds, so the result is close to exact. When several commands
fall between the render pose and the newest HMD update (low frame rate,
streaming delay), the choice can be wrong by a few command steps if head
motion is larger than one flight step.

### Testing

`frame_tests` covers the selection (newest, older, identity, non-finite,
empty). `observer_tests` covers a stale history, the identity guard, the
empty-history case through `lastApplied`, and that nothing changes
without `correctFramePose`. All four native test suites pass. Not run on
hardware; whether it removes the black borders is not known yet.

## 2026-11-06 — Readback generations (opt-in)

Affected files: `native/driver/Driver.cpp`, `native/driver/FrameHistory.h`,
`native/driver/resources/settings/default.vrsettings`,
`native/driver/DriverPathTests.cpp`, `native/driver/FrameTests.cpp`,
`native/driver/README.md`.

The readback pairing from 2026-11-01 still had three holes. A forward that
started and committed while the solver's readback was inside vrserver was
not seen as in progress, so the new transform was removed from the old pose.
Picking by position alone was a coin toss for head-pilot turns, which rotate
about the HMD and leave its position unchanged. And when two forwards of
one device overlapped and finished in the other order, the committed
transform went back to the older one.

With the new flag `readbackGenerations=true` (default `false`) every
forward of a device gets a generation number, and the last eight are kept
with their transform and physical pose. The readback compares the committed
generation before and after calling vrserver. If nothing changed and nothing
is in flight it uses the committed transform; otherwise it scores every
candidate from the generation committed before the call to the newest, by
position and orientation. A commit only moves forward; an older forward
finishing late leaves the committed transform alone and marks the device
until its next in-order commit. `DriverAudit` gains `overflow` (more than
eight forwards during one readback) and `reorderedCommits`.

Whether vrserver applies a forwarded pose before the call returns has not
been tested. If it does not, comparing generations at the hook boundary is
not enough.

### Testing

`driver_path_tests` reproduces the straddled commit (wrong with the flag
off, right with it on), a 1.4° turn about the device that only orientation
separates, and an out-of-order commit with each flag value. A two-thread
stress run (about 2 kHz pose updates with a new transform every update,
about 1 kHz readbacks with a 0.2 ms gap added inside the fake vrserver call)
mispaired roughly 330 of 836 readbacks with the flag off and none with it
on, over five runs. `frame_tests` covers the candidate scoring. All four
native test suites pass. Not run on hardware; SlimeVR and rebocap should
both be checked before the default changes.

## 2026-11-06 — DriverAudit: tick skew and command step

Affected files: `native/driver/Driver.cpp`, `native/driver/DriverPathTests.cpp`,
`native/driver/README.md`.

Each device takes the flight command current at its own pose update, so
right after a command change the HMD and a tracker updated before it are
flown by different steps, and the body sits one step away from the head
until the tracker updates again. Under `driverAudit`, the `DriverAudit`
line now samples that offset at every `RunFrame` (`skew`), the head
displacement of each command change (`commandStepM`), and the HMD and
other pose update counts (`updates`). Pose and frame output do not change.

### Testing

`driver_path_tests` checks the skew of a live device flown with the
previous command, that stale devices are not sampled, and the command
step. All four native test suites pass. Not run on hardware.

## 2026-11-06 — English / Japanese UI switch

Affected files: `src/FlugelKranz/Views/MainView.cs`,
`src/FlugelKranz/ViewModels/MainViewModel.cs`,
`src/FlugelKranz/ViewModels/SettingsStore.cs`,
`src/FlugelKranz/FlugelKranz.csproj`, and the new
`src/FlugelKranz/Localization/` (`UiStrings.cs`, `UiMessage.cs`,
`Languages/ja.json`, `Languages/en.json`) and
`tests/FlugelKranz.Tests/LocalizationTests.cs`.

The desktop app's text can now be shown in English. `ENG` / `JPN` buttons sit
in the top-right corner of the main screen and in the settings header; a click
switches every label, button, tooltip and status line immediately, without a
restart. The choice is saved as `"language": "en"` or `"ja"` at the root of
`config.json`. A missing or unknown value means Japanese, so existing configs
open unchanged, and the shared `schemaVersion` / Core settings are untouched.

Each language is one JSON table embedded from `Localization/Languages/`;
dropping in another file (for example `ko.json` with a `"KOR"` selector label)
adds a button for it, and any key it lacks falls back to Japanese. Status and
error messages from Core and the OpenVR runtime stay Japanese at the source,
so console output and logs are unchanged; the UI maps them back through the
`runtime.*` entries of `ja.json` for display only, including messages with
embedded values and multi-line restore / disconnect errors. The driver, native
code and command-line output are not changed.

### Testing

`LocalizationTests` checks that every table has the Japanese keys and
placeholders, that every Japanese string literal in `FlugelKranz.Core` and
`FlugelKranz.OpenVR` has an English translation, that the language is saved
and restored with Japanese as the default, and renders the window headlessly
in both languages, switching live through the selector. It drives the
headless session directly because `[AvaloniaFact]` discovery is broken by the
xunit v4 bump. 191 of 194 C# tests pass; the three failures are the same
pre-existing `Avalonia.Headless.XUnit` discovery errors. Their test bodies
were also run once through the same headless session and pass. Not tried in
the real app or in VR.

## 2026-11-06 — High-rate command loop (opt-in)

Affected files: `src/FlugelKranz.Core/FlightController.cs`,
`src/FlugelKranz.Core/FlightMotionSettings.cs`,
`src/FlugelKranz.Core/FreeFlightManipulator.cs`, the new
`src/FlugelKranz.Core/MotionWindow.cs` and
`src/FlugelKranz/Platform/HighResolutionDelay.cs`,
`src/FlugelKranz/ViewModels/MainViewModel.cs`,
`src/FlugelKranz/Views/MainView.cs`, both language tables, both project files
(`InternalsVisibleTo` for the tests) and the new
`tests/FlugelKranz.Tests/CommandLoopTests.cs`.

The app's command loop waited with `Task.Delay(10)`, which on Windows lasts
one 15.6 ms system tick, so the flight offset changed in steps of
speed × 15.6 ms (about 14 cm at 8.7 m/s). The driver hands every device the
command that is current at that device's update, so the HMD and SlimeVR
trackers can land on different steps; that shows up as the avatar shaking
during translation. A stopgap for that, not the fix (driver-side continuous
extrapolation is planned separately).

A new setting, **Command interval → High rate (about 3 ms)**, saved as
`"highRateCommandLoop"` at the root of `config.json`, makes the loop wait
3 ms on a `CREATE_WAITABLE_TIMER_HIGH_RESOLUTION` timer instead (Windows 10
1803+; falls back to `Task.Delay` if the timer cannot be created). The process
timer resolution is not raised. The loop period becomes about 3 ms plus the
iteration's own work. It is read on every iteration, so switching applies
immediately without restarting SteamVR. Missing or false means the old loop,
so the default and existing configs are unchanged.

Decay, smoothing and integration already depended on elapsed time only. Two
things depended on the tick: the drag / turn release velocity (one tick's
displacement over its duration, which over 3 ms is mostly sample noise) and
the inertia stop check (displacement per tick below
`InertiaStopDisplacementMetres`, which at 3 ms cut a glide off about five
times sooner). With the option on, the release velocity is measured over the
last 1/64 s of hand samples and the stop check uses at least 1/64 s, so a
throw and the glide that follows come out about the same at either rate. The
UI status report is now sent every 150 ms instead of every 10th iteration.

### Testing

`CommandLoopTests` checks that the high-rate wait is used only when the
option is on and that the controller switches between the two waits live,
and that a noisy 1 m/s drag released at 1/64 s and at 3 ms ticks glides
0.40–0.55 m and stops after 1–2 s at both rates (without the window the 3 ms
glide stops after 0.34 m). Not yet flown.

## 2026-11-06 — Save diagnostic logs from the app

Affected files: the new `src/FlugelKranz/Diagnostics/DiagnosticLogCollector.cs`
and `src/FlugelKranz/ViewModels/MainViewModel.Diagnostics.cs`,
`src/FlugelKranz/ViewModels/MainViewModel.cs`,
`src/FlugelKranz/Views/MainView.cs`, both language tables and the new
`tests/FlugelKranz.Tests/DiagnosticLogTests.cs`.

The settings panel has a **Diagnostic logs** section with a **Save diagnostic
logs…** button. It is the in-app version of `tools/collect-vr-logs.ps1` (kept
outside this repository): the same files, from the same places —
SteamVR `vrserver.txt` / `vrserver.previous.txt`, compositor, `vrmonitor`,
`driver_vrlink`, the newest `vrclient_*` for FlugelKranz, VRChat, SlimeVR and
AdvancedSettings, `steamvr.vrsettings`, `openvrpaths.vrpath`, the SlimeVR
driver / bindings / server / GUI logs, the Virtual Desktop streamer, OpenXR,
service and setup logs, and FlugelKranz's own `config.json` — and the same
`summary.txt` (SteamVR driver load lines, FlugelKranz driver lines, FrameAudit
headAge / missed / held / identityBypass for all and flying seconds,
SlimeVR / VD warning counts, the file list). The summary adds one section:
DriverAudit seconds grouped by the command interval most changes fell in
(<10 ms, 10–20 ms, ≥20 ms), with mean / max `commandStepM` and skew for
each, so a session flown with the high-rate loop off and then on shows both
halves apart.

A first click only lists what would go in (archive name, size, last write
time); **Save zip** then writes everything into one zip under
`Documents\FlugelKranz Logs\FlugelKranz-logs-<time>.zip`, and **Show in
folder** opens Explorer on it. Before writing, every file and the summary are
redacted: the user-profile path in any spelling (`C:\Users\name`,
`c:/users/name`, JSON-escaped `C:\Users\name`) becomes `%USERPROFILE%`,
and the Windows user name and the PC name, as whole words, become `<user>` /
`<pc>` (names under three characters are only removed inside the profile
path). Other content such as device serials or network addresses is kept, and
the UI says so. Source files are opened read-only with sharing, so logs still
held open by SlimeVR or Virtual Desktop are read without disturbing them. The
app makes no network calls; the note by the button points to GitHub Issues at
github.com/Qwerty0525/FlugelKranzWin for reports, and says to press the button
right after the problem session, after closing SteamVR and before starting it
again (SteamVR overwrites `vrserver.txt` on start; with Drag on, reopening
FlugelKranz connects to SteamVR and can start it).

### Testing

`DiagnosticLogTests` covers the redaction rules (every path spelling, whole
tokens only, short names, a differently named profile folder) and builds a
fake Steam / SlimeVR / VD / config layout to check the planned file list, the
zip entries, that no entry contains the user or PC name, the summary numbers,
and that every source file's hash is unchanged afterwards; a headless UI
test opens the section, lists, saves and checks the status. The summary was
also built from the 2026-11-06 14:36 flight's collected logs and matched the
script's summary.txt line for line apart from the order of equal counts.
205 C# tests, 202 pass (the 3 known `[AvaloniaFact]` discovery errors). Not
tried in the real app.

## 2026-11-06 — Detailed diagnostics setting (driverAudit)

Affected files: the new `src/FlugelKranz/Diagnostics/DriverSettingsFile.cs`,
`src/FlugelKranz/ViewModels/MainViewModel.Diagnostics.cs`,
`src/FlugelKranz/ViewModels/MainViewModel.cs`,
`src/FlugelKranz/Views/MainView.cs`, both language tables and the new
`tests/FlugelKranz.Tests/DriverSettingsFileTests.cs`.

The Diagnostic logs section gains a **Detailed diagnostics** checkbox for the
driver's `driverAudit` flag (the 1 Hz `DriverAudit` line in `vrserver.txt`).
The driver reads its flags once when SteamVR loads it, so the app writes the
flag into the driver's own `resources/settings/default.vrsettings` and the UI
says it takes effect after SteamVR is restarted. The file is found next to
the app (`driver/flugelkranz`, the release layout) or, failing that, among
the `external_drivers` registered in `openvrpaths.vrpath`; only the
`driver_flugelkranz.driverAudit` key changes, every other key is kept, and
the write goes through a temporary file. If neither place has the driver the
checkbox is disabled and says why. If the user's `steamvr.vrsettings` has its
own `driver_flugelkranz.driverAudit`, which wins, the UI says so; that file
is only read. Opening the app never writes. Reset restores the shipped value.

The shipped default stays `driverAudit: true` for this test build
(`DriverSettingsFile.ShippedDriverAudit` and
`native/driver/resources/settings/default.vrsettings`, which a test keeps in
step). `build-windows.ps1` copies the shipped file over the deployed one, so
a rebuild resets the setting.

### Testing

`DriverSettingsFileTests` checks the shipped value, that writing the flag
keeps every other key, unset / missing files, both ways of finding the
driver folder, and the view model: load without writing, toggle writes and
reports the restart, Reset, the steamvr.vrsettings note, and no driver
folder. 210 C# tests, 207 pass (the 3 known `[AvaloniaFact]` discovery
errors). Not tried in the real app.

## 2026-11-06 — Defaults: high-rate loop on, driverAudit off

Affected files: `src/FlugelKranz.Core/FlightMotionSettings.cs`,
`src/FlugelKranz/Diagnostics/DriverSettingsFile.cs`,
`native/driver/resources/settings/default.vrsettings`,
`native/driver/README.md`, `tests/FlugelKranz.Tests/CommandLoopTests.cs`,
`tests/FlugelKranz.Tests/DriverSettingsFileTests.cs`.

After the flight on 2026-11-06 (the high-rate loop cut the avatar shake about
threefold), **Command interval → High rate** is on by default and the shipped
**Detailed diagnostics** (`driverAudit`) is off.

`highRateCommandLoop` now defaults to `true`. The app writes every setting on
each save, so a `config.json` that has the key was saved by a build that had
the option, and its value (including `false`) is kept; a config without the
key, written before the option existed, gets the new default. Nothing is
migrated or rewritten on load.

`driverAudit` is `false` in the shipped `default.vrsettings` and in
`DriverSettingsFile.ShippedDriverAudit`, so the checkbox shows it off after a
rebuild and Reset turns it off. `build-windows.ps1` copies the shipped file
over the deployed one, so a deploy turns an enabled audit off again. The
driver's own default for a missing key was already `false`.

### Testing

`CommandLoopTests` checks the new default, that a config without the key
loads with it on and that a saved `false` stays off. `DriverSettingsFileTests`
checks the shipped file and the checkbox's Reset against the new value.
211 C# tests, 208 pass (the 3 known `[AvaloniaFact]` discovery errors).

## 2026-11-06 — Standing-origin check every 100 ms

Affected files: the new `src/FlugelKranz.OpenVR/StandingOriginCheck.cs`,
`src/FlugelKranz.OpenVR/DriverFlightRuntime.cs`,
`src/FlugelKranz.OpenVR/OpenVrFlightRuntime.cs`,
`tests/FlugelKranz.Tests/DriverFlightRuntimeTests.cs`,
`tests/FlugelKranz.Tests/OpenVrFlightRuntimeTests.cs`.

`DriverFlightRuntime.ReadPhysical` read the Standing origin
(`GetWorkingStandingZeroPoseToRawTrackingPose`) on every loop tick to stop
when another tool moves it. SteamVR logs
`[CVRChaperoneClient] the working set is already updated … do nothing` to
`vrclient_FlugelKranz.txt` for each call, so with the 3 ms loop the file grew
by about 280 lines a second (about 2.5 MB a minute in the 2026-11-06 18:30
log). The check now runs at most every 100 ms (the first read checks at
once). An external change still stops the flight with the same message,
within 100 ms instead of one tick. The legacy Chaperone backend
(`OpenVrFlightRuntime`, not used for the current flight) rate-limits the
check in `ReadPhysical` the same way; its `Apply` and `Restore` still check
before every write, since they write the working set themselves.

### Testing

`DriverFlightRuntimeTests` runs one second of 3 ms ticks against a manual
clock: 10 origin reads instead of 333, and a moved origin still throws
within 100 ms without writing. `OpenVrFlightRuntimeTests` checks the same
rate limit on reads and that `Apply` still refuses a moved origin. Not yet
checked against a real vrclient log.

## 2026-11-06 — Log-save warning while SteamVR runs

Affected files: `src/FlugelKranz/Diagnostics/DiagnosticLogCollector.cs`,
`src/FlugelKranz/ViewModels/MainViewModel.Diagnostics.cs`,
`src/FlugelKranz/Views/MainView.cs`, both language tables and
`tests/FlugelKranz.Tests/DiagnosticLogTests.cs`.

When **Save diagnostic logs…** is pressed while `vrserver.exe` is running,
the file list now opens with a warning: SteamVR is still running, and
closing it first (keeping the app open) and pressing the button again
captures the whole session. **Save zip** stays available. The check runs
each time the list is made, so pressing the button again after closing
SteamVR clears the warning and refreshes the file sizes. Only the process
list is read.

### Testing

The headless settings test now shows the warning with SteamVR reported
running, hides it after a refresh with SteamVR closed, and still saves the
zip while it is shown.

## 2026-10-25 — Redistribution notices

Affected files: `src/FlugelKranz/FlugelKranz.csproj`, `THIRD-PARTY-NOTICES.txt`,
and the new files `MODIFICATIONS.md`, `THIRD-PARTY-NOTICES-dotnet.txt` and
`THIRD-PARTY-NOTICES-SkiaSharp.txt`.

The project now copies this file and the third-party notices into the publish
output, so the Windows archive carries them. `THIRD-PARTY-NOTICES.txt` lists
the bundled components and their licences, and now points to two notice files
reproduced verbatim from the NuGet packages: the .NET runtime pack's (Brotli,
zlib-ng, Unicode data and others compiled into the runtime) and
SkiaSharp.NativeAssets.Win32's (Skia, HarfBuzz, libjpeg-turbo, libwebp, the
Adobe DNG SDK, piex and others in `libSkiaSharp.dll` / `libHarfBuzzSharp.dll`).
It also records that `Microsoft.DiaSymReader.Native.amd64.dll` is under the
Microsoft .NET Library License rather than MIT, and adds Wuffs.
