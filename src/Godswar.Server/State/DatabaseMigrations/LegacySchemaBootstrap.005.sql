CREATE OR REPLACE VIEW character_stat_summary AS
WITH equipment_stat_values AS (
    SELECT
        ce.user_id,
        stat.stat_key,
        CASE
            WHEN stat_values.values IS NULL THEN 0::numeric
            ELSE COALESCE(NULLIF(stat_values.values[
                LEAST(GREATEST(ce.item_quality::integer, 1), array_length(stat_values.values, 1))
            ], '')::numeric, 0::numeric) * stat.scale
        END AS stat_value
    FROM character_equip ce
    JOIN item_templates it ON it.id = ce.prop_id
    CROSS JOIN (
        VALUES
            ('MaxHP', 1::numeric), ('MaxMP', 1::numeric), ('Attack', 1::numeric), ('Defence', 1::numeric),
            ('MagicAk', 1::numeric), ('MagicRec', 1::numeric), ('Hit', 1::numeric), ('Miss', 1::numeric),
            ('FuryAddAk', 1::numeric), ('FuryAddRec', 1::numeric), ('InjureImbibe', 1::numeric),
            ('PhysicalDamage', 10000::numeric), ('MagicDamage', 10000::numeric),
            ('PhysicalDamageAbsorb', 1::numeric), ('MagicDamageAbsorb', 1::numeric),
            ('Cure', 10000::numeric), ('AcceptCure', 10000::numeric),
            ('HPRestore', 1::numeric), ('MPRestore', 1::numeric),
            ('IgnorePhyPer', 10000::numeric), ('IgnoreMagPer', 10000::numeric),
            ('PhyAppendDamageVal', 1::numeric), ('MagAppendDamageVal', 1::numeric),
            ('CriIncPer', 10000::numeric), ('CriIncVal', 1::numeric)
    ) AS stat(stat_key, scale)
    LEFT JOIN LATERAL (
        SELECT string_to_array(it.stats->>stat.stat_key, ',') AS values
        WHERE it.stats ? stat.stat_key
    ) stat_values ON true
),
equipment_base AS (
    SELECT
        user_id,
        SUM(stat_value) FILTER (WHERE stat_key = 'MaxHP') AS max_hp,
        SUM(stat_value) FILTER (WHERE stat_key = 'MaxMP') AS max_mp,
        SUM(stat_value) FILTER (WHERE stat_key = 'Attack') AS physical_attack,
        SUM(stat_value) FILTER (WHERE stat_key = 'Defence') AS physical_defense,
        SUM(stat_value) FILTER (WHERE stat_key = 'MagicAk') AS magic_attack,
        SUM(stat_value) FILTER (WHERE stat_key = 'MagicRec') AS magic_defense,
        SUM(stat_value) FILTER (WHERE stat_key = 'Hit') AS hit,
        SUM(stat_value) FILTER (WHERE stat_key = 'Miss') AS dodge,
        SUM(stat_value) FILTER (WHERE stat_key = 'FuryAddAk') AS critical,
        SUM(stat_value) FILTER (WHERE stat_key = 'FuryAddRec') AS critical_resistance,
        SUM(stat_value) FILTER (WHERE stat_key IN ('InjureImbibe', 'PhysicalDamageAbsorb', 'MagicDamageAbsorb')) AS damage_absorb,
        SUM(stat_value) FILTER (WHERE stat_key = 'PhysicalDamage') AS physical_damage_bonus,
        SUM(stat_value) FILTER (WHERE stat_key = 'MagicDamage') AS magic_damage_bonus,
        SUM(stat_value) FILTER (WHERE stat_key = 'Cure') AS cure_bonus,
        SUM(stat_value) FILTER (WHERE stat_key = 'AcceptCure') AS be_cure_bonus,
        SUM(stat_value) FILTER (WHERE stat_key = 'HPRestore') AS hp_recovery,
        SUM(stat_value) FILTER (WHERE stat_key = 'MPRestore') AS mp_recovery,
        SUM(stat_value) FILTER (WHERE stat_key = 'IgnorePhyPer') AS ignore_physical_defense,
        SUM(stat_value) FILTER (WHERE stat_key = 'IgnoreMagPer') AS ignore_magic_defense,
        SUM(stat_value) FILTER (WHERE stat_key = 'PhyAppendDamageVal') AS physical_append_damage,
        SUM(stat_value) FILTER (WHERE stat_key = 'MagAppendDamageVal') AS magic_append_damage,
        SUM(stat_value) FILTER (WHERE stat_key = 'CriIncPer') AS critical_damage_percent,
        SUM(stat_value) FILTER (WHERE stat_key = 'CriIncVal') AS critical_damage_flat
    FROM equipment_stat_values
    GROUP BY user_id
),
append_stats AS (
    SELECT
        user_id,
        SUM(CASE WHEN percent THEN attribute_value * 10000 ELSE attribute_value END) FILTER (WHERE stat_type = 13) AS max_hp,
        SUM(CASE WHEN percent THEN attribute_value * 10000 ELSE attribute_value END) FILTER (WHERE stat_type = 14) AS max_mp,
        SUM(CASE WHEN percent THEN attribute_value * 10000 ELSE attribute_value END) FILTER (WHERE stat_type = 0) AS physical_attack,
        SUM(CASE WHEN percent THEN attribute_value * 10000 ELSE attribute_value END) FILTER (WHERE stat_type = 1) AS physical_defense,
        SUM(CASE WHEN percent THEN attribute_value * 10000 ELSE attribute_value END) FILTER (WHERE stat_type = 2) AS magic_attack,
        SUM(CASE WHEN percent THEN attribute_value * 10000 ELSE attribute_value END) FILTER (WHERE stat_type = 3) AS magic_defense,
        SUM(CASE WHEN percent THEN attribute_value * 10000 ELSE attribute_value END) FILTER (WHERE stat_type = 4) AS hit,
        SUM(CASE WHEN percent THEN attribute_value * 10000 ELSE attribute_value END) FILTER (WHERE stat_type = 5) AS dodge,
        SUM(CASE WHEN percent THEN attribute_value * 10000 ELSE attribute_value END) FILTER (WHERE stat_type = 6) AS critical,
        SUM(CASE WHEN percent THEN attribute_value * 10000 ELSE attribute_value END) FILTER (WHERE stat_type = 7) AS critical_resistance,
        SUM(CASE WHEN percent THEN attribute_value * 10000 ELSE attribute_value END) FILTER (WHERE stat_type = 10) AS damage_absorb,
        SUM(CASE WHEN percent THEN attribute_value * 10000 ELSE attribute_value END) FILTER (WHERE stat_type = 8) AS physical_damage_bonus,
        SUM(CASE WHEN percent THEN attribute_value * 10000 ELSE attribute_value END) FILTER (WHERE stat_type = 9) AS magic_damage_bonus,
        SUM(CASE WHEN percent THEN attribute_value * 10000 ELSE attribute_value END) FILTER (WHERE stat_type = 18) AS cure_bonus,
        SUM(CASE WHEN percent THEN attribute_value * 10000 ELSE attribute_value END) FILTER (WHERE stat_type = 17) AS be_cure_bonus,
        SUM(CASE WHEN percent THEN attribute_value * 10000 ELSE attribute_value END) FILTER (WHERE stat_type = 15) AS hp_recovery,
        SUM(CASE WHEN percent THEN attribute_value * 10000 ELSE attribute_value END) FILTER (WHERE stat_type = 16) AS mp_recovery,
        SUM(CASE WHEN percent THEN attribute_value * 10000 ELSE attribute_value END) FILTER (WHERE stat_type = 19) AS ignore_physical_defense,
        SUM(CASE WHEN percent THEN attribute_value * 10000 ELSE attribute_value END) FILTER (WHERE stat_type = 20) AS ignore_magic_defense,
        SUM(CASE WHEN percent THEN attribute_value * 10000 ELSE attribute_value END) FILTER (WHERE stat_type = 23) AS physical_append_damage,
        SUM(CASE WHEN percent THEN attribute_value * 10000 ELSE attribute_value END) FILTER (WHERE stat_type = 24) AS magic_append_damage,
        SUM(CASE WHEN percent THEN attribute_value * 10000 ELSE attribute_value END) FILTER (WHERE stat_type = 25) AS critical_damage_percent,
        SUM(CASE WHEN percent THEN attribute_value * 10000 ELSE attribute_value END) FILTER (WHERE stat_type = 26) AS critical_damage_flat
    FROM character_equipment_attributes
    WHERE attribute_value IS NOT NULL
    GROUP BY user_id
),
holy_stone_values AS (
    SELECT
        ce.user_id,
        socket.effect_id,
        CASE
            WHEN socket.effect_id IN (1, 2, 3, 4) THEN (ARRAY[110, 170, 240, 320, 410, 500, 650, 850, 1100, 1400]::numeric[])[socket_level.safe_level]
            WHEN socket.effect_id IN (5, 6) THEN (ARRAY[120, 190, 280, 380, 500, 620, 850, 1200, 1650, 2200]::numeric[])[socket_level.safe_level]
            WHEN socket.effect_id = 8 THEN (ARRAY[150, 240, 340, 460, 590, 720, 950, 1300, 1800, 2400]::numeric[])[socket_level.safe_level]
            WHEN socket.effect_id IN (11, 12, 14, 16, 18, 20) THEN (ARRAY[60, 90, 130, 170, 210, 250, 350, 500, 700, 950]::numeric[])[socket_level.safe_level]
            ELSE (ARRAY[80, 120, 170, 230, 300, 370, 500, 700, 950, 1200]::numeric[])[socket_level.safe_level]
        END AS stat_value
    FROM character_equip ce
    CROSS JOIN LATERAL (
        VALUES
            (ce.holy_socket1_effect_id, ce.holy_socket1_level),
            (ce.holy_socket2_effect_id, ce.holy_socket2_level),
            (ce.holy_socket3_effect_id, ce.holy_socket3_level),
            (ce.holy_socket4_effect_id, ce.holy_socket4_level)
    ) AS socket(effect_id, effect_level)
    CROSS JOIN LATERAL (
        SELECT LEAST(GREATEST(COALESCE(socket.effect_level, 1)::integer, 1), 10) AS safe_level
    ) socket_level
    WHERE socket.effect_id IS NOT NULL
      AND socket.effect_id > 0
      AND socket.effect_level IS NOT NULL
),
holy_stone_stats AS (
    SELECT
        user_id,
        SUM(stat_value) FILTER (WHERE effect_id IN (9, 10, 11, 12, 13, 14, 19, 20)) AS damage_absorb,
        SUM(stat_value) FILTER (WHERE effect_id = 3) AS physical_damage_bonus,
        SUM(stat_value) FILTER (WHERE effect_id = 4) AS magic_damage_bonus,
        SUM(stat_value) FILTER (WHERE effect_id IN (15, 16)) AS hp_recovery,
        SUM(stat_value) FILTER (WHERE effect_id IN (17, 18)) AS mp_recovery,
        SUM(stat_value) FILTER (WHERE effect_id = 1) AS ignore_physical_defense,
        SUM(stat_value) FILTER (WHERE effect_id = 2) AS ignore_magic_defense,
        SUM(stat_value) FILTER (WHERE effect_id = 5) AS physical_append_damage,
        SUM(stat_value) FILTER (WHERE effect_id = 6) AS magic_append_damage,
        SUM(stat_value) FILTER (WHERE effect_id = 7) AS critical_damage_percent,
        SUM(stat_value) FILTER (WHERE effect_id = 8) AS critical_damage_flat
    FROM holy_stone_values
    GROUP BY user_id
),
talent_stats AS (
    SELECT
        ct.user_id,
        SUM(talent_effective_rank(ct.rank) * CASE WHEN tt.is_percent THEN tt.effect_value * 10000 ELSE tt.effect_value END) FILTER (WHERE tet.key = 'MaxHP') AS max_hp,
        SUM(talent_effective_rank(ct.rank) * CASE WHEN tt.is_percent THEN tt.effect_value * 10000 ELSE tt.effect_value END) FILTER (WHERE tet.key = 'MaxMP') AS max_mp,
        SUM(talent_effective_rank(ct.rank) * CASE WHEN tt.is_percent THEN tt.effect_value * 10000 ELSE tt.effect_value END) FILTER (WHERE tet.key = 'PhyAttack') AS physical_attack,
        SUM(talent_effective_rank(ct.rank) * CASE WHEN tt.is_percent THEN tt.effect_value * 10000 ELSE tt.effect_value END) FILTER (WHERE tet.key = 'PhyDefend') AS physical_defense,
        SUM(talent_effective_rank(ct.rank) * CASE WHEN tt.is_percent THEN tt.effect_value * 10000 ELSE tt.effect_value END) FILTER (WHERE tet.key = 'MagicAttack') AS magic_attack,
        SUM(talent_effective_rank(ct.rank) * CASE WHEN tt.is_percent THEN tt.effect_value * 10000 ELSE tt.effect_value END) FILTER (WHERE tet.key = 'MagicDefend') AS magic_defense,
        SUM(talent_effective_rank(ct.rank) * CASE WHEN tt.is_percent THEN tt.effect_value * 10000 ELSE tt.effect_value END) FILTER (WHERE tet.key = 'Hit') AS hit,
        SUM(talent_effective_rank(ct.rank) * CASE WHEN tt.is_percent THEN tt.effect_value * 10000 ELSE tt.effect_value END) FILTER (WHERE tet.key = 'Miss') AS dodge,
        SUM(talent_effective_rank(ct.rank) * CASE WHEN tt.is_percent THEN tt.effect_value * 10000 ELSE tt.effect_value END) FILTER (WHERE tet.key = 'FrenzyHit') AS critical,
        SUM(talent_effective_rank(ct.rank) * CASE WHEN tt.is_percent THEN tt.effect_value * 10000 ELSE tt.effect_value END) FILTER (WHERE tet.key = 'FrenzyMiss') AS critical_resistance,
        SUM(talent_effective_rank(ct.rank) * CASE WHEN tt.is_percent THEN tt.effect_value * 10000 ELSE tt.effect_value END) FILTER (WHERE tet.key = 'DamageSorb') AS damage_absorb,
        SUM(talent_effective_rank(ct.rank) * CASE WHEN tt.is_percent THEN tt.effect_value * 10000 ELSE tt.effect_value END) FILTER (WHERE tet.key = 'PhyDamage') AS physical_damage_bonus,
        SUM(talent_effective_rank(ct.rank) * CASE WHEN tt.is_percent THEN tt.effect_value * 10000 ELSE tt.effect_value END) FILTER (WHERE tet.key = 'MagicDamage') AS magic_damage_bonus,
        SUM(talent_effective_rank(ct.rank) * CASE WHEN tt.is_percent THEN tt.effect_value * 10000 ELSE tt.effect_value END) FILTER (WHERE tet.key = 'Cure') AS cure_bonus,
        SUM(talent_effective_rank(ct.rank) * CASE WHEN tt.is_percent THEN tt.effect_value * 10000 ELSE tt.effect_value END) FILTER (WHERE tet.key = 'Becure') AS be_cure_bonus,
        SUM(talent_effective_rank(ct.rank) * CASE WHEN tt.is_percent THEN tt.effect_value * 10000 ELSE tt.effect_value END) FILTER (WHERE tet.key = 'HPResume') AS hp_recovery,
        SUM(talent_effective_rank(ct.rank) * CASE WHEN tt.is_percent THEN tt.effect_value * 10000 ELSE tt.effect_value END) FILTER (WHERE tet.key = 'MPResume') AS mp_recovery
    FROM character_talents ct
    JOIN character_base cb ON cb.id = ct.user_id
    JOIN talent_templates tt ON tt.id = ct.talent_id AND tt.class_id = cb.profession
    JOIN talent_effect_templates tet ON tet.id = tt.effect_id
    WHERE ct.rank > 0
    GROUP BY ct.user_id
),
holy_suit_stats AS (
    SELECT
        cb.id AS user_id,
        SUM(h.effect_value) FILTER (WHERE h.effect_key = 'MaxHPD') AS max_hp,
        SUM(h.effect_value) FILTER (WHERE h.effect_key = 'MaxMPD') AS max_mp,
        SUM(h.effect_value) FILTER (WHERE h.effect_key = 'Attack') AS physical_attack,
        SUM(h.effect_value) FILTER (WHERE h.effect_key = 'Defence') AS physical_defense,
        SUM(h.effect_value) FILTER (WHERE h.effect_key = 'MagicAk') AS magic_attack,
        SUM(h.effect_value) FILTER (WHERE h.effect_key = 'MagicRec') AS magic_defense,
        SUM(h.effect_value) FILTER (WHERE h.effect_key = 'Hit') AS hit,
        SUM(h.effect_value) FILTER (WHERE h.effect_key = 'Miss') AS dodge,
        SUM(h.effect_value) FILTER (WHERE h.effect_key = 'InjureImbibe') AS damage_absorb
    FROM character_base cb
    JOIN holy_suit_effect_templates h ON cb.holy_suit_points >= h.unlock_points
    GROUP BY cb.id
),
skill_stats AS (
    SELECT user_id, COUNT(*)::integer AS learned_skill_count
    FROM character_skills
    GROUP BY user_id
),
combined AS (
    SELECT
        cb.id AS user_id,
        cb.account_id,
        cb.name,
        cb.profession,
        cb.fighter_job_lv AS level,
        cb."curHP" AS base_current_hp,
        cb."curMP" AS base_current_mp,
        GREATEST(1::numeric,
            cb."MaxHP"::numeric +
            COALESCE(eb.max_hp, 0) + COALESCE(ap.max_hp, 0) +
            COALESCE(ts.max_hp, 0) + COALESCE(hs.max_hp, 0)
        ) AS max_hp,
        GREATEST(0::numeric,
            cb."MaxMP"::numeric +
            COALESCE(eb.max_mp, 0) + COALESCE(ap.max_mp, 0) +
            COALESCE(ts.max_mp, 0) + COALESCE(hs.max_mp, 0)
        ) AS max_mp,
        COALESCE(eb.physical_attack, 0) + COALESCE(ap.physical_attack, 0) + COALESCE(ts.physical_attack, 0) + COALESCE(hs.physical_attack, 0) AS physical_attack,
        COALESCE(eb.physical_defense, 0) + COALESCE(ap.physical_defense, 0) + COALESCE(ts.physical_defense, 0) + COALESCE(hs.physical_defense, 0) AS physical_defense,
        COALESCE(eb.magic_attack, 0) + COALESCE(ap.magic_attack, 0) + COALESCE(ts.magic_attack, 0) + COALESCE(hs.magic_attack, 0) AS magic_attack,
        COALESCE(eb.magic_defense, 0) + COALESCE(ap.magic_defense, 0) + COALESCE(ts.magic_defense, 0) + COALESCE(hs.magic_defense, 0) AS magic_defense,
        COALESCE(eb.hit, 0) + COALESCE(ap.hit, 0) + COALESCE(ts.hit, 0) + COALESCE(hs.hit, 0) AS hit,
        COALESCE(eb.dodge, 0) + COALESCE(ap.dodge, 0) + COALESCE(ts.dodge, 0) + COALESCE(hs.dodge, 0) AS dodge,
        COALESCE(eb.critical, 0) + COALESCE(ap.critical, 0) + COALESCE(ts.critical, 0) AS critical,
        COALESCE(eb.critical_resistance, 0) + COALESCE(ap.critical_resistance, 0) + COALESCE(ts.critical_resistance, 0) AS critical_resistance,
        COALESCE(eb.damage_absorb, 0) + COALESCE(ap.damage_absorb, 0) + COALESCE(ts.damage_absorb, 0) + COALESCE(hs.damage_absorb, 0) + COALESCE(hst.damage_absorb, 0) AS damage_absorb,
        COALESCE(eb.physical_damage_bonus, 0) + COALESCE(ap.physical_damage_bonus, 0) + COALESCE(ts.physical_damage_bonus, 0) + COALESCE(hst.physical_damage_bonus, 0) AS physical_damage_bonus,
        COALESCE(eb.magic_damage_bonus, 0) + COALESCE(ap.magic_damage_bonus, 0) + COALESCE(ts.magic_damage_bonus, 0) + COALESCE(hst.magic_damage_bonus, 0) AS magic_damage_bonus,
        COALESCE(eb.cure_bonus, 0) + COALESCE(ap.cure_bonus, 0) + COALESCE(ts.cure_bonus, 0) AS cure_bonus,
        COALESCE(eb.be_cure_bonus, 0) + COALESCE(ap.be_cure_bonus, 0) + COALESCE(ts.be_cure_bonus, 0) AS be_cure_bonus,
        COALESCE(eb.hp_recovery, 0) + COALESCE(ap.hp_recovery, 0) + COALESCE(ts.hp_recovery, 0) + COALESCE(hst.hp_recovery, 0) AS hp_recovery,
        COALESCE(eb.mp_recovery, 0) + COALESCE(ap.mp_recovery, 0) + COALESCE(ts.mp_recovery, 0) + COALESCE(hst.mp_recovery, 0) AS mp_recovery,
        COALESCE(eb.ignore_physical_defense, 0) + COALESCE(ap.ignore_physical_defense, 0) + COALESCE(hst.ignore_physical_defense, 0) AS ignore_physical_defense,
        COALESCE(eb.ignore_magic_defense, 0) + COALESCE(ap.ignore_magic_defense, 0) + COALESCE(hst.ignore_magic_defense, 0) AS ignore_magic_defense,
        COALESCE(eb.physical_append_damage, 0) + COALESCE(ap.physical_append_damage, 0) + COALESCE(hst.physical_append_damage, 0) AS physical_append_damage,
        COALESCE(eb.magic_append_damage, 0) + COALESCE(ap.magic_append_damage, 0) + COALESCE(hst.magic_append_damage, 0) AS magic_append_damage,
        COALESCE(eb.critical_damage_percent, 0) + COALESCE(ap.critical_damage_percent, 0) + COALESCE(hst.critical_damage_percent, 0) AS critical_damage_percent,
        COALESCE(eb.critical_damage_flat, 0) + COALESCE(ap.critical_damage_flat, 0) + COALESCE(hst.critical_damage_flat, 0) AS critical_damage_flat,
        COALESCE(cr.weapon_score, 0) AS weapon_score,
        COALESCE(cr.weapon_rank, 0::smallint) AS weapon_rank,
        COALESCE(cr.weapon_aura_effect, 0) AS weapon_aura_effect,
        COALESCE(cr.armor_score, 0) AS armor_score,
        COALESCE(cr.armor_rank, 0::smallint) AS armor_rank,
        COALESCE(cr.armor_aura_effect, 0) AS armor_aura_effect,
        COALESCE(ss.learned_skill_count, 0) AS learned_skill_count
    FROM character_base cb
    LEFT JOIN equipment_base eb ON eb.user_id = cb.id
    LEFT JOIN append_stats ap ON ap.user_id = cb.id
    LEFT JOIN holy_stone_stats hst ON hst.user_id = cb.id
    LEFT JOIN talent_stats ts ON ts.user_id = cb.id
    LEFT JOIN holy_suit_stats hs ON hs.user_id = cb.id
    LEFT JOIN character_rank_summary cr ON cr.user_id = cb.id
    LEFT JOIN skill_stats ss ON ss.user_id = cb.id
)
SELECT
    user_id,
    account_id,
    name,
    profession,
    level,
    ROUND(max_hp)::integer AS max_hp,
    ROUND(max_mp)::integer AS max_mp,
    LEAST(GREATEST(base_current_hp, 0), ROUND(max_hp)::integer) AS current_hp,
    LEAST(GREATEST(base_current_mp, 0), ROUND(max_mp)::integer) AS current_mp,
    ROUND(physical_attack)::integer AS physical_attack,
    ROUND(physical_defense)::integer AS physical_defense,
    ROUND(magic_attack)::integer AS magic_attack,
    ROUND(magic_defense)::integer AS magic_defense,
    ROUND(hit)::integer AS hit,
    ROUND(dodge)::integer AS dodge,
    ROUND(critical)::integer AS critical,
    ROUND(critical_resistance)::integer AS critical_resistance,
    ROUND(damage_absorb)::integer AS damage_absorb,
    ROUND(physical_damage_bonus)::integer AS physical_damage_bonus,
    ROUND(magic_damage_bonus)::integer AS magic_damage_bonus,
    ROUND(cure_bonus)::integer AS cure_bonus,
    ROUND(be_cure_bonus)::integer AS be_cure_bonus,
    ROUND(hp_recovery)::integer AS hp_recovery,
    ROUND(mp_recovery)::integer AS mp_recovery,
    ROUND(ignore_physical_defense)::integer AS ignore_physical_defense,
    ROUND(ignore_magic_defense)::integer AS ignore_magic_defense,
    ROUND(physical_append_damage)::integer AS physical_append_damage,
    ROUND(magic_append_damage)::integer AS magic_append_damage,
    ROUND(critical_damage_percent)::integer AS critical_damage_percent,
    ROUND(critical_damage_flat)::integer AS critical_damage_flat,
    weapon_score,
    weapon_rank,
    weapon_aura_effect,
    armor_score,
    armor_rank,
    armor_aura_effect,
    learned_skill_count
FROM combined;