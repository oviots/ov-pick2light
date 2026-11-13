using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using P2L.CLI.Licensing;

static void Require(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
static async Task Reject(Func<Task> action)
{
    try { await action(); }
    catch (Exception) { return; }
    throw new Exception("Expected rejection");
}

using (var defaults = new LicenseApiClient(null, new FakeHandler(request =>
{
    Require(request.RequestUri!.AbsoluteUri == "https://auth-supra.winmart.vn/api/p2l/v1/challenge", "Production license URL default");
    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
        Content = new StringContent("{\"Status\":true,\"Data\":{}}"),
    });
}))) await defaults.PostAsync("challenge", new JsonObject(), default);
Console.WriteLine("PASS: missing license API URL defaults to production");

using var key = RSA.Create(2048);
var code = "P2L-" + new string('A', 48);
var id = Convert.ToHexStringLower(SHA256.HashData(key.ExportSubjectPublicKeyInfo()));
var nonce = new string('b', 64);
var requests = new List<string>();
var reject = false;
var wrongIdentity = false;
var invalidChallenge = false;
var handler = new FakeHandler(async request =>
{
    Require(request.RequestUri!.Scheme == "https", "HTTPS required");
    requests.Add(request.RequestUri.AbsolutePath);
    if (reject) return new HttpResponseMessage(HttpStatusCode.Forbidden);
    var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!.AsObject();
    JsonObject data;
    if (request.RequestUri.AbsolutePath.EndsWith("/challenge"))
    {
        Require((string?)body["deviceId"] == id && (string?)body["role"] == "activation", "Challenge identity/role");
        data = new JsonObject {["challenge"] = invalidChallenge ? "invalid" : nonce};
    }
    else
    {
        Require((string?)body["licenseCode"] == code, "License normalization");
        Require((string?)body["challenge"] == nonce, "Fresh nonce is sent");
        using var publicKey = RSA.Create();
        publicKey.ImportFromPem((string)body["publicKey"]!);
        Require(publicKey.ExportSubjectPublicKeyInfo().SequenceEqual(key.ExportSubjectPublicKeyInfo()), "Retry uses same key");
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(code)));
        Require(publicKey.VerifyData(Encoding.UTF8.GetBytes($"p2l.activate.v2\n{hash}\n{id}\n{nonce}"),
            Convert.FromBase64String((string)body["signature"]!), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1), "Signature matches server contract");
        data = new JsonObject {["deviceId"] = wrongIdentity ? new string('0', 64) : id};
    }
    return new HttpResponseMessage(HttpStatusCode.OK) {Content = new StringContent(new JsonObject {["Status"] = true, ["Data"] = data}.ToJsonString())};
});
using var api = new LicenseApiClient("https://fixture.example", handler);
Require(await DeviceActivation.ActivateAsync(api, key, "  " + code.ToLowerInvariant() + "  ", "test", default) == id, "Activation works");
Require(requests.Count == 2, "Challenge then activate only; no controller/MQTT call");
Console.WriteLine("PASS: activation normalization, signature, identity and API order");

await DeviceActivation.ActivateAsync(api, key, code, "test", default);
Console.WriteLine("PASS: retry preserves the enrolled key");
requests.Clear();
await Reject(() => DeviceActivation.ActivateAsync(api, key, "invalid", "test", default));
Require(requests.Count == 0, "Invalid license must not call API");
Console.WriteLine("PASS: invalid license rejected before HTTP");

invalidChallenge = true;
await Reject(() => DeviceActivation.ActivateAsync(api, key, code, "test", default));
Require(requests.Count == 1, "Invalid challenge must not submit license");
invalidChallenge = false;
Console.WriteLine("PASS: malformed challenge cannot submit activation");

wrongIdentity = true;
await Reject(() => DeviceActivation.ActivateAsync(api, key, code, "test", default));
wrongIdentity = false;
Console.WriteLine("PASS: unexpected machine identity rejected");

reject = true;
await Reject(() => DeviceActivation.ActivateAsync(api, key, code, "test", default));
Console.WriteLine("PASS: API rejection fails closed");
await Reject(() => Task.FromResult(new LicenseApiClient("http://fixture.example")));
Console.WriteLine("PASS: plaintext HTTP rejected");
foreach (var value in new[] {"true", "{}", "{\"ca\":\"\",\"rejectUnauthorized\":true}"})
    Require(RemoteTls.ReadCa(System.Text.Json.JsonDocument.Parse(value).RootElement) is null, "Optional CA must be absent");
Require(RemoteTls.ReadCa(System.Text.Json.JsonDocument.Parse("{\"ca\":\"fixture PEM\",\"rejectUnauthorized\":true}").RootElement) == "fixture PEM", "CA must be passed through");
foreach (var value in new[] {"false", "null", "{\"rejectUnauthorized\":false}", "{\"ca\":42}", "{\"enabled\":false}"})
    await Reject(() => Task.FromResult(RemoteTls.ReadCa(System.Text.Json.JsonDocument.Parse(value).RootElement)));
Console.WriteLine("PASS: legacy TLS, optional CA and verification policy");
Console.WriteLine("All 8 CLI activation/TLS checks passed. No OS vault, broker, controller or UAT was accessed.");

sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> action) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => action(request);
}
