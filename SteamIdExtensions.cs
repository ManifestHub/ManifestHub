using System.Security.Cryptography;
using SteamKit2;

namespace ManifestHub;

public static class SteamIdExtensions {
    private const string CsgoFriendCodeChars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public static string? AsCsgoFriendCode(this SteamID steamId) {
        if (!steamId.IsIndividualAccount || !steamId.IsValid) {
            return null;
        }

        var h = "CSGO"u8.ToArray();
        h = h.Concat(BitConverter.GetBytes(steamId.AccountID).Reverse()).ToArray();
        h = MD5.HashData(h.Reverse().ToArray());
        var hash = BitConverter.ToUInt32(h, 0);

        var steamId64 = steamId.ConvertToUInt64();
        var result = 0ul;

        for (var i = 0; i < 8; i++) {
            var idNib = (steamId64 >> (i * 4)) & 0xF;
            var hashNib = (hash >> i) & 0x1;
            var a = (result << 4) | idNib;

            result = ((result >> 28) << 32) | a;
            result = ((result >> 31) << 32) | (a << 1) | hashNib;
        }

        result = BitConverter.ToUInt64(BitConverter.GetBytes(result).Reverse().ToArray(), 0);
        var code = "";

        for (int i = 0; i < 13; i++) {
            if (i is 4 or 9) code += '-';

            code += CsgoFriendCodeChars[(int)(result & 31)];
            result = result >> 5;
        }

        return code[5..];
    }
}
