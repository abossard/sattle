"""Positive and intentionally negative compiler tests; no NuGet test framework required."""
from pathlib import Path
import json,os,subprocess,tempfile,sys
root=Path(sys.argv[1] if len(sys.argv)>1 else Path(__file__).resolve().parents[1]).resolve();dotnet=os.environ.get('DOTNET','dotnet')
cases=[
 ('typed rows compose',True,'Rows<Service> x = new ReadRows<Service>("s", new([])); Rows<Service> y = new UnionRows<Service>(x,x);'),
 ('property value matches',True,'var x = new PropertyBinding<string>(new(new("name")),new Constant<string>("a"));'),
 ('entity and relationship IDs differ',False,'EntityId x = new RelationshipId("edge");'),
 ('property binding rejects numeric value',False,'var x = new PropertyBinding<string>(new(new("name")),new Constant<int>(1));'),
 ('predicate rejects nonboolean operand',False,'var x = new And(new Constant<string>("bad"),new Constant<bool>(true));'),
 ('rows reject unrelated union',False,'Rows<Service> a = new ReadRows<Service>("s",new([])); Rows<Pod> b = new ReadRows<Pod>("p",new([])); var c = new UnionRows<Service>(a,b);'),
 ('patch expression rejects signed delta',False,'var x = new PatchThen(new PatchLiteral(null!),new DeltaLiteral(null!));'),
 ('typed edge rejects wrong endpoint',False,'var x = new TypedPattern<Service,Pod>(new("s"),new RelationshipType<Pod,Service>(new("calls")),new("p"));')]
results=[]
with tempfile.TemporaryDirectory(prefix='sattle-types-') as tmp:
 d=Path(tmp);(d/'NuGet.Config').write_text('<configuration><packageSources><clear /></packageSources></configuration>')
 refs=''.join(f'<ProjectReference Include="{root}/src/Sattle.{n}/Sattle.{n}.csproj" />' for n in ['Model','Projections','Runtime'])
 (d/'test.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><LangVersion>14.0</LangVersion><Nullable>enable</Nullable><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup><ItemGroup>'+refs+'</ItemGroup></Project>')
 for name,positive,body in cases:
  (d/'Test.cs').write_text('using Sattle.Model;using Sattle.Projections;using Sattle.Runtime;public sealed record Service;public sealed record Pod;public static class Demo{public static void Check(){'+body+'}}')
  p=subprocess.run([dotnet,'build',str(d/'test.csproj'),'--nologo','-m:1','-v:q'],text=True,capture_output=True,cwd=root)
  ok=p.returncode==0
  if ok!=positive or not positive and not any(c in p.stdout for c in ('CS1503','CS0029')):raise AssertionError(name+'\n'+p.stdout+p.stderr)
  results.append({'name':name,'expectation':'compiles' if positive else 'type error','status':'passed'});print('PASS',name)
(root/'docs/compiler-test-results.json').write_text(json.dumps({'testGroups':len(results),'results':results},indent=2)+'\n')
