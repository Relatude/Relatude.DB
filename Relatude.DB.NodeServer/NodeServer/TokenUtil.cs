using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
namespace Relatude.DB.NodeServer;
internal static class TokenUtil {
    public static string CreateToken(string userId, Guid userTokenId, string userIP) {
        var values = new Dictionary<string, string> {
                { "CreatedUtcTicks", DateTime.UtcNow.Ticks.ToString() },
                { "UserId", userId.ToString() },
                { "UserTokenId", userTokenId.ToString() },
                { "UserIP", userIP }
            };
        var json = JsonSerializer.Serialize(values);
        return StringEncryption.Encrypt(json, SimpleAuthentication.TokenEncryptionSecret, SimpleAuthentication.TokenEncryptionSalt);
    }
    public static bool IsTokenValid(string? token, string requestIP, [MaybeNullWhen(false)] out string? userId, Func<string, Guid?> tryGetCurrentUserTokenId) {
        try {

            userId = null;

            // decrypting
            if (token == null || token.Length < 30) return false; // token cannot be valid
            if (!StringEncryption.TryDecrypt(token, SimpleAuthentication.TokenEncryptionSecret, SimpleAuthentication.TokenEncryptionSalt, out var json)) return false; // decryption failed

            // parsing json
            var values = JsonSerializer.Deserialize<Dictionary<string, string>>(json); // may throw exception
            if (values == null) return false; // invalid json

            // expired?
            if (!values.TryGetValue("CreatedUtcTicks", out var createdUtcTicksString)) return false; // no createdUtcTicks
            if (!long.TryParse(createdUtcTicksString, out var createdUtcTicks)) return false; // invalid createdUtcTicks
            var createdUtc = new DateTime(createdUtcTicks, DateTimeKind.Utc);
            var age = DateTime.UtcNow.Subtract(createdUtc);
            if (age > TimeSpan.FromSeconds(SimpleAuthentication.TokenCookieMaxAgeInSec)) return false; // token expired

            // getting the user Id, but store it in a temporary variable
            if (!values.TryGetValue("UserId", out var tempUserIdString)) return false; // no userId            

            // user tokenId?
            if (!values.TryGetValue("UserTokenId", out var userTokenIdString)) return false; // no userTokenId
            if (!Guid.TryParse(userTokenIdString, out var tokenId)) return false; // invalid userTokenId

            var currentUserTokenId = tryGetCurrentUserTokenId(tempUserIdString); // callback to get the current user token id, now that we know the user id
            if (currentUserTokenId == null) return false; // user token not found   
            if (currentUserTokenId != tokenId) {
                Console.WriteLine("Token mismatch: " + currentUserTokenId + " != " + tokenId);
                return false; // token mismatch or reissued
            }

            // user IP?
            if (SimpleAuthentication.TokenLockedToIP) {
                if (!values.TryGetValue("UserIP", out var userIP)) return false; // no userIP
                if (userIP != requestIP) return false; // IP mismatch
            }

            // all ok!
            userId = tempUserIdString;
            return true;

        } catch { }
        // any other outcome is a failure
        userId = null;
        return false;
    }
}
