# Lip (VIVE Facial Tracker) pipeline

Everything between the USB camera and `GetLipData_v2`, from runtime 1.3.1.1.
Addresses are in the decompiles under `dumps/<dll>/decompiled/`.

## 1. Device (LCameraModule.dll)

- Match (`FUN_180005d70`, `LipCameraModule::Initial`): DirectShow video input
  device whose `DevicePath` contains `vid_0bb4`+`pid_0321` (HTC) or
  `vid_05a9`+`pid_0581` (OmniVision), or whose `FriendlyName` is
  `HTC Multimedia Camera`, else `USB Camera-OV580`. Not found -> `0x3ff`.
- It is a plain UVC camera (OmniVision OV580 stereo bridge). No vendor driver.

### Vendor channel: UVC extension unit

`IKsControl::KsProperty` with a `KSP_NODE` (`FUN_1800049c0` set, `FUN_180004aa0` get):

| Field | Value |
|---|---|
| Property set | `{2CCB0BDA-6331-4FDB-850E-79054DBD5671}` |
| Id (control selector) | `2` |
| Flags | set `0x10000002`, get `0x10000001` (`KSPROPERTY_TYPE_TOPOLOGY` \| SET/GET) |
| NodeId | the XU node found on the capture filter (`FUN_180004f50`) |
| Length | probed with a zero-size GET (`ERROR_MORE_DATA` returns it): 384 on current firmware; chunk size 256 if > 256 else 32 |

Every command is a SET followed by GETs until the reply's first byte is
`'V'` (0x56); the reply payload is bytes 1.. (`FUN_180004b80`, retries up to
11 times with 1 ms sleeps).

Commands seen:

| When | Buffer (bytes, rest zero) | Meaning (inferred) |
|---|---|---|
| Init probe (`FUN_180004f50`) | `51 52 ... [0xfe]=53 54` (SET), then `a1 a2 ... [0xfe]=a3 a4` (GET) | handshake, repeated until GET succeeds |
| Initial | register read `51 a2 60 04 01 00 00 00 00 ...` wrapping `a2 60 04 01 80 18 11 14` (`FUN_180004c80`: byte0 `0x51`, 1-4 = cmd, 5-8 = big-endian length, 9-10 = flags/chunk len, 11-12 = `00 01`) | reads a config byte; if reply[3]==1 the flag at reply[8] says the stereo halves are **swapped** |
| Start | `50 14 00 01 00` | enable streaming / IR (the "turn on" command) |

Confirmed on hardware (2026-10-21/22, Media Foundation, stock UVC driver):
- `IKsTopologyInfo` and `IKsControl` are available on the MF media source.
  The extension unit is topology node 3. The size probe first returned 384,
  then 64 on every later open; both sizes are known variants.
- From a cold start, the camera opens and accepts YUY2 400x400 but sends
  **no frames** until stream-on (`50 14 00 01`).
