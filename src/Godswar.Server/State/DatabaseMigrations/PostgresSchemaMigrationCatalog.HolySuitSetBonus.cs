namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    /// <summary>
    /// Turns the existing Holy Suit SET bonus into the client's staged,
    /// per-point, capped form.
    /// </summary>
    /// <remarks>
    /// This changes the existing set-bonus content only. It stays separate from
    /// the per-equipment percentage bonus, which scales each equipped item's own
    /// base stat by <c>holy_suit_progression_points(holy_suit_code)</c> percent
    /// and is truncated per item; the two are never combined.
    ///
    /// <c>per_point</c> is what one accumulated Holy Suit point adds once
    /// <c>unlock_points</c> is reached; <c>maximum</c> is the published ceiling
    /// the growth is clamped to. The pre-existing <c>effect_value</c> column is
    /// left untouched, so any reader that still needs the original
    /// <c>EquipEffectSuit.xml</c> number keeps working.
    /// </remarks>
    // Keep sealed content definitions immutable; the mutable template table
    // supplies the independent set-bonus balance values.
    // Append this new migration after the already published history. Inserting
    // it among September IDs breaks the runner's exact-prefix upgrade check.
    private static PostgresSchemaMigration CreateHolySuitSetBonus() => new(
        "20261007_233_holy_suit_set_bonus",
        "Holy Suit SET bonus: staged unlock, per-point growth and published ceiling",
        """
        ALTER TABLE public.holy_suit_effect_templates
            ADD COLUMN IF NOT EXISTS per_point numeric,
            ADD COLUMN IF NOT EXISTS maximum integer;

        COMMENT ON COLUMN public.holy_suit_effect_templates.per_point IS
            'Amount one accumulated Holy Suit point adds to this attribute once unlock_points is reached.';

        COMMENT ON COLUMN public.holy_suit_effect_templates.maximum IS
            'Published ceiling for this attribute; the per-point growth is clamped to it.';

        UPDATE public.holy_suit_effect_templates AS template
        SET per_point = values_row.per_point,
            maximum = values_row.maximum
        FROM (
            VALUES
                ('MaxHPD', 30.29::numeric, 10000),
                ('MaxMPD', 1.52::numeric, 500),
                ('Defence', 2.4::numeric, 800),
                ('MagicRec', 1.8182::numeric, 600),
                ('Hit', 0.4545::numeric, 150),
                ('Miss', 0.3636::numeric, 120),
                ('InjureImbibe', 1.52::numeric, 500),
                ('Attack', 3.02::numeric, 1000),
                ('MagicAk', 2.7273::numeric, 900)
        ) AS values_row(effect_key, per_point, maximum)
        WHERE template.effect_key = values_row.effect_key;

        """);
}
