# Eye (VIVE Pro Eye) pipeline

From runtime 1.3.1.1. Addresses refer to `dumps/<dll>/decompiled/`.

## 1. Tobii (EyeCameraDevice.dll)

- `tobii_api_create` -> `tobii_enumerate_local_device_urls` ->
  `tobii_device_create_ex(api, url, licenses, 1)` (`~0x180002xxx`, see
  `all_functions.c` around line 1280 and 6299).
- **License**: one Tobii signed license, a UTF-16 JSON string
  (`{"licenseKey":{"header":...`), 0x930 bytes, embedded in
  `EyeCameraDevice.dll` at VA `0x1800173e0`. Not reproduced here. A
  replacement has to load it from the user's own copy of the DLL (or the
  user's own Tobii license).
- Streams:
  - `tobii_wearable_data_subscribe` -> `FUN_180007f30`: converts with
    `FUN_180007dd0` (per eye `FUN_180007bc0`; mostly field-for-field copies)
    into a 5-entry ring of 200-byte records; frame counter and
    `timestamp_us / 1000`.
  - `tobii_wearable_limited_image_subscribe` -> `FUN_180008070`: copies the
    two **100x100 8-bit** eye images to globals `0x180019100` (left) and
    `0x18001b810` (right). This is the buffer the VRCFT module reads from
    `sr_runtime.exe` memory (`EyeCameraDevice.dll` + `0x19100`).
- Also used: `tobii_set_lens_configuration` (IPD config, calibration case 5),
  `tobii_calibration_*` (3D calibration), `tobii_update_timesync`,
  `tobii_system_clock`, `tobii_get_device_info`.
- HID side (`libEChipConnection.dll`, `EyeDeviceConfiguration.dll`): TODO;
  check whether eye tracking works without `EnableEyeFeature`.

## 2. Openness / wide / squeeze (EyePredictionModule.dll, v2)

`EyePredictionModule_v2::Process` (`0x18000dfc0`):

1. Validity of each eye from the Tobii record (`FUN_180002d80`).
2. Eye images (100x100, 2 channels) -> `cv::medianBlur(5)` -> CNN via
   EyePredictionTVM (same preprocessing as lip: ROI crop, merge,
   `resize(100x100, INTER_CUBIC)`, `/255`, split) -> 5 outputs.
3. `FUN_18000f8a0`: adaptive post-processing (presence gate at mean of
   outputs 0/1 vs 0.5, 20-frame absence counter, per-eye neutral/max
   adaptation after 15/200 frames, 25-frame windows with 0.05 tolerance,
   force openness 0.1 after 10 invalid frames, ...). Helpers
   `FUN_180012430`, `FUN_1800126d0`, `FUN_1800128a0`, `FUN_180012a20`,
   `FUN_180012b70`, `FUN_180012ea0`, `FUN_180013030`, `FUN_180011390`. TODO.
4. `FUN_18000e6e0`: blink state per eye (open > 0.6, closed <= 0.4) in a
   16-frame ring, evaluated once per 1000 ms.
5. Output: 8 floats consumed by `SRanipal.dll` `GetEyeData_v2`
   (`architecture.md`): no_user, openness L/R, wide L/R, squeeze L/R, frown L/R.

`EyePredictionModule_v2` state `+0x1c0` set -> bypass: passes Tobii's own
openness (`+0x24`/`+0x54` of the records) and no expressions.
