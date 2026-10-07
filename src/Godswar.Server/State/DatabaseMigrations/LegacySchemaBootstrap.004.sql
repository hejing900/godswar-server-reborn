CREATE TABLE IF NOT EXISTS holy_suit_effect_templates (
    effect_key varchar(32) PRIMARY KEY,
    stat_type smallint NOT NULL,
    unlock_points smallint NOT NULL,
    effect_value numeric NOT NULL,
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

CREATE TABLE IF NOT EXISTS character_skills (
    user_id integer NOT NULL REFERENCES character_base(id) ON DELETE CASCADE,
    skill_id integer NOT NULL REFERENCES skill_templates(skill_id),
    skill_level smallint NOT NULL DEFAULT 1,
    acquired_at timestamptz NOT NULL DEFAULT now(),
    source varchar(64) NOT NULL DEFAULT 'manual',
    PRIMARY KEY (user_id, skill_id)
);

CREATE INDEX IF NOT EXISTS ix_character_skills_skill_id ON character_skills (skill_id);

CREATE TABLE IF NOT EXISTS character_talents (
    user_id integer NOT NULL REFERENCES character_base(id) ON DELETE CASCADE,
    talent_id integer NOT NULL REFERENCES talent_templates(id),
    rank smallint NOT NULL DEFAULT 0,
    updated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (user_id, talent_id)
);

CREATE INDEX IF NOT EXISTS ix_character_talents_talent_id ON character_talents (talent_id);

SELECT setval(pg_get_serial_sequence('server', 'id'), GREATEST((SELECT COALESCE(MAX(id), 1) FROM server), 1), true);
SELECT setval(pg_get_serial_sequence('character_base', 'id'), GREATEST((SELECT COALESCE(MAX(id), 1) FROM character_base), 1), true);

CREATE OR REPLACE FUNCTION talent_effective_rank(rank_value integer)
RETURNS numeric
LANGUAGE sql
IMMUTABLE
AS $$
    SELECT CASE
        WHEN GREATEST(COALESCE(rank_value, 0), 0) <= 40
            THEN GREATEST(COALESCE(rank_value, 0), 0)
        WHEN GREATEST(COALESCE(rank_value, 0), 0) <= 60
            THEN 40 + ((GREATEST(COALESCE(rank_value, 0), 0) - 40) * 2)
        WHEN GREATEST(COALESCE(rank_value, 0), 0) <= 80
            THEN 80 + ((GREATEST(COALESCE(rank_value, 0), 0) - 60) * 3)
        WHEN GREATEST(COALESCE(rank_value, 0), 0) <= 90
            THEN 140 + ((GREATEST(COALESCE(rank_value, 0), 0) - 80) * 5)
        ELSE 190 + ((LEAST(GREATEST(COALESCE(rank_value, 0), 0), 100) - 90) * 7)
    END::numeric;
$$;

CREATE OR REPLACE VIEW class_talents AS
SELECT
    ct.id AS class_id,
    ct.display_name AS class_name,
    tt.tree_order,
    tt.id AS talent_id,
    tt.name,
    tt.prefix_id,
    tt.required_prefix_rank,
    tt.required_total_rank,
    tt.equip_request,
    tt.effect_type,
    tet.display_name AS effect_name,
    tt.effect_value,
    tt.is_percent
FROM talent_templates tt
JOIN class_templates ct ON ct.id = tt.class_id
JOIN talent_effect_templates tet ON tet.id = tt.effect_id;

CREATE OR REPLACE VIEW class_skills AS
SELECT
    ct.id AS class_id,
    ct.display_name AS class_name,
    st.skill_id,
    st.display_name,
    st.base_name,
    st.skill_level,
    st.previous_skill_id,
    st.min_level,
    st.max_level,
    st.description
FROM skill_templates st
CROSS JOIN LATERAL unnest(st.class_ids) AS skill_class(class_id)
JOIN class_templates ct ON ct.id = skill_class.class_id;

CREATE OR REPLACE VIEW class_skill_books AS
SELECT
    ct.id AS class_id,
    ct.display_name AS class_name,
    sbt.item_id,
    sbt.name_key,
    sbt.display_name,
    sbt.skill_id,
    sbt.base_name,
    sbt.skill_level,
    sbt.min_level,
    sbt.max_level,
    sbt.previous_skill_id
FROM skill_book_templates sbt
CROSS JOIN LATERAL unnest(sbt.class_ids) AS book_class(class_id)
JOIN class_templates ct ON ct.id = book_class.class_id;

CREATE OR REPLACE VIEW character_available_talents AS
SELECT
    cb.id AS user_id,
    cb.name AS character_name,
    ct.display_name AS class_name,
    tt.tree_order,
    tt.id AS talent_id,
    tt.name,
    COALESCE(chtt.rank, 0)::smallint AS current_rank,
    tt.required_prefix_rank,
    tt.required_total_rank,
    tt.effect_type,
    tt.effect_value,
    tt.is_percent
FROM character_base cb
JOIN class_templates ct ON ct.id = cb.profession
JOIN talent_templates tt ON tt.class_id = cb.profession
LEFT JOIN character_talents chtt ON chtt.user_id = cb.id AND chtt.talent_id = tt.id;

DROP VIEW IF EXISTS character_talent_stat_summary;
CREATE OR REPLACE VIEW character_talent_stat_summary AS
SELECT
    cb.id AS user_id,
    cb.name AS character_name,
    cb.profession,
    tt.id AS talent_id,
    tt.name AS talent_name,
    tet.key AS stat_key,
    tet.display_name AS stat_name,
    ct.rank,
    talent_effective_rank(ct.rank)::integer AS effective_rank,
    tt.effect_value,
    tt.is_percent,
    ROUND(talent_effective_rank(ct.rank) * CASE WHEN tt.is_percent THEN tt.effect_value * 10000 ELSE tt.effect_value END)::integer AS contribution
FROM character_talents ct
JOIN character_base cb ON cb.id = ct.user_id
JOIN talent_templates tt ON tt.id = ct.talent_id AND tt.class_id = cb.profession
JOIN talent_effect_templates tet ON tet.id = tt.effect_id
WHERE ct.rank > 0;

CREATE OR REPLACE VIEW character_available_skills AS
SELECT
    cb.id AS user_id,
    cb.name AS character_name,
    ct.display_name AS class_name,
    st.skill_id,
    st.display_name,
    st.base_name,
    st.skill_level,
    st.previous_skill_id,
    st.min_level,
    cb.fighter_job_lv AS character_level,
    (COALESCE(st.min_level, 1) <= cb.fighter_job_lv) AS level_unlocked,
    (chs.skill_id IS NOT NULL) AS learned,
    chs.source AS learned_source
FROM character_base cb
JOIN class_templates ct ON ct.id = cb.profession
JOIN skill_templates st ON cb.profession = ANY(st.class_ids)
LEFT JOIN character_skills chs ON chs.user_id = cb.id AND chs.skill_id = st.skill_id;

CREATE OR REPLACE VIEW npc_template_summary AS
SELECT
    nt.npc_key,
    nt.scene_key,
    nt.display_name,
    nt.description,
    COUNT(DISTINCT na.template_key) AS appearance_count,
    COUNT(DISTINCT ns.quest_id) AS quest_reference_count
FROM npc_text_templates nt
LEFT JOIN npc_appearance_templates na ON na.npc_key = nt.npc_key
LEFT JOIN npc_spawn_references ns ON ns.npc_key = nt.npc_key
GROUP BY nt.npc_key, nt.scene_key, nt.display_name, nt.description;

CREATE OR REPLACE VIEW npc_guide_templates AS
SELECT *
FROM npc_template_summary
WHERE display_name ILIKE '%guide%'
   OR npc_key IN ('Athens_094', 'Sparta_094');

CREATE OR REPLACE VIEW map_template_summary AS
SELECT
    mt.map_id,
    mt.scene_key,
    mt.display_name,
    mt.client_scene_id,
    mt.map_mode,
    mt.music_name,
    mt.event_scene_key,
    COUNT(DISTINCT msa.area_index) AS safe_area_count,
    COUNT(DISTINCT map.group_index || ':' || map.point_index) AS address_point_count,
    COUNT(DISTINCT ml.link_index || ':' || ml.target_map_id) AS link_count
FROM map_templates mt
LEFT JOIN map_safe_areas msa ON msa.map_id = mt.map_id
LEFT JOIN map_address_points map ON map.map_id = mt.map_id
LEFT JOIN map_links ml ON ml.map_id = mt.map_id
GROUP BY mt.map_id, mt.scene_key, mt.display_name, mt.client_scene_id, mt.map_mode, mt.music_name, mt.event_scene_key;

CREATE OR REPLACE VIEW monster_template_summary AS
SELECT
    COALESCE(mt.source_map_id, -1)::smallint AS map_id,
    mt.scene_key,
    COUNT(*) AS monster_count,
    COUNT(*) FILTER (WHERE mt.is_boss) AS boss_count,
    COUNT(*) FILTER (WHERE mt.is_elite) AS elite_count,
    COUNT(*) FILTER (WHERE mt.is_pet) AS pet_count
FROM monster_templates mt
GROUP BY COALESCE(mt.source_map_id, -1), mt.scene_key;

CREATE OR REPLACE VIEW boss_templates AS
SELECT *
FROM monster_templates
WHERE is_boss
ORDER BY source_map_id NULLS FIRST, display_name, template_key;

CREATE OR REPLACE VIEW item_allowed_attributes AS
SELECT
    it.id AS item_id,
    it.kind,
    it.display_name AS item_name,
    attrs.position,
    attrs.attribute_id,
    iat.name_key,
    iat.stat_type,
    iat.percent,
    iat.max_level,
    iat.level_values
FROM item_templates it
CROSS JOIN LATERAL (
    SELECT
        attr.ordinality::smallint AS position,
        attr.value::integer AS attribute_id
    FROM regexp_split_to_table(COALESCE(it.stats->>'MainAttribute', ''), ',') WITH ORDINALITY AS attr(value, ordinality)
    WHERE NULLIF(attr.value, '') IS NOT NULL
) attrs
LEFT JOIN item_attribute_templates iat ON iat.id = attrs.attribute_id;

CREATE OR REPLACE VIEW character_equipment_attributes AS
SELECT
    ce.user_id,
    cb.name AS character_name,
    ce.body_part_id,
    ce.prop_id,
    it.kind,
    it.display_name AS item_name,
    attr.attribute_slot,
    attr.attribute_id,
    iat.name_key,
    iat.stat_type,
    iat.percent,
    COALESCE(attr.attribute_level, ce.item_grade) AS attribute_level,
    COALESCE(
        attr.attribute_value,
        CASE
            WHEN iat.level_values IS NULL THEN NULL
            ELSE iat.level_values[LEAST(GREATEST(COALESCE(attr.attribute_level, ce.item_grade)::integer, 1), array_length(iat.level_values, 1))]
        END
    ) AS attribute_value,
    EXISTS (
        SELECT 1
        FROM item_allowed_attributes iaa
        WHERE iaa.item_id = ce.prop_id
          AND iaa.attribute_id = attr.attribute_id
    ) AS is_allowed_for_item
FROM character_equip ce
JOIN character_base cb ON cb.id = ce.user_id
LEFT JOIN item_templates it ON it.id = ce.prop_id
CROSS JOIN LATERAL (
    VALUES
        (1::smallint, ce.type1, ce.quality1, ce.value1::numeric),
        (2::smallint, ce.type2, ce.quality2, ce.value2::numeric),
        (3::smallint, ce.type3, ce.quality3, ce.value3::numeric),
        (4::smallint, ce.type4, ce.quality4, ce.value4::numeric),
        (5::smallint, ce.type5, ce.quality5, ce.value5::numeric)
) attr(attribute_slot, attribute_id, attribute_level, attribute_value)
LEFT JOIN item_attribute_templates iat ON iat.id = attr.attribute_id
WHERE attr.attribute_id IS NOT NULL
  AND attr.attribute_id >= 0;

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
        (
            (ce.type1 IS NOT NULL AND ce.type1 >= 0)::integer +
            (ce.type2 IS NOT NULL AND ce.type2 >= 0)::integer +
            (ce.type3 IS NOT NULL AND ce.type3 >= 0)::integer +
            (ce.type4 IS NOT NULL AND ce.type4 >= 0)::integer +
            (ce.type5 IS NOT NULL AND ce.type5 >= 0)::integer
        ) AS append_attribute_count,
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
    grade_score * append_attribute_count AS grade_score,
    base_score + (grade_score * append_attribute_count) AS item_score,
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

CREATE OR REPLACE VIEW character_rank_summary AS
WITH totals AS (
    SELECT
        user_id,
        COALESCE(SUM(item_score) FILTER (WHERE kind = 'weapon'), 0)::integer AS weapon_score,
        COALESCE(SUM(item_score) FILTER (
            WHERE kind <> 'weapon'
              AND kind NOT IN (
                  'mount',
                  'mounthead',
                  'mountarmor',
                  'mountsoul',
                  'mountornament',
                  'mountamulet'
              )
        ), 0)::integer AS armor_score
    FROM character_equipment_scores
    GROUP BY user_id
)
SELECT
    cb.id AS user_id,
    cb.name,
    COALESCE(t.weapon_score, 0) AS weapon_score,
    COALESCE(wr.rank_level, 0)::smallint AS weapon_rank,
    COALESCE(wr.aura_effect, 0) AS weapon_aura_effect,
    COALESCE(t.armor_score, 0) AS armor_score,
    COALESCE(ar.rank_level, 0)::smallint AS armor_rank,
    COALESCE(ar.aura_effect, 0) AS armor_aura_effect
FROM character_base cb
LEFT JOIN totals t ON t.user_id = cb.id
LEFT JOIN LATERAL (
    SELECT rank_level, aura_effect
    FROM equipment_rank_rules
    WHERE rank_kind = 'weapon'
      AND required_score <= COALESCE(t.weapon_score, 0)
    ORDER BY rank_level DESC
    LIMIT 1
) wr ON true
LEFT JOIN LATERAL (
    SELECT rank_level, aura_effect
    FROM equipment_rank_rules
    WHERE rank_kind = 'armor'
      AND required_score <= COALESCE(t.armor_score, 0)
    ORDER BY rank_level DESC
    LIMIT 1
) ar ON true;
