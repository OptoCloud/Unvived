# Architecture notes

Hand-written findings. Cite the binary and address (or export) for each claim.

## Process layout

- `SRanipalService` (Windows service, auto-start) -> `SRanipalService.exe`
  (.NET, tiny: 11 KB; decompile with `ilspycmd`) -> starts `sr_runtime.exe`.
- `sr_runtime.exe` imports `ViveSR_API.dll`, `ViveSR_Runtime.dll`, Qt5 (tray UI),
  `HID.DLL`/`SETUPAPI.dll` (device detection), `CrashRpt1403.dll`.
- `ViveSR_API.dll` and `ViveSR_Runtime.dll` both import `nanomsg.dll`: the
  runtime/app boundary is nanomsg (`ViveSR_Runtime_Listen*` exports).

`SRanipalService.exe` does nothing device-related: every 10 s it copies
`HKU\<logged-on SID>\SOFTWARE\VIVE\SRWorks` to `HKLM\SOFTWARE\VIVE\SRWorks`
(`SRService.OnTimer`). Removing it loses nothing.

## Client <-> runtime (ViveSR_Client.dll 1.3.6.8)

- Registry `HKLM\SOFTWARE\VIVE\SRWorks\SRanipal`: `ModuleFileName`
  (sr_runtime.exe path; the client launches it if absent), `PortMaster`
  (here `-1`), `PortAlive` (here `1003`). Error strings name them
  `ipc_req_master_port` / `ipc_req_master_alive_port`.
- Transport: nanomsg over `tcp://127.0.0.1:<port>`.
  - `FUN_1800015f0`, `FUN_180001920`: `NN_REQ` (0x30) sockets; recv timeout
    30000 ms resp. 1000 ms; `NN_RCVMAXSIZE` 27,600,000.
  - `FUN_1800019f0`: `NN_SUB` (0x21), subscribes to everything (empty
    prefix); receive thread `FUN_180001510` hands each message to a callback.
- Message format: TODO (`FUN_180002d00`, `FUN_180002e40`).

## SRanipal.dll (client SDK 1.3.6.8)

`Initial(type)` (`0x1800072a0`) creates, initialises, starts and links
runtime modules by ID:

| type | Modules (ViveSR_CreateModule ID) |
|---|---|
| 0 (eye v1) | `0xb` eye camera -> `0xc` eye prediction (Pro Eye only) |
| 2 (eye v2) | `0xb` eye camera -> `0xe` eye prediction v2 (Pro Eye only) |
| 3 (lip v2) | `9` lip camera -> `0xf` lip prediction v2 |
| 4 | `0xd`, then `SetParameterBool(0, true)` (unknown) |

Before eye init: `ViveSR_GetParameterBool(eye camera, 0xe)` must be true,
else `0x40e`. `VIVESR_CheckIsVBSRunning` (VIVE Business Streaming) allows the
prediction module on non-Pro-Eye headsets.

`GetEyeData_v2` (`0x180007f00`): `ViveSR_GetMultiData` on the eye camera
module fills `verbose_data`; on a Pro Eye a second call on the prediction
module returns 8 floats that **overwrite** Tobii's values:
`[0]>0` -> `no_user`, then left/right `eye_openness` (EyeData_v2 `+0x34`,
`+0x64`), then wide L/R (`+0xd8`, `+0xe4`), squeeze L/R (`+0xdc`, `+0xe8`),
frown L/R (`+0xe0`, `+0xec`). So openness, wide and squeeze all come from
HTC's eye CNN; Tobii gives gaze, origin and pupil.

`GetLipData_v2` (`0x180009100`): `ViveSR_GetMultiData` on the lip
prediction module, elements 2 -> `+4`, 3 -> `+0x10` (prediction_data),
and 1 -> the caller's image buffer when `image != NULL`.

## Module chain (hypothesis, from exports and imports)

```
Eye: EyeCameraModule -> (EyeCameraDevice.dll -> tobii_stream_engine.dll)
     -> EyePredictionModule[_v2] -> EyePredictionTVM -> tvm_runtime -> OpenCL
Lip: LipCameraModule (DirectShow) -> LipPredictionModule[_v2] -> LipPredictionTVM
```

## Prediction models (TVM, no TVM needed to run them)

`model/EyePrediction/00-0000.params_opencl.{json,params,dll}` and
`model/LPrediction/lip_opencl.*`: a TVM 0.4-era graph JSON, weights in TVM's
NDArray-list `.params` format (magic `0xF7E58D4F05049CB7`), and OpenCL
kernels compiled into the `.dll`. Shapes from the graph JSON
(`attrs.shape`) and `.params`; both models are the same network:

| Layer | Op | Output |
|---|---|---|
| input `data` | float32 | 1x2x100x100 |
| conv1 | conv 5x5, 2->20, stride 1, no pad, ReLU | 1x20x96x96 |
| pool | max 2x2 / 2 | 1x20x48x48 |
| conv2 | conv 5x5, 20->48, ReLU | 1x48x44x44 |
| pool | max 2x2 / 2 | 1x48x22x22 |
| conv3 | conv 3x3, 48->64, ReLU | 1x64x20x20 |
| fc5_ft | dense 25600->500, **no** activation | 1x500 |
| fc6_10 | dense 500->N, ReLU | eye N=5, lip N=30 |

Two input channels = two 100x100 images (eye: left/right IR; lip: presumably
the two halves of the 800x400 stereo frame, downscaled). The API exposes 37
lip shapes from 30 outputs and eye wide/squeeze from 5, so the module DLLs
map/derive outputs; preprocessing (crop, scale, normalisation) and that
mapping are what remain to read in `LipPredictionTVM.dll` / `EyePredictionTVM.dll`.

## Detailed notes

- `vrcft-contract.md`: what VRCFaceTracking needs.
- `lip-pipeline.md`: Facial Tracker camera, XU commands, preprocessing,
  post-processing (done).
- `eye-pipeline.md`: Tobii streams and license, eye CNN, post-processing
  (outline; heuristics TODO).

## Open questions

- [ ] nanomsg message framing (`ViveSR_Client.dll` `FUN_180002d00`,
      `FUN_180002e40`); only needed for a drop-in runtime.
- [ ] Parameter IDs (e.g. eye camera bool `0xe`, module `0xd`).
- [x] Tobii streams and license: wearable data + limited image,
      `tobii_device_create_ex` with an embedded HTC license.
- [ ] What `libEChipConnection` HID reports do; is eye tracking unusable
      without them?
- [x] Facial Tracker: VID 0BB4 PID 0321 (or 05A9:0581), UVC YUY2 400x400 =
      800x400 Y8; XU `{2CCB0BDA-...}` selector 2.
- [ ] Purpose of `Firmware/LipTracker/LipTracker.bin` (firmware update path?).
- [x] TVM models: small CNNs, runnable with numpy/ONNX from the `.params`.
- [ ] Eye post-processing heuristics (`FUN_18000f8a0` and helpers).
