"""Wire-shape tests and a deliberately limited semantic oracle, not a production compiler."""
from pathlib import Path
import copy,json,sys
from jsonschema import Draft202012Validator,FormatChecker
ROOT=Path(sys.argv[1] if len(sys.argv)>1 else Path(__file__).resolve().parents[1])
results=[]
def load(path):return json.loads(path.read_text())
def schema(name):
 s=load(ROOT/'schemas'/f'{name}.schema.json');Draft202012Validator.check_schema(s)
 return Draft202012Validator(s,format_checker=FormatChecker())
def record(name,fn,negative=False):
 try:fn()
 except (ValueError,AssertionError) as e:
  if not negative:raise
 else:
  if negative:raise AssertionError('Accepted invalid fixture: '+name)
 results.append({'name':name,'status':'passed'});print('PASS',name)
def validate(v,obj):
 errors=list(v.iter_errors(obj))
 if errors:raise ValueError(errors[0].message)
def mutate(obj,path,value):
 obj=copy.deepcopy(obj);p=obj
 for k in path[:-1]:p=p[k]
 p[path[-1]]=value;return obj
mapping={'graph-patch.json':'GraphPatch','signed-delta.json':'SignedDelta','ontology.json':'OntologyDefinition','graph-projection.json':'ProjectionDefinition','document-projection.json':'DocumentProjectionDefinition','document.json':'DocumentSnapshot','identity.json':'IdentityEntry'}
for file,name in mapping.items():
 record('schema accepts '+file,lambda f=file,n=name:validate(schema(n),load(ROOT/'examples'/f)))
for file,name,path,value,title in [
 ('graph-patch.json','GraphPatch',['unexpected'],1,'unknown field'),
 ('graph-patch.json','GraphPatch',['schemaVersion'],'999','unsupported schema version'),
 ('graph-patch.json','GraphPatch',['edits',0,'kind'],'invented','unknown edit tag'),
 ('graph-patch.json','GraphPatch',['from','tenant','value'],'','empty scoped ID'),
 ('signed-delta.json','SignedDelta',['terms',0,'weight'],'1.5','fractional weight'),
 ('signed-delta.json','SignedDelta',['terms',0,'weight'],'-0','noncanonical negative zero'),
 ('document.json','DocumentSnapshot',['blocks',0,'level'],7,'heading out of bounds'),
 ('graph-projection.json','ProjectionDefinition',['operators',1,'kind'],'execShell','unknown operator'),
 ('document-projection.json','DocumentProjectionDefinition',['maximumBlocks'],0,'unbounded document budget')]:
 record('schema rejects '+title,lambda f=file,n=name,p=path,v=value:validate(schema(n),mutate(load(ROOT/'examples'/f),p,v)),True)

def ontology_check(o):
 props={p['id']['value']:p for p in o['properties']};entities={t['id']['value']:t for t in o['entityTypes']};rels={t['id']['value']:t for t in o['relationshipTypes']}
 assert len(props)==len(o['properties']) and len(entities)==len(o['entityTypes']) and len(rels)==len(o['relationshipTypes'])
 assert o['noEntityType']['value'] in entities and o['noRelationshipType']['value'] in rels
 for t in list(entities.values())+list(rels.values()):
  assert all(p['value'] in props for p in t['properties'])
 for t in rels.values():assert all(x['value'] in entities for x in t['sourceTypes']+t['targetTypes'])
 return props,entities,rels

