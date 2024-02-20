using SteamKit2;

namespace ManifestHub;

public class ManifestInfoCallback(
    uint appId,
    uint depotId,
    ulong manifestId,
    byte[] depotKey,
    DepotManifest manifest) {
    public uint AppId { get; set; } = appId;
    public uint DepotId { get; set; } = depotId;
    public ulong ManifestId { get; set; } = manifestId;
    public byte[] DepotKey { get; set; } = depotKey;
    public DepotManifest Manifest { get; set; } = manifest;
}
