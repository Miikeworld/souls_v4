from pathlib import Path
import re,json,math
rows=[('GraveRend',30,1.35,48,50,3.2),('IronGale',26,1.6,36,32,3.4),('Mooncleaver',28,1.35,44,42,3),('AshenCleave',24,1.6,38,34,3.4),('RedReaver',30,1.35,46,48,3.1),('Stonebreaker',32,1.35,50,55,3),('TideSplitter',24,1.6,34,28,3.6),('GraveWolfI',20,1.5,26,24,3),('GraveWolfII',22,1.5,28,26,3),('GraveWolf',40,1.25,55,60,3.2),('MoltenArc',27,1.6,40,36,3.4),('SkyfallEdge',29,1.35,45,44,3),('MooncleaverRush',28,1.35,44,40,3.4),('Bonesunder',30,1.35,47,52,3),('LastEclipse',34,1.25,52,50,3.4)]
checks=0; ids=[]
for name,cost,mult,dmg,poise,reach in rows:
 p=Path('Assets/_Project/Combat/Arts/'+name+'.asset');s=p.read_text(encoding='utf-8');field=lambda n: float(re.search(r'^  '+n+r': (.+)',s,re.M)[1])
 for n,v in [('manaCost',math.ceil(cost*.75)),('cooldown',2.2 if mult==1.25 else 1.8 if mult==1.35 else 1.2),('damagePerHit',dmg),('poiseDamage',poise),('range',reach),('visualTheme',1),('contactMode',1 if name=='TideSplitter' else 0),('recoveryTransitionDelay',.06),('ribbonLife',.14 if name=='LastEclipse' else .10)]:
  assert abs(field(n)-v)<.00001,(name,n,field(n),v);checks+=1
 def windows(key):
  block = re.search(r'^  '+key+r':\n((?:  - \{x: .+\n)+)', s, re.M)
  assert block, (name, 'missing '+key)
  return [(float(a), float(b)) for a,b in re.findall(r'x: ([\d.]+), y: ([\d.]+)', block[1])]
 hits, trails = windows('hitWindows'), windows('trailWindows')
 assert all(0 <= a < b <= 1 for a,b in hits+trails), (name, 'invalid normalized contact/trail gate')
 assert all(any(c <= a and b <= d for c,d in trails) for a,b in hits), (name, 'damage outside blade trail')
 assert all(hits[i][1] < hits[i+1][0] for i in range(len(hits)-1)), (name, 'overlapping damage windows')
 if name == 'GraveWolf': assert len(hits) == 2, 'Full Grave Wolf must have two separate contacts'
 if name in ('Mooncleaver','MooncleaverRush'): assert len(trails) == 2 and len(hits) == 1, (name, 'rise/fall trails separate from landing damage')
 checks += 3
 # Setup now supplies owned particle cues. Do not require the old, empty prototype list.
 fx = re.search(r'  fxCues:(.*?)(?=\n  projectile:)', s, re.S)
 if fx:
  owned_fx = {re.search(r'^guid: (.+)', m.read_text(encoding='utf-8'), re.M)[1]
              for m in Path('Assets/_Project/FX').rglob('*.prefab.meta')}
  for ref in re.findall(r'prefab: \{fileID: [^,}]+, guid: ([0-9a-f]+)', fx[1]):
   assert ref in owned_fx, (name, 'cue must use a project-owned variant', ref)
 checks+=1
 guid=re.search(r'^guid: (.+)',Path(str(p)+'.meta').read_text(encoding='utf-8'),re.M)[1]; ids.append({'asset':str(p),'guid':guid,'state':re.search(r'^  stateName: (.+)',s,re.M)[1],'icon':re.search(r'^  icon: (.+)',s,re.M)[1]})
assert len(set(x['guid'] for x in ids))==15
manifest = Path('Tools/CrowdSkillIdentities.json')
if manifest.exists():
 assert json.loads(manifest.read_text(encoding='utf-8')) == ids, 'Skill identity/state/icon changed since baseline'
else:
 manifest.write_text(json.dumps(ids,indent=2),encoding='utf-8')
print(f'{checks} skill asset checks passed across all 15 entries; GUID/state/icon identities match the saved baseline. Visual contact timing still requires Unity.')

# Verify the saved owned prefab references as well as the future setup code.
# A valid shader alone cannot repair a renderer whose material slot is null.
folder = Path('Assets/_Project/FX/Crowd')
materials = {}
for meta in folder.glob('*.mat.meta'):
 guid = re.search(r'^guid: (.+)', meta.read_text(encoding='utf-8'), re.M)[1]
 materials[guid] = Path(str(meta)[:-5])
material_checks = 0
for prefab in folder.glob('*.prefab'):
 text = prefab.read_text(encoding='utf-8')
 for block in re.finditer(r'  m_Materials:\n((?:  - .+\n)+)', text):
  for reference in block[1].splitlines():
   guid = re.search(r'guid: ([0-9a-f]+)', reference)
   assert guid, (prefab.name, 'null particle/trail material slot')
   assert guid[1] in materials, (prefab.name, 'material must belong to project', guid[1])
   material = materials[guid[1]].read_text(encoding='utf-8')
   assert re.search(r'm_Shader: \{fileID: 4800000, guid: 0406db5a14f94604a8c57ccfbc9f3b46', material), (materials[guid[1]], 'URP particle Unlit shader')
   material_checks += 1
assert material_checks > 0, 'No saved particle renderer materials checked'
print(f'{material_checks} saved project-owned particle/trail slots reference valid URP materials.')
