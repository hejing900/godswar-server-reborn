ALTER TABLE character_equip ADD COLUMN IF NOT EXISTS holy_suit_type smallint NOT NULL DEFAULT 0;
ALTER TABLE character_equip ADD COLUMN IF NOT EXISTS holy_suit_level smallint NOT NULL DEFAULT 0;
ALTER TABLE character_equip ADD COLUMN IF NOT EXISTS holy_suit_code integer NOT NULL DEFAULT 0;

CREATE TABLE IF NOT EXISTS holy_suit_tiers (
    type smallint PRIMARY KEY,
    name varchar(32) NOT NULL,
    max_level smallint NOT NULL DEFAULT 10,
    material_item_id integer,
    material_name varchar(64),
    source varchar(128) NOT NULL
);

INSERT INTO holy_suit_tiers (type, name, max_level, material_item_id, material_name, source)
VALUES
    (0, 'Common', 0, NULL, NULL, 'EquipSuitInfoIni.xml'),
    (1, 'Bronze', 10, 9010, 'Bronze Ware', 'EquipSuitInfoIni.xml + EquipName.dat'),
    (2, 'Silver', 10, 9011, 'Silver Ware', 'EquipSuitInfoIni.xml + EquipName.dat'),
    (3, 'Gold', 10, 9012, 'Gold Ware', 'EquipSuitInfoIni.xml + EquipName.dat'),
    (4, 'Platinum', 10, 9013, 'Platinum Ingot', 'EquipSuitInfoIni.xml + EquipName.dat'),
    (5, 'Mithril', 10, 9014, 'Mithril Ingot', 'EquipSuitInfoIni.xml + EquipName.dat'),
    (6, 'Orichalcum', 10, 9015, 'Orichalcum Ingot', 'EquipSuitInfoIni.xml + EquipName.dat'),
    (7, 'Adamantium', 10, 9016, 'Adamantite', 'EquipSuitInfoIni.xml + EquipName.dat')
ON CONFLICT (type) DO UPDATE
SET name = EXCLUDED.name,
    max_level = EXCLUDED.max_level,
    material_item_id = EXCLUDED.material_item_id,
    material_name = EXCLUDED.material_name,
    source = EXCLUDED.source;

CREATE TABLE IF NOT EXISTS holy_suit_upgrade_requirements (
    suit_type smallint NOT NULL REFERENCES holy_suit_tiers(type),
    from_level smallint NOT NULL,
    to_suit_type smallint NOT NULL REFERENCES holy_suit_tiers(type),
    to_level smallint NOT NULL,
    required_exp bigint NOT NULL DEFAULT 0,
    required_prisms integer NOT NULL DEFAULT 0,
    source varchar(128) NOT NULL,
    PRIMARY KEY (suit_type, from_level)
);

