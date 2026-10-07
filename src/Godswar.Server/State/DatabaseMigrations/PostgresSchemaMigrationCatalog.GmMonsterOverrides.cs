namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    /// <summary>
    /// Operator-authored monster spawns and monster attribute overrides.
    /// </summary>
    /// <remarks>
    /// Three tables, because "刷怪" has three meanings the GM tool must express:
    /// <list type="bullet">
    /// <item><c>gm_monster_spawns</c> - brand-new points. The row carries the
    /// full 10020 appearance packet, copied by the tool from an existing
    /// published spawn of the same map and template and then rewritten: the
    /// bytes after offset 44 are not reversed, so a point must be based on a
    /// captured template rather than invented.</item>
    /// <item><c>gm_monster_spawn_edits</c> - changes to a published point,
    /// keyed by its published <c>(map_id, object_id)</c>. <c>enabled = false</c>
    /// is how a point is removed: <c>monster_spawn_definitions</c> is immutable
    /// (its own DELETE/UPDATE trigger rejects edits).</item>
    /// <item><c>gm_monster_attributes</c> - per map+template combat ratings,
    /// which are code formulas and therefore cannot be expressed in a
    /// packet.</item>
    /// </list>
    /// Every override column is nullable, and NULL means "not configured" -
    /// never zero. Nothing here participates in an immutable content revision:
    /// the server reads these tables at startup and on the next map entry, so a
    /// change never rewrites published content or its hash.
    /// </remarks>
    private static PostgresSchemaMigration CreateGmMonsterOverrides() => new(
        "20261004_228_gm_monster_overrides",
        "Store operator-authored GM monster spawns and attribute overrides",
        """
        CREATE TABLE IF NOT EXISTS public.gm_monster_spawns (
            id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            name varchar(120) NOT NULL
                CHECK (btrim(name) <> '' AND name = btrim(name)),
            waypoint_id bigint NULL
                REFERENCES public.gm_waypoints (id) ON DELETE SET NULL,
            map_id smallint NOT NULL CHECK (map_id BETWEEN 0 AND 255),
            scene_key varchar(96) NOT NULL CHECK (btrim(scene_key) <> ''),
            template_key varchar(128) NOT NULL CHECK (btrim(template_key) <> ''),
            display_name varchar(255) NOT NULL DEFAULT '',
            object_id bigint NOT NULL CHECK (object_id BETWEEN 1 AND 4294967295),
            pos_x real NOT NULL CHECK (
                pos_x <> ALL (ARRAY['NaN'::real, 'Infinity'::real, '-Infinity'::real])
            ),
            pos_z real NOT NULL CHECK (
                pos_z <> ALL (ARRAY['NaN'::real, 'Infinity'::real, '-Infinity'::real])
            ),
            clear_bytes bytea NOT NULL
                CHECK (octet_length(clear_bytes) BETWEEN 108 AND 1200),
            enabled boolean NOT NULL DEFAULT true,
            note text NOT NULL DEFAULT '',
            created_at timestamp with time zone NOT NULL DEFAULT now(),
            updated_at timestamp with time zone NOT NULL DEFAULT now(),
            updated_by varchar(64) NOT NULL DEFAULT ''
        );

        CREATE UNIQUE INDEX IF NOT EXISTS uq_gm_monster_spawns_name
            ON public.gm_monster_spawns (name);
        CREATE UNIQUE INDEX IF NOT EXISTS uq_gm_monster_spawns_map_object
            ON public.gm_monster_spawns (map_id, object_id);
        CREATE INDEX IF NOT EXISTS ix_gm_monster_spawns_map
            ON public.gm_monster_spawns (map_id, template_key);

        COMMENT ON TABLE public.gm_monster_spawns IS
            'Operator-authored monster spawn points written by the GM tool. '
            'clear_bytes is a complete 10020 appearance packet copied from a '
            'published spawn of the same map and template, with the level, HP, '
            'position and facing fields rewritten. Read at map entry; it never '
            'rewrites published content or its revision hash.';
        COMMENT ON COLUMN public.gm_monster_spawns.waypoint_id IS
            'The GM map marker this point was created from, when it came from one.';

        CREATE TABLE IF NOT EXISTS public.gm_monster_spawn_edits (
            map_id smallint NOT NULL CHECK (map_id BETWEEN 0 AND 255),
            object_id bigint NOT NULL CHECK (object_id BETWEEN 1 AND 4294967295),
            enabled boolean NOT NULL DEFAULT true,
            pos_x real NULL CHECK (
                pos_x <> ALL (ARRAY['NaN'::real, 'Infinity'::real, '-Infinity'::real])
            ),
            pos_z real NULL CHECK (
                pos_z <> ALL (ARRAY['NaN'::real, 'Infinity'::real, '-Infinity'::real])
            ),
            facing real NULL CHECK (
                facing <> ALL (ARRAY['NaN'::real, 'Infinity'::real, '-Infinity'::real])
            ),
            level integer NULL CHECK (level IS NULL OR level BETWEEN 1 AND 10000),
            current_health integer NULL CHECK (current_health IS NULL OR current_health >= 0),
            maximum_health integer NULL CHECK (maximum_health IS NULL OR maximum_health >= 1),
            physical_attack integer NULL CHECK (physical_attack IS NULL OR physical_attack >= 0),
            magic_attack integer NULL CHECK (magic_attack IS NULL OR magic_attack >= 0),
            physical_defense integer NULL CHECK (physical_defense IS NULL OR physical_defense >= 0),
            magic_defense integer NULL CHECK (magic_defense IS NULL OR magic_defense >= 0),
            hit integer NULL CHECK (hit IS NULL OR hit >= 0),
            dodge integer NULL CHECK (dodge IS NULL OR dodge >= 0),
            critical integer NULL CHECK (critical IS NULL OR critical >= 0),
            critical_resistance integer NULL
                CHECK (critical_resistance IS NULL OR critical_resistance >= 0),
            note text NOT NULL DEFAULT '',
            updated_at timestamp with time zone NOT NULL DEFAULT now(),
            updated_by varchar(64) NOT NULL DEFAULT '',
            PRIMARY KEY (map_id, object_id)
        );

        COMMENT ON TABLE public.gm_monster_spawn_edits IS
            'Per-published-spawn overrides keyed by (map_id, object_id). '
            'enabled=false removes the point, because monster_spawn_definitions '
            'rejects UPDATE and DELETE outright. NULL in any override column '
            'means "keep the published/formula value", never zero.';

        CREATE TABLE IF NOT EXISTS public.gm_monster_attributes (
            map_id smallint NOT NULL CHECK (map_id BETWEEN 0 AND 255),
            template_key varchar(128) NOT NULL CHECK (btrim(template_key) <> ''),
            display_name varchar(255) NOT NULL DEFAULT '',
            level integer NULL CHECK (level IS NULL OR level BETWEEN 1 AND 10000),
            current_health integer NULL CHECK (current_health IS NULL OR current_health >= 0),
            maximum_health integer NULL CHECK (maximum_health IS NULL OR maximum_health >= 1),
            physical_attack integer NULL CHECK (physical_attack IS NULL OR physical_attack >= 0),
            magic_attack integer NULL CHECK (magic_attack IS NULL OR magic_attack >= 0),
            physical_defense integer NULL CHECK (physical_defense IS NULL OR physical_defense >= 0),
            magic_defense integer NULL CHECK (magic_defense IS NULL OR magic_defense >= 0),
            hit integer NULL CHECK (hit IS NULL OR hit >= 0),
            dodge integer NULL CHECK (dodge IS NULL OR dodge >= 0),
            critical integer NULL CHECK (critical IS NULL OR critical >= 0),
            critical_resistance integer NULL
                CHECK (critical_resistance IS NULL OR critical_resistance >= 0),
            note text NOT NULL DEFAULT '',
            updated_at timestamp with time zone NOT NULL DEFAULT now(),
            updated_by varchar(64) NOT NULL DEFAULT '',
            PRIMARY KEY (map_id, template_key)
        );

        COMMENT ON TABLE public.gm_monster_attributes IS
            'Per map+template monster combat-rating overrides. Attack, defence, '
            'hit, dodge and critical are code formulas (MonsterCombatProfileCatalog) '
            'and only critical resistance was previously database-tunable, so this '
            'table is what makes the rest operator-configurable. It is applied as '
            'the LAST step of the resolve chain, after the passive-monster attack '
            'clamp. NULL means "use the formula value", never zero.';
        """);
}
