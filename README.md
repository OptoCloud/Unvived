# Unvived

Face and eye tracking for the VIVE Facial Tracker and VIVE Pro Eye in
VRCFaceTracking, with no VIVE software installed. Built by reverse
engineering HTC's SRanipal runtime and replacing it with an open
implementation. Not affiliated with HTC.

Installed version studied: SRanipal runtime **1.3.1.1** (`C:\Program Files\VIVE\SRanipal`),
with `tobii_stream_engine.dll` 3.3.0.1 for the eye tracker.

## Layout

- `scripts/inventory.ps1`: copies the install into `vendor/` (untracked) and
  regenerates `docs/inventory.md` (versions, hashes, imports, exports).
- `scripts/pe_info.py`: stdlib-only PE import/export dumper used by it.
- `docs/`: findings. `inventory.md` is generated; `architecture.md` is written
  by hand.

HTC's binaries, firmware, models and decompiler output are never committed
(see `.gitignore`).

## What is installed

| Piece | Files | Role |
|---|---|---|
| Service | `SRanipalService.exe` (.NET) | Windows service, auto-start; launches `sr_runtime.exe` |
| Runtime | `sr_runtime.exe` (Qt tray app), `ViveSR_Runtime.dll`, `ViveSR_API.dll`, `nanomsg.dll` | Module host; serves apps over nanomsg IPC |
| Eye camera | `EyeCameraModule.dll`, `eye_camera_device/vive_pro_eye/*` | Talks to the Pro Eye's Tobii tracker via `tobii_stream_engine.dll`; `libEChipConnection.dll` / `USBDevConnectDLL.dll` poke the headset over HID |
| Eye prediction | `EyePredictionModule.dll`, `EyePredictionTVM.dll`, `model/EyePrediction/*` | TVM (OpenCL) model: openness, squeeze, wide etc. from eye data |
| Lip camera | `LCameraModule.dll`, `Firmware/LipTracker/LipTracker.bin` | Facial Tracker USB camera capture (DirectShow/COM) |
| Lip prediction | `LPredictionModule.dll`, `LipPredictionTVM.dll`, `model/LPrediction/*` | TVM (OpenCL) model: lip/jaw/cheek blend shapes |
| Tools | `tools/eye_calibration*`, `eye_eula`, `lip_sync_notification` | Unity apps (Mono, decompilable with ILSpy) |
| Updater | `SRanipalUpdateService/*` | Auto-updater |

Every module DLL implements the same interface (`Initial`, `Start`, `Stop`,
`Release`, `Process`, `DefineInputs`/`DefineOutputs` over `ViveSR::MemoryElement`,
typed `Get/SetParameter`), created by a `Create*Module` export and chained
with `ViveSR_LinkModule`.

## Plan

1. **Map the runtime.** `ViveSR_API.dll` / `ViveSR_Runtime.dll`: the module
   graph, parameter IDs, and the nanomsg wire protocol between apps
   (`SRanipal.dll` + `ViveSR_Client.dll` from the SDK) and `sr_runtime.exe`.
2. **Eye.** Document what `EyeCameraDevice.dll` asks Tobii for and what the
   HID calls to the headset do (`EnableEyeFeature`, `SendEyeStatus`,
   `PeriodicPowerSignal`), and how `VerboseData` and the eye expression data
   are built from it.
3. **Lip.** Document the Facial Tracker's USB interface (capture format, any
   vendor control requests, firmware upload) and the model's input/output,
   so the camera can be read without `LCameraModule.dll`.
4. **Replace.** A small open runtime that reads both devices and either
   speaks the SRanipal client protocol (existing apps keep working) or
   publishes directly to the consumer that matters (e.g. VRCFaceTracking / OSC).
5. **Uninstall.** Remove the VIVE software and confirm everything still works.

Open questions are tracked in `docs/architecture.md`.

## Facial Tracker module (VRCFaceTracking)

`src/` holds a VRCFaceTracking module that replaces SRanipal for the VIVE
Facial Tracker. It reads the camera through Media Foundation, runs SRanipal's
own lip model and post-processing, and maps the result to Unified Expressions.

- `Unvived.Core`: capture, preprocessing, the network (Direct3D 11
  compute shaders, with a SIMD CPU fallback), post-processing.
- `Unvived.FacialTracker`: the VRCFT module. `pwsh scripts/package-module.ps1`
  builds `artifacts\Unvived.FacialTracker-<version>.zip`. Install it with
  VRCFT's "install module from zip". Quit SRanipal first: it holds the camera.
- `Unvived.Cli` (`unvived`): `devices`, `fetch-model`, `bench`,
  `run [--cpu]`, `grab`.

**Model weights.** HTC's weights (`lip_opencl.params`) are not redistributed.
HTC still hosts the old installers on its CDN, and ships the current runtime inside the free Steam app *VIVE Console for
SteamVR*. The module looks for the file in an SRanipal install or in
`VIVEDriver\App\SRanipal` in any Steam library, and copies it next to
itself. `unvived fetch-model <module dir>` asks Steam to install VIVE Console
and waits for the file. After that, VIVE Console can be uninstalled; none of
its code is run. All known download sources, with hashes, are in `docs/model-sources.md`.

**Cost** (`unvived bench`, this machine): GPU about 1.3 ms and 0.2 ms of CPU per
frame (around 1% of a core at 60 fps); CPU fallback about 6 ms and one full
core at 60 fps.

Tests: `dotnet test`. `py -I tests/reference/make_fixtures.py <params>`
regenerates the OpenCV fixture and the local network fixture, which is
derived from HTC's weights and is ignored by git.

## Legal

This is interoperability work on software and hardware the author owns.
Nothing from HTC or Tobii is redistributed here; the replacement is written
from documented behaviour, not copied code.
