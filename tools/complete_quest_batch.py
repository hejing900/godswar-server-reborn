"""Complete the 38 reviewed quest definitions from their explicit client goals.

Unbound native display IDs never prevent creating named/map goal counters.
Names here are verbatim goal labels; no monster spawn or numeric ID is invented.
"""
import json,re,sys
from pathlib import Path
import gen_quest_objectives as g
from quest_client_goal_sources import monster_names

ROOT=Path(__file__).resolve().parents[1]
OUT=ROOT/'artifacts/quest-runtime-content-20261007'
CONTENT=ROOT/'src/Godswar.Server/Domain/World/Content'
# Explicit target labels read from Chinese Objectives/Details/QuestText.
KILLS={410:['利爪鹰身人'],414:['邪恶花仙'],456:['烈火犬狼','波斯女猎手'],
       467:['烈火犬狼','波斯女猎手'],510:['阿卡迪亚女战士'],607:['底比斯的各种怪物'],
       656:['守林者'],1211:['独眼食人魔'],1212:['邪恶羊头杖'],1220:['阿克泰'],
       1456:['烈火犬狼','波斯女猎手'],1467:['烈火犬狼','波斯女猎手'],
       1508:['女巫'],1510:['黑暗女巫'],1607:['底比斯的各种怪物'],1656:['守林者']}
COLLECT={147:['亚马逊女战士'],149:['密特拉近卫兵'],151:['木人'],152:['木制傀儡'],
         323:['亡灵侍者'],325:['波斯祆教士'],362:['波斯白袍法师'],411:['山地龟'],
         475:['波斯法师','波斯强盗兵'],567:['雅典巡逻士兵'],599:['不死生物'],
         1147:['毒蝮'],1149:['密特拉近卫兵'],1151:['木人'],1162:['阿尔西诺埃巨鳄'],
         1323:['亡灵法师'],1325:['迦勒底神学士'],1362:['波斯白袍法师'],
         1411:['迷途的仙女'],1475:['波斯法师','波斯强盗兵'],1567:['斯巴达巡逻士兵'],1599:['不死生物']}

