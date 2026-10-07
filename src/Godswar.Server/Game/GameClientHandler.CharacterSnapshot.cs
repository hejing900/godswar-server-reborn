using Godswar.Server.Application.Characters;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task<bool> RefreshCharacterSnapshotAsync(
        string phase,
        CancellationToken cancellationToken)
    {
        if (_account is null)
        {
            _characterLoadSnapshot = null;
            _character = null;
            _characterSnapshotLoaded = false;
            _characterSnapshotBootstrapPending = false;
            return false;
        }

        var hadOwnership =
            TryCaptureCurrentPlayerOwnership(out var ownership);
        try
        {
            var accountSnapshot = await _characterSnapshots.ReadAsync(
                _account.Id,
                _processRealmId,
                cancellationToken);
            if (accountSnapshot.RealmId != _processRealmId)
            {
                throw new CharacterSnapshotUnavailableException(
                    CharacterSnapshotFailureReason.InvalidData,
                    "The character snapshot belongs to another realm.");
            }
            if (hadOwnership &&
                !RevalidateCurrentPlayerOwnership(ownership))
            {
                return false;
            }

            var hydrated =
                CharacterLoadSnapshotHydrator.Hydrate(accountSnapshot);
            if (hydrated is not null)
            {
                InstallUpdatedCharacter(hydrated.Character);
                hydrated = hydrated with
                {
                    Character = _character ??
                        throw new InvalidDataException(
                            "The hydrated character was not installed.")
                };
            }
            else
            {
                _character = null;
            }

            _characterLoadSnapshot = hydrated;
            _characterSnapshotLoaded = true;
            _characterSnapshotBootstrapPending = hydrated is not null;
            if (hydrated is not null)
            {
                // The per-day quest counters live in their own table, so they are
                // read here - before any accept gate can be asked - rather than
                // with the character row. This is what makes a restart not reset
                // them: the gate reads what the last session wrote.
                await LoadQuestDailyCompletionsAsync(cancellationToken);
                _registry.UpdateActivePetHealingRuntime(
                    _session,
                    hydrated.Pets);
            }
            ResetPlayerMovementEcs();
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CharacterSnapshotUnavailableException ex)
        {
            RejectCharacterSnapshot(
                phase,
                CharacterSnapshotMetrics.ReasonCode(ex.Reason));
            return false;
        }
        catch (Exception ex)
        {
            RejectCharacterSnapshot(
                phase,
                $"unexpected_{ex.GetType().Name}");
            return false;
        }
    }

    private void RejectCharacterSnapshot(
        string phase,
        string reason)
    {
        _characterLoadSnapshot = null;
        _character = null;
        _characterSnapshotLoaded = false;
        _characterSnapshotBootstrapPending = false;
        Console.Error.WriteLine(
            $"[character-snapshot] rejected phase={phase} reason={reason}");
        _session.Disconnect();
    }
}
