"""Resolve explicitly named goals through matching client model records.

Name aliases come from the same INI file and section in both locales, with
matching model/texture records. This includes Monster_New.ini; no spawn or
nearby-monster guess selects a target.
"""
import collections
import re
from pathlib import Path

CLIENT = Path(r'D:\Godswar Origin\Localization')


def monster_names(generator):
    table=generator.monster_ids()
    by_english=collections.defaultdict(set)
    for mid,name in table.items():by_english[name.strip().casefold()].add(mid)
    aliases=collections.defaultdict(set)
    for line in generator.read_text(CLIENT/'zh_cn/Text/QuestMonster.dat').splitlines():
        parts=line.split('\t')
        if len(parts)>1 and parts[0].lstrip('\ufeff').isdigit():
            aliases[parts[1].strip()].add(int(parts[0].lstrip('\ufeff')))
    provenance=[]
    def records(path):
        records={}
        normalized='\n'.join(generator.read_text(path).splitlines())
        for section,body in re.findall(r'^\[([^\]\n]+)\]\s*\n(.*?)(?=^\[|\Z)',normalized,re.M|re.S):
            attrs=dict(re.findall(r'^\s*(\w+)\s*=\s*([^\r\n]*)',body,re.M))
            if 'Name' in attrs:
                records[section]=attrs
        return records
    for cn_path in sorted((CLIENT/'zh_cn/Monster').rglob('Monster*.ini')):
        relative=cn_path.relative_to(CLIENT/'zh_cn')
        en_path=CLIENT/'en_us'/relative
        if not en_path.exists():continue
        cn=records(cn_path);en=records(en_path)
        for section,attrs in cn.items():
            other=en.get(section)
            if not other or any(attrs.get(key)!=other.get(key) for key in ('FileName','TextureName')):continue
            ids=by_english.get(other['Name'].strip().casefold(),set())
            aliases[attrs['Name'].strip()].update(ids)
            provenance.append(dict(file=str(relative),section=section,chinese=attrs['Name'].strip(),
                english=other['Name'].strip(),monsters=sorted(ids)))
    return {name:sorted(ids) for name,ids in aliases.items()},provenance


def chinese_monster_mentions(text, aliases, monsters):
    candidates=[]
    for name,ids in aliases.items():
        if name and name in text and len(ids)==1 and ids[0] in monsters:
            candidates.append((name,ids[0],monsters[ids[0]]))
    candidates=[c for c in candidates if not any(c[0]!=other[0] and c[0] in other[0] for other in candidates)]
    candidates.sort(key=lambda c:text.index(c[0]))
    unique={}
    for entry in candidates:unique.setdefault(entry[1],entry)
    return list(unique.values())


def explicit_monster_quantity(row,label,english_name,generator):
    """Return a quantity only when it is attached to this named target."""
    from gen_quest_runtime_requirements import normalized_monster_words
    escaped=re.escape(label)
    patterns=[r'(\d+|一|两|二)\s*(?:只|个|名|对|份)?\s*(?:(?:位于|在)[^，。；]{1,30}的)?\s*'+escaped,
              escaped+r'\s*(\d+)\s*(?:只|个|名)']
    for field in ('Objectives','Details','QuestText','IncompleteText'):
        plain=generator.COLOR.sub('',row['chinese'].get(field,''))
        for pattern in patterns:
            match=re.search(pattern,plain)
            if match:
                count={'一':1,'两':2,'二':2}.get(match[1]) or int(match[1])
                return count,'中文 '+field+' 明确击杀数量'
        # "each N" belongs to the goal list, never arbitrary story prose.
        if field=='Objectives':
            match=re.search(r'各\s*(\d+)',plain)
            if match:return int(match[1]),'中文 Objectives 各目标明确数量'
    plain=generator.COLOR.sub('',row['english'].get('Objectives',''))
    plain=re.sub(r'(?<=\d),(?=\d{3}(?:\D|$))','',plain)
    # A number before an item such as "10 Snake Tails" is not a Snake kill
    # quota. The legacy parser is used only to verify a kill clause exists;
    # its absent-number defaults are never accepted as quantity evidence.
    parsed=generator.parse_objectives(plain,generator.monster_ids())
    from gen_quest_runtime_requirements import monster_mentions
    if not any(monster_mentions(target,{1:english_name}) for target,_ in parsed):return None,None
    match=re.search(r'\b(\d+)\s+(?:(?:of|the|a|an)\s+)*'+
        re.escape(normalized_monster_words(english_name))+r'\b',normalized_monster_words(plain))
    return (int(match[1]),'英文 Objectives 明确击杀数量') if match else (None,None)
