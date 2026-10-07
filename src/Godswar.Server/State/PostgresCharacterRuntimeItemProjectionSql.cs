namespace Godswar.Server.State;

// Runtime reads stay bound to the process-pinned content revision rather
// than following a mutable compatibility publication pointer.
internal static partial class PostgresCharacterRuntimeItemProjectionSql
{
    public static readonly string CalculatedStatsForCharacter =
        $$"""
        WITH
        {{PostgresMountGearPassiveProjectionSql.CommonTableExpressions}}
        {{PostgresCharacterPetOwnerMergeProjectionSql.CommonTableExpression}}
        {{PostgresCharacterPetLearnedSkillProjectionSql.CommonTableExpression}}
        {{PostgresCharacterHolySpiritCombatProjectionSql.CommonTableExpressions}}
        equipment_stat_values AS (
            SELECT
                equipment.user_id,
                stat.stat_name,
                {{PostgresCharacterHolySuitProjectionSql.AdjustedBaseValue}} * stat.scale AS stat_value
            FROM character_items equipment
            JOIN character_base owner
              ON owner.id = equipment.user_id
            JOIN item_template_content_revisions revision
              ON revision.revision = @itemContentRevision
             AND revision.sealed_at IS NOT NULL
            JOIN item_template_content_definitions template
             ON template.revision = revision.revision
             AND template.id = equipment.prop_id
             AND (
                 template.equipment_slot = equipment.slot_index
                 OR template.kind = 'ring'
                    AND equipment.slot_index IN (8, 9)
             )
            CROSS JOIN (
                VALUES
                    ('MaxHP', 'max_hp', 1::numeric),
                    ('MaxMP', 'max_mp', 1::numeric),
                    ('Attack', 'physical_attack', 1::numeric),
                    ('Defence', 'physical_defense', 1::numeric),
                    ('MagicAk', 'magic_attack', 1::numeric),
                    ('MagicRec', 'magic_defense', 1::numeric),
                    ('Hit', 'hit', 1::numeric),
                    ('Miss', 'dodge', 1::numeric),
                    {{EquipmentStatusRatingValues}}
                    ('FuryAddAk', 'critical', 1::numeric),
                    ('FuryAddRec', 'critical_resistance', 1::numeric),
                    ('InjureImbibe', 'damage_absorb', 1::numeric),
                    ('PhysicalDamage', 'physical_damage_bonus', 10000::numeric),
                    ('MagicDamage', 'magic_damage_bonus', 10000::numeric),
                    ('PhysicalDamageAbsorb', 'damage_absorb', 1::numeric),
                    ('MagicDamageAbsorb', 'damage_absorb', 1::numeric),
                    ('Cure', 'cure_bonus', 10000::numeric),
                    ('AcceptCure', 'be_cure_bonus', 10000::numeric),
                    ('HPRestore', 'hp_recovery', 1::numeric),
                    ('MPRestore', 'mp_recovery', 1::numeric),
                    ('IgnorePhyPer', 'ignore_physical_defense', 10000::numeric),
                    ('IgnoreMagPer', 'ignore_magic_defense', 10000::numeric),
                    ('PhyAppendDamageVal', 'physical_append_damage', 1::numeric),
                    ('MagAppendDamageVal', 'magic_append_damage', 1::numeric),
                    ('CriIncPer', 'critical_damage_percent', 10000::numeric),
                    ('CriIncVal', 'critical_damage_flat', 1::numeric)
            ) stat(source_key, stat_name, scale)
            JOIN LATERAL (
                SELECT string_to_array(
                    template.stats->>stat.source_key, ',') AS values
                WHERE template.stats ? stat.source_key
            ) stat_values ON true
            {{PostgresCharacterHolySuitProjectionSql.BaseValueLateralJoin}}
            WHERE equipment.item_location = 0
              AND equipment.user_id = @characterId
              AND owner.fighter_job_lv >=
                  COALESCE(template.min_level, 1)
              AND (
                  template.max_level IS NULL
                  OR owner.fighter_job_lv <= template.max_level
              )
              AND (
                  cardinality(template.class_ids) = 0
                  OR owner.profession = ANY(template.class_ids)
              )
        ),
        attribute_stat_values AS (
            SELECT
                equipment.user_id,
                stat.stat_name,
                CASE WHEN attribute_template.percent
                    THEN attribute_value.value * 10000
                    ELSE attribute_value.value
                END AS stat_value
            FROM character_items equipment
            JOIN character_base owner
              ON owner.id = equipment.user_id
            JOIN item_template_content_revisions revision
              ON revision.revision = @itemContentRevision
             AND revision.sealed_at IS NOT NULL
            JOIN item_template_content_definitions equipment_template
              ON equipment_template.revision = revision.revision
             AND equipment_template.id = equipment.prop_id
             AND (
                 equipment_template.equipment_slot = equipment.slot_index
                 OR equipment_template.kind = 'ring'
                    AND equipment.slot_index IN (8, 9)
             )
            CROSS JOIN LATERAL (
                VALUES
                    (equipment.attribute1),
                    (equipment.attribute2),
                    (equipment.attribute3),
                    (equipment.attribute4),
                    (equipment.attribute5),
                    (equipment.class_attribute1)
            ) attribute(attribute_id)
            JOIN item_attribute_content_definitions attribute_template
              ON attribute_template.revision = @itemContentRevision
             AND attribute_template.id = attribute.attribute_id
            JOIN (
                VALUES
                    (0, 'physical_attack'), (1, 'physical_defense'),
                    (2, 'magic_attack'), (3, 'magic_defense'),
                    (4, 'hit'), (5, 'dodge'), (6, 'critical'),
                    {{AttributeStatusRatingValues}}
                    (7, 'critical_resistance'),
                    (8, 'physical_damage_bonus'), (9, 'magic_damage_bonus'),
                    (10, 'damage_absorb'), (13, 'max_hp'), (14, 'max_mp'),
                    (15, 'hp_recovery'), (16, 'mp_recovery'),
                    (17, 'be_cure_bonus'), (18, 'cure_bonus'),
                    (19, 'ignore_physical_defense'),
                    (20, 'ignore_magic_defense'),
                    (23, 'physical_append_damage'),
                    (24, 'magic_append_damage'),
                    (25, 'critical_damage_percent'),
                    (26, 'critical_damage_flat'),
                    (27, 'life_absorption'),
                    (28, 'damage_rebound')
            ) stat(stat_type, stat_name)
              ON stat.stat_type = attribute_template.stat_type
            CROSS JOIN LATERAL (
                SELECT attribute_template.level_values[
                    LEAST(
                        GREATEST(equipment.item_grade::integer, 1),
                        array_length(
                            attribute_template.level_values,
                            1))
                ] AS value
            ) attribute_value
            WHERE equipment.item_location = 0
              AND equipment.user_id = @characterId
              AND owner.fighter_job_lv >=
                  COALESCE(equipment_template.min_level, 1)
              AND (
                  equipment_template.max_level IS NULL
                  OR owner.fighter_job_lv <= equipment_template.max_level
              )
              AND (
                  cardinality(equipment_template.class_ids) = 0
                  OR owner.profession = ANY(equipment_template.class_ids)
              )
              AND attribute.attribute_id IS NOT NULL
              AND attribute.attribute_id >= 0
              AND attribute_value.value IS NOT NULL
        ),
        talent_stat_values AS (
            SELECT
                talent.user_id,
                stat.stat_name,
                talent_effective_rank(talent.rank) *
                    CASE WHEN template.is_percent
                        THEN template.effect_value * 10000
                        ELSE template.effect_value
                    END AS stat_value
            FROM character_talents talent
            JOIN character_base character
              ON character.id = talent.user_id
            JOIN gameplay_talent_definitions template
              ON template.id = talent.talent_id
             AND template.class_id = character.profession
             AND template.revision = COALESCE(
                 @gameplayContentRevision,
                 (
                     SELECT publication.revision
                     FROM gameplay_content_publication publication
                     WHERE publication.family = 'gameplay'
                 )
             )
            JOIN gameplay_talent_effect_definitions effect
              ON effect.revision = template.revision
             AND effect.id = template.effect_id
            JOIN (
                VALUES
                    ('MaxHP', 'max_hp'), ('MaxMP', 'max_mp'),
                    ('PhyAttack', 'physical_attack'),
                    ('PhyDefend', 'physical_defense'),
                    ('MagicAttack', 'magic_attack'),
                    ('MagicDefend', 'magic_defense'),
                    ('Hit', 'hit'), ('Miss', 'dodge'),
                    {{TalentStatusRatingValues}}
                    ('FrenzyHit', 'critical'),
                    ('FrenzyMiss', 'critical_resistance'),
                    ('DamageSorb', 'damage_absorb'),
                    ('PhyDamage', 'physical_damage_bonus'),
                    ('MagicDamage', 'magic_damage_bonus'),
                    ('Cure', 'cure_bonus'), ('Becure', 'be_cure_bonus'),
                    ('HPResume', 'hp_recovery'), ('MPResume', 'mp_recovery')
            ) stat(effect_key, stat_name)
              ON stat.effect_key = effect.key
            WHERE talent.rank > 0
        ),
        -- Holy Suit SET bonus: character-wide, staged by unlock_points, grown by
        -- per_point for every accumulated point and clamped by maximum. This is
        -- separate from the per-equipment percentage bonus, which scales an
        -- item's own base stat by holy_suit_progression_points() percent.
        holy_suit_stat_values AS (
            SELECT
                character.id AS user_id,
                stat.stat_name,
                LEAST(
                    TRUNC(
                        COALESCE(set_balance.per_point, effect.effect_value / 440)
                        * character.holy_suit_points),
                    COALESCE(
                        set_balance.maximum::numeric,
                        effect.effect_value * 3 / 4)) AS stat_value
            FROM character_base character
            JOIN holy_suit_effect_content_definitions effect
              ON effect.revision = @itemContentRevision
             AND character.holy_suit_points >= effect.unlock_points
            LEFT JOIN holy_suit_effect_templates set_balance
              ON set_balance.effect_key = effect.effect_key
            JOIN (
                VALUES
                    ('MaxHPD', 'max_hp'), ('MaxMPD', 'max_mp'),
                    ('Attack', 'physical_attack'),
                    ('Defence', 'physical_defense'),
                    ('MagicAk', 'magic_attack'),
                    ('MagicRec', 'magic_defense'),
                    ('Hit', 'hit'), ('Miss', 'dodge'),
                    ('InjureImbibe', 'damage_absorb')
            ) stat(effect_key, stat_name)
              ON stat.effect_key = effect.effect_key
        ),
        {{PostgresCharacterCombatSecondaryProjectionSql.CommonTableExpressions}}
        all_stat_values AS (
            SELECT * FROM equipment_stat_values
            UNION ALL SELECT * FROM attribute_stat_values
            UNION ALL SELECT * FROM holy_stone_stat_values
            WHERE stat_name IS NOT NULL
            UNION ALL SELECT * FROM talent_stat_values
            UNION ALL SELECT * FROM holy_suit_stat_values
            UNION ALL SELECT * FROM mount_gear_spirit_stat_values
            UNION ALL SELECT * FROM pet_owner_merge_stat_values
            UNION ALL SELECT * FROM pet_learned_skill_stat_values
            UNION ALL SELECT * FROM combat_secondary_stat_values
        ),
        stat_totals AS (
            SELECT
                user_id,
                COALESCE(SUM(stat_value) FILTER (WHERE stat_name = 'max_hp'), 0) AS max_hp,
                COALESCE(SUM(stat_value) FILTER (WHERE stat_name = 'max_mp'), 0) AS max_mp,
                COALESCE(SUM(stat_value) FILTER (WHERE stat_name = 'physical_attack'), 0) AS physical_attack,
                COALESCE(SUM(stat_value) FILTER (WHERE stat_name = 'physical_defense'), 0) AS physical_defense,
                COALESCE(SUM(stat_value) FILTER (WHERE stat_name = 'magic_attack'), 0) AS magic_attack,
                COALESCE(SUM(stat_value) FILTER (WHERE stat_name = 'magic_defense'), 0) AS magic_defense,
                COALESCE(SUM(stat_value) FILTER (WHERE stat_name = 'hit'), 0) AS hit,
                COALESCE(SUM(stat_value) FILTER (WHERE stat_name = 'dodge'), 0) AS dodge,
                {{StatusRatingTotals}}
                COALESCE(SUM(stat_value) FILTER (WHERE stat_name = 'critical'), 0) AS critical,
                COALESCE(SUM(stat_value) FILTER (WHERE stat_name = 'critical_resistance'), 0) AS critical_resistance,
                COALESCE(SUM(stat_value) FILTER (WHERE stat_name = 'damage_absorb'), 0) AS damage_absorb,
                COALESCE(SUM(stat_value) FILTER (WHERE stat_name = 'physical_damage_bonus'), 0) AS physical_damage_bonus,
                COALESCE(SUM(stat_value) FILTER (WHERE stat_name = 'magic_damage_bonus'), 0) AS magic_damage_bonus,
                COALESCE(SUM(stat_value) FILTER (WHERE stat_name = 'cure_bonus'), 0) AS cure_bonus,
                COALESCE(SUM(stat_value) FILTER (WHERE stat_name = 'be_cure_bonus'), 0) AS be_cure_bonus,
                COALESCE(SUM(stat_value) FILTER (WHERE stat_name = 'hp_recovery'), 0) AS hp_recovery,
                COALESCE(SUM(stat_value) FILTER (WHERE stat_name = 'mp_recovery'), 0) AS mp_recovery,
                COALESCE(SUM(stat_value) FILTER (WHERE stat_name = 'ignore_physical_defense'), 0) AS ignore_physical_defense,
                COALESCE(SUM(stat_value) FILTER (WHERE stat_name = 'ignore_magic_defense'), 0) AS ignore_magic_defense,
                COALESCE(SUM(stat_value) FILTER (WHERE stat_name = 'physical_append_damage'), 0) AS physical_append_damage,
                COALESCE(SUM(stat_value) FILTER (WHERE stat_name = 'magic_append_damage'), 0) AS magic_append_damage,
                COALESCE(SUM(stat_value) FILTER (WHERE stat_name = 'critical_damage_percent'), 0) AS critical_damage_percent,
                COALESCE(SUM(stat_value) FILTER (WHERE stat_name = 'critical_damage_flat'), 0) AS critical_damage_flat,
                COALESCE(SUM(stat_value) FILTER (WHERE stat_name = 'physical_damage_reduction'), 0) AS physical_damage_reduction,
                COALESCE(SUM(stat_value) FILTER (WHERE stat_name = 'magic_damage_reduction'), 0) AS magic_damage_reduction,
                COALESCE(SUM(stat_value) FILTER (WHERE stat_name = 'critical_damage_reduction'), 0) AS critical_damage_reduction,
                COALESCE(SUM(stat_value) FILTER (WHERE stat_name = 'life_absorption'), 0) AS life_absorption,
                COALESCE(SUM(stat_value) FILTER (WHERE stat_name = 'damage_rebound'), 0) AS damage_rebound,
                {{OwnerMergeInternalStatTotals}}
                COALESCE(SUM(stat_value) FILTER (WHERE stat_name = 'physical_flat_absorption'), 0) AS physical_flat_absorption,
                COALESCE(SUM(stat_value) FILTER (WHERE stat_name = 'magic_flat_absorption'), 0) AS magic_flat_absorption,
                COALESCE(SUM(stat_value) FILTER (WHERE stat_name = 'critical_damage_flat_reduction'), 0) AS critical_damage_flat_reduction,
                COALESCE(SUM(stat_value) FILTER (WHERE stat_name = 'damage_rebound_flat'), 0) AS damage_rebound_flat
            FROM all_stat_values
            GROUP BY user_id
        )
        SELECT
            cb.id AS user_id,
            cb.account_id,
            cb.name,
            cb.profession,
            cb.fighter_job_lv AS level,
            {{DonatorMaximumHealthSelects}}
            {{DonatorPhysicalAttackSelect}}
            ROUND(COALESCE(stats.physical_defense, 0))::integer AS physical_defense,
            {{DonatorMagicAttackSelect}}
            ROUND(COALESCE(stats.magic_defense, 0))::integer AS magic_defense,
            ROUND(COALESCE(stats.hit, 0))::integer AS hit,
            ROUND(COALESCE(stats.dodge, 0))::integer AS dodge,
            ROUND(COALESCE(stats.critical, 0))::integer AS critical,
            ROUND(COALESCE(stats.critical_resistance, 0))::integer AS critical_resistance,
            ROUND(COALESCE(stats.damage_absorb, 0))::integer AS damage_absorb,
            ROUND(COALESCE(stats.physical_damage_bonus, 0))::integer AS physical_damage_bonus,
            ROUND(COALESCE(stats.magic_damage_bonus, 0))::integer AS magic_damage_bonus,
            ROUND(COALESCE(stats.cure_bonus, 0))::integer AS cure_bonus,
            ROUND(COALESCE(stats.be_cure_bonus, 0))::integer AS be_cure_bonus,
            ROUND(COALESCE(stats.hp_recovery, 0))::integer AS hp_recovery,
            ROUND(COALESCE(stats.mp_recovery, 0))::integer AS mp_recovery,
            ROUND(COALESCE(stats.ignore_physical_defense, 0))::integer AS ignore_physical_defense,
            ROUND(COALESCE(stats.ignore_magic_defense, 0))::integer AS ignore_magic_defense,
            ROUND(COALESCE(stats.physical_append_damage, 0))::integer AS physical_append_damage,
            ROUND(COALESCE(stats.magic_append_damage, 0))::integer AS magic_append_damage,
            ROUND(COALESCE(stats.critical_damage_percent, 0))::integer AS critical_damage_percent,
            ROUND(COALESCE(stats.critical_damage_flat, 0))::integer AS critical_damage_flat,
            item_rank_projection.weapon_score,
            item_rank_projection.weapon_rank,
            item_rank_projection.weapon_aura_effect,
            item_rank_projection.armor_score,
            item_rank_projection.armor_rank,
            item_rank_projection.armor_aura_effect,
            COALESCE((
                SELECT COUNT(*)::integer
                FROM character_skills skill
                WHERE skill.user_id = cb.id
            ), 0) AS learned_skill_count,
            ROUND(COALESCE(stats.physical_damage_reduction, 0))::integer AS physical_damage_reduction,
            ROUND(COALESCE(stats.magic_damage_reduction, 0))::integer AS magic_damage_reduction,
            ROUND(COALESCE(stats.critical_damage_reduction, 0))::integer AS critical_damage_reduction,
            ROUND(COALESCE(stats.life_absorption, 0))::integer AS life_absorption,
            ROUND(COALESCE(stats.damage_rebound, 0))::integer AS damage_rebound,
            ROUND(COALESCE(stats.physical_flat_absorption, 0))::integer AS physical_flat_absorption,
            ROUND(COALESCE(stats.magic_flat_absorption, 0))::integer AS magic_flat_absorption,
            ROUND(COALESCE(stats.critical_damage_flat_reduction, 0))::integer AS critical_damage_flat_reduction,
            ROUND(COALESCE(stats.damage_rebound_flat, 0))::integer AS damage_rebound_flat,
            weapon_combat_projection.basic_attack_interval_milliseconds,
            weapon_combat_projection.basic_attack_range,
            {{StatusRatingSelects}},
            {{OwnerMergeInternalStatSelects}}
        FROM character_base cb
        LEFT JOIN stat_totals stats ON stats.user_id = cb.id
        {{DonatorBenefitJoins}}
        {{RankLateralJoinForCharacterAlias}}
        {{PostgresCharacterWeaponCombatProjectionSql.LateralJoinForCharacterAlias}}
        WHERE cb.account_id = @accountId
          AND cb.id = @characterId
          AND cb.lifecycle_state = 'active';

        """;
}