def projection_check(p,o,input_ontologies=None):
 props,entities,rels=ontology_check(o)
 input_ontologies=input_ontologies or {}
 assert p['ontology']==o['reference'],'ontology pin mismatch'
 nodes={n['id']['value']:n for n in p['operators']};assert len(nodes)==len(p['operators']),'duplicate operator'
 incoming={n:[] for n in nodes};bindings={x['name'] for x in p['inputs']}
 for w in p['wires']:
  a,b=w['from']['value'],w['to']['value'];assert a in nodes and b in nodes,'unknown operator';assert w['output']=='out','unknown output port'
  expected={'join':{'left','right'},'union':{'left','right'}}.get(nodes[b]['kind'],{'in'})
  assert w['input'] in expected,'unknown input port';incoming[b].append((w['input'],a))
 visiting=set();rows={}
 def expr(e,row):
  if e['kind']=='field':assert e['field'] in row,'unknown field';return row[e['field']]
  if e['kind']=='literal':return {'boolean':'boolean','text':'text','integer':'integer','decimal':'decimal','entity':'entity'}.get(e['value']['kind'],'unknown')
  args=[expr(x,row) for x in e['arguments']];f=e['function']
  if f in ('equal','notEqual'):assert len(args)==2 and args[0]==args[1],'comparison type';return 'boolean'
  if f in ('and','or'):assert len(args)==2 and args==['boolean','boolean'];return 'boolean'
  raise ValueError('semantic oracle does not implement '+f)
 def visit(n):
  if n in rows:return rows[n]
  assert n not in visiting,'ordinary cycle';visiting.add(n);node=nodes[n];kind=node['kind'];ins=incoming[n]
  assert len({x[0] for x in ins})==len(ins),'duplicate input port'
  if kind in ('input','match'):assert not ins and node['binding'] in bindings,'source binding'
  elif kind in ('join','union'):assert {x[0] for x in ins}=={'left','right'},'binary ports'
  else:assert len(ins)==1 and ins[0][0]=='in','unary port'
  upstream=[visit(x[1]) for x in ins];row=upstream[0] if upstream else {}
  if kind=='input':row={x['name']:x['type'] for x in node['row']['fields']};assert len(row)==len(node['row']['fields'])
  elif kind=='match':
   source=input_ontologies.get(node['binding'],o)
   source_props,source_entities,source_rels=ontology_check(source)
   row={}
   for pat in node['pattern']['nodes']:
    t=pat['type']['value'];assert t in source_entities;assert pat['variable'] not in row;row[pat['variable']]='entity'
    for prop in source_entities[t]['properties']:row[pat['variable']+'.'+prop['value']]=source_props[prop['value']]['valueKind']
   for edge in node['pattern']['edges']:
    assert edge['type']['value'] in source_rels and row.get(edge['source'])=='entity' and row.get(edge['target'])=='entity';row[edge['variable']]='relationship'
  elif kind=='map':row={a['field']:expr(a['value'],row) for a in node['fields']}
  elif kind=='filter':assert expr(node['predicate'],row)=='boolean'
  elif kind=='emitEntity':
   t=node['type']['value'];assert t in entities;owned={x['value'] for x in entities[t]['properties']}
   for assignment in node['properties']:
    prop=assignment['property']['value'];assert prop in owned and props[prop]['role']=='graphData','property ownership/role';assert expr(assignment['value'],row)==props[prop]['valueKind'],'property type'
   for a in node['identity']['components']:expr(a['value'],row)
  else:raise ValueError('semantic oracle does not implement '+kind)
  visiting.remove(n);rows[n]=row;return row
 for n in nodes:visit(n)
 if 'outputs' in p:assert all(x['value'] in nodes for x in p['outputs'])
 for t in p.get('templates',[]):
  row=rows[t['rows']['value']];assert all(x in row for x in t['blockIdentityFields']+t['rowIdentityFields']+t['fields']);assert all(x['field'] in row for x in t['orderBy']);assert set(t['rowIdentityFields']+t['blockIdentityFields']).issubset({x['field'] for x in t['orderBy']}),'stable tie break required'
 return rows

