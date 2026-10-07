using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.Application.World;

/// <summary>
/// The combat-rating overrides an operator configured, per field.
/// </summary>
/// <remarks>
/// Every field is nullable and <c>NULL</c> means "keep whatever the formula
/// produced" - never zero. Two layers exist (per map+template and per
/// individual spawn point) and they are overlaid field by field, so the more
/// specific layer only has to name the fields it actually changes.
/// </remarks>
internal sealed record MonsterAttributeOverride(
    int? Level = null,
    int? CurrentHealth = null,
    int? MaximumHealth = null,
    int? PhysicalAttack = null,
    int? MagicAttack = null,
    int? PhysicalDefense = null,
    int? MagicDefense = null,
    int? Hit = null,
    int? Dodge = null,
    int? Critical = null,
    int? CriticalResistance = null)
{
    public static readonly MonsterAttributeOverride None = new();

    public bool IsEmpty =>
        Level is null && CurrentHealth is null && MaximumHealth is null &&
        PhysicalAttack is null && MagicAttack is null &&
        PhysicalDefense is null && MagicDefense is null &&
        Hit is null && Dodge is null && Critical is null &&
        CriticalResistance is null;

    /// <summary>This layer first, then <paramref name="more"/> wherever it speaks.</summary>
    public MonsterAttributeOverride Overlay(MonsterAttributeOverride more)
    {
        ArgumentNullException.ThrowIfNull(more);
        return new MonsterAttributeOverride(
            Level: more.Level ?? Level,
            CurrentHealth: more.CurrentHealth ?? CurrentHealth,
            MaximumHealth: more.MaximumHealth ?? MaximumHealth,
            PhysicalAttack: more.PhysicalAttack ?? PhysicalAttack,
            MagicAttack: more.MagicAttack ?? MagicAttack,
            PhysicalDefense: more.PhysicalDefense ?? PhysicalDefense,
            MagicDefense: more.MagicDefense ?? MagicDefense,
            Hit: more.Hit ?? Hit,
            Dodge: more.Dodge ?? Dodge,
            Critical: more.Critical ?? Critical,
            CriticalResistance: more.CriticalResistance ?? CriticalResistance);
    }
}

/// <summary>An override for one published spawn point, keyed by (map, object id).</summary>
internal sealed record MonsterSpawnEdit(
    bool Enabled,
    float? X,
    float? Z,
    float? Facing,
    MonsterAttributeOverride Attributes);

/// <summary>A brand-new spawn point an operator authored.</summary>
internal sealed record MonsterSpawnOverrideRow(
    long Id,
    string Name,
    long? WaypointId,
    short MapId,
    string SceneKey,
    string TemplateKey,
    string DisplayName,
    uint ObjectId,
    float X,
    float Z,
    byte[] Packet,
    bool Enabled);

/// <summary>
/// A validated, frozen read of the three GM monster tables.
/// </summary>
/// <remarks>
/// Built by <c>PostgresMonsterOverrideSnapshotReader</c> at startup. Validation
/// happens in this constructor so a malformed row fails the load instead of
/// reaching a spawn packet; the server only ever sees an
/// <see cref="MonsterOverrideSnapshot"/> that passed it.
/// </remarks>
internal sealed class MonsterOverrideSnapshot
{
    public static readonly MonsterOverrideSnapshot Empty = new([], [], []);

    /// <summary>A bound so a runaway GM table cannot exhaust a map entry.</summary>
    public const int MaximumSpawns = 20_000;

    private MonsterOverrideSnapshot(
        IReadOnlyList<MonsterSpawnOverrideRow> spawns,
        IReadOnlyList<((short MapId, uint ObjectId) Key, MonsterSpawnEdit Edit)> edits,
        IReadOnlyList<((short MapId, string TemplateKey) Key, MonsterAttributeOverride Override)>
            templateAttributes)
    {
        Spawns = spawns;
        Edits = edits;
        TemplateAttributes = templateAttributes;
        EditsByKey = edits.ToDictionary(static row => row.Key, static row => row.Edit);
        TemplateAttributesByKey = templateAttributes
            .ToDictionary(static row => row.Key, static row => row.Override);
    }