INSERT INTO holy_suit_upgrade_requirements (
    suit_type, from_level, to_suit_type, to_level, required_exp, required_prisms, source
)
VALUES
    (1, 0, 1, 1, 9688, 0, 'HelpSystemConfig.lua'),
    (1, 1, 1, 2, 58127, 0, 'HelpSystemConfig.lua'),
    (1, 2, 1, 3, 174380, 0, 'HelpSystemConfig.lua'),
    (1, 3, 1, 4, 348759, 0, 'HelpSystemConfig.lua'),
    (1, 4, 1, 5, 581265, 0, 'HelpSystemConfig.lua'),
    (1, 5, 1, 6, 4198026, 0, 'HelpSystemConfig.lua'),
    (1, 6, 1, 7, 4843876, 0, 'HelpSystemConfig.lua'),
    (1, 7, 1, 8, 5489727, 0, 'HelpSystemConfig.lua'),
    (1, 8, 1, 9, 6458502, 0, 'HelpSystemConfig.lua'),
    (1, 9, 1, 10, 7427277, 0, 'HelpSystemConfig.lua'),
    (1, 10, 2, 1, 3875101, 0, 'HelpSystemConfig.lua'),
    (2, 1, 2, 2, 58127, 0, 'HelpSystemConfig.lua'),
    (2, 2, 2, 3, 9416496, 0, 'HelpSystemConfig.lua'),
    (2, 3, 2, 4, 14647883, 0, 'HelpSystemConfig.lua'),
    (2, 4, 2, 5, 18832991, 0, 'HelpSystemConfig.lua'),
    (2, 5, 2, 6, 23018100, 0, 'HelpSystemConfig.lua'),
    (2, 6, 2, 7, 27278774, 0, 'HelpSystemConfig.lua'),
    (2, 7, 2, 8, 31475509, 0, 'HelpSystemConfig.lua'),
    (2, 8, 2, 9, 35672243, 0, 'HelpSystemConfig.lua'),
    (2, 9, 2, 10, 41967345, 0, 'HelpSystemConfig.lua'),
    (2, 10, 3, 1, 57661505, 0, 'HelpSystemConfig.lua'),
    (3, 1, 3, 2, 61505605, 0, 'HelpSystemConfig.lua'),
    (3, 2, 3, 3, 63497705, 0, 'HelpSystemConfig.lua'),
    (3, 3, 3, 4, 69193805, 0, 'HelpSystemConfig.lua'),
    (3, 4, 3, 5, 73037906, 0, 'HelpSystemConfig.lua'),
    (3, 5, 3, 6, 76882006, 0, 'HelpSystemConfig.lua'),
    (3, 6, 3, 7, 80726106, 0, 'HelpSystemConfig.lua'),
    (3, 7, 3, 8, 88414306, 0, 'HelpSystemConfig.lua'),
    (3, 8, 3, 9, 96102508, 0, 'HelpSystemConfig.lua'),
    (3, 9, 3, 10, 100000000, 0, 'HelpSystemConfig.lua'),
    (3, 10, 4, 1, 133833876, 0, 'HelpSystemConfig.lua'),
    (4, 1, 4, 2, 155886267, 0, 'HelpSystemConfig.lua'),
    (4, 2, 4, 3, 184272577, 0, 'HelpSystemConfig.lua'),
    (4, 3, 4, 4, 234454153, 0, 'HelpSystemConfig.lua'),
    (4, 4, 4, 5, 295435479, 0, 'HelpSystemConfig.lua'),
    (4, 5, 4, 6, 373355467, 0, 'HelpSystemConfig.lua'),
    (4, 6, 4, 7, 485358297, 0, 'HelpSystemConfig.lua'),
    (4, 7, 4, 8, 616532565, 0, 'HelpSystemConfig.lua'),
    (4, 8, 4, 9, 735697878, 0, 'HelpSystemConfig.lua'),
    (4, 9, 4, 10, 866697995, 0, 'HelpSystemConfig.lua'),
    (4, 10, 5, 1, 999999999, 0, 'HelpSystemConfig.lua'),
    (5, 1, 5, 2, 0, 12, 'HelpSystemConfig.lua'),
    (5, 2, 5, 3, 0, 15, 'HelpSystemConfig.lua'),
    (5, 3, 5, 4, 0, 18, 'HelpSystemConfig.lua'),
    (5, 4, 5, 5, 0, 21, 'HelpSystemConfig.lua'),
    (5, 5, 5, 6, 0, 24, 'HelpSystemConfig.lua'),
    (5, 6, 5, 7, 0, 27, 'HelpSystemConfig.lua'),
    (5, 7, 5, 8, 0, 30, 'HelpSystemConfig.lua'),
    (5, 8, 5, 9, 0, 33, 'HelpSystemConfig.lua'),
    (5, 9, 5, 10, 0, 36, 'HelpSystemConfig.lua'),
    (5, 10, 6, 1, 0, 39, 'HelpSystemConfig.lua'),
    (6, 1, 6, 2, 0, 42, 'HelpSystemConfig.lua'),
    (6, 2, 6, 3, 0, 45, 'HelpSystemConfig.lua'),
    (6, 3, 6, 4, 0, 48, 'HelpSystemConfig.lua'),
    (6, 4, 6, 5, 0, 51, 'HelpSystemConfig.lua'),
    (6, 5, 6, 6, 0, 54, 'HelpSystemConfig.lua'),
    (6, 6, 6, 7, 0, 57, 'HelpSystemConfig.lua'),
    (6, 7, 6, 8, 0, 60, 'HelpSystemConfig.lua'),
    (6, 8, 6, 9, 0, 63, 'HelpSystemConfig.lua'),
    (6, 9, 6, 10, 0, 66, 'HelpSystemConfig.lua'),
    (6, 10, 7, 1, 0, 69, 'HelpSystemConfig.lua'),
    (7, 1, 7, 2, 0, 72, 'HelpSystemConfig.lua'),
    (7, 2, 7, 3, 0, 75, 'HelpSystemConfig.lua'),
    (7, 3, 7, 4, 0, 78, 'HelpSystemConfig.lua'),
    (7, 4, 7, 5, 0, 81, 'HelpSystemConfig.lua'),
    (7, 5, 7, 6, 0, 84, 'HelpSystemConfig.lua'),
    (7, 6, 7, 7, 0, 87, 'HelpSystemConfig.lua'),
    (7, 7, 7, 8, 0, 90, 'HelpSystemConfig.lua'),
    (7, 8, 7, 9, 0, 93, 'HelpSystemConfig.lua'),
    (7, 9, 7, 10, 0, 96, 'HelpSystemConfig.lua')
