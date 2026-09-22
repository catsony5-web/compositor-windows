import json
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont, ImageChops, ImageStat
from pypdf import PdfReader

root = Path(__file__).resolve().parents[3] / 'artifacts/qa/vector-preview20'
font = ImageFont.truetype('C:/Windows/Fonts/malgun.ttf', 23)
small = ImageFont.truetype('C:/Windows/Fonts/malgun.ttf', 16)
cad = json.loads((root / 'cad-batch/results.json').read_text(encoding='utf-8'))
assert len(cad) == 44 and all(r['VectorLayers'] > 0 and r['Ink'] > 100 for r in cad)

comparison = Image.new('RGB', (2400, 850), '#e5e7eb')
draw = ImageDraw.Draw(comparison)
for index, (name, label) in enumerate([('raster', '이전 방식 · 픽셀 미리보기 확대'), ('vector', '새 디자인 모드 · 벡터 원본에서 다시 그리기')]):
    comparison.paste(Image.open(root / 'cad-verified' / f'page-1-{name}-1600.png'), (index * 1200, 50))
    draw.text((index * 1200 + 20, 10), label + ' (1600%)', fill='#101828', font=font)
comparison.save(root / 'vector-comparison.png')

for start in range(0, len(cad), 12):
    sheet = Image.new('RGB', (1500, 1240), '#e5e7eb'); draw = ImageDraw.Draw(sheet)
    for index, record in enumerate(cad[start:start + 12]):
        picture = Image.open(root / 'cad-batch' / record['Preview']).convert('RGB'); picture.thumbnail((480, 270))
        x, y = (index % 3) * 500, (index // 3) * 310
        sheet.paste(picture, (x + (500 - picture.width) // 2, y + 30 + (270 - picture.height) // 2))
        draw.text((x + 8, y + 5), record['Preview'].removesuffix('.png'), fill='black', font=small)
    sheet.save(root / f'cad-contact-{start // 12 + 1}.png')

exports = []
for folder in ['cad-verified', 'ai']:
    for record in json.loads((root / folder / 'results.json').read_text(encoding='utf-8')):
        page = record['Page']; reader = PdfReader(root / folder / f'page-{page}-vector.pdf')
        visited = set(); counts = {'Images': 0, 'Forms': 0}
        def resources(dictionary):
            if not dictionary:
                return
            for reference in dictionary.get('/XObject', {}).get_object().values() if '/XObject' in dictionary else []:
                key = (reference.idnum, reference.generation)
                if key in visited:
                    continue
                visited.add(key); obj = reference.get_object()
                if obj.get('/Subtype') == '/Image':
                    counts['Images'] += 1
                if obj.get('/Subtype') == '/Form':
                    counts['Forms'] += 1; resources(obj.get('/Resources'))
        resources(reader.pages[0].get('/Resources'))
        if folder == 'cad-verified':
            assert counts['Images'] == 0, 'CAD paths were replaced with bitmap images'
        original = Image.open(root / folder / f'page-{page}-vector-1600.png').convert('RGB')
        exported = Image.open(root / folder / f'page-{page}-pdf-1600.png').convert('RGB')
        mean_error = sum(ImageStat.Stat(ImageChops.difference(original, exported)).mean) / 3
        assert mean_error < 3, (folder, page, mean_error)
        exports.append({'Kind': folder, 'Page': page, **counts, 'MeanRGBError': mean_error, 'RoundtripExact': record['RoundtripExact']})

report = {'CadFiles': len(cad), 'CadTotalSeconds': sum(r['Milliseconds'] for r in cad) / 1000,
          'LargestCadVectorBytes': max(r['VectorBytes'] for r in cad), 'Exports': exports}
(root / 'verification.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
print(json.dumps(report, indent=2))
