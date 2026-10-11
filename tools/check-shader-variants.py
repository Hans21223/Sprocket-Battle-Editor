# Checks that every keyword set CustomMapBridge.cs assigns exists (as material-local keywords) in the game's
# compiled HDRP/Lit and HDRP/TerrainLit, for each pass that draws it.
import UnityPy, sys, re, os
UnityPy.config.FALLBACK_UNITY_VERSION = '6000.3.21f1'
src = open(sys.argv[1], encoding='utf-8').read()
data = sys.argv[2]
arrays = {m.group(1): set(re.findall(r'"([^"]+)"', m.group(2))) for m in re.finditer(r'static readonly string\[\] (\w+) = \{([^}]*)\}', src)}
def variants(file, name):
    env = UnityPy.load(os.path.join(data, file))
    for o in env.objects:
        if o.type.name != 'Shader': continue
        pf = o.read_typetree()['m_ParsedForm']
        if pf['m_Name'] != name: continue
        names = pf['m_KeywordNames']
        out = {}
        for p in pf['m_SubShaders'][0]['m_Passes']:
            out[p['m_State']['m_Name']] = [frozenset(names[i] for i in sp['m_KeywordIndices']) for lst in p['progFragment']['m_PlayerSubPrograms'] for sp in lst], {names[i] for i in p['progFragment']['m_SerializedKeywordStateMask']}
        return out
def check(passes, kw, wanted):
    ok = True
    for name in wanted:
        sets, declared = passes[name]
        need = kw & declared   # a pass only sees the keywords it declares
        local = {k for k in declared if k.startswith('_')}
        hit = any(s & local == need for s in sets)
        print(f'  {name}: {"ok" if hit else "MISSING"} {sorted(need)}')
        ok &= hit
    return ok
def material_keywords(declared, kw):
    # keywords a material can set: any declared keyword starting with '_' (material features), excluding ones we set
    return {k for k in declared if k.startswith('_')} - kw
lit = variants('globalgamemanagers.assets', 'HDRP/Lit')
terrain = variants('sharedassets20.assets', 'HDRP/TerrainLit')
foliage = variants('sharedassets17.assets', 'NatureManufacture/HDRP/Foliage/Foliage')
ok = True
for a, passes in [('OpaqueKeywords', ['GBuffer', 'DepthOnly', 'ShadowCaster', 'MotionVectors']), ('MappedKeywords', ['GBuffer', 'DepthOnly', 'ShadowCaster', 'MotionVectors']), ('BlendedKeywords', ['Forward'])]:
    print(a); ok &= check(lit, arrays[a], passes)
for a in ['TerrainKeywords', 'MappedTerrainKeywords', 'EightLayerTerrainKeywords']:
    print(a); ok &= check(terrain, arrays[a], ['GBuffer', 'DepthOnly', 'ShadowCaster'])
print('FoliageKeywords'); ok &= check(foliage, arrays['FoliageKeywords'], ['GBuffer', 'DepthOnly', 'ShadowCaster', 'MotionVectors'])
print('KEYWORD_SETS_OK' if ok else 'KEYWORD_SETS_MISSING')
