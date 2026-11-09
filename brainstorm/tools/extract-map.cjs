const fs=require('fs'),vm=require('vm');
const s=fs.readFileSync('dist/roadmap.js','utf8');
const sources=vm.runInNewContext('('+s.match(/const sources = (\[[\s\S]*?\n  \]);/)[1]+')');
const nodes=vm.runInNewContext('('+s.match(/const mapNodes = (\[[\s\S]*?\n  \]);/)[1]+')');
const edges=vm.runInNewContext('('+s.match(/const mapConnections=(\[[^;]+\]);/)[1]+')');
fs.writeFileSync('dist/landscape.json',JSON.stringify({sources,nodes,edges},null,2));
