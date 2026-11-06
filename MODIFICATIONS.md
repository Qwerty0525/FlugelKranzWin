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
