"""Read explicit client goal fields, preserving previously working server goals.

XML identifies the objective maps. Names/quantities are extracted from the
client goal records; no nearby spawn, quest title or prose intent selects a goal.
"""
import json
import re
from pathlib import Path

ROOT=Path(__file__).resolve().parents[1]
OUT=ROOT/'artifacts/quest-runtime-content-20261007'


def complete(generated,generator):
    audit=json.loads((OUT/'all-quest-text-and-rules.json').read_text(encoding='utf-8'))
    rows={r['id']:r for r in audit}
    retained={int(q):v for q,v in json.loads((ROOT/'tools/data/quest-retained-kill-objectives.json').read_text(encoding='utf-8')).items()}
    monsters=generator.monster_ids()
    from quest_client_goal_sources import monster_names,chinese_monster_mentions,explicit_monster_quantity
    aliases,model_sources=monster_names(generator)
    (OUT/'monster-model-name-provenance.json').write_text(json.dumps(model_sources,
        ensure_ascii=False,indent=2),encoding='utf-8')
    collection_ids={r['id'] for r in json.loads(
        (OUT/'collection-and-capture-rules.json').read_text(encoding='utf-8'))['collections']}
    from gen_quest_runtime_requirements import monster_mentions,normalized_monster_words
    result={q:[tuple(g) for g in gg] for q,gg in retained.items()}
    notes=[];pending=[]
    for q,r in rows.items():
        if q in result:continue
        attrs=r['xml']
        if not attrs.get('CreatureMapID') or r['requirements']['playerKills'] or q in (187,538,1187,1538):continue
        obj=generator.COLOR.sub('',r['chinese'].get('Objectives',''))
        # Some Objectives explicitly say "the specified monsters"; their
        # Details contains the actual species. Read named fields in order,
        # accepting only names that resolve uniquely in the client catalog.
        name_fields=('Objectives','Details','QuestText') if re.search(r'指定(?:的)?怪物',obj) else ('Objectives',)
        candidates=[]
        source_field='Objectives'
        for field in name_fields:
            field_text=generator.COLOR.sub('',r['chinese'].get(field,''))
            candidates=chinese_monster_mentions(field_text,aliases,monsters)
            if candidates:
                source_field=field;break
        candidates=[c for c in candidates if not any(c[0]!=other[0] and c[0] in other[0] for other in candidates)]
        # Coordinate slots follow the order in the client goal field, not the
        # dictionary order of the monster-name catalog.
        candidates.sort(key=lambda c: field_text.index(c[0]))
        unique={}
        for name,mid,en in candidates:unique.setdefault(mid,(name,mid,en))
        candidates=list(unique.values())
        source=f'中文 {source_field} 与客户端怪物表的明确名称'
        if not candidates:
            en_obj=r['english'].get('Objectives','')
            hits=monster_mentions(en_obj,monsters)
            candidates=[(name,mid,name) for mid,name in hits]
            # Preserve the field's occurrence order after plural normalization.
            ordered_words=normalized_monster_words(en_obj)
            candidates.sort(key=lambda c: ordered_words.index(normalized_monster_words(c[0])))
            source='英文 Objectives 与客户端怪物表的明确名称'
        spots=generator.positions(attrs)
        if not candidates or not spots or len(candidates)>4 or (len(spots)>1 and len(candidates)!=len(spots)):
            # Never turn a declared but unresolved kill goal into a talk quest.
            pending.append(dict(id=q,reason='目标名称不能与客户端怪物表唯一对应',chinese=obj,
                english=r['english'].get('Objectives',''),maps=attrs['CreatureMapID']))
            continue
        goals=[]
        for index,(label,mid,en) in enumerate(candidates):
            count,qtysource=explicit_monster_quantity(r,label,en,generator)
            if count is None and q in collection_ids:
                notes.append(dict(id=q,monster=mid,clientName=label,name=en,
                    role='collection-source-only',targetSource=source,
                    quantitySource='没有独立击杀数量；按收集数量完成'))
                continue
            if count is None:
                # The user explicitly authorized only this quantity fallback.
                peers=[(abs(int(rows[p]['xml']['MinLevel'])-int(attrs['MinLevel'])),
                        0 if g[3]==mid else 1,p,g[2]) for p,gg in retained.items() if p in rows and
                        rows[p]['xml']['Faction']==attrs['Faction'] for g in gg if g[2]>1]
                _,_,peer,count=min(peers)
                qtysource=f'用户授权相近等级数量：任务 {peer}'
            mp,x,z=spots[min(index,len(spots)-1)]
            goals.append((en,'',count,mid,mp,x,z))
            notes.append(dict(id=q,monster=mid,clientName=label,name=en,count=count,
                targetSource=source,quantitySource=qtysource))
        if goals:result[q]=goals
    (OUT/'kill-rule-provenance.json').write_text(json.dumps(dict(added=notes,unresolved=pending),
        ensure_ascii=False,indent=2),encoding='utf-8')
    return sorted(result.items()),[r['id'] for r in pending]