def main():
    rows={r['id']:r for r in json.loads((OUT/'all-quest-text-and-rules.json').read_text(encoding='utf-8'))}
    collects={r['id']:r for r in json.loads((OUT/'collection-and-capture-rules.json').read_text(encoding='utf-8'))['collections']}
    retained={int(q):goals for q,goals in json.loads((ROOT/'tools/data/quest-retained-kill-objectives.json').read_text(encoding='utf-8')).items()}
    aliases,models=monster_names(g);monsters=g.monster_ids()
    cn_native={}
    for line in g.read_text(Path(r'D:\Godswar Origin\Localization\zh_cn\Text\QuestMonster.dat')).splitlines():
        p=line.split('\t')
        if len(p)>1 and p[0].lstrip('\ufeff').isdigit():cn_native[int(p[0].lstrip('\ufeff'))]=p[1].strip()
    names_by_key={}
    for record in models:
        names_by_key.setdefault(record['section'],set()).add(record['chinese'])
    def variants(label,maps):
        names={label};keys=set();native_ids=set();source_peers=[]
        for record in models:
            if label in record['chinese']:
                names.update((record['chinese'],record['english']));keys.add(record['section']);native_ids.update(record['monsters'])
        for mid,name in cn_native.items():
            if label in name:
                names.update((name,monsters.get(mid,name)));native_ids.add(mid)
        # Reuse the already playable target binding when another quest names
        # exactly this Chinese species on the same map. This handles localized
        # model/name changes without guessing a nearby monster or replacing an ID.
        for peer,goals in retained.items():
            if len(goals)!=1 or peer not in rows or goals[0][4] not in maps:continue
            plain=g.COLOR.sub('',rows[peer]['chinese'].get('Objectives',''))
            plain=re.split(r'然后|回复|复命|并向',plain)[0]
            if label not in plain:continue
            goal=goals[0];names.add(goal[1] or goal[0]);source_peers.append(peer)
            if goal[3]:native_ids.add(goal[3])
        return sorted(names),sorted(keys),sorted(native_ids),sorted(source_peers)
    def peer(q,label):
        r=rows[q];lvl=int(r['xml']['MinLevel']);camp=r['xml']['Faction']
        # Map-wide goals use nearby-level ordinary kill quotas, not a boss's one.
        singular=label in ('守林者','阿克泰')
        counterpart=q-1000 if q>=1000 else q+1000
        candidates=[]
        target_maps={int(x) for x in r['xml']['CreatureMapID'].split(',') if x}
        for p,goals in retained.items():
            if p not in rows:continue
            if rows[p]['xml']['Faction']!=camp and p!=counterpart:continue
            for goal in goals:
                if goal[2]<=0 or (singular and goal[2]!=1) or (not singular and goal[2]<=1):continue
                candidates.append((0 if p==counterpart else 1,0 if goal[4] in target_maps else 1,
                    abs(int(rows[p]['xml']['MinLevel'])-lvl),p,goal[2]))
        _,_,_,p,count=min(candidates)
        return count,f'用户授权参考任务 {p}（最低等级{rows[p]["xml"]["MinLevel"]}）'
    result=[]
    for q in sorted(KILLS.keys()|COLLECT.keys()):
        r=rows[q];attrs=r['xml'];text=' '.join(g.COLOR.sub('',r['chinese'].get(f,'')) for f in ('Objectives','Details','QuestText'))
        labels=KILLS.get(q,COLLECT.get(q));targets=[]
        mp=sorted({int(x) for x in (attrs.get('ItemMapID') if q in COLLECT else attrs['CreatureMapID']).split(',') if x})
        if not mp:mp=sorted({int(x) for x in attrs['CreatureMapID'].split(',') if x})
        for label in labels:
            assert label in text,(q,label,'target must be explicitly present in Chinese client')
            names,keys,native_ids,binding_peers=variants(label,mp)
            if len(names)==1 and not keys:
                # Read the same quest's explicit parallel goal name, not a
                # guessed English translation or a nearby live monster.
                from gen_quest_runtime_requirements import monster_mentions
                hits=monster_mentions(r['english'].get('Objectives',''),monsters)
                if len(labels)==1 and len(hits)==1:
                    mid,en_name=hits[0];names.append(en_name);native_ids.append(mid)
            scope='map' if q in (607,1607) else 'named'
            if q in (599,1599):
                # The exact English category is "Undead Creature Swatches".
                # Use explicitly named Undead species from the Thebes catalog.
                scope='named'
                for model in models:
                    if model['file'].replace('\\','/').startswith('Monster/Thebes_All/') and model['english'].startswith('Undead '):
                        names.extend((model['english'],model['chinese']));keys.append(model['section'])
            count=None;source=None
            if q in KILLS:
                objective=g.COLOR.sub('',r['chinese']['Objectives'])
                explicit=re.search(r'各\s*(\d+)',objective) or re.search(r'(\d+)\s*只\s*'+re.escape(label),objective)
                if explicit:count=int(explicit[1]);source='中文 Objectives 明确数量'
                else:count,source=peer(q,label)
            targets.append(dict(label=label,names=sorted(set(names)),keys=sorted(set(keys)),
                scope=scope,count=count,quantitySource=source,bindingsFromExistingQuests=binding_peers,
                nativeId=native_ids[0] if len(native_ids)==1 and scope!='map' else 0))
        row=dict(id=q,title=g.COLOR.sub('',r['chinese']['Title']),kind='collect' if q in COLLECT else 'kill',
            maps=mp,targets=targets,minimumLevel=int(attrs['MinLevel']))
        if q in COLLECT:
            row.update(count=collects[q]['count'],quantitySource=collects[q]['source'],item=collects[q]['displayName'])
        result.append(row)
    assert len(result)==38
    def quote(s):return json.dumps(s,ensure_ascii=False)
    lines=['// Generated by tools/complete_quest_batch.py; explicit client goals only.',
           'namespace Godswar.Server.Domain.World.Content;','internal static class QuestReviewedBatch','{',
           '    internal sealed record Target(string Label, string[] Names, string[] Templates, bool AnyMonster, int Required, uint NativeId);',
           '    internal sealed record Rule(bool Collection, bool PreserveKillQuota, uint[] Maps, Target[] Targets);',
           '    internal static readonly IReadOnlyDictionary<uint, Rule> ByQuestId = new Dictionary<uint, Rule>','    {']
    for row in result:
        targets=[]
        for t in row['targets']:
            targets.append('new('+quote(t['label'])+', ['+', '.join(quote(n) for n in t['names'])+'], ['+
                ', '.join(quote(k) for k in t['keys'])+'], '+str(t['scope']=='map').lower()+', '+str(t['count'] or 0)+', '+str(t['nativeId'])+'u)')
        lines.append(f"        [{row['id']}u] = new({str(row['kind']=='collect').lower()}, {str(row['id'] in retained).lower()}, [{', '.join(str(m)+'u' for m in row['maps'])}], [{', '.join(targets)}]),")
    lines+=['    };','    internal static readonly IReadOnlyDictionary<string, string[]> ChineseNamesByTemplate = new Dictionary<string, string[]>','    {']
    for key,names in sorted(names_by_key.items()):
        lines.append('        ['+quote(key)+'] = ['+', '.join(quote(n) for n in sorted(names) if n)+'],')
    lines+=['    };','}','']
    (CONTENT/'QuestReviewedBatch.Generated.cs').write_text('\n'.join(lines),encoding='utf-8')
    (OUT/'reviewed-38-quest-rules.json').write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
    report=['# 原38条待处理任务规则','', '| ID | 名称 | 完成要求 | 数量来源 |','|---|---|---|---|']
    for row in result:
        description=('15%获得 '+row['item']+'，收集'+str(row['count']) if row['kind']=='collect' else
            '；'.join(t['label']+'：'+str(t['count']) for t in row['targets']))
        source=row.get('quantitySource') or '；'.join(t['quantitySource'] for t in row['targets'])
        report.append(f"| {row['id']} | {row['title']} | {description} | {source} |")
    (OUT/'reviewed-38-quest-rules.md').write_text('\n'.join(report)+'\n',encoding='utf-8')
    print('Generated all 38 reviewed quest rules; raw labels and quantity references recorded.')

if __name__=='__main__':main()