ON CONFLICT (suit_type, from_level) DO UPDATE
SET to_suit_type = EXCLUDED.to_suit_type,
    to_level = EXCLUDED.to_level,
    required_exp = EXCLUDED.required_exp,
    required_prisms = EXCLUDED.required_prisms,
    source = EXCLUDED.source;

CREATE TABLE IF NOT EXISTS holy_suit_effect_templates (
    effect_key varchar(32) PRIMARY KEY,
    stat_type smallint NOT NULL,
    unlock_points smallint NOT NULL,
    effect_value numeric NOT NULL,
    per_point numeric,
    maximum integer,
    source varchar(128) NOT NULL
);

INSERT INTO holy_suit_effect_templates (effect_key, stat_type, unlock_points, effect_value, source)
VALUES
    ('MaxHPD', 1, 5, 13328.5714285716, 'EquipEffectSuit.xml'),
    ('MaxMPD', 2, 10, 670.2857143, 'EquipEffectSuit.xml'),
    ('Defence', 5, 20, 1059.428571, 'EquipEffectSuit.xml'),
    ('MagicRec', 6, 40, 800, 'EquipEffectSuit.xml'),
    ('Hit', 7, 60, 200, 'EquipEffectSuit.xml'),
    ('Miss', 8, 80, 160, 'EquipEffectSuit.xml'),
    ('InjureImbibe', 9, 100, 670.2857143, 'EquipEffectSuit.xml'),
    ('Attack', 3, 120, 1330.285714, 'EquipEffectSuit.xml'),
    ('MagicAk', 4, 120, 1200, 'EquipEffectSuit.xml')
ON CONFLICT (effect_key) DO UPDATE
SET stat_type = EXCLUDED.stat_type,
    unlock_points = EXCLUDED.unlock_points,
    effect_value = EXCLUDED.effect_value,
    source = EXCLUDED.source;

