using System.Text;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace DreadHungerServerManager.Bot.Tests;

/// <summary>Discord の代わりに要求へ署名する鍵（テストのたびに作る）。</summary>
internal sealed class TestKeys
{
    private readonly Ed25519PrivateKeyParameters _privateKey = new(new SecureRandom());

    public string PublicKeyHex => Convert.ToHexString(_privateKey.GeneratePublicKey().GetEncoded()).ToLowerInvariant();

    public (string Signature, string Timestamp) Sign(string body, DateTimeOffset now)
    {
        var timestamp = now.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var message = Encoding.UTF8.GetBytes(timestamp + body);
        var signer = new Ed25519Signer();
        signer.Init(true, _privateKey);
        signer.BlockUpdate(message, 0, message.Length);
        return (Convert.ToHexString(signer.GenerateSignature()).ToLowerInvariant(), timestamp);
    }
}
