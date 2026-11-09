from pathlib import Path
import os,subprocess,sys
root=Path(__file__).resolve().parents[1];dotnet=os.environ.get('DOTNET','dotnet')
def run(args):subprocess.run(args,cwd=root,check=True)
run([dotnet,'build','Sattle.Design.sln','--nologo','-m:1'])
run([dotnet,str(root/'tests/Sattle.Specification/bin/Debug/net10.0/Sattle.Specification.dll'),str(root)])
run([sys.executable,'tests/validate_schemas.py',str(root)])
run([sys.executable,'tests/compile_contracts.py',str(root)])
print('All checks passed. Production engine remains unimplemented.')
