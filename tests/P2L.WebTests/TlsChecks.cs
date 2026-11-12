using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using MQTTnet;
using P2L.Mqtt;
using P2L.Mqtt.Configuration;

internal static class TlsChecks
{
    public static async Task RunAsync()
    {
        using var rootKey = RSA.Create(2048);
        var rootRequest = new CertificateRequest("CN=P2L local test CA", rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var root = rootRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-10), DateTimeOffset.UtcNow.AddDays(10));
        foreach (var scenario in new[] {"valid", "wrong-host", "expired", "unknown-ca", "no-ca"})
        {
            using var leafKey = RSA.Create(2048);
            var leafRequest = new CertificateRequest("CN=localhost", leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName(scenario == "wrong-host" ? "other.invalid" : "localhost");
            leafRequest.CertificateExtensions.Add(san.Build());
            leafRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            leafRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection {new("1.3.6.1.5.5.7.3.1")}, true));
            using var signed = leafRequest.Create(root, DateTimeOffset.UtcNow.AddDays(-2),
                scenario == "expired" ? DateTimeOffset.UtcNow.AddDays(-1) : DateTimeOffset.UtcNow.AddDays(2), RandomNumberGenerator.GetBytes(16));
            using var leaf = signed.CopyWithPrivateKey(leafKey);
            using var otherKey = RSA.Create(2048);
            using var other = new CertificateRequest("CN=Unknown CA", otherKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
                .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var server = Task.Run(async () =>
            {
                try
                {
                    using var tcp = await listener.AcceptTcpClientAsync(timeout.Token);
                    using var ssl = new SslStream(tcp.GetStream());
                    await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions {ServerCertificate = leaf}, timeout.Token);
                    var header = new byte[2];
                    await ssl.ReadExactlyAsync(header, timeout.Token);
                    var length = header[1] & 127;
                    var shift = 7;
                    var digit = header[1];
                    while ((digit & 128) != 0)
                    {
                        var next = new byte[1];
                        await ssl.ReadExactlyAsync(next, timeout.Token);
                        digit = next[0]; length |= (digit & 127) << shift; shift += 7;
                    }
                    var packet = new byte[length];
                    await ssl.ReadExactlyAsync(packet, timeout.Token);
                    var wire = Encoding.UTF8.GetString(packet);
                    if (!wire.Contains("fixture-user") || !wire.Contains("fixture-password")) throw new Exception("MQTT credentials changed");
                    await ssl.WriteAsync(packet[6] == 5 ? new byte[] {0x20, 3, 0, 0, 0} : new byte[] {0x20, 2, 0, 0}, timeout.Token);
                    return true;
                }
                catch (Exception ex) when (ex is System.Security.Authentication.AuthenticationException or IOException) { return false; }
            });
            var settings = new MqttSettings {Host = "localhost", Port = ((IPEndPoint)listener.LocalEndpoint).Port,
                Username = "fixture-user", Password = "fixture-password",
                Tls = new MqttTlsSettings {CaCertPem = scenario == "no-ca" ? null : (scenario == "unknown-ca" ? other : root).ExportCertificatePem()}};
            var options = (MqttClientOptions)typeof(MqttBridgeService).GetMethod("BuildOptions", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] {settings})!;
            using var client = new MqttClientFactory().CreateMqttClient();
            var accepted = false;
            try { await client.ConnectAsync(options, timeout.Token); accepted = true; }
            catch (MQTTnet.Exceptions.MqttCommunicationException) { }
            finally { listener.Stop(); }
            var credentialsSent = await server;
            if (accepted != (scenario == "valid") || credentialsSent != accepted) throw new Exception("TLS validation failed: " + scenario);
            Console.WriteLine("PASS: local MQTT TLS " + scenario);
        }
    }
}
