# Where to get SRanipal's model weights

The module needs HTC's lip model, `lip_opencl.params`. The eye model is
listed too, for the later Pro Eye work. These files are HTC's and are not
redistributed here. Each user fetches them from one of the sources below.
All were checked on 2026-10-21.

## Known-good files

The weights did not change between runtime 1.3.1.1 and 1.3.6.5.

| File | Size (bytes) | SHA-256 |
|---|---|---|
| `model/LPrediction/lip_opencl.params` | 51,474,034 | `45d8f94445c8dc727e13fff30048453894e1b931c2affe74d1b75ff214755b28` |
| `model/EyePrediction/00-0000.params_opencl.params` | 51,423,934 | `bac8013e0423068924f190a1de44afd5e1dd0c7c10d1d394926e46fc1b075ded` |

Both hashes match in every source marked "verified" below: the installed
1.3.1.1 runtime, files extracted from the HTC-signed 1.3.1.1 MSI, and the
direct downloads.

## Sources

| Source | What | Size (bytes) | Hash | Status |
|---|---|---|---|---|
| [HTC CDN](https://dl.vive.com/SRSDK/runtime/VIVE_SRanipalInstaller_1.3.1.1.msi) | Official MSI 1.3.1.1 | 230,817,792 | SHA-256 `185b67660c132c7c7c904e565963ab349e89c2a9b16eb2e3b10a2210bd2b5c4d`, MD5 `44b59a4a54ea4196f12c282a38ea175c` | Verified: Authenticode-signed by HTC Corp. (DigiCert); both models extracted and hashed |
| [HTC CDN](https://dl.vive.com/SRSDK/runtime/VIVE_SRanipalInstaller.msi) | Official MSI, unversioned name (is 1.3.2.0) | 233,721,856 | SHA-256 `8ff4531efa52272a2096d89d52e3ef2881ced3ff0be0a71ecc9a2c3cf6b3bd22`, MD5 `2ad71ea3562a8f59fb54aa160b869cc8` | Live; same MD5 as archive.org's 1.3.2.0 |
| [Wayback Machine, 2026-10-21](https://web.archive.org/web/20261021183747/https://dl.vive.com/SRSDK/runtime/VIVE_SRanipalInstaller_1.3.1.1.msi) | Snapshot of HTC's 1.3.1.1 link | | | Saved 2026-10-21; not downloaded or checked |
| [Wayback Machine, 2026-10-21](https://web.archive.org/web/20261021183910/https://dl.vive.com/SRSDK/runtime/VIVE_SRanipalInstaller.msi) | Snapshot of HTC's unversioned (1.3.2.0) link | | | Saved 2026-10-21; not downloaded or checked |
| [archive.org](https://archive.org/download/vive-sranipal-installer/VIVE_SRanipalInstaller_1.3.1.1.msi) | MSI 1.3.1.1 | 230,817,792 | Same as HTC's 1.3.1.1 | Verified (byte-identical) |
| [archive.org](https://archive.org/download/vive-sranipal-installer/VIVE_SRanipalInstaller_1.3.2.0.msi) | MSI 1.3.2.0 | 233,721,856 | MD5 `2ad71ea3562a8f59fb54aa160b869cc8` (archive.org's) | Live |
| [archive.org](https://archive.org/download/vive-sranipal-installer/SRanipal_1.3.6.5.zip) | Runtime 1.3.6.5 zip (the version VRCFT recommended) | 221,419,325 | MD5 `da5bb609b7f0cce722747add73155ad5` (archive.org's) | Live |
| [archive.org, single file](https://archive.org/download/vive-sranipal-installer/SRanipal_1.3.6.5.zip/SRanipal%2Fmodel%2FLPrediction%2Flip_opencl.params) | `lip_opencl.params` served out of the 1.3.6.5 zip | 51,474,034 | Known-good | Verified |
| [archive.org, single file](https://archive.org/download/vive-sranipal-installer/SRanipal_1.3.6.5.zip/SRanipal%2Fmodel%2FEyePrediction%2F00-0000.params_opencl.params) | Eye model out of the same zip | 51,423,934 | Known-good | Verified |
| [archive.org item](https://archive.org/details/vive-sranipal-installer-1.3.2.0) | Second upload of MSIs 1.3.1.1 and 1.3.2.0 | | Same MD5s as above | Live |
| [GitHub, EtiTheSpirit/MiniFaceTracker](https://raw.githubusercontent.com/EtiTheSpirit/MiniFaceTracker/master/FaceTrackerDriver/lib/SRanipalSystem/model/LPrediction/lip_opencl.params) | `lip_opencl.params` committed to a third-party repo | 51,474,034 | Known-good | Verified; could be taken down at any time |
| [Google Drive](https://drive.google.com/file/d/1Wt9MSsSa8LJLP2NFp0UmjRw9uHtbbFFO/view) | MSI 1.3.1.1, an old VRCFT docs mirror | | | Live (page names the file); not downloaded |
| [Steam: VIVE Console for SteamVR](https://store.steampowered.com/app/1635730/VIVE_Console_for_SteamVR/) | HTC's current channel; installs to `steamapps\common\VIVEDriver\App\SRanipal` | | | Not checked (SteamDB blocks automated reads) |

## Dead ends

- `hub.vive.com/en-US/download/VIVE_SRanipalInstaller_<version>.msi`: now
  redirects to the developer portal. Older Wayback Machine captures of these
  links only redirect to `hub.vive.com/sso/login`: the downloads needed an
  account, so the crawler never got the files.
- `developer.vive.com/resources/vive-sense/eye-and-facial-tracking-sdk/download/{latest,archive}/`
  return 404.
- Other versioned names under `dl.vive.com/SRSDK/runtime/` (1.3.0.9,
  1.3.1.0, 1.3.3.0, 1.3.6.5 to 1.3.6.8, 1.1.x) return 404.

## Extracting from the MSI without installing

Nothing in the MSI has to run. The MSI is an OLE compound file, and its
largest stream is a cabinet (`MSCF`) whose files are stored under
component keys rather than their real names. With 7-Zip:

    7z x VIVE_SRanipalInstaller_1.3.1.1.msi -omsi
    7z x msi/_7F9B862A1CE3B4FC1BDFAD5E707CD901 -ocab   # the ~228 MB cabinet stream
    # lip model: the 51,474,034-byte file; check its SHA-256 against the table above

Before extracting, check the MSI's signature:
`Get-AuthenticodeSignature <msi>` should report `Valid` and `CN=HTC Corp.`.