- **Stream-on alone leaves the IR illuminators off**: frames arrive but are
  black (mean about 1/255) even with a face in view. The working activation,
  from [LordOfDragons/vivefacialtracker](https://github.com/LordOfDragons/vivefacialtracker)
  (MIT), is: stream off, wait 250 ms, then write these 11 image sensor
  registers, then stream on, wait 250 ms:
  `00=40 08=01 70=00 02=ff 03=ff 04=ff 0e=00 05=b2 06=b2 07=b2 0f=03`.
  Before every command, send the `51 52 .. 53 54` test buffer. With that, the
  face image has a mean of about 55/255 and the model reports presence of about 1.0.
  HTC's runtime (1.3.1.1) never sends these writes itself, so presumably the
  firmware or an earlier tool set them, and they were lost on this unit.
- Register write layout: `50 ab 60 01 01 | addr[4] BE | 90 01 00 01 | value[4] BE`.
  The register read uses `51` (see Initial above). Replies: `55` = busy,
  `56` = done, followed by the echoed command.
- The frame is two 400x400 IR views side by side, as described below. With
  this unit, the halves need no swap: a smile reads as symmetric.
- Calling `IMFMediaSource::Shutdown` while a synchronous `ReadSample` is
  pending hangs the read. Stop by letting the next frame return instead.
  Stream-off on close also switches the IR off.

### Video

`FUN_1800013a0`: capture filter -> SampleGrabber -> NullRenderer.
Media type: `MEDIATYPE_Video`, subtype **YUY2**, `FORMAT_VideoInfo`. If the
default sample size is not 480000, the format is forced to **400x400**
(= 320000 bytes). The grab callback (`FUN_180001860`) memcpys the raw
buffer, no conversion: the "YUY2" frame **is an 800x400 8-bit grayscale
image** (two 400x400 IR views side by side).

`Process`: waits up to 3 s for a frame (`FUN_180001af0`); if the swapped
flag is set, outputs `hconcat(frame[:, 400:800], frame[:, 0:400])`.
Outputs: image (320000 B), frame counter, `clock()` timestamp.
Wireless adapter in use -> `Start` returns `0x41b`.

## 2. Preprocessing (LPredictionModule.dll + LipPredictionTVM.dll)

1. `cv::medianBlur(frame, 5)` on the 800x400 image (`LipPredictionModule_v2::Process`).
2. ROIs (constructor `0x180006bc0`): `(0,0,400,400)` and `(400,0,400,400)`.
3. `FUN_180002fa0` (LipPredictionTVM): crop both ROIs, `cv::merge` into one
   2-channel image, `cv::resize` to 100x100 with `INTER_CUBIC` (`interpolation = 2`
   at `0x1800031b3`), `convertTo(CV_32F, 1/255)`, `cv::split` into
   the two input planes -> tensor `data[1,2,100,100]`.
4. Run the CNN (see `architecture.md`), 30 outputs `o[0..29]`.

## 3. Post-processing (FUN_180007390, LPredictionModule.dll)

Writes into a 60-float array `w` (`PredictionData_v2.blend_shape_weight`);
state struct is `0x310` bytes (init `FUN_180006d80`).

- **Presence**: `o[0] < 0.4` -> count absent frames; after > 25 in a row all
  of `w[0..24]` = 0, `w[37] = o[0]`, and every adaptive value resets to 0.5
  (one to 0.7). Present frames: after 26 in a row, processing starts.
- **Neutral points** `n[]` (init 0.5): after 150 present frames, every 13th
  frame four of them (`o[1], o[5], o[6], o[2]`-related) are pulled toward the
  current output by a 1-D Kalman step (R = 20.5, Q = 0.001) when the change
  is < 0.2 (or < 0.55 and the value < 0.9 for the 4th).
- **Split around neutral**: `o[1]` -> `w[0]` (below) / `w[1]` (above); same
  for `o[5]` -> `w[5]/w[6]`, `o[6]` -> `w[7]/w[8]`; `w[2] = max(0, o[2] - n)`;
  `w[3] = o[3]`, `w[4] = o[4]`; `w[9..24]` = `o[7..22]`; `w[25..31]` = `o[23..29]`.
- **Running max normalisation**: for `w[0..31]` keep a per-shape max; divide
  by it (for index >= 27 only once the max reaches 0.502).
- **Mouth/tongue rules** on `w[26..32]` (`w[0x1a]..w[0x20]`): keep the larger
  of each opposing pair, thresholds 0.1/0.2/0.3/0.4/0.6, `x1.5`, `+0.12`,
  `w[32] = (w[26]-0.4)/0.6` etc.; all zeroed when `w[3] < 0.05`.
- **Smoothing**: 33 values through a 1-D Kalman filter (R = 0.5, Q = 0.25).
- **Combined shapes**: `w[33] = w[28]*w[29]`, `w[34] = w[27]*w[29]`,
  `w[35] = w[28]*w[30]`, `w[36] = w[27]*w[30]`; `w[41]` = pre-rule `w[26]`.

The exact branch structure is in the decompile; a reimplementation should
be checked frame-by-frame against the original on recorded frames.
