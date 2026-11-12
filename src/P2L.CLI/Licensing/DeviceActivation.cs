using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace P2L.CLI.Licensing;

internal static class DeviceActivation
{
    public static string NormalizeLicense(string value)
    {
        var code = value.Trim().ToUpperInvariant();
        if (code.Length != 52 || !code.StartsWith("P2L-", StringComparison.Ordinal)
            || code[4..].Any(c => !char.IsAsciiHexDigit(c)))
            throw new InvalidOperationException("License must be P2L- followed by 48 hexadecimal characters.");
        return code;
    }

    public static async Task<string> ActivateAsync(LicenseApiClient api, RSA key, string license, string deviceName, CancellationToken ct)
    {
        var code = NormalizeLicense(license);
        var id = Convert.ToHexStringLower(SHA256.HashData(key.ExportSubjectPublicKeyInfo()));
        var result = await api.PostAsync("challenge", new JsonObject {["deviceId"] = id, ["role"] = "activation"}, ct);
        var nonce = result.GetProperty("challenge").GetString();
        if (nonce is null || nonce.Length != 64 || nonce.Any(c => !char.IsAsciiHexDigit(c)))
            throw new InvalidDataException("Invalid activation challenge");
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(code)));
        var signature = Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes($"p2l.activate.v2\n{hash}\n{id}\n{nonce}"),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        var response = await api.PostAsync("activate", new JsonObject
        {
            ["licenseCode"] = code, ["publicKey"] = key.ExportSubjectPublicKeyInfoPem(),
            ["deviceName"] = deviceName, ["challenge"] = nonce, ["signature"] = signature
        }, ct);
        if (response.GetProperty("deviceId").GetString() != id) throw new InvalidDataException("Activation device identity mismatch");
        return id;
    }
}
