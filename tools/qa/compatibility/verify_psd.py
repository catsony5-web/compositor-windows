"""Independent Pillow checks for PSDs produced by scripts/Test.ps1 (not shipped)."""
from pathlib import Path
from PIL import Image

root = Path("artifacts/test-results/compatibility")
with Image.open(root / "flat.psd") as image:
    image.load()
    assert image.size == (64, 40) and image.mode == "RGBA"
    assert image.getpixel((0, 0)) == (255, 0, 0, 42)
    assert image.getpixel((10, 10)) == (255, 0, 0, 255)
with Image.open(root / "layers.psd") as image:
    image.load()
    assert image.getpixel((0, 0)) == (255, 0, 0, 255)
    assert image.getpixel((6, 7)) == (255, 0, 102, 255)
with Image.open(root / "masked.psd") as image:
    image.load()
    assert image.size == (64, 40)
    assert image.getchannel("A").getextrema()[0] == 0
    assert image.getchannel("A").getextrema()[1] == 128
print("PASS: independently decoded exported PSD pixels, transparency and composite")
