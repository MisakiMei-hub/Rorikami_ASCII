"""Build stable, detailed ASCII frames with cell-aligned RGB foreground colors."""
from pathlib import Path
import argparse
import gzip
import json
import struct
import subprocess
import numpy as np
from PIL import Image, ImageDraw, ImageFilter, ImageFont

ROOT = Path(__file__).resolve().parent
WIDTH, HEIGHT, FPS = 240, 68, 24
# Actual matte layouts, not moving-character bounding boxes.
MATTE_LAYOUTS = np.array([(0, 0, 320, 180), (36, 0, 284, 180),
                          (0, 18, 320, 162), (0, 44, 320, 136)], np.int32)


def probe(path):
    return json.loads(subprocess.check_output([
        "ffprobe", "-v", "error", "-show_format", "-show_streams", "-of", "json", str(path)]))


def read_frame(stream, length):
    data = bytearray()
    while len(data) < length:
        chunk = stream.read(length - len(data))
        if not chunk: break
        data.extend(chunk)
    if data and len(data) != length: raise RuntimeError("Incomplete decoded video frame")
    return data


def decode(video, filters):
    return subprocess.Popen(["ffmpeg", "-v", "error", "-i", str(video), "-an", "-vf", filters,
                             "-pix_fmt", "rgb24", "-f", "rawvideo", "pipe:1"], stdout=subprocess.PIPE)


