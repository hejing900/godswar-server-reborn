using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private void InstallUpdatedCharacter(GameCharacter updated)
    {
        ArgumentNullException.ThrowIfNull(updated);
        if (updated.RealmId != _processRealmId)
        {
            throw new InvalidOperationException(
                "The refreshed character belongs to another realm.");
        }

        var current = _character;
        if (current is not null && current.Id == updated.Id)
        {
            if (current.RealmId != updated.RealmId)
            {
                throw new InvalidOperationException(
                    "A character refresh cannot change realms.");
            }

            updated.CurrentMap = current.CurrentMap;
            updated.PositionX = current.PositionX;
            updated.PositionZ = current.PositionZ;
            updated.PositionRevision = Math.Max(
                updated.PositionRevision,
                current.PositionRevision);
            InstallUpdatedCharacterVitals(current, updated);
            RestoreCalculatedStatsProjection(updated);
            updated.CheckpointOwnerId =
                current.CheckpointOwnerId;
            updated.CheckpointOwnerGeneration =
                current.CheckpointOwnerGeneration;
            updated.FashionHidden =
                ResolveFashionHiddenAfterEquipmentChange(
                    current,
                    updated);
            updated.EquipmentEffectsVisible =
                current.EquipmentEffectsVisible;
            // Quest progress belongs to the session's live character. A store
            // round-trip that was built for an equipment change must not replace it
            // with an empty list.
            updated.Quests = current.Quests;
            updated.QuestCompletedIds = current.QuestCompletedIds;
            updated.QuestDailyCompletions = current.QuestDailyCompletions;
        }

        _character = updated;
    }

    /// <summary>
    /// Re-applies the runtime stat projection to a freshly installed character.
    /// </summary>
    /// <remarks>
    /// A reload (login, equipment, and every pet summon/recall) hydrates a clean
    /// database projection, and the altar and title bonuses are deliberately kept
    /// out of that reusable projection so they cannot compound. The live fields
    /// they belong to are therefore rebuilt here, otherwise a pet transition keeps
    /// the pet's part of the change and silently drops every altar attribute the
    /// member had - HP, MP, attack, hit, dodge and absorption alike. The current
    /// vitals are carried over rather than clamped against the clean base.
    /// <para>
    /// The revision is restored afterwards: this only re-attaches a bonus that
    /// belongs to the state the caller just loaded, and the enter path compares the
    /// live revision against the checkpoint it claimed. Bumping it here made every
    /// login fail with <c>checkpoint_revision_mismatch</c>.
    /// </para>
    /// </remarks>
    private static void RestoreCalculatedStatsProjection(GameCharacter updated)
    {
        if (updated.CalculatedStats is not { } baseStats)
        {
            return;
        }

        var vitalsRevision = updated.VitalsRevision;
        CharacterCalculatedStatsProjectionApplier.Apply(
            updated,
            baseStats,
            CharacterHealthProjectionMode.PreserveAbsolute);
        updated.VitalsRevision = vitalsRevision;
    }

    private static void InstallUpdatedCharacterVitals(
        GameCharacter current,
        GameCharacter updated)
    {
        lock (current.VitalsSync)
        {
            var currentHp = current.CurrentHp;
            var currentMp = current.CurrentMp;
            if (current.CalculatedStats is not null &&
                updated.CalculatedStats is null)
            {
                // Legacy bag and wallet writes reload only base character
                // columns. Once a session owns a calculated projection, that
                // base-only result has no authority to replace its maxima or
                // live vitals.
                updated.MaxHp = current.MaxHp;
                updated.MaxMp = current.MaxMp;
                updated.CurrentHp = currentHp;
                updated.CurrentMp = currentMp;
                updated.CalculatedStats = current.CalculatedStats;
                updated.VitalsRevision = current.VitalsRevision;
                return;
            }

            updated.CurrentHp = Math.Clamp(
                currentHp,
                0,
                Math.Max(1, updated.MaxHp));
            updated.CurrentMp = Math.Clamp(
                currentMp,
                0,
                Math.Max(0, updated.MaxMp));
            updated.VitalsRevision = Math.Max(
                updated.VitalsRevision,
                current.VitalsRevision);
            if (updated.CurrentHp != currentHp ||
                updated.CurrentMp != currentMp)
            {
                updated.MarkVitalsChanged();
            }
        }
    }
}
