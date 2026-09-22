"""Independent flood reference on a 4000% source render; no editor selection code."""
from collections import deque
from pathlib import Path
import json
import sys
from PIL import Image, ImageDraw, ImageFont

root = Path(sys.argv[1])
reports = []
for name in ("actual-beam", "actual-room"):
    folder = root / name
    record = json.loads((folder / "results.json").read_text(encoding="utf-8-sig"))
    source = Image.open(folder / "source-4000.png").convert("RGB")
    w, h = source.size
    seed = record["Seed"]
    crop = record["Crop"]
    sx = int((seed["X"] - crop["X"]) * 40)
    sy = int((seed["Y"] - crop["Y"]) * 40)
    color = source.getpixel((sx, sy))
    eligible = bytearray(sum((a - b) ** 2 for a, b in zip(pixel, color)) <= 16 * 16 * 3 for pixel in source.getdata())
    expected = bytearray(w * h)
    queue = deque([sy * w + sx])
    while queue:
        i = queue.popleft()
        if not eligible[i] or expected[i]:
            continue
        expected[i] = 1
        x, y = i % w, i // w
        if x: queue.append(i - 1)
        if x + 1 < w: queue.append(i + 1)
        if y: queue.append(i - w)
        if y + 1 < h: queue.append(i + w)
    def differences(filename):
        mask = Image.open(folder / filename).convert("L").tobytes()
        return sum((v >= 128) != bool(e) for v, e in zip(mask, expected))
    previous = differences("previous-mask.png")
    precise = differences("precise-mask.png")
    assert precise < previous * .15, (name, previous, precise)
    reports.append(dict(Region=name, ReferenceSelectedPixels=sum(expected), PreviousDifferentPixels=previous,
                        PreciseDifferentPixels=precise, ReductionPercent=100 * (1 - precise / previous),
                        Milliseconds=record["Milliseconds"], Samples=record["Samples"]))

font = ImageFont.truetype("C:/Windows/Fonts/malgun.ttf", 22)
comparison = Image.new("RGB", (1440, 620), "#e5e7eb")
draw = ImageDraw.Draw(comparison)
for i, (filename, label) in enumerate((("previous-4000.png", "이전 선택 · 경계 어긋남"), ("precise-4000.png", "개선된 선택 · 벡터 경계와 글자 반영"))):
    region = Image.open(root / "actual-beam" / filename).convert("RGB").crop((460, 260, 920, 620))
    region = region.resize((720, 564), Image.Resampling.NEAREST)
    comparison.paste(region, (i * 720, 56))
    draw.text((i * 720 + 20, 14), label, font=font, fill="#1f2937")
comparison.save(root / "wand-comparison.png")
(root / "verification.json").write_text(json.dumps(reports, indent=2), encoding="utf-8")
print(json.dumps(reports, indent=2))
