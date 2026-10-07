using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class Program
{
    private static NpcSpawnDefinition CreateNpcDefinition(uint objectId, float x, float z)
    {
        return new NpcSpawnDefinition(
            0,
            "Sparta",
            $"Sparta_Test_{objectId}",
            $"Sparta_Test_{objectId}_Male1",
            objectId,
            x,
            z,
            objectId,
            NpcSpawnDefinitionFactory.DefaultAppearanceType,
            NpcSpawnDefinitionFactory.DefaultFacing,
            [],
            []);
    }

    internal static CapturedMonsterSpawn CreateCapturedMonster(
        uint objectId,
        float x,
        float z,
        string templateKey,
        uint objectType = 0x00000212,
        uint tier = 1,
        uint maximumHealth = 237,
        short mapId = 0,
        string sceneKey = "Sparta")
    {
        var packet = new byte[108];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), 10020);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), objectType);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8, 4), objectId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12, 4), tier);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(20, 4), maximumHealth);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(24, 4), maximumHealth);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(28, 4), x);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(36, 4), z);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(40, 4), 1f);
        Encoding.ASCII.GetBytes(templateKey).CopyTo(packet.AsSpan(44));

        return new CapturedMonsterSpawn(
            mapId,
            sceneKey,
            templateKey,
            templateKey,
            objectId,
            x,
            z,
            packet);
    }

    private static async Task CheckMapRegistryWorldReadinessAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var outboundClient = new TcpClient();
            var acceptTask = listener.AcceptTcpClientAsync(timeout.Token).AsTask();
            await outboundClient.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            using var inboundClient = await acceptTask;
            await using var session =
                new ClientSession(new RawTcpLegacyTransport(outboundClient));

            using var existingOutboundClient = new TcpClient();
            var existingAcceptTask = listener.AcceptTcpClientAsync(timeout.Token).AsTask();
            await existingOutboundClient.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            using var existingInboundClient = await existingAcceptTask;
            await using var existingSession =
                new ClientSession(new RawTcpLegacyTransport(existingOutboundClient));

            var character = CreateCharacter();
            var existingCharacter = CreateCharacter();
            existingCharacter.Id += 1;
            existingCharacter.AccountId += 1;
            existingCharacter.Name = "ExistingHero";
            var registry = new GameSessionRegistry();
            registry.JoinMap(
                existingSession,
                existingCharacter.AccountId,
                existingCharacter,
                0x6402);
            Check.Throws<InvalidOperationException>(
                () => registry.JoinMap(session, character.AccountId, character, 0x6402, worldReady: false),
                "map registry rejects duplicate player world object IDs");
            registry.JoinMap(session, character.AccountId, character, 0x6401, worldReady: false);
            Check.Equal(1, registry.GetMapSessions(character.CurrentMap).Count, "not-ready session is hidden from map snapshots");
            Check.True(
                !registry.TryGetMapSessionByObjectId(character.CurrentMap, 0x6401, null, out _),
                "not-ready session is hidden from object lookup");

            Check.True(
                !registry.TryMarkWorldReady(session, new Dictionary<uint, long>(), out var unseenPlayers),
                "activation waits for unseen ready players");
            Check.Equal(1, unseenPlayers.Count, "activation returns the unseen ready player");
            Check.Equal(0x6402u, unseenPlayers[0].ObjectId, "activation returns the correct unseen object");

            var knownWorldRevisions = unseenPlayers.ToDictionary(
                player => player.ObjectId,
                player => player.WorldRevision);
            existingCharacter.Equipment = "[2443,24,90,60,250,,10,12,1,1,0]#";
            registry.UpdateCharacter(existingSession, existingCharacter);
            Check.True(
                !registry.TryMarkWorldReady(session, knownWorldRevisions, out var changedPlayers),
                "activation waits for a player changed during bootstrap");
            Check.Equal(1, changedPlayers.Count, "activation returns the changed ready player");
            Check.True(
                changedPlayers[0].WorldRevision > unseenPlayers[0].WorldRevision,
                "changed player has a newer world revision");

            knownWorldRevisions[changedPlayers[0].ObjectId] = changedPlayers[0].WorldRevision;
            for (var movementIndex = 0; movementIndex < 512; movementIndex++)
            {
                existingCharacter.PositionX += 1f;
                registry.UpdateCharacter(
                    existingSession,
                    existingCharacter,
                    advanceWorldRevision: false);
            }

            Check.True(
                registry.TryMarkWorldReady(
                    session,
                    knownWorldRevisions,
                    out var remainingPlayers),
                "activation succeeds after existing players are known");
            Check.Equal(0, remainingPlayers.Count, "successful activation has no unseen players");
            Check.Equal(2, registry.GetMapSessions(character.CurrentMap).Count, "ready session enters map snapshots");
            Check.True(
                registry.TryGetMapSessionByObjectId(character.CurrentMap, 0x6401, null, out var context) &&
                context.WorldReady,
                "ready session enters object lookup");
            registry.Remove(session);
            registry.Remove(existingSession);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static void CheckNpcSpawnFrame(byte[] stream, int offset, NpcSpawnDefinition definition)
    {
        Check.Equal((ushort)108, ReadUInt16(stream, offset), $"NPC {definition.NpcKey} declared length");
        Check.Equal((ushort)0x2724, ReadUInt16(stream, offset + 2), $"NPC {definition.NpcKey} opcode");
        var expectedObjectType =
            ((uint)(ushort)definition.MapId << 16) |
            (definition.AppearanceType & ushort.MaxValue);
        Check.Equal(
            expectedObjectType,
            ReadUInt32(stream, offset + 4),
            $"NPC {definition.NpcKey} map and appearance type");
        Check.Equal(definition.ObjectId, ReadUInt32(stream, offset + 8), $"NPC {definition.NpcKey} object id");
        Check.Equal(1u, ReadUInt32(stream, offset + 12), $"NPC {definition.NpcKey} active marker");
        Check.Equal(0u, ReadUInt32(stream, offset + 20), $"NPC {definition.NpcKey} neutral field");
        Check.Equal(1521u, ReadUInt32(stream, offset + 24), $"NPC {definition.NpcKey} appearance metadata");
        Check.Equal(definition.X, ReadSingle(stream, offset + 28), $"NPC {definition.NpcKey} X");
        Check.Equal(0f, ReadSingle(stream, offset + 32), $"NPC {definition.NpcKey} Y");
        Check.Equal(definition.Z, ReadSingle(stream, offset + 36), $"NPC {definition.NpcKey} Z");
        Check.Equal(definition.Facing, ReadSingle(stream, offset + 40), $"NPC {definition.NpcKey} facing");
        Check.Equal(
            definition.TemplateKey,
            ReadFixedAscii(stream, offset + 44, 64),
            $"NPC {definition.NpcKey} template");
    }

    private static async Task CheckConcurrentSendOrderingAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var cancellationToken = timeout.Token;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var outboundClient = new TcpClient();
            var acceptTask = listener.AcceptTcpClientAsync(cancellationToken).AsTask();
            await outboundClient.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
            using var inboundClient = await acceptTask;
            await using var session =
                new ClientSession(new RawTcpLegacyTransport(outboundClient));

            var clearPackets = Enumerable.Range(0, ConcurrentPacketCount)
                .Select(CreateConcurrentPacket)
                .ToArray();
            var receiveTask = ReadExactlyAsync(
                inboundClient.GetStream(),
                ConcurrentPacketCount * ConcurrentPacketLength,
                cancellationToken);

            var startGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var sendTasks = Enumerable.Range(0, ConcurrentPacketCount)
                .Select(packetId => Task.Run(async () =>
                {
                    await startGate.Task;
                    await session.SendAsync(clearPackets[packetId], cancellationToken);
                }, cancellationToken))
                .ToArray();

            startGate.SetResult(true);
            await Task.WhenAll(sendTasks).WaitAsync(cancellationToken);
            var encryptedStream = await receiveTask;

            var receiveCipher = new PacketCipher();
            receiveCipher.Transform(encryptedStream);
            AssertConcurrentFrames(encryptedStream);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static GameCharacter CreateCharacter()
    {
        return new GameCharacter
        {
            Id = 731,
            AccountId = 17,
            Name = "ProtocolHero",
            Gender = 2,
            Camp = 1,
            Profession = 3,
            Hair = 7,
            CurrentMap = 2,
            Level = 177,
            Experience = 123_987,
            CurrentHp = 123_456,
            CurrentMp = 23_456,
            MaxHp = 234_567,
            MaxMp = 34_567,
            TalentPoints = 456_789,
            TalentExperience = 67,
            PositionX = 321.125f,
            PositionZ = -654.75f,
            CalculatedStats = new CharacterStats
            {
                PhysicalAttack = 91_001,
                PhysicalDefense = 82_002,
                MagicAttack = 73_003,
                MagicDefense = 64_004,
                Hit = 55_005,
                Dodge = 46_006,
                Critical = 37_007,
                CriticalResistance = 28_008,
                PhysicalDamageBonus = 1_234,
                MagicDamageBonus = 2_345,
                DamageAbsorb = 19_009,
                BeCureBonus = 3_456,
                CureBonus = 4_567
            }
        };
    }

    private static GameCharacter CreateAppearanceCharacter()
    {
        var character = CreateCharacter();
        var slots = Enumerable.Repeat("[]", 21).ToArray();
        slots[0] = "[2443,24,90,60,250,,10,12,1,1,0]";
        slots[3] = "[2261,13,103,133,33,40,10,12,1,1,0]";
        slots[10] = "[1834,24,90,250,60,230,10,12,1,1,0]";
        slots[15] = "[14504,374,414,,,,7,8,1,1,0]";
        slots[20] = "[16184,,,,,,1,1,1,1,0]";
        character.Equipment = string.Join('#', slots) + '#';
        character.Face = 4;
        return character;
    }

    private static byte[] CreateConcurrentPacket(int packetId)
    {
        var packet = new byte[ConcurrentPacketLength];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), ConcurrentPacketLength);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), ConcurrentPacketOpcode);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4, 4), packetId);

        for (var offset = 8; offset < packet.Length; offset++)
        {
            packet[offset] = ConcurrentPayloadByte(packetId, offset);
        }

        return packet;
    }

    private static void AssertConcurrentFrames(byte[] clearStream)
    {
        Check.Equal(
            ConcurrentPacketCount * ConcurrentPacketLength,
            clearStream.Length,
            "concurrent send stream length");

        var seenPacketIds = new HashSet<int>();
        for (var frameIndex = 0; frameIndex < ConcurrentPacketCount; frameIndex++)
        {
            var frame = clearStream.AsSpan(
                frameIndex * ConcurrentPacketLength,
                ConcurrentPacketLength);
            Check.Equal(
                (ushort)ConcurrentPacketLength,
                BinaryPrimitives.ReadUInt16LittleEndian(frame),
                $"frame {frameIndex} declared length");
            Check.Equal(
                ConcurrentPacketOpcode,
                BinaryPrimitives.ReadUInt16LittleEndian(frame[2..]),
                $"frame {frameIndex} opcode");

            var packetId = BinaryPrimitives.ReadInt32LittleEndian(frame[4..]);
            Check.True(
                packetId is >= 0 and < ConcurrentPacketCount,
                $"frame {frameIndex} packet id {packetId} is in range");
            Check.True(seenPacketIds.Add(packetId), $"packet id {packetId} is unique");

            for (var offset = 8; offset < frame.Length; offset++)
            {
                Check.Equal(
                    ConcurrentPayloadByte(packetId, offset),
                    frame[offset],
                    $"frame {frameIndex} packet {packetId} payload byte {offset}");
            }
        }

        Check.Equal(ConcurrentPacketCount, seenPacketIds.Count, "unique concurrent packet count");
    }

    private static byte ConcurrentPayloadByte(int packetId, int offset)
    {
        return (byte)((packetId * 31 + offset * 17 + 0x5A) & 0xFF);
    }

    private static async Task<byte[]> ReadExactlyAsync(
        NetworkStream stream,
        int byteCount,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[byteCount];
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException(
                    $"Loopback stream closed after {offset} of {buffer.Length} bytes.");
            }

            offset += read;
        }

        return buffer;
    }

    private static ushort ReadUInt16(byte[] packet, int offset)
    {
        return BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(offset, 2));
    }

    private static uint ReadUInt32(byte[] packet, int offset)
    {
        return BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(offset, 4));
    }

    private static int ReadInt32(byte[] packet, int offset)
    {
        return BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(offset, 4));
    }

    private static float ReadSingle(byte[] packet, int offset)
    {
        return BinaryPrimitives.ReadSingleLittleEndian(packet.AsSpan(offset, 4));
    }

    private static string ReadFixedAscii(byte[] packet, int offset, int length)
    {
        return Encoding.ASCII.GetString(packet, offset, length).TrimEnd('\0');
    }
}
