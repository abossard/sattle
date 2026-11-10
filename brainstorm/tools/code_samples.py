"""Site excerpts come directly from compile-checked example source."""
from pathlib import Path
import re, textwrap

EXAMPLES = [
 ('composition','Master, stacked and combined graphs','master','One definition binds the master, a service view and a business view that consumes both.',[1,9,11]),
 ('ingestion','Consume an input stream','input','Supply events or batches through the same async port. Handle each receipt.',[2]),
 ('subscription','Observe graph commits','diff','The graph feed carries snapshots, commits, progress and gaps.',[]),
 ('adapter','Adapt Hubble records','input','Pass native records and their versioned mapping to an injected adapter.',[3]),
 ('graph-data','Put data on an edge','master','An observed call has an identity, endpoints, a type and a health property.',[4]),
 ('identity','Resolve a stable identity','identity','Use the tenant, cluster and Kubernetes UID as the object key.',[5]),
 ('ontology','Declare a property','rule','The ontology decides which values a property accepts.',[6]),
 ('pattern','Match a graph pattern','rule','Bind the service endpoints and their observed call.',[7]),
 ('compile','Validate a composition','rule','Compile the whole graph space before installing its definitions.',[8]),
 ('union','Combine compatible rows','projection','Both branches must supply the same typed endpoint-pair shape.',[10]),
 ('health','Aggregate dependency health','projection','Group by service and ask whether any dependency has failed.',[12]),
 ('patches','Batch and compose ordered patches','diff','A batch keeps event boundaries. Ordered composition checks adjacent revisions.',[13]),
 ('deltas','Subtract deltas, then choose a base','diff','Signed arithmetic and patch conversion are separate typed requests.',[14]),
 ('bounded-read','Read a finite neighborhood','master','Pin a graph revision, scope and work budget for stored or virtual data.',[15]),
 ('recursion','Bound a traversal','projection','Follow Calls for up to four hops. Fixed-point recursion has additional requirements.',[16]),
 ('correspondence','Record a correspondence','identity','Connect a source deployment to a distinct target capability.',[17]),
 ('lens','Propose a backward edit','projection','Use an explicitly bidirectional rule and its versioned context.',[18]),
 ('configuration','Replace a projection definition','rule','An expected version guards the configuration edit.',[19]),
 ('document','Project and render Markdown','document','Pass the returned typed snapshot to a renderer. Keep its source revisions.',[20]),
]

def read_samples(root):
    source=root/'contracts/examples/Sattle.Examples/UsageExamples.cs'
    lines=source.read_text().splitlines()
    result=[]
    for id,title,role,copy,steps in EXAMPLES:
        start=next(i for i,l in enumerate(lines) if l.strip()==f'#region {id}')+1
        end=next(i for i in range(start,len(lines)) if lines[i].strip()=='#endregion')
        result.append(dict(id=id,title=title,role=role,copy=copy,steps=steps,
            file=source.name,line=start+1,text=textwrap.dedent('\n'.join(lines[start:end])),language='csharp'))
    result.append(dict(id='verification',title='Build and verify the design',role='rule',
        copy='Run from the extracted sattle-design folder with .NET 10 and Python installed.',steps=[21],
        file='Terminal',line=0,language='shell',text='python3 -m pip install -r requirements.txt\npython3 tools/verify.py',
        note='This builds the contracts and caller examples, then runs the finite model and selected schema/compiler checks. It does not execute a production engine.'))
    return result