def matte_candidate(rgb):
    """Measure matte edges before logo removal; ignore the corner logo."""
    value = rgb.max(axis=2)
    h, w = value.shape
    if (value[:, :w * 2 // 3] > 18).mean() < 0.01: return None
    rows = (value[:, w // 4:w * 2 // 3] <= 10).mean(axis=1) >= 0.995
    cols = (value[h // 4:h * 3 // 4] <= 10).mean(axis=0) >= 0.995
    top = bottom = left = right = 0
    while top < h // 4 and rows[top]: top += 1
    while bottom < h // 4 and rows[h - bottom - 1]: bottom += 1
    while left < w // 4 and cols[left]: left += 1
    while right < w // 4 and cols[w - right - 1]: right += 1
    return np.array((left, top, w - right, h - bottom))


def stable_layouts(video):
    decoder = decode(video, f"fps={FPS},scale=320:180:flags=area")
    modes, dark, previous = [], [], 0
    try:
        while raw := read_frame(decoder.stdout, 320 * 180 * 3):
            rgb = np.frombuffer(raw, np.uint8).reshape(180, 320, 3)
            candidate = matte_candidate(rgb)
            signal = rgb.max(axis=2).copy()
            signal[:22, 235:] = 0  # Only exclude the original logo rectangle.
            dark.append(signal.max() < 8)
            if candidate is not None:
                errors = np.max(abs(MATTE_LAYOUTS - candidate), axis=1)
                mode = int(np.argmin(errors))
                if errors[mode] <= 8: previous = mode
            modes.append(previous)
    finally:
        decoder.stdout.close()
    if decoder.wait() != 0: raise RuntimeError("FFmpeg matte analysis failed")
    modes = np.asarray(modes, np.uint8)
    starts = np.r_[0, np.flatnonzero(np.diff(modes.astype(np.int16))) + 1]
    ends = np.r_[starts[1:], len(modes)]
    # Remove short detector glitches; apply real changes at a cut, without
    # continuously moving the crop or easing its position over the image.
    for start, end in zip(starts, ends):
        if start and end - start < 8: modes[start:end] = modes[start - 1]
    return modes, np.asarray(dark, bool)


def character_lut():
    """Calibrate character coverage instead of assuming equally dense steps."""
    candidates = " .,:;-=+*oax%#MW&8B@"
    try:
        font = ImageFont.truetype("consola.ttf", 32)
        coverage = []
        for character in candidates:
            image = Image.new("L", (20, 40))
            ImageDraw.Draw(image).text((0, 0), character, font=font, fill=255)
            coverage.append(np.asarray(image).mean())
        order = np.argsort(coverage, kind="stable")
        glyphs = np.frombuffer(candidates.encode("ascii"), np.uint8)[order]
        levels = np.asarray(coverage)[order]
        levels = levels / levels[-1] * 255
    except OSError:
        glyphs = np.frombuffer(b" .-:,;=*+xoa#8WM%B&@", np.uint8)
        levels = np.array([0, 27, 27, 44, 48, 70, 77, 84, 84, 126, 134, 143,
                           179, 183, 191, 192, 192, 192, 216, 255])
    lut = glyphs[np.argmin(abs(np.arange(256)[:, None] - levels[None, :]), axis=1)]
    return lut, glyphs.tobytes().decode("ascii")


def convert_cells(rgb, lut, previous=None):
    rgb = np.asarray(rgb, np.float32)
    luminance = rgb @ np.array([0.2126, 0.7152, 0.0722], np.float32)
    blurred = np.asarray(Image.fromarray(luminance.astype(np.uint8)).filter(ImageFilter.GaussianBlur(0.8)), np.float32)
    # Fixed shadow lift + local contrast avoids per-frame brightness pumping.
    tone = np.clip(255 * (luminance / 255) ** 0.75 + 0.75 * (luminance - blurred), 0, 255)
    glyphs = lut[np.rint(tone).astype(np.uint8)]
    if previous is not None:
        old_tone, old_rgb, old_glyphs = previous
        unchanged = (abs(tone - old_tone) < 2) & (np.max(abs(rgb - old_rgb), axis=2) < 6)
        glyphs[unchanged] = old_glyphs[unchanged]
    peak = rgb.max(axis=2)
    lifted = 255 * (peak / 255) ** 0.82
    gain = np.divide(lifted, peak, out=np.ones_like(peak), where=peak > 0)
    # Preserve hue, gently lift shadows, and quantize channels to avoid noise.
    colors = np.clip(np.rint(rgb * gain[:, :, None] / 16) * 16, 0, 255).astype(np.uint8)
    frame = np.empty((HEIGHT, WIDTH, 4), np.uint8)
    frame[:, :, 0], frame[:, :, 1:] = glyphs, colors
    return frame, (tone, rgb, glyphs)


def main():
    parser = argparse.ArgumentParser(description="Build this project's 640x360 source as stable RGB ASCII.")
    parser.add_argument("--video", type=Path, default=ROOT.parent / "video.m4s")
    parser.add_argument("--audio", type=Path, default=ROOT.parent / "audio.m4s")
    args = parser.parse_args()
    video, audio = args.video.resolve(), args.audio.resolve()
    info, audio_info = probe(video), probe(audio)
    stream = info["streams"][0]
    if (stream["width"], stream["height"]) != (640, 360):
        raise ValueError("Matte layouts and logo coordinates require this project's 640x360 source")
    print("Analyzing original matte layouts before logo removal...", flush=True)
    modes, dark = stable_layouts(video)
    transitions = int(np.count_nonzero(np.diff(modes.astype(np.int16))))
    print(f"Stable crop: {transitions} transitions across {len(modes)} frames", flush=True)
    subprocess.run(["ffmpeg", "-v", "error", "-y", "-i", str(audio), "-vn",
                    "-acodec", "pcm_s16le", str(ROOT / "audio.wav")], check=True)
    duration = float(probe(ROOT / "audio.wav")["format"]["duration"])
    offset = max(0, round((float(stream.get("start_time", 0)) -
                           float(audio_info["streams"][0].get("start_time", 0))) * 1000))
    filters = (f"fps={FPS},pad=iw+8:ih+8:4:4,"
               "delogo=x=479:y=6:w=165:h=38:show=0,crop=640:360:4:4")
    decoder = decode(video, filters)
    lut, ramp = character_lut()
    previous, count = None, 0
    temporary = ROOT / "movie.ascii.building.gz"
    try:
        with gzip.open(temporary, "wb", compresslevel=6) as output:
            output.write(b"ASCII03\n" + struct.pack("<5i", WIDTH, HEIGHT, FPS, round(duration * 1000), offset))
            while raw := read_frame(decoder.stdout, 640 * 360 * 3):
                if count >= len(modes): raise RuntimeError("Analysis and conversion frame counts differ")
                decoded = np.frombuffer(raw, np.uint8).reshape(360, 640, 3)
                left, top, right, bottom = MATTE_LAYOUTS[modes[count]] * 2
                rgb = np.asarray(Image.fromarray(decoded[top:bottom, left:right]).resize((WIDTH, HEIGHT), Image.Resampling.LANCZOS))
                if dark[count]: rgb = np.zeros_like(rgb)
                if count == 0 or modes[count] != modes[count - 1]: previous = None
                frame, previous = convert_cells(rgb, lut, previous)
                output.write(frame.transpose(2, 0, 1).tobytes())
                count += 1
                if count % (FPS * 30) == 0: print(f"Converted {count // FPS}s", flush=True)
        if decoder.wait() != 0: raise RuntimeError("FFmpeg conversion failed")
        if count != len(modes): raise RuntimeError("Analysis and conversion frame counts differ")
        temporary.replace(ROOT / "movie.ascii.gz")
    finally:
        decoder.stdout.close()
        if decoder.poll() is None:
            decoder.terminate()
            decoder.wait()
    starts = np.r_[0, np.flatnonzero(np.diff(modes.astype(np.int16))) + 1]
    manifest = dict(format="ASCII03", bytes_per_cell=4, storage="per-frame character / R / G / B planes", width=WIDTH, height=HEIGHT,
                    fps=FPS, frames=count, video_seconds=count / FPS, audio_seconds=duration,
                    video_offset_ms=offset, source_video=video.name, source_audio=audio.name,
                    watermark_removed=True, auto_trim_black_borders=True,
                    stable_crop_transitions=transitions, character_ramp=ramp,
                    color_encoding="cell-aligned RGB, 16-step channel quantization",
                    contrast="fixed shadow lift and local contrast; calibrated glyph coverage",
                    matte_runs=[dict(start_frame=int(start), layout=int(modes[start])) for start in starts],
                    default_display="fill terminal viewport")
    (ROOT / "movie.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    print(json.dumps({k: v for k, v in manifest.items() if k != "matte_runs"}, indent=2), flush=True)


if __name__ == "__main__": main()
