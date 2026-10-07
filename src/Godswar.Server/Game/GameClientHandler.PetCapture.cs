using Godswar.Server.Application.Pets;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private const uint PetCaptureSkillId = 4734;
    private const uint MysteriousTuckNetItemId = 10084;
    private const uint RockElfEggItemId = 10150;
    private const float PetCaptureRange = 9f;
    private static readonly TimeSpan PetCaptureCastTime =
        TimeSpan.FromSeconds(6);

    private async Task<bool> TryHandlePetCapturePacketAsync(
        GamePacket packet,
        CancellationToken cancellationToken)
    {
        if (packet.Opcode != Opcodes.PetCaptureRequest)
        {
            return false;
        }

        await HandlePetCaptureAsync(packet, cancellationToken);
        return true;
    }

    private async Task HandlePetCaptureAsync(
        GamePacket packet,
        CancellationToken cancellationToken)
    {
        var character = _character;
        if (PetCaptureRequest.TryRead(packet, out var questCapture) &&
            await TryHandleQuestCaptureAsync(questCapture, cancellationToken)) return;
        if (!PetCaptureRequest.TryRead(packet, out var request) ||
            _account is null ||
            character is null ||
            character.CurrentMap is not (200 or 205) ||
            !TryResolvePetCaptureTarget(
                request,
                out var target,
                out var captureKind) ||
            !HasPetCaptureInventoryCapacity(character, request.KitBagSlot))
        {
            Console.WriteLine(
                $"[pet-capture] rejected request " +
                $"character={character?.Name ?? "<none>"} " +
                $"len={packet.Length} hex={packet.ToHexPreview()}");
            await SendPetCaptureCastEndAsync(
                broadcastToWorld: false,
                cancellationToken);
            return;
        }

        var control = ResolvePlayerSkillCastControl(DateTimeOffset.UtcNow);
        if (control != PlayerSkillCastControl.None)
        {
            await SendBlockedSkillCastNoticeAsync(
                control,
                cancellationToken);
            return;
        }

        var mapId = character.CurrentMap;
        var worldObjectId = CurrentPlayerObjectId;
        var selfCastStart = PacketBuilder.MonsterSkillCastVisual(
            LocalPlayerObjectId,
            target.ObjectId,
            PetCaptureSkillId,
            character.PositionX,
            character.PositionZ,
            target.X,
            target.Z);
        var worldCastStart = PacketBuilder.MonsterSkillCastVisual(
            worldObjectId,
            target.ObjectId,
            PetCaptureSkillId,
            character.PositionX,
            character.PositionZ,
            target.X,
            target.Z);
        var started = await TryBeginPendingSkillCastAsync(
            PetCaptureSkillId,
            PetCaptureCastTime,
            "pet_capture",
            async token =>
            {
                if (!await _registry
                    .DeliverMonsterCastStartToViewerAdmissionAsync(
                        _session,
                        mapId,
                        target.ObjectId,
                        selfCastStart,
                        target.SpawnGeneration,
                        token,
                        "PetCaptureCastStartSelf"))
                {
                    throw new InvalidOperationException(
                        "The pet-capture cast start could not be admitted.");
                }

                await _registry
                    .BroadcastMonsterCastStartToViewersAdmissionAsync(
                        _session,
                        mapId,
                        target.ObjectId,
                        worldCastStart,
                        target.SpawnGeneration,
                        token,
                        "PetCaptureCastStartWorld");
            },
            token => CompletePetCaptureAsync(
                request,
                target,
                captureKind,
                token),
            cancellationToken,
            () => IsPetCaptureCompletionValid(
                request,
                target,
                captureKind));
        if (!started)
        {
            await SendPetCaptureCastEndAsync(
                broadcastToWorld: false,
                cancellationToken);
        }
    }

    private async Task CompletePetCaptureAsync(
        PetCaptureRequest request,
        MonsterRuntimeSnapshot expectedTarget,
        PetCaptureKind captureKind,
        CancellationToken cancellationToken)
    {
        var captured = _registry.TryCaptureMonster(
            _session,
            expectedTarget,
            DateTimeOffset.UtcNow,
            out var result);
        PetDurableReceipt? receipt = null;
        try
        {
            if (!captured)
            {
                Console.WriteLine(
                    $"[pet-capture] target claim lost " +
                    $"character={_character?.Name ?? "<none>"} " +
                    $"target={request.TargetObjectId}");
                return;
            }

            var intent = new PetCaptureIntent(
                expectedTarget.ObjectId,
                expectedTarget.RuntimeInstanceId,
                expectedTarget.SpawnGeneration,
                expectedTarget.HealthRevision,
                captureKind.EggItemId,
                captureKind.Difficulty,
                captureKind.Context);
            var bagBefore = _character?.KitBag;
            receipt = await HandleDurableBagItemActivationAsync(
                PetCommandOperationIdentity.ServerSessionLifecycle(
                    Guid.NewGuid(),
                    _commandConnectionId),
                request.KitBagSlot,
                cancellationToken,
                intent);
            if (receipt?.Status == PetDurableReceiptStatus.PetCaptured &&
                bagBefore is not null &&
                _character is { } character)
            {
                await SendPetCaptureAcquisitionAsync(
                    bagBefore,
                    character.KitBag,
                    cancellationToken,
                    captureKind.EggItemId);
            }
            Console.WriteLine(
                $"[pet-capture] completed " +
                $"character={_character?.Name ?? "<none>"} " +
                $"target={result.ObjectId} " +
                $"status={receipt?.Status.ToString() ?? "unavailable"}");
        }
        finally
        {
            if (captured)
            {
                await _registry.BroadcastToCurrentWorldInstanceAsync(
                    _session,
                    PacketBuilder.RemoveWorldObjects(
                        expectedTarget.ObjectId),
                    CancellationToken.None,
                    includeRoutingSession: true,
                    label: "PetCapturedRemove");
            }
            await SendPetCaptureCastEndAsync(
                broadcastToWorld: true,
                CancellationToken.None);
        }
    }

    private static bool HasPetCaptureInventoryCapacity(
        GameCharacter character,
        int netSlot)
    {
        var net = KitBagSlots.GetItem(character.KitBag, netSlot);
        if (net.Id != MysteriousTuckNetItemId || net.Stack <= 0)
        {
            return false;
        }

        return net.Stack == 1 ||
            Enumerable.Range(0, 96).Any(slot =>
                KitBagSlots.GetItem(
                    character.KitBag,
                    slot).IsEmpty);
    }

    private static bool IsWithinPetCaptureRange(
        GameCharacter character,
        MonsterRuntimeSnapshot target)
    {
        var deltaX = (double)character.PositionX - target.X;
        var deltaZ = (double)character.PositionZ - target.Z;
        return (deltaX * deltaX) + (deltaZ * deltaZ) <=
            PetCaptureRange * PetCaptureRange;
    }

    private bool IsPetCaptureCastPending() =>
        IsSkillCastPending(PetCaptureSkillId);

    private async Task SendPetCaptureCastEndAsync(
        bool broadcastToWorld,
        CancellationToken cancellationToken)
    {
        await _session.SendAsync(
            PacketBuilder.SkillCastInterrupt(LocalPlayerObjectId),
            cancellationToken,
            "PetCaptureCastEndSelf");
        if (broadcastToWorld && _character is { } character)
        {
            await _registry.BroadcastToMapAsync(
                character.CurrentMap,
                PacketBuilder.SkillCastInterrupt(
                    CurrentPlayerObjectId),
                cancellationToken,
                _session,
                "PetCaptureCastEndWorld");
        }
    }
}
