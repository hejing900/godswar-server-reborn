"""Complete item/capture requirements without replacing working kill objectives.

Existing item quantities are preserved. New missing quantities use the user's
nearest-level policy; provenance is written alongside the generated content.
"""
import collections
import json
import re
import sys
from pathlib import Path
import gen_quest_objectives as kills
import gen_quest_collect_objectives as items
import gen_sparta_quest_chain as chain
from audit_quest_runtime_content import blocks
from quest_client_goal_sources import monster_names,chinese_monster_mentions

ROOT = Path(__file__).resolve().parents[1]
CONTENT = ROOT / 'src/Godswar.Server/Domain/World/Content'
OUT = ROOT / 'artifacts/quest-runtime-content-20261007'
CLIENT = Path(r'D:\Godswar Origin\Localization')



def normalized_monster_words(value):
    return ' '.join(word[:-2] if word.endswith('sses') else kills.fold_head_word(word) for word in
        re.sub(r'[^0-9A-Za-z]+',' ',kills.COLOR.sub('',value)).lower().split())


def monster_mentions(text, monsters):
    plain = normalized_monster_words(text)
    candidates = []
    for mid,name in monsters.items():
        if re.search(r'player.*level|level.*player',name,re.I):continue
        if kills.contains_words(plain, normalized_monster_words(name)):
            candidates.append((mid,name))
    return [(mid,name) for mid,name in candidates if not any(mid!=other and
        len(kills.needle(othername))>len(kills.needle(name)) and
        kills.contains_words(kills.needle(othername),kills.needle(name))
        for other,othername in candidates)]


