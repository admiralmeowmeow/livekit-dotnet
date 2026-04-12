using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LiveKitD3D11Viewer.Auth;

internal static class LiveKitTokenFactory
{
    public static string CreateViewerToken(AppOptions options)
    {
        return CreateJoinToken(
            options,
            identity: options.Identity,
            participantName: options.ParticipantName,
            canPublish: false,
            canSubscribe: true);
    }

    private static string CreateJoinToken(
        AppOptions options,
        string identity,
        string participantName,
        bool canPublish,
        bool canSubscribe)
    {
        var issuedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var expiresAt = issuedAt + 6 * 60 * 60;

        var header = new Dictionary<string, object>
        {
            ["alg"] = "HS256",
            ["typ"] = "JWT",
        };

        var payload = new Dictionary<string, object>
        {
            ["iss"] = options.ApiKey,
            ["sub"] = identity,
            ["name"] = participantName,
            ["nbf"] = issuedAt,
            ["exp"] = expiresAt,
            ["video"] = new Dictionary<string, object>
            {
                ["roomJoin"] = true,
                ["room"] = options.RoomName,
                ["canPublish"] = canPublish,
                ["canSubscribe"] = canSubscribe,
                ["canPublishData"] = true,
            },
        };

        var headerSegment = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(header));
        var payloadSegment = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(payload));
        var signingInput = $"{headerSegment}.{payloadSegment}";

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(options.ApiSecret));
        var signature = hmac.ComputeHash(Encoding.UTF8.GetBytes(signingInput));
        return $"{signingInput}.{Base64UrlEncode(signature)}";
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
