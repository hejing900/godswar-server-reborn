using System.Buffers.Binary;
using Godswar.Server.Application.Characters;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Game;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class BackhaulSkillHandlerChecks
{
    private static async Task CheckDeadMovementAndReviveAuthorityAsync()
    {
        foreach (var mode in new[]
                 {
                     PlayerRuntimeMode.Legacy,
                     PlayerRuntimeMode.Ecs
                 })
        {
            await CheckDeadMovementDoesNotInterruptCastAsync(mode);
        }

        await CheckReviveAdmissionRejectsWithoutMutationAsync();
        await CheckCaptureProvenFreeReviveAsync();
    }

    private static async Task CheckDeadMovementDoesNotInterruptCastAsync(
        PlayerRuntimeMode mode)
    {
        await using var fixture = await InterruptFixture.CreateAsync(
            $"DeadMovementCast{mode}",
            mode);
        await fixture.BeginCastAsync();
        await AssertCastStartedAsync(
            fixture,
            $"dead {mode} movement gate");

        fixture.Character.CurrentHp = 0;
        SetField(fixture.Handler, "_positionDirty", true);
        var initialX = fixture.Character.PositionX;
        var initialZ = fixture.Character.PositionZ;
        var initialPositionRevision =
            fixture.Character.PositionRevision;
        var initialLifeRevision =
            fixture.Registry.GetPlayerLifeRevision(
                fixture.Socket.Session);
        var movements = new[]
        {
            CreateControlPacket(Opcodes.WalkBegin),
            CreateAuthorityWalkPacket(
                fixture.Character.PositionX + 5f,
                fixture.Character.PositionZ - 5f),
            CreateControlPacket(Opcodes.WalkEnd)
        };

        foreach (var movement in movements)
        {
            await InvokePacketAsync(fixture.Handler, movement);
            Check.True(
                HasPendingSkillCast(fixture.Handler),
                $"dead {mode} opcode {movement.Opcode} preserves pending cast");
        }

        await Task.Delay(50);
        Check.Equal(
            0,
            fixture.Socket.Available,
            $"dead {mode} movement emits no interrupt or movement packet");
        Check.Equal(
            initialX,
            fixture.Character.PositionX,
            $"dead {mode} movement preserves X");
        Check.Equal(
            initialZ,
            fixture.Character.PositionZ,
            $"dead {mode} movement preserves Z");
        Check.Equal(
            initialPositionRevision,
            fixture.Character.PositionRevision,
            $"dead {mode} movement preserves position revision");
        Check.Equal(
            0,
            fixture.Store.PositionWrites.Count,
            $"dead {mode} movement never persists position");
        Check.Equal(
            0,
            fixture.Store.VitalsWrites.Count,
            $"dead {mode} movement never persists vitals");
        Check.Equal(
            initialLifeRevision,
            fixture.Registry.GetPlayerLifeRevision(
                fixture.Socket.Session),
            $"dead {mode} movement preserves life revision");
    }

    private static async Task CheckReviveAdmissionRejectsWithoutMutationAsync()
    {
        await using var fixture = await InterruptFixture.CreateAsync(
            "ReviveAdmissionAuthority");
        fixture.Character.CurrentHp = 0;
        fixture.Character.CurrentMp = 77;

        var valid = CreateRevivePacket(
            LocalPlayerObjectId,
            ReviveRequest.FreeReviveType);
        Check.Equal(
            (ushort)10028,
            Opcodes.Revive,
            "the revive request opcode is the captured 10028");
        Check.True(
            ReviveRequest.TryParse(valid.Buffer, out var parsed) &&
            parsed.PlayerObjectId == LocalPlayerObjectId &&
            parsed.ReviveType == ReviveRequest.FreeReviveType,
            "capture-proven exact free-revive frame parses");

        // Captured 2026-09-15 from the running client: C2S 10028 with
        // object 1167 and revive type 2 (free), sent 13.5 s after death and
        // answered with the Athens landing frame.
        var capturedFrame = new GamePacket(
            Convert.FromHexString("0C002C278F04000002000000"));
        Check.True(
            ReviveRequest.TryParse(capturedFrame.Buffer, out var capturedParsed) &&
            capturedParsed.PlayerObjectId == 0x48Fu &&
            capturedParsed.ReviveType == ReviveRequest.FreeReviveType,
            "captured free-revive frame 0C002C278F04000002000000 parses");
        Check.True(
            ReviveRequest.TryParse(
                CreateRevivePacket(
                    LocalPlayerObjectId,
                    ReviveRequest.FreeReviveType,
                    opcode: Opcodes.ReviveLegacy).Buffer,
                out _),
            "legacy 10019 revive frame still parses");

        var trailing = CreateRevivePacket(
            LocalPlayerObjectId,
            ReviveRequest.FreeReviveType,
            actualLength: 13,
            declaredLength: 12);
        var wrongOpcode = CreateRevivePacket(
            LocalPlayerObjectId,
            ReviveRequest.FreeReviveType,
            opcode: Opcodes.Ping);
        Check.True(
            !ReviveRequest.TryParse(trailing.Buffer, out _),
            "revive parser rejects trailing bytes beyond the exact frame");
        Check.True(
            !ReviveRequest.TryParse(wrongOpcode.Buffer, out _),
            "revive parser rejects another opcode with the same shape");

        var rejected = new[]
        {
            CreateMalformedRevivePacket(),
            trailing,
            CreateRevivePacket(
                LocalPlayerObjectId + 1,
                ReviveRequest.FreeReviveType),
            CreateRevivePacket(
                LocalPlayerObjectId,
                reviveType: 1)
        };
        foreach (var request in rejected)
        {
            await AssertReviveRejectedWithoutMutationAsync(
                fixture,
                request,
                $"rejected revive opcode={request.Opcode} length={request.Length}");
        }

        fixture.Character.CurrentHp = 25;
        await AssertReviveRejectedWithoutMutationAsync(
            fixture,
            valid,
            "living-character free revive");
    }

    private static async Task CheckCaptureProvenFreeReviveAsync()
    {
        CheckReviveLandingCatalog();

        // An uncaptured death map keeps the camp-capital fallback. The fixture
        // character dies on Peloponnese (map 13).
        await CheckFreeReviveLandingAsync(
            "CaptureProvenFreeRevive",
            deathMap: PeloponneseMapId,
            expectedMap: GameDefaults.SpartaCapitalMap,
            expectedX: GameDefaults.StartingPositionX,
            expectedZ: GameDefaults.StartingPositionZ,
            landing: "camp-capital fallback");

        // Captured 2026-09-15: a character killed in Athens city (map 1) is
        // answered with the 28-byte landing frame for x = 20, z = -100 on map
        // 1, so the free revive returns the character to the map it died on
        // rather than to a single global coordinate.
        await CheckFreeReviveLandingAsync(
            "CapturedAthensReviveLanding",
            deathMap: GameDefaults.AthensCapitalMap,
            expectedMap: GameDefaults.AthensCapitalMap,
            expectedX: 20f,
            expectedZ: -100f,
            landing: "captured Athens landing");
    }

    private static void CheckReviveLandingCatalog()
    {
        // Three capture-proven rows. Athens (2026-09-15) and Megara (2026-09-25)
        // were the first two; the Cursed Land's map 29 was added by the
        // 2026-10-04 capture, whose character died there six times and was
        // answered with the same 28-byte landing frame every time.
        Check.Equal(
            3,
            ReviveLandingCatalog.Captured.Count,
            "revive landing catalog carries every capture-proven row");
        Check.True(
            ReviveLandingCatalog.TryResolve(
                (byte)CursedLandTransportProtocol.MapId,
                out var cursed) &&
            cursed is { MapId: 29, X: -196f, Z: 44f },
            "revive landing catalog resolves the captured Cursed Land point");
        Check.True(
            ReviveLandingCatalog.TryResolve(
                GameDefaults.AthensCapitalMap,
                out var athens) &&
            athens is { MapId: 1, X: 20f, Z: -100f },
            "revive landing catalog resolves the captured Athens point");
        // Captured 2026-09-25 00:13: the landing frame for Megara is
        // 1C002227 A7000000 00006042 00000000 0000B042 12001200 01000000,
        // which puts the character on map 18 at x = 56, z = 88. It shares no
        // coordinate with the Athens point, so each map carries its own.
        Check.True(
            ReviveLandingCatalog.TryResolve(
                MegaraMapId,
                out var megara) &&
            megara is { MapId: 18, X: 56f, Z: 88f },
            "revive landing catalog resolves the captured Megara point");
        Check.True(
            !ReviveLandingCatalog.TryResolve(
                GameDefaults.SpartaCapitalMap,
                out _) &&
            !ReviveLandingCatalog.TryResolve(PeloponneseMapId, out _),
            "revive landing catalog leaves uncaptured maps to the fallback");
    }

    private static async Task CheckFreeReviveLandingAsync(
        string characterName,
        byte deathMap,
        byte expectedMap,
        float expectedX,
        float expectedZ,
        string landing)
    {
        await using var fixture = await InterruptFixture.CreateAsync(
            characterName);
        var character = fixture.Character;
        character.CurrentMap = deathMap;
        character.CurrentHp = 0;
        character.CurrentMp = 0;
        SetField(fixture.Handler, "_characterSnapshotLoaded", true);
        SetField(
            fixture.Handler,
            "_characterSnapshotBootstrapPending",
            true);
        SetField(
            fixture.Handler,
            "_characterLoadSnapshot",
            new HydratedCharacterLoadSnapshot(
                character,
                [],
                [],
                new CharacterPetShedSnapshot(2, 0),
                [],
                []));
        var initialLifeRevision =
            fixture.Registry.GetPlayerLifeRevision(
                fixture.Socket.Session);

        await InvokePacketAsync(
            fixture.Handler,
            CreateRevivePacket(
                LocalPlayerObjectId,
                ReviveRequest.FreeReviveType));

        Check.Equal(
            initialLifeRevision + 1,
            fixture.Registry.GetPlayerLifeRevision(
                fixture.Socket.Session),
            $"free revive advances life revision exactly once ({landing})");
        Check.Equal(
            expectedMap,
            character.CurrentMap,
            $"free revive lands on the expected map ({landing})");
        Check.Equal(
            expectedX,
            character.PositionX,
            $"free revive restores the expected X ({landing})");
        Check.Equal(
            expectedZ,
            character.PositionZ,
            $"free revive restores the expected Z ({landing})");
        Check.Equal(
            character.MaxHp / 10,
            character.CurrentHp,
            $"free revive restores ten percent HP ({landing})");
        Check.Equal(
            character.MaxMp / 10,
            character.CurrentMp,
            $"free revive restores ten percent MP ({landing})");
        Check.Equal(
            1,
            fixture.Store.PositionWrites.Count,
            $"free revive persists one position checkpoint ({landing})");
        Check.Equal(
            1,
            fixture.Store.VitalsWrites.Count,
            $"free revive persists one vitals checkpoint ({landing})");
        Check.True(
            fixture.Store.PositionWrites[0] is
            {
                MapId: var persistedMap,
                X: var persistedX,
                Z: var persistedZ
            } &&
            persistedMap == expectedMap &&
            persistedX == expectedX &&
            persistedZ == expectedZ,
            $"free revive persists the restored position ({landing})");
        Check.True(
            fixture.Store.VitalsWrites[0] is
            {
                CurrentHp: 250,
                CurrentMp: 150
            },
            $"free revive persists the restored vitals ({landing})");

        var reachedEnterComplete = false;
        // An empty 24-slot bag still emits its complete detail/index pages.
        // Keep the read bounded above that deterministic bootstrap size.
        for (var index = 0; index < 128; index++)
        {
            var response = await fixture.Socket.ReadPacketAsync();
            if (ReadUInt16(response, 2) == Opcodes.GameServerReady)
            {
                reachedEnterComplete = true;
                break;
            }
        }
        Check.True(
            reachedEnterComplete,
            $"free revive completes the bounded re-entry bootstrap ({landing})");
    }

    private static async Task AssertReviveRejectedWithoutMutationAsync(
        InterruptFixture fixture,
        GamePacket request,
        string description)
    {
        var character = fixture.Character;
        var initialMap = character.CurrentMap;
        var initialX = character.PositionX;
        var initialZ = character.PositionZ;
        var initialHp = character.CurrentHp;
        var initialMp = character.CurrentMp;
        var initialPositionRevision = character.PositionRevision;
        var initialVitalsRevision = character.VitalsRevision;
        var initialLifeRevision =
            fixture.Registry.GetPlayerLifeRevision(
                fixture.Socket.Session);

        await InvokePacketAsync(fixture.Handler, request);

        Check.Equal(initialMap, character.CurrentMap,
            $"{description} preserves map");
        Check.Equal(initialX, character.PositionX,
            $"{description} preserves X");
        Check.Equal(initialZ, character.PositionZ,
            $"{description} preserves Z");
        Check.Equal(initialHp, character.CurrentHp,
            $"{description} preserves HP");
        Check.Equal(initialMp, character.CurrentMp,
            $"{description} preserves MP");
        Check.Equal(initialPositionRevision, character.PositionRevision,
            $"{description} preserves position revision");
        Check.Equal(initialVitalsRevision, character.VitalsRevision,
            $"{description} preserves vitals revision");
        Check.Equal(
            initialLifeRevision,
            fixture.Registry.GetPlayerLifeRevision(
                fixture.Socket.Session),
            $"{description} preserves life revision");
        Check.Equal(
            0,
            fixture.Store.PositionWrites.Count,
            $"{description} persists no position");
        Check.Equal(
            0,
            fixture.Store.VitalsWrites.Count,
            $"{description} persists no vitals");
        Check.Equal(
            0,
            fixture.Socket.Available,
            $"{description} emits no lifecycle packet");
        Check.True(
            fixture.Registry.TryGetMapSessionByCharacterId(
                character.CurrentMap,
                character.Id,
                excludeSession: null,
                out var context) &&
            ReferenceEquals(
                context.Session,
                fixture.Socket.Session),
            $"{description} preserves registered world membership");
    }

    private static GamePacket CreateAuthorityWalkPacket(
        float targetX,
        float targetZ)
    {
        var packet = new byte[20];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            checked((ushort)packet.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            Opcodes.Walk);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(4),
            0xCAFE_BABEu);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(8),
            targetX);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(12),
            targetZ);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(16),
            1f);
        return new GamePacket(packet);
    }

    private static GamePacket CreateMalformedRevivePacket()
    {
        var packet = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            checked((ushort)packet.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            Opcodes.Revive);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(4),
            LocalPlayerObjectId);
        return new GamePacket(packet);
    }

    private static GamePacket CreateRevivePacket(
        uint playerObjectId,
        int reviveType,
        int actualLength = 12,
        ushort declaredLength = 12,
        ushort opcode = Opcodes.Revive)
    {
        var packet = new byte[actualLength];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            declaredLength);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            opcode);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(4),
            playerObjectId);
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(8),
            reviveType);
        return new GamePacket(packet);
    }
}
