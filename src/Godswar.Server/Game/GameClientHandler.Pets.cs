using System.Buffers.Binary;
using Godswar.Server.Application.Pets;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private const int PetPresenceRequestLength = 8;

    private async Task HandlePetPresenceRequestAsync(
        GamePacket packet,
        PetPresenceOperation operation,
        CancellationToken cancellationToken)
    {
        if (!TryReadPetId(packet, out var petId))
        {
            Console.WriteLine(
                $"[pet] rejected malformed presence operation=" +
                $"{operation} length={packet.Length}");
            return;
        }

        PetDurableReceipt? receipt;
        if (packet.ClientOperationId is { } operationId &&
            petId != 0)
        {
            receipt = await HandleDurablePetPresenceAsync(
                PetCommandOperationIdentity.SecureClient(operationId),
                petId,
                operation,
                cancellationToken);
        }
        else
        {
            if (petId == 0 ||
                !AllowLegacyPlayerMutationFallback("pet_presence"))
            {
                return;
            }

            receipt = await HandleDurablePetPresenceAsync(
                PetCommandOperationIdentity.RawLocalServer(
                    Guid.NewGuid(),
                    _commandConnectionId),
                petId,
                operation,
                cancellationToken);
        }

        // Call Out and Recall add or remove the one pet that supplies the
        // summoned skill passives, so the owner's calculated stats are
        // republished here. Take is refreshed by its own projection, which is
        // the only place that can compare the previously carried pet, and the
        // login restore calls the durable transition directly and publishes
        // the complete status itself.
        if (receipt is { Succeeded: true } &&
            operation is PetPresenceOperation.CallOut or
                PetPresenceOperation.Recall)
        {
            await SendPetSkillOwnerStatRefreshAsync(
                "PetPresenceSkillSource",
                cancellationToken);
        }
    }

    private async Task BroadcastPetWorldPresenceTransitionAsync(
        PetBootstrapSnapshot? previousCarriedPet,
        PetBootstrapSnapshot? currentCarriedPet,
        CancellationToken cancellationToken)
    {
        if (_character is null ||
            !_worldPresenceAnnounced ||
            !RevalidateCurrentWorldEffectOwnership(
                "pet_world_presence"))
        {
            return;
        }

        var previousWorldPet = previousCarriedPet is
            {
                IsCarried: true,
                IsSummoned: true,
                ContributesToCharacter: false
            }
            ? previousCarriedPet
            : null;
        var currentWorldPet = currentCarriedPet is
            {
                IsCarried: true,
                IsSummoned: true,
                ContributesToCharacter: false
            }
            ? currentCarriedPet
            : null;

        if (previousWorldPet is not null &&
            (currentWorldPet is null ||
             previousWorldPet.PetId != currentWorldPet.PetId))
        {
            // The companion is not a world object of its own. Measured
            // 2026-10-02: removing the id the presence frame carries (0x2808 +4)
            // took nothing away - the other clients kept the companion - and the
            // client's receive tables carry no case for 10248 at all (0x4ee900
            // covers 10001..10199, 0x4eea40 covers 10328..10358, and 10245 is the
            // single exception between them). The pet rides on the owner's
            // presentation, which is why it follows without the server ever
            // sending its position. What retires it is therefore rebuilding the
            // owner's presentation: the same announce the equipment path uses,
            // which retires the old model (0x2728) before rebuilding it (0x2725),
            // and the rebuilt model carries no companion. This runs before the
            // 0x2808 below, which re-attaches the new pet when one is called out.
            await BroadcastEquipmentRefreshAsync(
                currentWorldPet is null ? "pet-recall" : "pet-swap",
                cancellationToken);
            await _registry.BroadcastToCurrentWorldInstanceAsync(
                _session,
                PacketBuilder.PetOperationResult(
                    checked((uint)previousWorldPet.PetId),
                    PetOperationResultCode.RecallSucceeded),
                cancellationToken,
                includeRoutingSession: false,
                label: "PetWorldRecall");
        }

        if (currentWorldPet is not null &&
            (previousWorldPet is null ||
             previousWorldPet != currentWorldPet))
        {
            await _registry.BroadcastToCurrentWorldInstanceAsync(
                _session,
                PacketBuilder.PetWorldPresence(
                    currentWorldPet,
                    CurrentPlayerObjectId),
                cancellationToken,
                includeRoutingSession: false,
                label: "PetWorldCallOut");
        }
    }

    private async Task RestorePersistedPetPresenceAsync(
        CancellationToken cancellationToken)
    {
        if (_account is null || _character is null)
        {
            return;
        }

        IReadOnlyList<PetBootstrapSnapshot> pets;
        try
        {
            pets = (await _ownedPetSnapshots.ReadOwnedPetsAsync(
                _account.Id,
                _character.Id,
                cancellationToken))
                .Select(CharacterLoadSnapshotHydrator.MapPet)
                .ToArray();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine(
                $"[pet] presence restore skipped character={_character.Name} error={ex.GetType().Name}");
            return;
        }

        await RestorePetPresenceAsync(
            pets,
            summonCarriedPet: false,
            cancellationToken);
    }

    private async Task RestorePetPresenceAsync(
        IReadOnlyList<PetBootstrapSnapshot> pets,
        bool summonCarriedPet,
        CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            return;
        }

        var carried = pets.SingleOrDefault(static pet => pet.IsCarried);
        if (carried is null)
        {
            return;
        }

        var petId = checked((uint)carried.PetId);
        if (carried.ContributesToCharacter)
        {
            // A merged pet remains logically carried and summoned in durable
            // state, but the native unite presentation owns its model. Never
            // replay Call Out or the companion will appear beside its owner.
            await PublishPetOwnerMergeStartedAsync(
                carried,
                cancellationToken);
            return;
        }

        var callOutResultAlreadySent = false;
        if (!carried.IsSummoned && summonCarriedPet)
        {
            // Login policy keeps the selected companion visible. Persist the
            // transition through the ordinary authoritative command path so
            // the state, command receipt, and pet-operation audit all agree.
            // Reuse one operation identity for this session's login retry.
            var identity =
                PetCommandOperationIdentity.ServerSessionLifecycle(
                _loginPetCallOutOperationId ??= Guid.NewGuid(),
                _commandConnectionId);
            var receipt = await HandleDurablePetPresenceAsync(
                identity,
                carried.PetId,
                PetPresenceOperation.CallOut,
                cancellationToken);
            var refreshed = _characterLoadSnapshot?.Pets.SingleOrDefault(
                candidate => candidate.PetId == carried.PetId);
            if (receipt is
                    {
                        Status: PetDurableReceiptStatus.PetCareExhausted,
                        IsCarried: true,
                        IsSummoned: false
                    } &&
                refreshed is
                    {
                        IsCarried: true,
                        IsSummoned: false,
                        ContributesToCharacter: false
                    } &&
                !PetCareDecayPolicy.CanBeSummoned(
                    refreshed.Satiety,
                    refreshed.RemainingLifetime))
            {
                // An exhausted pet is a valid recalled selection. The durable
                // command already published Call Out failure; restore Take
                // below and allow the rest of the login bootstrap to finish.
                carried = refreshed;
                Console.WriteLine(
                    $"[pet] login call-out skipped character={_character.Name} " +
                    $"pet={petId} reason=care_exhausted " +
                    $"satiety={carried.Satiety} lifetime={carried.RemainingLifetime}");
            }
            else if (receipt is not
                    {
                        Succeeded: true,
                        IsCarried: true,
                        IsSummoned: true
                    } ||
                refreshed is not
                    {
                        IsCarried: true,
                        IsSummoned: true
                    })
            {
                throw new InvalidDataException(
                    "The carried pet could not be called out during login.");
            }

            else
            {
                carried = refreshed;
                callOutResultAlreadySent = true;
            }
        }
        if (carried.IsSummoned)
        {
            // The owned-pet bootstrap restores the durable selection, but the
            // stock client does not recreate the companion model from 10248
            // alone after a fresh login. Replay the same successful Call Out
            // presentation result used by a live summon first, then bind the
            // selected pet to its local world owner. Neither packet mutates
            // authoritative state.
            if (!callOutResultAlreadySent)
            {
                await _session.SendAsync(
                    PacketBuilder.PetOperationResult(
                        petId,
                        PetOperationResultCode.CallOutSucceeded),
                    cancellationToken,
                    "PetCallOutRestore");
            }
            await _session.SendAsync(
                PacketBuilder.PetWorldPresence(
                    petId,
                    LocalPlayerObjectId),
                cancellationToken,
                "PetWorldPresenceRestore");
        }
        else
        {
            await _session.SendAsync(
                PacketBuilder.PetOperationResult(
                    petId,
                    PetOperationResultCode.TakeSucceeded),
                cancellationToken,
                "PetTakeRestore");
        }

        StartPetOwnerMergeEnergyRecharge();
        StartPetCareDecay();

        Console.WriteLine(
            $"[pet] presence restored character={_character.Name} pet={petId} summoned={carried.IsSummoned}");
    }

    private static bool TryReadPetId(
        GamePacket packet,
        out uint petId)
    {
        petId = 0;
        if (packet.Length != PetPresenceRequestLength ||
            packet.Buffer.Length != PetPresenceRequestLength)
        {
            return false;
        }

        petId = BinaryPrimitives.ReadUInt32LittleEndian(packet.Payload);
        return petId != 0;
    }

}
