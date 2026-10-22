# Prior art (checked 2026-10-21)

No existing project drives the VIVE Facial Tracker with HTC's own lip model
and post-processing outside SRanipal, and none replaces the Pro Eye path.

| Project | What it is | Relevance |
|---|---|---|
| [EinDev/FaceTrackingClone](https://github.com/EinDev/FaceTrackingClone) (MIT, C#, created 2026-09-27) | Native VRCFT module: Media Foundation UVC capture of `0BB4:0321`, watchdog/recovery, **Project Babble's ONNX model** (224x224, 45 shapes, downloaded at install), left view only | Closest. Shows capture works through the stock `usbvideo` driver with no XU activation; claims the eye side "cannot be replaced" (we found Tobii stream engine + license, see `eye-pipeline.md`). Not used: depends on Babble's model |
| Baballonia / VRCFT-Babble (Project Babble) | Separate app with a `VFTCapture` backend, sends to VRCFT over OSC | Not wanted (separate app, Babble model) |
| [BITFOXDEN/Driver.SteamFrame.ViveFacialTracker](https://github.com/BITFOXDEN/Driver.SteamFrame.ViveFacialTracker) (GPL-3.0) | usbfs daemon for Steam Frame, streams MJPEG to Baballonia | Documents the raw-USB XU handshake: unit 4, selector 2, magic / stream-off / 11 sensor registers / stream-on; a port reset clears a stuck test-pattern state. Useful cross-check only |
| [LordOfDragons/vivefacialtracker](https://github.com/LordOfDragons/vivefacialtracker) (MIT, Python) | Linux test app streaming the camera | Cross-check for XU commands (`dev_info`) |
| ViveStreamingFaceTrackingModule (VRCFT registry) | VIVE Focus 3 / XR Elite over VIVE Streaming | Different hardware |
| SRanipalTrackingModule (VRCFT registry) | Needs the SRanipal runtime | What we replace |

Notes from these:

- Stock UVC driver on Windows: one mode, YUY2 400x400 @ 60 fps, 320000 B
  per frame. FaceTrackingClone says no vendor activation is needed there;
  SRanipal still sends `50 14 00 01 00` at Start (`lip-pipeline.md`). To be
  tested on hardware.
- VRCFT 5.4 hosts modules out of process; only one process can own the UVC
  device, so the SRanipal runtime must not be running.
