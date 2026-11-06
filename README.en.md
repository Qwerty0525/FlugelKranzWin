<!-- Language switch: one badge per line (they render side by side). Keep the same list in every README*.md; the current language uses the darker color. -->
[![日本語](https://img.shields.io/badge/lang-%E6%97%A5%E6%9C%AC%E8%AA%9E-e05d44)](README.md)
[![English](https://img.shields.io/badge/lang-English-003d7a)](README.en.md)

# FlugelKranzWin

A free-flight and infinite-walking tool for Windows x64 / SteamVR. You move through space by combining an inertial Space Drag with rotation on all three axes: Pitch, Yaw and Roll. The app and executable are named `FlugelKranz`.

This repository is a Windows derivative of [FlugelKranz](https://github.com/ReinaS-64892/FlugelKranz). Development, distribution and support here are limited to Windows / SteamVR. **For the Linux / Monado / WiVRn version, see the parent repository.** Linux-related code inherited from upstream remains, but it is not supported by this repository.

## Installation

You need Windows x64, SteamVR, a supported HMD and controllers, and the bundled dedicated SteamVR driver. Follow the [build, installation and update instructions](WINDOWS.md) (Japanese) to put the app and the driver in place, and set up frame pose correction.

On Quest 2 + Touch / Virtual Desktop, with time-based correction enabled, a report from real hardware says that black edges, darkening and jitter during head movement, both while a rotation is held and while rotating, have improved (2026-09-13). A wobble in the view that was reported afterwards has also been reported as resolved on real hardware, after a fix that passes identity transforms through unchanged and keeps the original frame prediction while a rotation is held (2026-09-14). The settings and the scope of testing are described in [WINDOWS.md](WINDOWS.md#動作確認) (Japanese).

## First steps

1. Start SteamVR and, with the HMD and both controllers tracked, launch `FlugelKranz.exe`.
2. Select `F` mode and turn it ON. Release the control buttons once before pressing them.
3. Hold the Touch grip and move your hand to pull the space. If you let go while your hand is moving, inertia carries on, depending on your settings.
4. Press left X / right A and twist your wrist to rotate on all three axes.
5. OFF stops flight and inertia. In F mode with Drag enabled, only Drag can be used, and `↻` returns the position and orientation to where they started.

## Controls and behavior

### Startup and common controls

On Windows the app starts OFF and restores the saved mode. Start SteamVR and the dedicated driver before turning it ON. If it cannot connect, the settings panel shows an error. The large `ON` / `OFF` button shows the current state and toggles it when pressed. Turning OFF keeps the current position and orientation, but ends any grab in progress and any inertia. In F mode with Drag enabled, you can still use Drag on its own by releasing the input and grabbing again. While flight is OFF, head pilot, stick thrust and inertia on release are disabled. If Drag is disabled as well, space manipulation stops.

The round `I` / `F` button switches between Infinite Walking and Free Flight, `⚙` opens the settings, and `↻` is a full reset to the position and orientation from when you connected. A full reset puts the position and orientation back immediately while staying ON. After turning ON, after switching modes and after a full reset, release any control input you are still holding, then grab again.

Switching from infinite walking to free flight completes immediately and keeps the current position and orientation. Switching from free flight to infinite walking takes 1 second: the height returns to the height from when you connected, and pitch and roll return to level, keeping only the Yaw relative to the orientation from when you connected. Drag / Turn input is not accepted during the transition.

### Controller input

These are the default SteamVR Input bindings. Each action is active while its button is held.

| Controller | Drag (move space) | Turn (rotate space) |
| --- | --- | --- |
| Touch left / right | Press grip | Press left X / right A |
| Valve Index left / right | Press trigger | Press A |

You can change them from `⚙` → "Open SteamVR controller bindings" (「SteamVR のバインド設定を開く」), or with `FlugelKranz.exe --bindings`. The app name is `FlugelKranz` and the app key is `org.flugelkranz.windows`. Drag / Turn / reset_hold can each be assigned to buttons or touches, separately for each hand. The touch combinations and Index force-sensing settings of the upstream Linux version are not used.

`reset_hold` is unassigned by default. Assign it in SteamVR if you need it.

| Input | Infinite walking (I) | Free flight (F) |
| --- | --- | --- |
| Hold reset_hold on one hand for 1 second | Height reset | Level reset |
| Hold reset_hold on both hands for 1 second | Switch to free flight | Switch to infinite walking |

The two-hand mode switch takes priority over the one-hand reset. You can also use `I` / `F` and `↻` in the UI.

### Head pilot prototype (Windows / F mode)

In F mode, head pilot and enhanced stick thrust are enabled by default. You can change this with the "Free flight — Head pilot (experimental)" (「自由飛行 — 頭部操縦（試作）」) setting. A saved enabled / disabled setting is kept. You need to enable the SteamVR developer setting **Experimental overlay input overrides**. If it is disabled, the app shows the reason and ends the connection.

- **Short-press X / A** on Touch to turn head pilot ON / OFF. While the prototype is in use, it replaces the original controller Turn. It toggles the moment you release the button after a short press, and the physical direction of your head at the moment it turns ON becomes neutral. There is no wait for double-click detection.
- While it is ON, tilting your head away from neutral rotates you continuously: looking down pitches you forward, looking up pitches you backward, tilting your head left or right rolls, and turning left or right turns you. Returning to neutral brings the rotation command to zero.
- **Holding X / A for 0.5 seconds** zeroes movement and rotation inertia, while keeping the position and orientation and the head pilot ON/OFF state. Releasing after the long press does not toggle the pilot state. If head pilot is still ON with your head tilted, or if there is stick input, the commands that follow make you move again.
- The **left stick** thrusts relative to your current gaze. Pushing up accelerates you in the direction you are looking, and pushing down in the opposite direction. Left and right input is ignored. You can thrust even with head pilot OFF. Movement inertia remains after you let go of the stick.
- Drag can be enabled or disabled with its own setting, "Free flight — Drag" (「自由飛行 — Drag」) (enabled by default). Grabbing works as before, while the button is held. Disabling it during a grab does not produce a release acceleration, and after re-enabling it you release the button and grab again. On Windows, in F mode with Drag enabled, the app connects even with flight OFF, both at app startup and when settings change, so that you can use Drag. During Drag, keeping your hand fixed in space takes priority, and head rotation commands and stick acceleration are not applied. Disabling the prototype returns X / A to the original Turn.
- **While the dashboard is open, all controls, movement inertia and rotation inertia stop**, and the position and orientation are held. Head pilot and the input override are released as well. After closing it, release the stick and control buttons, and click again to start head pilot.

The prototype's fixed values are a 3° head dead zone, up to 90°/s at 25° (a quadratic curve in between), a 15% stick dead zone, a maximum acceleration of 14 m/s², and a speed limit of 12 m/s during stick acceleration. While you thrust with the stick, the natural decay of movement inertia stops, and at full input the speed limit is reached in about 0.86 seconds. When you let go of the stick, the existing deceleration setting applies. The decay of rotation inertia and the rotation of the velocity vector keep their previous settings. This is separate from the earlier request to tune the defaults to match OVRAS.

Only while the app is ON in F mode with the prototype enabled does it take over the input sources for the left stick and X / A at high priority. You do not need to remove VRChat's bindings. Right stick click and B are not used. Existing personal bindings may lack new assignments for `/actions/pilot`, so check them in SteamVR. The Index default is A on both hands.

This prototype's input override, dashboard handling and coexistence with other overlays have not been tested on real hardware. Detecting interaction with floating panels is not implemented, and a panel merely being shown does not release the override. The render correction fix has also been merged into main. On Quest 2 + Touch / Virtual Desktop, the known wobble has been reported as resolved.

### Infinite walking mode

Infinite walking does not use inertia. One-hand Drag applies only the vertical movement of the grabbing hand, unchanged, to the Y movement of the space. Two-hand Drag is based on the midpoint between both hands and doubles the vertical movement. Moving your hands sideways or forward and back has no effect on Drag.

Turn is always a rotation about the Y axis, pivoting on the HMD position. One-hand Turn cancels out the Yaw of the grabbing controller, and two-hand Turn cancels out the Yaw of the HMD rather than the tilt of the controllers. When you switch between one hand and two hands, the current orientation is taken as the new starting orientation, so switching alone does not make the space jump.

The height reset with one-hand reset_hold returns only the Y position to the height from when you connected, over 1 second. The X/Z position and the orientation are kept. Controls are not accepted during the reset; when it finishes, release the input and grab again.

**Drag smoothing** (ドラグスムーズ), which is specific to infinite walking, smooths how Drag, Turn and Two-hand Turn (Turn Head) follow your hands, each separately. All of them default to `0.0` seconds and can be set from `0.0` to `1.0` seconds. At `0.0` seconds there is no interpolation and the input is applied directly; larger values make the following gentler.

**Infinite walking boost** (無限歩行ブースト) adds the HMD's horizontal movement during Turn in the direction of travel, compensating for the feeling of your feet sliding backward as you turn. Its value, "Strength" (「補正値」), is a multiplier on the added movement; the default is `0.6` and the range is `0.0` to `2.0`. `0.0` disables it, and `1.0` adds the same distance as the measured horizontal movement.

### Free flight mode

One-hand Drag moves on all axes so that the grabbing controller's position stays fixed in space, and two-hand Drag fixes the midpoint between both hands. When a second hand joins, you re-grab at the midpoint at that moment, so the space does not jump. If you release one hand from a two-hand Drag, the whole Drag ends. Release the input on the remaining hand once as well, then grab again.

One-hand Turn rotates on all axes so as to cancel out the grabbing controller's rotation. The default pivot is the HMD position. In the settings you can change it to the midpoint of the available HMD and left / right controller positions. While Drag and Turn are used at the same time, the Drag hand, or the midpoint of a two-hand Drag, takes priority over this setting as the pivot.

Two-hand Turn applies only the rotation component about the line joining the two controllers. When a second hand joins, or when you go back from two hands to one, you re-grab at the position and orientation at that moment, and the switch itself produces no angular velocity. One-hand Drag and Turn with the other hand can be used at the same time, so you can rotate around the Drag's fixed point while keeping it in place.

During two-hand Drag, the Drag brake (ドラグブレーキ) is applied to both movement inertia and rotation inertia. One-hand Drag stops only movement inertia, and Turn stops only rotation inertia. When you let go, the movement speed or angular velocity measured at that moment carries over as inertia.

The level reset with one-hand reset_hold turns the forward direction of the HMD, as currently displayed, to the nearest horizontal direction and returns pitch and roll to level over 1 second. During the rotation it holds the same pivot as a normal Turn, and if you are dragging at the same time, the Drag position takes priority. Controls are not accepted during the reset; when it finishes, release the input and grab again.

The inertia parameters used in free flight mode are as follows.

- **Inertia cutoff** (慣性カットオフ): inertia does not start if the release speed is below `5 cm/s` for Drag or `45 °/s` for Turn. It is not used to stop inertia that has already started, even when deceleration brings it below the threshold. Unchecking it disables it.
- **Inertia acceleration multiplier** (慣性加速倍率) multiplies the release speed by `0.0` to `5.0`, separately for Drag and Turn. The defaults are Drag `5.0` and Turn `0.4`.
- **Z acceleration** (Z加速) strengthens only the forward / back component on Drag release by an extra multiplier, relative to the direction the player is facing. It is weighted by the absolute value of the dot product with that direction, so no extra acceleration is applied sideways. The range is `1.0` to `5.0` and the default is `2.0`.
- **Inertia acceleration boost mode** (慣性加速ブーストモード): when enabled, the Drag brake is not applied while the controller's movement speed during Drag is above the cutoff, and the acceleration on release is added to the remaining inertia. When the controller slows down or stops again, the brake resumes. The speed after the addition is capped by the maximum multiplier, "Max multiplier" (「最大加速倍率」), based on the larger of the remaining inertia and this release's acceleration; the default is enabled, `4.0`× (`1.0` to `6.0`).
- **Vector rotation multiplier** (ベクトル回転倍率) is how much Turn rotates the current movement inertia vector. At the default `1.0` it follows Turn completely and traces a curved path, and at `0.0` it keeps the direction of travel from before the rotation.
- **Inertia deceleration** (慣性減速) is an exponential decay rate per second. `0.0` means no deceleration and `10.0` decelerates quickly; the default is `2.0 /s`.
- **Deceleration exemption** (慣性減速免除): the exemption time is a fraction of the predicted time that normal deceleration would take to bring the current speed down to a practical stopping speed. Drag and Turn can be set separately, and the defaults are Drag `0.20` and Turn `0.15`. The "Exemption ratio" (「免除割合」) has a shared default of `0.90`.
- **Drag smoothing** smooths how Drag and Turn follow your hands, each separately, to suppress small tracking jitter. The range is `0.0` to `1.0` seconds, and the defaults are Drag `0.01` seconds and Turn `0.05` seconds. At `0.0` seconds there is no interpolation and the input is applied directly. Inertia speed is always calculated from the actual controller movement, regardless of this setting.
- **Drag brake** fully damps the corresponding inertia when you start grabbing with Drag / Turn. One-hand Drag acts on linear velocity, Turn on angular velocity, and two-hand Drag on both. Its duration, "Ramp time" (「適用時間」), can be set from `0.0` to `1.0` seconds; the default is `0.40` seconds, and `0.0` seconds stops immediately. When it triggers, it cancels the corresponding deceleration exemption.

While HMD tracking is lost, space updates pause, keeping the ON state, the control state and inertia, and they resume when tracking returns. If a hand loses tracking, that hand's control is released. Two-hand Turn re-grabs based on the hand that is still tracked and continues. Two-hand Drag is released entirely if one hand is lost, so release the input on the remaining hand too, then grab again.

On a normal exit, the flight transform is released. While OFF, the connection stays open and the position and orientation are held. If the app's heartbeat stops for 500 ms, the running driver releases the transform, and you need to reconnect to recover. If the SteamVR Standing reference space from when you connected changes, controls stop.

### Display language, command interval, gravity compass, diagnostic logs

- **Display language**: `ENG` / `JPN` at the top right of the main screen switches between English and Japanese.
- **Command interval (experimental)**: the default is High rate (about 3 ms) (「高頻度（約 3 ms）」). Flight commands are sent in finer steps, which reduces the jitter where the avatar's body and head drift apart while moving. Off goes back to the old interval of about 15.6 ms. The switch applies at once, and throws and glides come out about the same as before.
- **Gravity compass**: a small see-through ball above the back of your hand that shows the real room as you see it. The glowing dot is real down (gravity); the white dot is the flown VRChat world's down. Release builds start with it off; turn it on under Gravity compass → Show (「重力コンパス」→「表示する」). The default hand is the right hand. It is a SteamVR overlay, so it does not affect VRChat's controls or picture.
- **Save diagnostic logs…** (「診断ログを保存…」): under Diagnostic logs, this packs the SteamVR, SlimeVR, Virtual Desktop and FlugelKranz logs and a summary.txt into one zip in `Documents\FlugelKranz Logs\`. The PC name, the Windows user name and user-folder paths are replaced; anything else (device serial numbers, for example) stays, so look through the zip before posting it. Nothing is sent anywhere. Close SteamVR first (keep this window open) to capture the whole session. To report a problem, open an issue on [GitHub Issues](https://github.com/Qwerty0525/FlugelKranzWin/issues) and attach the zip.

The window title shows the app version.

## Saving settings

Settings are saved to `%USERPROFILE%/.config/FlugelKranz/config.json` whenever they change. `FLUGELKRANZ_CONFIG` changes the settings file, and `XDG_CONFIG_HOME` changes the settings directory. Each setting's reset button returns that item to its default.

## Architecture and development

```text
FlugelKranz.exe (Avalonia / Windows)
  ├─ Core: Drag / Turn, inertia, mode control
  └─ DriverFlightRuntime: SteamVR Input + shared-memory IPC
       ├─ Reads the physical pose before the transform
       └─ Sends the flight transform to the dedicated SteamVR driver
            ├─ Applies movement and rotation to tracked device poses
            └─ Corrects Direct Mode frame poses to the physical HMD pose
```

The flown pose passed to the game is kept separate from the physical pose passed to the HMD display correction. Control calculations use the pose from before the transform, which prevents feedback from flight results back into the input. Chaperone and VRChat's horizon settings are not rewritten.

For development steps, see [WINDOWS.md](WINDOWS.md) (Japanese); for coordinate transforms, IPC and render correction, see the [driver documentation](native/driver/README.md) (Japanese); for working conventions, see [AGENTS.md](AGENTS.md) (Japanese).

## Upstream, reference implementations and license

- [FlugelKranz / Reina_Sakiria](https://github.com/ReinaS-64892/FlugelKranz): the original free-flight specification, Core and UI. This is where the Linux version is developed.
- [FlugelKranzWin / kqnade](https://github.com/kqnade/FlugelKranzWin): the Windows / SteamVR version this fork is based on. The changes made in this fork are listed in [MODIFICATIONS.md](MODIFICATIONS.md).
- [Kawaii Move Assist](https://github.com/ReinaS-64892/reina_s_kawaii_move_assist): used as a reference for the SteamVR pose-update hook and the world-space body pose structure.
- [OpenVR Advanced Settings](https://github.com/OpenVR-Advanced-Settings/OpenVR-AdvancedSettings): a reference implementation for SteamVR Input, space manipulation and Chaperone.

The main program is licensed under [GPL-3.0](LICENSE). The OpenVR and MinHook licenses are included in their vendor directories and in the distributed driver folder. When you distribute it, provide the corresponding source of the main program and the licenses and copyright notices of the third-party code you use.

On first launch the defaults are F mode (flight OFF), head pilot and Drag enabled, and the head as the rotation pivot. To match the settings used on real hardware on 2026-09-14, the Drag cutoff is `0.05 m/s` and the Drag inertia acceleration multiplier is `5.0`. Saved settings take precedence.
