namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    /// <summary>
    /// Operator-authored NPCs and their multi-page dialogue trees.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The client renders NPC dialogue text from its own Lua, and the
    /// <c>Set_NpcFun_Text</c> dispatcher in the live
    /// <c>Localization/en_us/UI/XML/NpcFun/NpcFun.lua</c> is a plain
    /// <c>if/elseif</c> chain over the function flag with <b>no</b> <c>else</c>:
    /// a flag nobody handles renders nothing at all. So free text is only
    /// possible by shipping a client patch, and the tool generates it from these
    /// rows. The server's half is purely numeric: it decides which page and
    /// which result numbers go out.
    /// </para>
    /// <para>
    /// A page is one client window; a button carries the number the server
    /// receives when it is pressed and the page to show next
    /// (<c>next_page_index IS NULL</c> ends the conversation). Twelve buttons per
    /// page is the client's ceiling (<c>FirstWin_Button1..12</c>).
    /// </para>
    /// <para>
    /// <c>function_flag</c> defaults to <c>85</c>: the client's constant table
    /// uses 0..120 and 85/86 are the only two small values that are neither
    /// defined nor used by any published <c>npc_dialogue_profiles.dialog_index</c>.
    /// </para>
    /// </remarks>
    private static PostgresSchemaMigration CreateGmNpcContent() => new(
        "20261004_229_gm_npc_content",
        "Store operator-authored GM NPCs and their dialogue trees",
        """
        CREATE TABLE IF NOT EXISTS public.gm_npc_dialogues (
            dialogue_key varchar(120) PRIMARY KEY
                CHECK (btrim(dialogue_key) <> '' AND dialogue_key = btrim(dialogue_key)),
            display_name varchar(255) NOT NULL DEFAULT '',
            function_flag integer NOT NULL DEFAULT 85
                CHECK (function_flag BETWEEN 0 AND 67108864),
            entry_page integer NOT NULL DEFAULT 1 CHECK (entry_page >= 1),
            note text NOT NULL DEFAULT '',
            enabled boolean NOT NULL DEFAULT true,
            created_at timestamp with time zone NOT NULL DEFAULT now(),
            updated_at timestamp with time zone NOT NULL DEFAULT now(),
            updated_by varchar(64) NOT NULL DEFAULT ''
        );

        COMMENT ON TABLE public.gm_npc_dialogues IS
            'One operator-authored dialogue tree. function_flag is the client '
            'dispatch value (Type) the server sends; 85 is the reserved GM slot.';

        CREATE TABLE IF NOT EXISTS public.gm_npc_dialogue_pages (
            dialogue_key varchar(120) NOT NULL
                REFERENCES public.gm_npc_dialogues (dialogue_key) ON DELETE CASCADE,
            page_index integer NOT NULL CHECK (page_index BETWEEN 1 AND 100000),
            body_text text NOT NULL DEFAULT '',
            note text NOT NULL DEFAULT '',
            PRIMARY KEY (dialogue_key, page_index)
        );

        COMMENT ON TABLE public.gm_npc_dialogue_pages IS
            'One client window. body_text is the free text the generated client '
            'patch renders; the server never sends it.';

        CREATE TABLE IF NOT EXISTS public.gm_npc_dialogue_buttons (
            dialogue_key varchar(120) NOT NULL,
            page_index integer NOT NULL,
            slot smallint NOT NULL CHECK (slot BETWEEN 1 AND 12),
            label varchar(120) NOT NULL DEFAULT '',
            result_number integer NOT NULL
                CHECK (result_number BETWEEN 1 AND 2147483647),
            next_page_index integer NULL CHECK (
                next_page_index IS NULL OR next_page_index BETWEEN 1 AND 100000),
            PRIMARY KEY (dialogue_key, page_index, slot),
            FOREIGN KEY (dialogue_key, page_index)
                REFERENCES public.gm_npc_dialogue_pages (dialogue_key, page_index)
                ON DELETE CASCADE,
            UNIQUE (dialogue_key, result_number)
        );

        COMMENT ON TABLE public.gm_npc_dialogue_buttons IS
            'A clickable slot on a page. result_number is what the server '
            'receives; next_page_index NULL means the conversation ends. slot is '
            'capped at 12 because the client only has FirstWin_Button1..12.';

        CREATE TABLE IF NOT EXISTS public.gm_npc_spawns (
            id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            name varchar(120) NOT NULL
                CHECK (btrim(name) <> '' AND name = btrim(name)),
            map_id smallint NOT NULL CHECK (map_id BETWEEN 0 AND 255),
            npc_key varchar(96) NOT NULL CHECK (btrim(npc_key) <> ''),
            template_key varchar(255) NOT NULL CHECK (btrim(template_key) <> ''),
            dialogue_key varchar(120) NULL
                REFERENCES public.gm_npc_dialogues (dialogue_key) ON DELETE SET NULL,
            waypoint_id bigint NULL
                REFERENCES public.gm_waypoints (id) ON DELETE SET NULL,
            object_id bigint NOT NULL CHECK (object_id BETWEEN 1 AND 4294967295),
            appearance_type integer NOT NULL DEFAULT 0 CHECK (appearance_type >= 0),
            pos_x real NOT NULL CHECK (
                pos_x <> ALL (ARRAY['NaN'::real, 'Infinity'::real, '-Infinity'::real])
            ),
            pos_z real NOT NULL CHECK (
                pos_z <> ALL (ARRAY['NaN'::real, 'Infinity'::real, '-Infinity'::real])
            ),
            facing real NOT NULL DEFAULT 0 CHECK (
                facing <> ALL (ARRAY['NaN'::real, 'Infinity'::real, '-Infinity'::real])
            ),
            enabled boolean NOT NULL DEFAULT true,
            note text NOT NULL DEFAULT '',
            created_at timestamp with time zone NOT NULL DEFAULT now(),
            updated_at timestamp with time zone NOT NULL DEFAULT now(),
            updated_by varchar(64) NOT NULL DEFAULT ''
        );

        CREATE UNIQUE INDEX IF NOT EXISTS uq_gm_npc_spawns_name
            ON public.gm_npc_spawns (name);
        CREATE UNIQUE INDEX IF NOT EXISTS uq_gm_npc_spawns_map_object
            ON public.gm_npc_spawns (map_id, object_id);
        CREATE INDEX IF NOT EXISTS ix_gm_npc_spawns_map
            ON public.gm_npc_spawns (map_id);

        COMMENT ON TABLE public.gm_npc_spawns IS
            'Operator-authored NPC placements. The server synthesises the 10020 '
            'appearance packet from appearance_type + template_key + position '
            '(see PacketBuilder.WriteNpcWorldObjectAppearance), so no captured '
            'packet has to be stored; template_key must still name a template the '
            'published content already uses on this map, or the client cannot '
            'render the actor. The dialogue it opens comes from dialogue_key.';
        """);
}