o=load(ROOT/'examples/ontology.json');p=load(ROOT/'examples/graph-projection.json');d=load(ROOT/'examples/document-projection.json')
record('semantic ontology references',lambda:ontology_check(o))
record('semantic graph projection fields and property ownership',lambda:projection_check(p,o))
record('semantic document projection stable identity and ordering',lambda:projection_check(d,o))
record('semantic rejects unknown property',lambda:projection_check(mutate(p,['operators',1,'properties',0,'property','value'],'madeUp'),o),True)
record('semantic rejects incompatible property value',lambda:projection_check(mutate(p,['operators',1,'properties',0,'value'],{'kind':'literal','value':{'kind':'boolean','value':True}}),o),True)
record('semantic rejects missing input field',lambda:projection_check(mutate(p,['operators',1,'identity','components',0,'value','field'],'missing'),o),True)
record('semantic rejects wire from nonexistent node',lambda:projection_check(mutate(p,['wires',0,'from','value'],'missing'),o),True)
record('semantic rejects missing binary/unary port',lambda:projection_check(mutate(p,['wires'],[]),o),True)
record('semantic rejects ontology mismatch',lambda:projection_check(mutate(p,['ontology','version'],'2'),o),True)
record('semantic rejects unstable document ordering',lambda:projection_check(mutate(d,['templates',0,'orderBy'],[{'field':'name','direction':'ascending'}]),o),True)
cycle=copy.deepcopy(d);cycle['wires'].append({'from':{'value':'rows'},'output':'out','to':{'value':'rows'},'input':'in'})
record('semantic rejects ordinary operator cycle',lambda:projection_check(cycle,o),True)
# Validate actual portable fixed-point definitions against the admitted finite fragment.
fp={'kind':'fixpoint','id':{'value':'closure'},'relation':'reachable',
 'row':{'fields':[{'name':'from','type':'entity','optional':False},{'name':'to','type':'entity','optional':False}]},
 'body':[{'kind':'input','id':{'value':'seed'},'binding':'edges','row':{'fields':[]}},
         {'kind':'distinct','id':{'value':'result'}}],
 'wires':[{'from':{'value':'seed'},'output':'out','to':{'value':'result'},'input':'in'}],
 'result':{'value':'result'},'finiteDomainBinding':'finite-nodes','maximumFacts':100,'maximumIterations':100}
def recursion(block,finite_bindings):
 assert block['finiteDomainBinding'] in finite_bindings,'no finite pinned domain'
 assert block['maximumFacts']>0 and block['maximumIterations']>0
 ids={n['id']['value'] for n in block['body']};assert block['result']['value'] in ids
 assert all(n['kind'] in {'input','match','join','union','distinct'} for n in block['body']),'unsupported recursive operator'
 assert all(n.get('conflict')=='requireEqual' for n in block['body'] if n['kind']=='union'),'recursive union must be set-compatible'
 assert all(w['from']['value'] in ids and w['to']['value'] in ids for w in block['wires'])
record('recursion admits finite positive portable operator body',lambda:recursion(fp,{'finite-nodes'}))
record('recursion rejects missing finite domain binding',lambda:recursion(mutate(fp,['finiteDomainBinding'],'unbounded'),{'finite-nodes'}),True)
for kind,title in [('emitEntity','fresh identities'),('group','aggregation'),('window','time window')]:
 bad=mutate(fp,['body',1,'kind'],kind)
 record('recursion rejects '+title,lambda block=bad:recursion(block,{'finite-nodes'}),True)
