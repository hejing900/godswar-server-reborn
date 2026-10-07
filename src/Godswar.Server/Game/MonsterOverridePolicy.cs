using System.Buffers.Binary;
using Godswar.Server.Application.World;
using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.Game;

/// <summary>
/// Merges the operator's GM monster rows into the published spawn list, and
/// answers the combat-rating overrides the resolver applies.
/// </summary>
/// <remarks>
/// <para>
/// The published content is immutable and hashed, so overrides are applied here,
/// on the copy a map entry builds, and never written back. Three shapes:
/// disabling a published point, editing one, and appending a new one.
/// </para>
/// <para>
/// Packet offsets are the captured appearance layout:
/// <c>12</c> level/tier, <c>20</c> current HP, <c>24</c> maximum HP, <c>28</c> x,
/// <c>36</c> z, <c>40</c> facing, <c>44</c> the ASCII template key. Everything
/// after 44 is the un-reversed appearance data, which is why a new point must be
/// based on a captured packet of the same template.
/// </para>
/// </remarks>
internal static class MonsterOverridePolicy
{
    private const int TierOffset = 12;
    private const int CurrentHealthOffset = 20;
    private const int MaximumHealthOffset = 24;
    private const int XOffset = 28;
    private const int ZOffset = 36;
    private const int FacingOffset = 40;

    /// <summary>The same bounds the parser and the spawn table enforce.</summary>
    private const int MinimumPacketLength = 108;
    private const int MaximumPacketLength = 1200;

    /// <summary>
    /// The spawn list one map entry should use: published rows with the
    /// operator's edits applied, then the operator's new points.
    /// </summary>
    /// <remarks>
    /// The caller still runs every returned row through the ordinary validation
    /// and filters (reserved player ids, grid bounds, NPC collisions), so a GM
    /// row cannot bypass a safety check the published rows go through.
    /// </remarks>
    public static List<CapturedMonsterSpawn> Apply(
        IReadOnlyList<CapturedMonsterSpawn> published,
        short mapId)
    {
        ArgumentNullException.ThrowIfNull(published);
        var overrides = MonsterOverrideCatalog.Current;
        var result = new List<CapturedMonsterSpawn>(published.Count);
        foreach (var spawn in published)
        {
            if (!overrides.TryGetEdit(spawn.MapId, spawn.ObjectId, out var edit))
            {
                result.Add(spawn);
                continue;
            }

            if (!edit.Enabled)
            {
                Console.WriteLine(
                    $"[mob] GM override removed spawn map={spawn.MapId} " +
                    $"object={spawn.ObjectId} template={spawn.TemplateKey}");
                continue;
            }

            var edited = ApplyEdit(spawn, edit);
            if (edited is null)
            {
                result.Add(spawn);
                continue;
            }

            result.Add(edited);
        }

        if (overrides.Spawns.Count > 0)
        {
            AppendAuthoredSpawns(result, published, overrides, mapId);
        }

        return result;
    }

    /// <summary>
    /// The combat-rating overrides for one spawn: the per-point layer over the
    /// per-template layer, so a point only names what it changes.
    /// </summary>
    public static bool TryResolveAttributes(
        short mapId,
        uint objectId,
        string templateKey,
        out MonsterAttributeOverride overrides)
    {
        var snapshot = MonsterOverrideCatalog.Current;
        var hasTemplate = snapshot.TryGetTemplateAttributes(
            mapId,
            templateKey,
            out var template);
        var hasEdit = snapshot.TryGetEdit(mapId, objectId, out var edit);
        if (!hasTemplate && !hasEdit)
        {
            overrides = MonsterAttributeOverride.None;
            return false;
        }

        overrides = (hasTemplate ? template : MonsterAttributeOverride.None)
            .Overlay(hasEdit ? edit.Attributes : MonsterAttributeOverride.None);
        return !overrides.IsEmpty;
    }

    private static void AppendAuthoredSpawns(
        List<CapturedMonsterSpawn> result,
        IReadOnlyList<CapturedMonsterSpawn> published,
        MonsterOverrideSnapshot overrides,
        short mapId)
    {
        // A new point reuses an existing template's appearance bytes, so the
        // template has to exist on this map already. Without that check a typo
        // would produce a monster the client cannot render and the combat
        // resolver would treat as a boss.
        var knownTemplates = published
            .Select(static spawn => spawn.TemplateKey)
            .ToHashSet(StringComparer.Ordinal);
        var usedObjectIds = published
            .Select(static spawn => spawn.ObjectId)
            .ToHashSet();
        foreach (var row in overrides.Spawns)
        {
            if (row.MapId != mapId || !row.Enabled)
            {
                continue;
            }

            if (!knownTemplates.Contains(row.TemplateKey))
            {
                Console.WriteLine(
                    $"[mob] GM spawn skipped: template not published on this map " +
                    $"map={row.MapId} template={row.TemplateKey} name={row.Name}");
                continue;
            }

            if (!usedObjectIds.Add(row.ObjectId))
            {
                Console.WriteLine(
                    $"[mob] GM spawn skipped: object id already used " +
                    $"map={row.MapId} object={row.ObjectId} name={row.Name}");
                continue;
            }

            result.Add(new CapturedMonsterSpawn(
                row.MapId,
                row.SceneKey,
                row.TemplateKey,
                string.IsNullOrEmpty(row.DisplayName) ? row.TemplateKey : row.DisplayName,
                row.ObjectId,
                row.X,
                row.Z,
                row.Packet.ToArray()));
            Console.WriteLine(
                $"[mob] GM spawn applied map={row.MapId} object={row.ObjectId} " +
                $"template={row.TemplateKey} x={row.X:F1} z={row.Z:F1} name={row.Name}");
        }
    }

    private static CapturedMonsterSpawn? ApplyEdit(
        CapturedMonsterSpawn spawn,
        MonsterSpawnEdit edit)
    {
        if (spawn.Packet.Length is < MinimumPacketLength or > MaximumPacketLength)
        {
            Console.WriteLine(
                $"[mob] GM edit skipped: packet length out of range " +
                $"map={spawn.MapId} object={spawn.ObjectId} length={spawn.Packet.Length}");
            return null;
        }

        var packet = spawn.Packet.ToArray();
        var x = edit.X ?? BinaryPrimitives.ReadSingleLittleEndian(packet.AsSpan(XOffset, 4));
        var z = edit.Z ?? BinaryPrimitives.ReadSingleLittleEndian(packet.AsSpan(ZOffset, 4));
        var facing = edit.Facing ??
            BinaryPrimitives.ReadSingleLittleEndian(packet.AsSpan(FacingOffset, 4));
        var level = edit.Attributes.Level is { } configuredLevel
            ? checked((uint)configuredLevel)
            : BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(TierOffset, 4));
        var maximumHealth = edit.Attributes.MaximumHealth is { } configuredMaximum
            ? checked((uint)configuredMaximum)
            : BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(MaximumHealthOffset, 4));
        var currentHealth = edit.Attributes.CurrentHealth is { } configuredCurrent
            ? Math.Min(checked((uint)configuredCurrent), maximumHealth)
            : Math.Min(
                BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(CurrentHealthOffset, 4)),
                maximumHealth);

        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(TierOffset, 4), level);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(MaximumHealthOffset, 4),
            maximumHealth);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(CurrentHealthOffset, 4),
            currentHealth);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(XOffset, 4), x);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(ZOffset, 4), z);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(FacingOffset, 4), facing);

        return spawn with { X = x, Z = z, Packet = packet };
    }
}
