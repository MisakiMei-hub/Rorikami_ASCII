"""Convert the neighboring split media into a portable PowerShell ASCII movie."""
from pathlib import Path
import argparse
import gzip
import json
import struct
import subprocess
import numpy as np
from PIL import Image

ROOT = Path(__file__).resolve().parent
WIDTH, HEIGHT, FPS = 240, 68, 24
DECODE_WIDTH, DECODE_HEIGHT = 320, 180
CHARS = np.frombuffer(b" .:-=+*#%@", dtype=np.uint8)


def active_bounds(rgb, previous):
    """Trim contiguous, almost completely black matte bands only.

    Inspect the center of each edge to avoid the corner logo and isolated
    compression noise. Keep the last crop during completely dark transitions.
    """
    bright = rgb.max(axis=2)
    if np.percentile(bright, 99) < 18:
        return previous
    height, width = bright.shape
    rows = (bright[:, width // 5:width * 4 // 5] <= 12).mean(axis=1) >= 0.99
    columns = (bright[height // 5:height * 4 // 5, :] <= 12).mean(axis=0) >= 0.99
    top = bottom = left = right = 0
    while top < height // 4 and rows[top]:
        top += 1
    while bottom < height // 4 and rows[height - bottom - 1]:
        bottom += 1
    while left < width // 4 and columns[left]:
        left += 1
    while right < width // 4 and columns[width - right - 1]:
        right += 1
    # Round very small edge fluctuations; real matte changes at cuts remain immediate.
    top, bottom, left, right = [round(value / 2) * 2 for value in (top, bottom, left, right)]
    bounds = (left, top, width - right, height - bottom)
    if max(abs(a - b) for a, b in zip(bounds, previous)) <= 2:
        return previous
    return bounds


def probe(path):
    return json.loads(subprocess.check_output([
        "ffprobe", "-v", "error", "-show_format", "-show_streams", "-of", "json", str(path)
    ]))


def main():
    parser = argparse.ArgumentParser(description="Build the bundled 640x360 source as an ASCII movie.")
    parser.add_argument("--video", type=Path, default=ROOT.parent / "video.m4s", help="Path to the source video")
    parser.add_argument("--audio", type=Path, default=ROOT.parent / "audio.m4s", help="Path to the source audio")
    args = parser.parse_args()
    video, audio = args.video.resolve(), args.audio.resolve()
    info, audio_info = probe(video), probe(audio)
    subprocess.run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-i", str(audio),
                    "-vn", "-acodec", "pcm_s16le", str(ROOT / "audio.wav")], check=True)
    duration = float(probe(ROOT / "audio.wav")["format"]["duration"])
    offset = max(0, round((float(info["streams"][0].get("start_time", 0)) -
                           float(audio_info["streams"][0].get("start_time", 0))) * 1000))
    # Classic console foreground colors; black is replaced by dark gray so
    # the dark foreground remains readable against the black background.
    palette = np.array([
        [0, 0, 0], [0, 0, 128], [0, 128, 0], [0, 128, 128],
        [128, 0, 0], [128, 0, 128], [128, 128, 0], [192, 192, 192],
        [128, 128, 128], [0, 0, 255], [0, 255, 0], [0, 255, 255],
        [255, 0, 0], [255, 0, 255], [255, 255, 0], [255, 255, 255]
    ], dtype=np.int32)
    cube = np.indices((32, 32, 32)).reshape(3, -1).T.astype(np.int32) * 8 + 4
    color_lut = ((cube[:, None, :] - palette[None, :, :]) ** 2).sum(axis=2).argmin(axis=1).astype(np.uint8)
    color_lut[color_lut == 0] = 8
    # Pad first so delogo can interpolate a watermark which touches the right edge.
    # Source logo rectangle: x=476..639, y=6..35. Originals are never rewritten.
    filters = (f"fps={FPS},pad=iw+8:ih+8:4:4,"
               "delogo=x=480:y=10:w=164:h=30:show=0,crop=640:360:4:4,"
               f"scale={DECODE_WIDTH}:{DECODE_HEIGHT}:flags=area")
    command = ["ffmpeg", "-hide_banner", "-loglevel", "error", "-i", str(video),
               "-an", "-vf", filters,
               "-pix_fmt", "rgb24", "-f", "rawvideo", "pipe:1"]
    process = subprocess.Popen(command, stdout=subprocess.PIPE)
    frame_bytes = DECODE_WIDTH * DECODE_HEIGHT * 3
    bounds = (0, 0, DECODE_WIDTH, DECODE_HEIGHT)
    count = 0
    with gzip.open(ROOT / "movie.ascii.gz", "wb", compresslevel=6) as output:
        output.write(b"ASCII01\n")
        output.write(struct.pack("<5i", WIDTH, HEIGHT, FPS, round(duration * 1000), offset))
        while True:
            data = bytearray()
            while len(data) < frame_bytes:
                chunk = process.stdout.read(frame_bytes - len(data))
                if not chunk:
                    break
                data.extend(chunk)
            if not data:
                break
            if len(data) != frame_bytes:
                raise RuntimeError("Incomplete decoded video frame")
            decoded = np.frombuffer(data, dtype=np.uint8).reshape(DECODE_HEIGHT, DECODE_WIDTH, 3)
            bounds = active_bounds(decoded, bounds)
            left, top, right, bottom = bounds
            cropped = Image.fromarray(decoded[top:bottom, left:right])
            rgb = np.asarray(cropped.resize((WIDTH, HEIGHT), Image.Resampling.BOX)).reshape(-1, 3)
            luminance = rgb @ np.array([0.2126, 0.7152, 0.0722])
            indices = np.minimum(9, ((luminance / 255.0) ** 0.85 * 9.99).astype(np.int32))
            quant = rgb.astype(np.int32) >> 3
            colors = color_lut[quant[:, 0] * 1024 + quant[:, 1] * 32 + quant[:, 2]]
            frame = np.empty((WIDTH * HEIGHT, 2), dtype=np.uint8)
            frame[:, 0] = CHARS[indices]
            frame[:, 1] = colors
            output.write(frame.tobytes())
            count += 1
            if count % (FPS * 30) == 0:
                print(f"Converted {count // FPS}s", flush=True)
    if process.wait() != 0:
        raise RuntimeError("FFmpeg video conversion failed")
    manifest = dict(width=WIDTH, height=HEIGHT, fps=FPS, frames=count,
                    video_seconds=count / FPS, audio_seconds=duration,
                    video_offset_ms=offset, source_video=video.name, source_audio=audio.name,
                    watermark_removed=True, auto_trim_black_borders=True,
                    default_display="fill terminal viewport")
    (ROOT / "movie.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    print(json.dumps(manifest, indent=2), flush=True)


if __name__ == "__main__":
    main()