bad_union=copy.deepcopy(fp);bad_union['body'].append({'kind':'union','id':{'value':'union'},'conflict':'preferFirstPort'})
record('recursion rejects source-precedence union',lambda:recursion(bad_union,{'finite-nodes'}),True)
record('recursion rejects missing result relation',lambda:recursion(mutate(fp,['result','value'],'missing'),{'finite-nodes'}),True)
# Graph-space composition oracle: only the operators implemented above are admitted.
def graph_space_check(space,catalog):
 root=space['master']; children=space['projections']
 all_defs=[root['ingestionProjection']]+[x['projection'] for x in children]
 ids=[x['id']['value'] for x in all_defs]
 graph_ids=[root['graph']['value']]+[x['outputGraph']['value'] for x in children]
 assert len(set(ids))==len(ids),'duplicate projection id'
 assert len(set(graph_ids))==len(graph_ids),'duplicate graph id'
 def ont(ref):
  key=(ref['id'],ref['version']);assert key in catalog,'unknown ontology'
  value=catalog[key];assert value['reference']==ref,'ontology digest mismatch';return value
 projection_check(root['ingestionProjection'],ont(root['ingestionProjection']['ontology']))
 by_id={x['projection']['id']['value']:x for x in children};active=set();done=set()
 def visit(pid):
  if pid in done:return
  assert pid not in active,'projection dependency cycle';active.add(pid)
  child=by_id[pid];definition=child['projection'];bindings=[x['name'] for x in definition['inputs']]
  assert len(set(bindings))==len(bindings),'duplicate declared binding'
  ports=[x['port'] for x in child['graphInputs']]
  assert ports and len(set(ports))==len(ports),'missing or duplicate graph port'
  assert set(ports).issubset(bindings),'undeclared graph binding'
  input_schemas={}
  for binding in child['graphInputs']:
   source=binding['source']
   if source['kind']=='master':source_def=root['ingestionProjection']
   else:
    parent=source['projection']['value'];assert parent in by_id,'missing upstream graph'
    visit(parent);source_def=by_id[parent]['projection']
   assert binding['expectedOntology']==source_def['ontology'],'source ontology pin mismatch'
   input_schemas[binding['port']]=ont(binding['expectedOntology'])
  assert all(n['binding'] in input_schemas for n in definition['operators'] if n['kind']=='match'),'unbound graph match'
  projection_check(definition,ont(definition['ontology']),input_schemas)
  active.remove(pid);done.add(pid)
 for pid in by_id:visit(pid)
 return done
space=load(ROOT/'examples/graph-space.json')
catalog={(x['reference']['id'],x['reference']['version']):x for x in [o,load(ROOT/'examples/ontology-metrics.json'),load(ROOT/'examples/ontology-business.json')]}
record('schema accepts explicit master and stacked combined projections',lambda:validate(schema('GraphSpaceDefinition'),space))
record('composition validates source-local types and target-local emission',lambda:graph_space_check(space,catalog))
for path,value,title in [
 (['projections',0,'outputGraph','value'],'master','duplicate graph identity'),
 (['projections',0,'projection','id','value'],space['master']['ingestionProjection']['id']['value'],'duplicate projection identity'),
 (['projections',0,'graphInputs',0,'port'],'undeclared','undeclared graph port'),
 (['projections',0,'graphInputs',0,'expectedOntology','version'],'2','stale source ontology'),
 (['projections',1,'graphInputs',1,'source','projection','value'],'absent','missing upstream'),
 (['projections',0,'graphInputs',0,'source'],{'kind':'projection','projection':{'value':'business'}},'projection cycle'),
 (['projections',0,'graphInputs'],[],'rootless projection'),
 (['projections',1,'projection','operators',3,'properties',0,'value','field'],'m.name','wrong source property type'),
 (['projections',1,'projection','operators',4,'type','value'],'Service','input type used as output type')]:
 record('composition rejects '+title,lambda p=path,v=value:graph_space_check(mutate(space,p,v),catalog),True)
duplicated=copy.deepcopy(space);duplicated['projections'][0]['graphInputs']*=2
record('composition rejects duplicate input ports',lambda:graph_space_check(duplicated,catalog),True)
changed_catalog=copy.deepcopy(catalog);changed_catalog[('metrics','1')]['reference']['digest']['value']='changed'
record('composition requires downstream revalidation after source schema change',lambda:graph_space_check(space,changed_catalog),True)

(ROOT/'docs/schema-test-results.json').write_text(json.dumps({'testGroups':len(results),'results':results,'scope':'wire schemas and selected finite semantic fixtures; not a production compiler'},indent=2)+'\n')
print(f'PASS {len(results)} schema/semantic checks')
