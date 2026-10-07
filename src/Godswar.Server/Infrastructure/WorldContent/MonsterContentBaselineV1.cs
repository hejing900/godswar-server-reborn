using System.Reflection;
using System.Security.Cryptography;
using Godswar.Server.Application.World;
using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.Infrastructure.WorldContent;

internal static class MonsterContentBaselineV1
{
    public const int ExpectedEntryCount = 3341;
    public const string ExpectedRevision =
        "A0A1BD6B4D9F5C78700E2DD5089B2E71F7AA3EDA4B2B6B653754FB8230B68FED";
    public const string ExpectedArtifactSha256 =
        "689B5619C593489E0ECC4EC4F383271420D0ABC9B3680FA30030246EC7FBA634";
    public const string Source = "reviewed-capture-promotion-v1";

    private const string ResourceName =
        "Godswar.Server.Infrastructure.WorldContent." +
        "Baselines.MonsterContentBaseline.v1.gz";

    public static CapturedMonsterSpawn[] LoadDefinitions()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream =
            assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidDataException(
                "The reviewed monster baseline resource is missing.");
        using var compressed = new MemoryStream();
        stream.CopyTo(compressed);
        var bytes = compressed.ToArray();
        var artifactHash = Convert.ToHexString(SHA256.HashData(bytes));
        if (!string.Equals(
                ExpectedArtifactSha256,
                artifactHash,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The reviewed monster baseline artifact checksum is invalid.");
        }

        var definitions = MonsterContentBaselineCodec.Deserialize(bytes);
        if (definitions.Length != ExpectedEntryCount)
        {
            throw new InvalidDataException(
                "The reviewed monster baseline entry count is invalid.");
        }

        var revision = WorldContentRevisionHasher.HashMonsters(definitions);
        if (!string.Equals(
                ExpectedRevision,
                revision.Sha256,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The reviewed monster baseline content revision is invalid.");
        }

        return definitions;
    }
}
