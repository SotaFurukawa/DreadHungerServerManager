using System.Text;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace DreadHungerServerManager.Bot.Lambda.Discord;

/// <summary>
/// Discord からの要求の署名（Ed25519）を検証する。検証できない要求はすべて拒否する（Discord の必須要件）。
/// 署名の対象は「X-Signature-Timestamp の値 + 本文」。
/// </summary>
public static class DiscordSignature
{
    /// <summary>古すぎる要求（再送攻撃）を拒否する時間。</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(5);

    public static bool Verify(string publicKeyHex, string? signatureHex, string? timestamp, string body, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(signatureHex) || string.IsNullOrEmpty(timestamp)
            || !long.TryParse(timestamp, out var seconds)
            || Math.Abs((now - DateTimeOffset.FromUnixTimeSeconds(seconds)).TotalSeconds) > MaxAge.TotalSeconds)
        {
            return false;
        }

        try
        {
            var signature = Convert.FromHexString(signatureHex);
            var publicKey = Convert.FromHexString(publicKeyHex);
            if (signature.Length != 64 || publicKey.Length != 32)
            {
                return false;
            }

            var message = Encoding.UTF8.GetBytes(timestamp + body);
            var verifier = new Ed25519Signer();
            verifier.Init(false, new Ed25519PublicKeyParameters(publicKey));
            verifier.BlockUpdate(message, 0, message.Length);
            return verifier.VerifySignature(signature);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
