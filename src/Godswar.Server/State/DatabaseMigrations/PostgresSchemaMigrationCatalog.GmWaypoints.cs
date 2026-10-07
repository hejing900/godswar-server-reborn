namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    /// <summary>
    /// Operator-authored map markers the GM tool stores, one row per point.
    /// </summary>
    /// <remarks>
    /// The GM tool (a separate process) reads and writes this table directly, so
    /// it must exist in the schema the server owns and migrates. No foreign key
    /// to <c>map_templates</c> is declared on purpose: migrations also run before
    /// the first world-content publication, which is why
    /// <c>monster_combat_balance</c> uses the same plain range check. Coordinates
    /// reject the three non-finite floats exactly like the immutable spawn
    /// tables, because a marker is copied into a spawn packet verbatim.
    /// </remarks>
    private static PostgresSchemaMigration CreateGmWaypoints() => new(
        "20261004_227_gm_waypoints",
        "Store operator-authored GM map markers",
        """
        CREATE TABLE IF NOT EXISTS public.gm_waypoints (
            id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            name varchar(120) NOT NULL
                CHECK (btrim(name) <> '' AND name = btrim(name)),
            note text NOT NULL DEFAULT '',
            character_name varchar(64) NOT NULL DEFAULT '',
            map_id smallint NOT NULL
                CHECK (map_id BETWEEN 0 AND 255),
            pos_x real NOT NULL
                CHECK (
                    pos_x <> ALL (
                        ARRAY[
                            'NaN'::real,
                            'Infinity'::real,
                            '-Infinity'::real
                        ]
                    )
                ),
            pos_z real NOT NULL
                CHECK (
                    pos_z <> ALL (
                        ARRAY[
                            'NaN'::real,
                            'Infinity'::real,
                            '-Infinity'::real
                        ]
                    )
                ),
            facing real NOT NULL DEFAULT 0
                CHECK (
                    facing <> ALL (
                        ARRAY[
                            'NaN'::real,
                            'Infinity'::real,
                            '-Infinity'::real
                        ]
                    )
                ),
            source varchar(16) NOT NULL DEFAULT 'character'
                CHECK (source IN ('character', 'manual')),
            created_at timestamp with time zone NOT NULL DEFAULT now(),
            updated_at timestamp with time zone NOT NULL DEFAULT now(),
            updated_by varchar(64) NOT NULL DEFAULT ''
        );

        CREATE UNIQUE INDEX IF NOT EXISTS uq_gm_waypoints_name
            ON public.gm_waypoints (name);
        CREATE INDEX IF NOT EXISTS ix_gm_waypoints_map
            ON public.gm_waypoints (map_id, id);

        COMMENT ON TABLE public.gm_waypoints IS
            'Operator-authored map markers written by tools/Godswar.GmTool. '
            'A marker is only a saved map/position pair: nothing here changes '
            'runtime content until a spawn or NPC entry references it. '
            'Independent of every immutable content revision hash.';
        COMMENT ON COLUMN public.gm_waypoints.character_name IS
            'The character the point was captured from, empty for a manual entry.';
        COMMENT ON COLUMN public.gm_waypoints.source IS
            'character = captured from a character position, manual = typed in.';
        """);
}
