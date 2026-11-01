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