WITH entries AS (
    SELECT
        cb.id AS user_id,
        item.ordinality - 1 AS body_part_id,
        string_to_array(trim(both '[]' FROM item.entry), ',') AS item_parts
    FROM character_base cb
    JOIN character_kitbag ck ON ck.user_id = cb.id
    CROSS JOIN LATERAL regexp_split_to_table(COALESCE(ck.equip, ''), '#') WITH ORDINALITY AS item(entry, ordinality)
    WHERE item.ordinality BETWEEN 1 AND 13
      AND item.entry <> ''
      AND item.entry <> '[]'
),
parsed AS (
    SELECT
        user_id,
        body_part_id,
        COALESCE(NULLIF(item_parts[12], '')::integer, 0) AS holy_suit_code
    FROM entries
    WHERE NULLIF(item_parts[1], '') IS NOT NULL
)
UPDATE character_equip ce
SET holy_suit_code = parsed.holy_suit_code,
    holy_suit_type = CASE WHEN parsed.holy_suit_code <= 0 THEN 0 ELSE LEAST(GREATEST(parsed.holy_suit_code / 100, 0), 7) END,
    holy_suit_level = CASE WHEN parsed.holy_suit_code <= 0 THEN 0 ELSE LEAST(GREATEST(parsed.holy_suit_code % 100, 0), 10) END
FROM parsed
WHERE ce.user_id = parsed.user_id
  AND ce.body_part_id = parsed.body_part_id;

CREATE OR REPLACE VIEW character_equipment_scores AS
WITH score_parts AS (
    SELECT
        ce.user_id,
        ce.body_part_id,
        ce.prop_id,
        ce.item_quality,
        ce.item_grade,
        ce.bound,
        ce.stack,
        ce.item_exp,
        ce.holy_suit_type,
        ce.holy_suit_level,
        ce.holy_suit_code,
        it.kind,
        it.display_name,
        it.equipment_slot,
        CASE
            WHEN base_values.values IS NULL THEN 0
            ELSE COALESCE(NULLIF(base_values.values[
                LEAST(GREATEST(ce.item_quality::integer, 1), array_length(base_values.values, 1))
            ], '')::integer, 0)
        END AS base_score,
        CASE
            WHEN app_values.values IS NULL THEN 0
            ELSE COALESCE(NULLIF(app_values.values[
                LEAST(GREATEST(ce.item_grade::integer, 1), array_length(app_values.values, 1))
            ], '')::integer, 0)
        END AS grade_score
    FROM character_equip ce
    LEFT JOIN item_templates it ON it.id = ce.prop_id
    LEFT JOIN LATERAL (
        SELECT string_to_array(it.stats->>'BaseFraction', ',') AS values
        WHERE it.stats ? 'BaseFraction'
    ) base_values ON true
    LEFT JOIN LATERAL (
        SELECT string_to_array(it.stats->>'AppFraction', ',') AS values
        WHERE it.stats ? 'AppFraction'
    ) app_values ON true
)
SELECT
    user_id,
    body_part_id,
    prop_id,
    kind,
    display_name,
    equipment_slot,
    item_quality,
    item_grade,
    bound,
    stack,
    item_exp,
    base_score,
    grade_score,
    base_score + grade_score AS item_score,
    holy_suit_type,
    holy_suit_level,
    holy_suit_code
FROM score_parts;

CREATE OR REPLACE VIEW character_holy_suit_equipment AS
SELECT
    ce.user_id,
    cb.name AS character_name,
    ce.body_part_id,
    ce.prop_id,
    it.kind,
    it.display_name,
    ce.item_exp,
    ce.holy_suit_code,
    ce.holy_suit_type,
    ce.holy_suit_level,
    tier.name AS holy_suit_name,
    req.to_suit_type,
    next_tier.name AS next_holy_suit_name,
    req.to_level AS next_holy_suit_level,
    req.required_exp,
    req.required_prisms,
    CASE
        WHEN req.required_exp > 0 THEN ROUND((ce.item_exp::numeric / req.required_exp::numeric) * 100, 2)
        ELSE NULL
    END AS exp_progress_percent
FROM character_equip ce
JOIN character_base cb ON cb.id = ce.user_id
LEFT JOIN item_templates it ON it.id = ce.prop_id
LEFT JOIN holy_suit_tiers tier ON tier.type = ce.holy_suit_type
LEFT JOIN holy_suit_upgrade_requirements req
    ON req.suit_type = ce.holy_suit_type
   AND req.from_level = ce.holy_suit_level
LEFT JOIN holy_suit_tiers next_tier ON next_tier.type = req.to_suit_type;