    /// <summary>Validates the rows, then freezes them. Throws on malformed input.</summary>
    public static MonsterOverrideSnapshot Create(
        IReadOnlyList<MonsterSpawnOverrideRow> spawns,
        IReadOnlyList<((short MapId, uint ObjectId) Key, MonsterSpawnEdit Edit)> edits,
        IReadOnlyList<((short MapId, string TemplateKey) Key, MonsterAttributeOverride Override)>
            templateAttributes)
    {
        ArgumentNullException.ThrowIfNull(spawns);
        ArgumentNullException.ThrowIfNull(edits);
        ArgumentNullException.ThrowIfNull(templateAttributes);
        if (spawns.Count > MaximumSpawns)
        {
            throw new InvalidDataException(
                $"The GM monster override table holds {spawns.Count} spawns, " +
                $"above the supported maximum of {MaximumSpawns}.");
        }

        var keys = new HashSet<(short, uint)>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var spawn in spawns)
        {
            if (spawn.MapId is < 0 or > 255 ||
                spawn.ObjectId == 0 ||
                string.IsNullOrWhiteSpace(spawn.TemplateKey) ||
                string.IsNullOrWhiteSpace(spawn.SceneKey) ||
                !float.IsFinite(spawn.X) ||
                !float.IsFinite(spawn.Z) ||
                spawn.Packet.Length is < 108 or > 1200 ||
                !keys.Add((spawn.MapId, spawn.ObjectId)) ||
                !names.Add(spawn.Name))
            {
                throw new InvalidDataException(
                    $"The GM monster spawn '{spawn.Name}' is malformed or " +
                    "duplicates another row.");
            }
        }

        foreach (var ((mapId, templateKey), _) in templateAttributes)
        {
            if (mapId is < 0 or > 255 || string.IsNullOrWhiteSpace(templateKey))
            {
                throw new InvalidDataException(
                    "A GM monster attribute override names an invalid map or template.");
            }
        }

        return new MonsterOverrideSnapshot(
            spawns.ToArray(),
            edits.ToArray(),
            templateAttributes.ToArray());
    }

    public IReadOnlyList<MonsterSpawnOverrideRow> Spawns { get; }

    public IReadOnlyList<((short MapId, uint ObjectId) Key, MonsterSpawnEdit Edit)> Edits { get; }

    public IReadOnlyList<((short MapId, string TemplateKey) Key, MonsterAttributeOverride Override)>
        TemplateAttributes { get; }

    private Dictionary<(short, uint), MonsterSpawnEdit> EditsByKey { get; }

    private Dictionary<(short, string), MonsterAttributeOverride> TemplateAttributesByKey { get; }

    public bool IsEmpty =>
        Spawns.Count == 0 && Edits.Count == 0 && TemplateAttributes.Count == 0;

    public bool TryGetEdit(short mapId, uint objectId, out MonsterSpawnEdit edit) =>
        EditsByKey.TryGetValue((mapId, objectId), out edit!);

    public bool TryGetTemplateAttributes(
        short mapId,
        string templateKey,
        out MonsterAttributeOverride overrides) =>
        TemplateAttributesByKey.TryGetValue((mapId, templateKey), out overrides!);
}

/// <summary>
/// Process-wide handle on the GM monster overrides, mirroring
/// <c>MonsterLootContentCatalog</c>.
/// </summary>
/// <remarks>
/// <see cref="Current"/> never throws: packet builders and the combat resolver
/// also run in database-less tooling and checks, where an unconfigured override
/// set is simply empty. Callers that need the real rows read them from the
/// snapshot the server installed at startup.
/// </remarks>
internal static class MonsterOverrideCatalog
{
    private static MonsterOverrideSnapshot _current = MonsterOverrideSnapshot.Empty;

    public static MonsterOverrideSnapshot Current => Volatile.Read(ref _current);

    public static void Install(MonsterOverrideSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Volatile.Write(ref _current, snapshot);
    }
}
