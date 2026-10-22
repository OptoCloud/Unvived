# What VRChat face tracking needs from SRanipal

Source: `SRanipalExtTrackingModule.dll` 1.5 (VRCFT module
`74f18332-1bcf-4b55-832d-9c91069afa31`, decompiled with ilspycmd to
`dumps/SRanipalExtTrackingModule/`). This is the whole surface a replacement
has to provide for VRCFaceTracking.

## Loading

- Reads `HKLM\Software\VIVE\SRWorks\SRanipal\ModuleFileName` for the path of
  `sr_runtime.exe` (falls back to `C:\Program Files\VIVE\SRanipal\sr_runtime.exe`)
  only to get its file version: `1.3.6.*` -> `ModuleLibs\New`, else `ModuleLibs\Old`.
- `SetDllDirectory(ModuleLibs\New|Old)`, then `LoadLibrary("SRanipal.dll")`.
  The client SDK in there: `SRanipal.dll`, `ViveSR_Client.dll`, `nanomsg.dll`,
  `libHTC_License.dll`, `SRWorks_Log.dll` (1.3.6.8 / 1.3.2.0).

## SRanipal.dll calls used (all cdecl)

| Call | Use |
|---|---|
| `Initial(int anipalType, void* config)` | type 2 = eye v2, 3 = lip v2. Returns `Error`; `WORK` = ok, `FOXIP_SO` = wireless, retried until it changes |
| `GetEyeData_v2(EyeData_v2*)` | every ~10 ms; `WORK` or the module re-inits |
| `GetLipData_v2(LipData_v2*)` | every ~10 ms; caller supplies `image` buffer (800x400 8-bit gray) |
| `IsViveProEye()` | true -> per-eye gaze; false -> averaged gaze |

Other declared but unused: `SetEyeParameter`, `GetEyeParameter`,
`IsUserNeedCalibration`, `LaunchEyeCalibration`, `Register/UnregisterEyeDataCallback[_v2]`,
`SRanipal_UpdateTimeSync`, `SRanipal_GetSystemTime`.

## Fields read

Eye (`EyeData_v2.verbose_data`, per eye, each gated on its validity bit):
- `eye_openness` (openness validity)
- `pupil_diameter_mm` (pupil validity)
- `gaze_direction_normalized` (gaze validity), X flipped
- `gaze_origin_mm.x` (only in an unused convergence helper)

Eye (`EyeData_v2.expression_data`): `left/right.eye_wide`, `left/right.eye_squeeze`.

Lip (`LipData_v2.prediction_data.blend_shape_weight[0..36]`, `LipShape_v2`
order): indices 0-32 are mapped to Unified Expressions; image copied if non-null.

## Eye camera image (side channel)

Not part of the API: with admin rights the module opens `sr_runtime.exe`
and reads 20000 bytes from `EyeCameraDevice.dll` base + `0x19100`
(runtime 1.3.1.1) or `+0x19190` (1.3.2.0): two 100x100 8-bit images
(left, right), shown side by side. So `EyeCameraDevice.dll` keeps a
downscaled IR image of each eye in a global buffer.

## Consequence for the replacement

Two ways to plug in:

1. **Own VRCFT module** (simplest): reads the devices directly and fills
   `UnifiedTracking.Data` itself. No SRanipal ABI needed.
2. **Drop-in `SRanipal.dll`** in a module's `ModuleLibs`: the five calls
   above with the SDK struct layouts. Works with the existing module and
   any game using the SDK, but must match `EyeData_v2`/`LipData_v2` exactly.

Either way the inputs are: Tobii gaze/openness/pupil (Pro Eye), eye
wide/squeeze (HTC's model on top of Tobii data), and 37 lip weights
(HTC's lip model on the Facial Tracker image).

## Packaging pitfalls (found 2026-10-22, VRCFT 5.4.5)

- Local modules are installed from a **zip** containing `module.json`
  (`scripts/package-module.ps1`).
- The module host (`VRCFaceTracking.ModuleProcess.exe`) loads the module
  DLL but does **not** resolve other DLLs next to it. A separate
  `Unvived.Core.dll` failed with `FileNotFoundException`, so Core is
  compiled into the module assembly.
- `module.json` needs a valid `ModulePageUrl`: without one, the module
  details page crashes the whole UI (`XamlBindingHelper.ConvertValue`,
  "The parameter is incorrect", `0xc000027b` in `Microsoft.UI.Xaml.dll`).
- Logs: `Documents\VRCFaceTracking\latest.log` (module output included).
  The "Got init ... Eye: {} Expr: {}" line prints `eyeSuccess` twice;
  "Tracking initialized via <module>" is the line that confirms it started.
