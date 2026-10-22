"""Reference outputs for Unvived.Tests, computed with OpenCV/numpy.

    py -I tests/reference/make_fixtures.py [path/to/lip_opencl.params]

Writes tests/fixtures/image.json (committed: OpenCV results on a synthetic
frame) and, with a params file, tests/fixtures/local/network.json
(ignored: outputs of a numpy implementation of the network, derived from
HTC's weights).
"""
import hashlib
import json
import struct
import sys
from pathlib import Path

import cv2
import numpy as np

WIDTH, HEIGHT = 800, 400
ROOT = Path(__file__).resolve().parent.parent / "fixtures"


def synthetic_frame():
    """Same generator as TestFrames.Synthetic in the C# tests."""
    out = bytearray(WIDTH * HEIGHT)
    state = 12345
    for i in range(len(out)):
        state = (1103515245 * state + 12345) & 0x7FFFFFFF
        noise = (state >> 16) & 0x3F
        y, x = divmod(i, WIDTH)
        out[i] = (x // 4 + y // 3 + noise) & 0xFF
    return np.frombuffer(bytes(out), np.uint8).reshape(HEIGHT, WIDTH)


def model_input(frame):
    blurred = cv2.medianBlur(frame, 5)
    views = [blurred[:, 0:400], blurred[:, 400:800]]
    merged = cv2.merge(views)
    small = cv2.resize(merged, (100, 100), interpolation=cv2.INTER_CUBIC)
    return blurred, small


def load_params(path):
    data = Path(path).read_bytes()
    pos = 16
    (count,) = struct.unpack_from("<Q", data, pos)
    pos += 8
    names = []
    for _ in range(count):
        (n,) = struct.unpack_from("<Q", data, pos)
        pos += 8
        names.append(data[pos:pos + n].decode())
        pos += n
    pos += 8
    tensors = {}
    for name in names:
        pos += 16 + 8
        (ndim,) = struct.unpack_from("<i", data, pos)
        pos += 4 + 4
        shape = struct.unpack_from(f"<{ndim}q", data, pos)
        pos += 8 * ndim
        (nbytes,) = struct.unpack_from("<q", data, pos)
        pos += 8
        tensors[name] = np.frombuffer(data, np.float32, nbytes // 4, pos).reshape(shape)
        pos += nbytes
    return tensors


def conv_relu(x, w, b):
    out_c, in_c, k, _ = w.shape
    size = x.shape[1] - k + 1
    # im2col: (in_c*k*k, size*size)
    cols = np.empty((in_c, k, k, size, size), np.float64)
    for ky in range(k):
        for kx in range(k):
            cols[:, ky, kx] = x[:, ky:ky + size, kx:kx + size]
    y = w.reshape(out_c, -1).astype(np.float64) @ cols.reshape(in_c * k * k, -1)
    y = y.reshape(out_c, size, size) + b[:, None, None]
    return np.maximum(y, 0)


def pool(x):
    c, h, w = x.shape
    return x.reshape(c, h // 2, 2, w // 2, 2).max(axis=(2, 4))


def network(t, x):
    x = pool(conv_relu(x, t["conv1_weight"], t["conv1_bias"]))
    x = pool(conv_relu(x, t["conv2_weight"], t["conv2_bias"]))
    x = conv_relu(x, t["conv3_weight"], t["conv3_bias"])
    x = t["fc5_ft_weight"].astype(np.float64) @ x.reshape(-1) + t["fc5_ft_bias"]
    x = t["fc6_10_weight"].astype(np.float64) @ x + t["fc6_10_bias"]
    return np.maximum(x, 0)


def main():
    frame = synthetic_frame()
    blurred, small = model_input(frame)
    ROOT.mkdir(parents=True, exist_ok=True)
    image = {
        "opencv": cv2.__version__,
        "frame_sha256": hashlib.sha256(frame.tobytes()).hexdigest(),
        "blurred_sha256": hashlib.sha256(blurred.tobytes()).hexdigest(),
        "left_100": small[:, :, 0].reshape(-1).tolist(),
        "right_100": small[:, :, 1].reshape(-1).tolist(),
    }
    (ROOT / "image.json").write_text(json.dumps(image))
    print("wrote", ROOT / "image.json")

    if len(sys.argv) > 1:
        x = small.transpose(2, 0, 1).astype(np.float32) * np.float32(1 / 255)
        out = network(load_params(sys.argv[1]), x.astype(np.float64))
        local = ROOT / "local"
        local.mkdir(exist_ok=True)
        (local / "network.json").write_text(json.dumps({"params": sys.argv[1], "outputs": out.tolist()}))
        print("wrote", local / "network.json", np.round(out, 4).tolist())


if __name__ == "__main__":
    main()
