using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private void JoinCurrentWorld(
        bool worldReady)
    {
        if (_character is null)
        {
            throw new InvalidOperationException(
                "A character is required before joining a world.");
        }

        var accountId = _account?.Id ?? _character.AccountId;
        var admission = _session.GatewayWorldAdmission;
        if (admission is null)
        {
            _registry.JoinPlayerMap(
                _session,
                accountId,
                _character,
                worldReady);
        }
        else
        {
            _registry.JoinPlayerGatewayWorld(
                _session,
                accountId,
                _character,
                admission,
                worldReady);
        }

        _registry.UpdateSummonedPet(
            _session,
            _characterLoadSnapshot?.Pets.SingleOrDefault(
                static pet => pet.IsCarried &&
                    pet.IsSummoned &&
                    !pet.ContributesToCharacter));

        _registry.RegisterPveMonsterKillRewardPreparer(
            _session,
            PreparePveDerivedKillRewardAsync);
        _registry.RegisterQuestPlayerKillRecorder(_session, RecordQuestPlayerKillAsync);
        _registry.UpdateActivePetHealingRuntime(
            _session,
            _characterLoadSnapshot?.Pets ??
                Array.Empty<PetBootstrapSnapshot>());
        var activeTrainingPet = _registry.IsTrainingDummyCore(_character)
            ? _characterLoadSnapshot?.Pets.SingleOrDefault(
                static pet => pet.ContributesToCharacter)
            : null;
        if (activeTrainingPet is not null)
        {
            _registry.SetPetOwnerMergePresentation(
                _session,
                active: true,
                activeTrainingPet.Aptitude,
                activeTrainingPet.CompletedRebirths);
        }
    }

    private bool ValidateGatewayAdmission()
    {
        var admission = _session.GatewayWorldAdmission;
        if (admission is null)
        {
            return true;
        }

        if (_account is null ||
            admission.AccountId != _account.Id ||
            admission.RealmId != _processRealmId ||
            !_registry.AcceptsGatewayAdmission(admission))
        {
            return false;
        }

        if (_character is null)
        {
            return admission.CharacterId == 0;
        }

        return _character.RealmId == _processRealmId &&
            admission.CharacterId == _character.Id &&
            admission.MapId.TryGetLegacyValue(out var mapId) &&
            mapId == _character.CurrentMap;
    }
}
