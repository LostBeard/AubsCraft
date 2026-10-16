"""Keeps the creature models of a ModelDumpObf dump and writes them compactly: python filter_models.py all.json out.json"""
import json, sys

SKIP = ('ArmorStand', 'Banner', 'Bell', 'Book', 'Boat', 'Raft', 'Chest', 'Elytra', 'LeashKnot', 'Minecart', 'Shield', 'Skull', 'Trident', 'SpinAttack',
        'PlayerCape', 'PlayerEars', 'Saddle', 'Arrow', 'BeeStinger', 'WindCharge', 'LlamaSpit', 'ShulkerBullet', 'EvokerFangs', 'DragonHead')

def r(v):
    return round(v, 4) if isinstance(v, float) else v

def part(p):
    out = {'n': p['name']}
    pose = [r(x) for x in p['pose']]
    if any(pose): out['p'] = pose
    if 'scale' in p: out['s'] = [r(x) for x in p['scale']]
    if p['cubes']:
        out['c'] = [dict(uv=c['uv'], o=[r(x) for x in c['o']], d=[r(x) for x in c['d']], **({'g': [r(x) for x in c['g']]} if any(c['g']) else {}), **({'m': True} if c['m'] else {}))
                    for c in p['cubes']]
    if p['children']: out['k'] = [part(c) for c in p['children']]
    return out

src = json.load(open(sys.argv[1]))
dst = {}
for key, model in sorted(src.items()):
    if any(s in key for s in SKIP): continue
    dst[key] = {'tex': model['tex'], 'root': part(model['root'])}
json.dump(dst, open(sys.argv[2], 'w'), separators=(',', ':'))
print(len(dst), 'models')