def main():
    old = {int(q):tuple(value) for q,value in json.loads(
        (ROOT/'tools/data/quest-retained-collect-objectives.json').read_text(encoding='utf-8')).items()}
    rows=chain.quest_rows(); selected,_=chain.select(rows,chain.published_npcs())
    monsters=kills.monster_ids(); catalog=items.quest_items(); result=[]; captures=[]
    aliases,_=monster_names(kills)
    retained_kills={int(q):goals for q,goals in json.loads(
        (ROOT/'tools/data/quest-retained-kill-objectives.json').read_text(encoding='utf-8')).items()}
    audit={r['id']:r for r in json.loads((OUT/'all-quest-text-and-rules.json').read_text(encoding='utf-8'))}
    cn_monsters={}
    for line in kills.read_text(str(CLIENT/'zh_cn/Text/QuestMonster.dat')).splitlines():
        parts=line.split('\t')
        if len(parts)>1 and parts[0].lstrip('\ufeff').isdigit():
            cn_monsters[int(parts[0].lstrip('\ufeff'))]=parts[1].strip()
    cn_items={}
    for line in kills.read_text(str(CLIENT/'zh_cn/Text/QuestItem.dat')).splitlines():
        parts=line.split('\t')
        if len(parts)>1 and parts[0].lstrip('\ufeff').isdigit():
            cn_items.setdefault(parts[1].strip(),[]).append(int(parts[0].lstrip('\ufeff')))
    for qid,camp in [(r[0],r[1]) for group in selected.values() for r in group]:
        attrs=rows[qid]; text=blocks(CLIENT/f'en_us/Text/Quest/{qid}.dat')
        obj=kills.COLOR.sub('',text.get('Objectives',''))
        cn_obj=kills.COLOR.sub('',audit[qid]['chinese'].get('Objectives','')).strip()
        if cn_obj.startswith('捕捉'):
            targets=kills.parse_objectives(re.sub(r'^Capture','Kill',obj,flags=re.I),monsters)
            spots=kills.positions(attrs); out=[]
            for index,(name,count) in enumerate(targets):
                mid,resolved=kills.resolve_monster(name,monsters)
                if not mid or not spots:continue
                # A legacy parser's absent-number default is not evidence.
                # Verify the count against full client fields for this species.
                explicit_count=None;quantity_source=None
                cn_name=cn_monsters.get(mid,'')
                for field in ('Objectives','Details','QuestText','IncompleteText'):
                    plain=kills.COLOR.sub('',audit[qid]['chinese'].get(field,''))
                    match=re.search(r'(\d+|一|两|二)\s*(?:只|条|个)\s*'+re.escape(cn_name),plain) if cn_name else None
                    if match:
                        explicit_count={'一':1,'两':2,'二':2}.get(match[1]) or int(match[1])
                        quantity_source='中文 '+field+' 明确数量';break
                if explicit_count is None:
                    match=re.search(r'\bCapture\s+(\d+)\b',obj,re.I)
                    if match:explicit_count=int(match[1]);quantity_source='英文 Objectives 明确数量'
                if explicit_count is None:
                    raise ValueError(f'capture {qid}: no verified quantity for {name}')
                count=explicit_count
                mp,x,z=spots[min(index,len(spots)-1)]
                out.append(dict(target=name,name=resolved,count=count,monster=mid,map=mp,x=x,z=z,
                    quantitySource=quantity_source))
            if qid in (538,1538):
                pairs = [('Flower Pixie',5275),('Minotaur',5276)]
                out = [dict(target=name,name=name,count=1,monster=mid,map=int(attrs['GiverMapID']),
                    x=0,z=0,quantitySource='中文 Objectives：一只；英文 Objectives：1',
                    coordinateSource='未提供目标坐标，不是刷怪位置') for name,mid in pairs]
            captures.append(dict(id=qid,targets=out,text=obj))
        value=old.get(qid)
        cn_item_label=None
        source='保留现有服务端道具要求' if value else None
        if not value:
            item_name=items.resolve(obj,catalog)
            if item_name and re.search(r'collect|bring|obtain|find|loot|gather|get|retrieve',obj,re.I):
                mid,how=items.pick(catalog[item_name],camp)
                if mid:
                    count=re.search(r'(\d+)\s+(?:'+re.escape(item_name)+r')',obj,re.I)
                    value=(mid,int(count[1]) if count else None);source='客户端道具表和任务正文'
        if not value and attrs.get('ItemMapID'):
            cn=kills.COLOR.sub('',audit[qid]['chinese'].get('Objectives',''))
            names=[(name,ids) for name,ids in cn_items.items() if name and name in cn]
            names=[n for n in names if not any(n[0]!=other[0] and n[0] in other[0] for other in names)]
            if len(names)==1:
                name,ids=names[0]
                cn_item_label=name
                # Keep ambiguous display IDs zero; these are virtual counters,
                # never bag items, and must not fabricate a native display ID.
                item_id=ids[0] if len(ids)==1 else 0
                explicit=re.search(r'(\d+)(?:个|只|条|份|粒|块)?'+re.escape(name),cn)
                value=(item_id,int(explicit[1]) if explicit else None)
                source='中文 Objectives 明确道具名；显示ID已验证' if item_id else '中文 Objectives 明确道具名；显示ID待核对'
            else:
                explicit=re.search(r'(?:取得|得到|获得|收集|获取|找回)(\d+)?(?:个|只|条|份|粒|块)?([^，。；,.;]+)',cn)
                if explicit:
                    cn_item_label=explicit[2].strip()
                    value=(0,int(explicit[1]) if explicit[1] else None)
                    source='中文 Objectives 明确收集要求；显示ID待核对：'+explicit[2]
        if not value:continue
        if value[1] is None:
            cn=kills.COLOR.sub('',audit[qid]['chinese'].get('Objectives',''))
            exact=re.search(r'(?:取得|得到|获得|收集|获取|找回)\s*(\d+)',cn)
            if exact:value=(value[0],int(exact[1]));source+='；中文 Objectives 明确数量'
        if value[1] is None and cn_item_label:
            for field in ('Details','QuestText','IncompleteText'):
                cn=kills.COLOR.sub('',audit[qid]['chinese'].get(field,''))
                exact=re.search(r'(\d+|一|两|二)\s*(?:个|只|条|份|粒|块)?\s*'+
                    re.escape(cn_item_label),cn)
                if not exact:
                    # "珠子，你能去帮我们弄12颗吗" explicitly names this
                    # collection and puts its quantity after the item name.
                    exact=re.search(re.escape(cn_item_label)+r'[^0-9。；]{0,16}(\d+)\s*(?:颗|个|只|条|份|粒|块)',cn)
                if exact:
                    count={'一':1,'两':2,'二':2}.get(exact[1]) or int(exact[1])
                    value=(value[0],count);source+='；中文 '+field+' 明确数量';break
        if value[1] is None:
            exact=None
            if not exact:
                exact=re.search(r'\b(?:collect|bring|obtain|find|loot|gather|get|retrieve)\s+(\d+)',obj,re.I)
            if exact:value=(value[0],int(exact[1]));source+='；英文 Objectives 明确数量'
        if value[1] is None:
            details=kills.COLOR.sub('',text.get('Details',''))
            if re.search(r'\bI must have one\b',details,re.I) and item_name:
                value=(value[0],1);source+='；英文 Details 明确 I must have one'
        # Item names such as Snake Fangs must not create a Snake monster source.
        # A paired kill objective is selected by the runtime from the retained
        # kill table. Pure collection sources are explicit "from X" clauses.
        source_clause=re.search(r'\bfrom\s+(.+?)(?:\.|;|\bthen\b|$)',obj,re.I)
        cn_source_targets=chinese_monster_mentions(cn_obj,aliases,monsters)
        source_targets=[(mid,name) for _,mid,name in cn_source_targets]
        if not source_targets and source_clause:source_targets=monster_mentions(source_clause[1],monsters)
        spots=kills.positions(dict(attrs,CreatureMapID=attrs.get('ItemMapID') or attrs.get('CreatureMapID',''),
            CreatureMapPos=attrs.get('ItemMapPos') if attrs.get('ItemMapID') else attrs.get('CreatureMapPos','')))
        source_maps=[int(m) for m in (attrs.get('ItemMapID') or attrs.get('CreatureMapID','')).split(',') if m]
        sources=[]
        for index,(mid,name) in enumerate(source_targets):
            if not spots:continue
            mp,x,z=spots[min(index,len(spots)-1)]
            sources.append(dict(monster=mid,name=name,map=mp,x=x,z=z))
        if qid in retained_kills:
            # Existing playable targets/counts have priority over locale changes.
            sources=[dict(monster=g[3],name=g[1] or g[0],map=g[4],x=g[5],z=g[6])
                for g in retained_kills[qid]]
        result.append(dict(id=qid,camp=camp,level=int(attrs['MinLevel']),item=value[0],count=value[1],
            source=source,monsterSources=sources,sourceMaps=source_maps,
            displayName=cn_item_label or next((name for name,ids in cn_items.items() if value[0] and value[0] in ids),'任务道具')))
    # An inferred quantity must never become another task's reference source.
    quantity_peers=[dict(r) for r in result if r['count'] and
        ('明确' in r['source'] or r['id'] in items.VERIFIED or r['count']>1)]
    for row in result:
        if row['count']:continue
        peer=min((r for r in quantity_peers if r['camp']==row['camp']),
            key=lambda r:(abs(r['level']-row['level']),r['id']))
        row['count']=peer['count'];row['source']+=f"；用户授权相近等级数量：任务 {peer['id']}"
    lines=['// Generated by tools/gen_quest_runtime_requirements.py.',
        'namespace Godswar.Server.Domain.World.Content;','',
        'internal static class StarterQuestCollectObjectives','{',
        '    internal readonly record struct CollectObjective(uint ItemId, int Required, string DisplayName = "任务道具");',
        '    public static IReadOnlyDictionary<uint, CollectObjective> ByQuestId { get; } =',
        '        new Dictionary<uint, CollectObjective>','        {']
    for r in sorted(result,key=lambda r:r['id']):
        lines.append(f"            [{r['id']}u] = new({r['item']}u, {r['count']}, {json.dumps(r['displayName'],ensure_ascii=False)}),")
    lines+=['        };','    public static bool TryGet(uint questId, out CollectObjective objective) =>',
        '        ByQuestId.TryGetValue(questId, out objective);','}','']
    (CONTENT/'StarterQuestCollectObjectives.cs').write_text('\n'.join(lines),encoding='utf-8')
    lines=['// Generated by tools/gen_quest_runtime_requirements.py.',
        'namespace Godswar.Server.Domain.World.Content;','',
        'internal static class QuestRuntimeRequirements','{',
        '    internal sealed record CollectionSource(QuestObjective[] Monsters, uint[] Maps);',
        '    internal static readonly IReadOnlyDictionary<uint, CollectionSource> Collections =',
        '        new Dictionary<uint, CollectionSource>','        {']
    def quoted(s):return json.dumps(s,ensure_ascii=False)
    for r in sorted(result,key=lambda r:r['id']):
        targets=', '.join(f"new({quoted(t['name'])}, {quoted(t['name'])}, 0, {t['monster']}u, {t['map']}u, {t['x']:g}f, {t['z']:g}f)" for t in r['monsterSources'])
        maps=', '.join(f'{m}u' for m in r['sourceMaps'])
        lines.append(f"            [{r['id']}u] = new([{targets}], [{maps}]),")
    lines+=['        };','    internal static readonly IReadOnlyDictionary<uint, QuestObjective[]> Captures =',
        '        new Dictionary<uint, QuestObjective[]>','        {']
    for r in sorted(captures,key=lambda r:r['id']):
        targets=', '.join(f"new({quoted(t['target'])}, {quoted(t['name'])}, {t['count']}, {t['monster']}u, {t['map']}u, {t['x']:g}f, {t['z']:g}f)" for t in r['targets'])
        lines.append(f"            [{r['id']}u] = [{targets}],")
    lines += ['        };', '    internal static readonly IReadOnlyDictionary<uint, uint> ScrollItems =',
        '        new Dictionary<uint, uint>', '        {']
    for tag in re.findall(r'<[^>]+>',(CLIENT/'zh_cn/Settings/Sys/ItemBaseAttribute.xml').read_text(encoding='utf-8-sig')):
        attrs=dict(re.findall(r'(\w+)\s*=\s*"([^"]*)"',tag))
        if attrs.get('Quest','').isdigit() and attrs.get('ID','').isdigit() and int(attrs['Quest']) in rows:
            lines.append(f"            [{attrs['ID']}u] = {attrs['Quest']}u,")
    lines+=['        };','}','']
    (CONTENT/'QuestRuntimeRequirements.Generated.cs').write_text('\n'.join(lines),encoding='utf-8')
    audit=json.loads((OUT/'all-quest-text-and-rules.json').read_text(encoding='utf-8'))
    lines=['// Generated from the enabled Chinese client.',
        'namespace Godswar.Server.Domain.World.Content;',
        'internal static class QuestProgressTexts','{',
        '    internal static readonly IReadOnlyDictionary<uint, string> Titles = new Dictionary<uint, string>','    {']
    for row in audit:
        title=kills.COLOR.sub('',row['chinese'].get('Title','')).strip()
        lines.append(f"        [{row['id']}u] = {quoted(title)},")
    lines+=['    };','}','']
    (CONTENT/'QuestProgressTexts.Generated.cs').write_text('\n'.join(lines),encoding='utf-8')
    (OUT/'collection-and-capture-rules.json').write_text(json.dumps(dict(collections=result,captures=captures),ensure_ascii=False,indent=2),encoding='utf-8')
    print(f'collections={len(result)} captures={len(captures)}')

if __name__=='__main__':
    sys.stdout.reconfigure(encoding='utf-8');main()
