"""Check sprocketmaps-shaders.bundle against the game: same shader platform, and for every material feature set it
compiled, every global keyword combination the game's own HDRP/Lit uses in that pass. Needs UnityPy.

    python verify-bundle.py Build/sprocketmaps-shaders.bundle game-lit-globals.json "<Sprocket>/Sprocket_Data"
"""
import json
import os
import sys

import UnityPy

UnityPy.config.FALLBACK_UNITY_VERSION = '6000.3.21f1'


def lit(env):
    for o in env.objects:
        if o.type.name == 'Shader':
            t = o.read_typetree()
            if t['m_ParsedForm']['m_Name'] == 'HDRP/Lit':
                return t
    raise SystemExit('no HDRP/Lit in ' + str(env))


bundle, globals_path, game = sys.argv[1:4]
bundle_env = UnityPy.load(bundle)
ours = lit(bundle_env)
if not any(o.class_id == 142 for o in bundle_env.objects):
    raise SystemExit('No AssetBundle object: the runtime refuses the bundle (enable com.unity.modules.assetbundle)')
theirs = lit(UnityPy.load(os.path.join(game, 'globalgamemanagers.assets')))
ok = ours['platforms'] == theirs['platforms']
print('platforms', ours['platforms'], 'game', theirs['platforms'])
# Transparency is a global-scope keyword in HDRP/Lit: transparent and opaque materials use different global sets.
GLOBAL = lambda k: not k.startswith('_') or k == '_SURFACE_TYPE_TRANSPARENT'
wanted = {p['name']: {tuple(sorted(k for k in s['keywords'] if GLOBAL(k))) for s in p['sets']}
          for p in json.load(open(globals_path))['passes']}
pf = ours['m_ParsedForm']
names = pf['m_KeywordNames']
for p in pf['m_SubShaders'][0]['m_Passes']:
    name = p['m_State']['m_Name']
    combos = {}
    for lst in p['progFragment']['m_PlayerSubPrograms']:
        for sp in lst:
            keys = [names[i] for i in sp['m_KeywordIndices']]
            local = tuple(sorted(k for k in keys if not GLOBAL(k)))
            combos.setdefault(local, set()).add(tuple(sorted(k for k in keys if GLOBAL(k))))
    vertex = sum(len(lst) for lst in p['progVertex']['m_PlayerSubPrograms'])
    fragment = sum(len(lst) for lst in p['progFragment']['m_PlayerSubPrograms'])
    if fragment and not vertex:
        print(f'  {name}: {fragment} fragment programs but NO vertex programs')
        ok = False
    need = wanted.get(name)
    if need is None:
        print(f'  {name}: not a pass the game uses ({len(combos)} feature sets)')
        continue
    for local, have in sorted(combos.items()):
        transparent = any('_SURFACE_TYPE_TRANSPARENT' in g for g in have)
        missing = {g for g in need if ('_SURFACE_TYPE_TRANSPARENT' in g) == transparent} - have
        status = 'ok' if not missing else f'MISSING {len(missing)} of {len(need)}'
        # A transparent-only pass legitimately has no opaque feature sets, and the reverse.
        print(f'  {name}: {" ".join(local) or "(none)"}: {len(have)} global sets, {status}')
        ok &= not missing
present = {p['m_State']['m_Name'] for p in pf['m_SubShaders'][0]['m_Passes']}
for name in sorted(set(wanted) - present):
    print(f'  {name}: pass MISSING from the bundle')
    ok = False
print('BUNDLE_VARIANTS_OK' if ok else 'BUNDLE_VARIANTS_INCOMPLETE')
