from pathlib import Path
import zipfile,json,hashlib
root=Path(__file__).resolve().parents[1];source=root/'contracts'
manifest=source/'docs/PACKAGE-MANIFEST.json';manifest.unlink(missing_ok=True)
files=sorted(p for p in source.rglob('*') if p.is_file() and not set(p.parts)&{'bin','obj','__pycache__'})
manifest.write_text(json.dumps({'files':[{'path':str(p.relative_to(source)),'sha256':hashlib.sha256(p.read_bytes()).hexdigest()} for p in files]},indent=2)+'\n');files.append(manifest)
with zipfile.ZipFile(root/'dist/sattle-design-v0.1.zip','w',zipfile.ZIP_DEFLATED) as z:
 for p in files:z.write(p,Path('sattle-design')/p.relative_to(source))
print(f'Packaged {len(files)} design files.')
