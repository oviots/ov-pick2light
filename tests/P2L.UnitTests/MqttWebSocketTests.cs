using System.Text;
using System.Text.Json;
using System.Net.Security;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;
using P2L.Mqtt;
using P2L.Mqtt.Configuration;

namespace P2L.UnitTests;

public sealed class MqttWebSocketTests
{
    [Theory]
    [InlineData("ws://localhost:8083/mqtt?tenant=p2l&block=01", true, false)]
    [InlineData("wss://localhost:8084/p2l/mqtt?tenant=p2l&block=01", false, true)]
    public void ExplicitWebSocketUrlPreservesEndpointAndDeterminesTls(string url, bool configuredTls, bool expectedTls)
    {
        var options = MqttBridgeService.BuildOptions(new MqttSettings
        {
            Host = "ignored.invalid", Port = 19999, WebSocketUrl = url,
            Tls = new() { Enabled = configuredTls },
        });

        var channel = Assert.IsType<MqttClientWebSocketOptions>(options.ChannelOptions);
        Assert.Equal(url, channel.Uri);
        Assert.Contains("mqtt", channel.SubProtocols);
        Assert.Equal(expectedTls, channel.TlsOptions?.UseTls ?? false);
    }

    [Fact]
    public void PlainWebSocketIgnoresDefaultEnabledTlsAndDoesNotLoadUnusedCertificates()
    {
        var options = MqttBridgeService.BuildOptions(new MqttSettings
        {
            WebSocketUrl = "ws://localhost:8083/mqtt",
            Tls = new()
            {
                // Enabled defaults to true. These paths must not be used by plaintext WS.
                CaCertPath = "/nonexistent/p2l-test-ca.pem",
                ClientCertPath = "/nonexistent/p2l-test-client.pfx",
            },
        });
        var channel = Assert.IsType<MqttClientWebSocketOptions>(options.ChannelOptions);
        Assert.False(channel.TlsOptions?.UseTls ?? false);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData(" \t", false)]
    [InlineData(null, true)]
    public void MissingWebSocketUrlRetainsLegacyTcpHostPortAndTls(string? url, bool tls)
    {
        var options = MqttBridgeService.BuildOptions(new MqttSettings
        {
            Host = "legacy.example", Port = 18883, WebSocketUrl = url,
            Tls = new() { Enabled = tls },
        });
        var channel = Assert.IsType<MqttClientTcpOptions>(options.ChannelOptions);
        var endpoint = Assert.IsType<DnsEndPoint>(channel.RemoteEndpoint);
        Assert.Equal("legacy.example", endpoint.Host);
        Assert.Equal(18883, endpoint.Port);
        Assert.Equal(tls, channel.TlsOptions?.UseTls ?? false);
    }

    [Theory]
    [InlineData("ws://localhost:8083/mqtt")]
    [InlineData("wss://localhost:8084/mqtt")]
    public void WebSocketRetainsCredentialsPersistentSessionAndLastWill(string url)
    {
        var options = MqttBridgeService.BuildOptions(new MqttSettings
        {
            WebSocketUrl = url, ClientId = "stable-bridge", Username = "p2l-user", Password = "p2l-test-password",
            SessionExpirySeconds = 1234, StatusTopic = "p2l/01/status",
        });

        Assert.Equal("stable-bridge", options.ClientId);
        Assert.NotNull(options.Credentials);
        Assert.Equal("p2l-user", options.Credentials.GetUserName(options));
        Assert.Equal("p2l-test-password", Encoding.UTF8.GetString(options.Credentials.GetPassword(options)));
        Assert.Equal(MqttProtocolVersion.V500, options.ProtocolVersion);
        Assert.False(options.CleanSession);
        Assert.Equal(1234u, options.SessionExpiryInterval);
        Assert.Equal("p2l/01/status", options.WillTopic);
        Assert.True(options.WillRetain);
        Assert.Equal(MqttQualityOfServiceLevel.AtLeastOnce, options.WillQualityOfServiceLevel);
        using var will = JsonDocument.Parse(options.WillPayload);
        Assert.Equal("bridge_status", will.RootElement.GetProperty("type").GetString());
        Assert.False(will.RootElement.GetProperty("online").GetBoolean());
        Assert.False(will.RootElement.GetProperty("gatewayConnected").GetBoolean());
    }

    [Fact]
    public void PrimaryAndBackupChooseTheirOwnWebSocketEndpoint()
    {
        var settings = new MqttSettings
        {
            WebSocketUrl = "wss://primary.example:8084/mqtt?role=primary",
            BackupBroker = new()
            {
                WebSocketUrl = "ws://localhost:8083/mqtt?role=backup",
                ClientId = "stable-backup", Tls = new() { Enabled = true },
            },
        };
        var primary = Assert.IsType<MqttClientWebSocketOptions>(MqttBridgeService.BuildOptions(settings).ChannelOptions);
        var backup = Assert.IsType<MqttClientWebSocketOptions>(MqttBridgeService.BuildOptions(settings.BackupBroker).ChannelOptions);
        Assert.Equal(settings.WebSocketUrl, primary.Uri);
        Assert.Equal(settings.BackupBroker.WebSocketUrl, backup.Uri);
        Assert.True(primary.TlsOptions.UseTls);
        Assert.False(backup.TlsOptions?.UseTls ?? false);
    }

    [Theory]
    [InlineData(SslPolicyErrors.None, true)]
    [InlineData(SslPolicyErrors.RemoteCertificateNameMismatch, false)]
    [InlineData(SslPolicyErrors.RemoteCertificateChainErrors, false)]
    [InlineData(SslPolicyErrors.RemoteCertificateNotAvailable, false)]
    public void DefaultWssValidationRequiresSuccessfulOsCertificatePolicy(SslPolicyErrors errors, bool expected)
    {
        var options = MqttBridgeService.BuildOptions(new MqttSettings
        {
            WebSocketUrl = "wss://localhost:8084/mqtt", Tls = new() { Enabled = false },
        });
        var channel = Assert.IsType<MqttClientWebSocketOptions>(options.ChannelOptions);
        var validate = channel.TlsOptions.CertificateValidationHandler;
        Assert.NotNull(validate);
        using var certificates = new Certificates();
        using var chain = new X509Chain();
        Assert.Equal(expected, validate(new MqttClientCertificateValidationEventArgs(
            certificates.Server, chain, errors, channel)));
    }

    [Fact]
    public void WssCustomCaValidatesServerChainAndStillRejectsWrongNameOrDifferentCa()
    {
        using var certificates = new Certificates();
        var options = MqttBridgeService.BuildOptions(new MqttSettings
        {
            WebSocketUrl = "wss://localhost:8084/mqtt",
            Tls = new() { Enabled = false, CaCertPath = certificates.CaPath },
        });
        var channel = Assert.IsType<MqttClientWebSocketOptions>(options.ChannelOptions);
        var validate = channel.TlsOptions.CertificateValidationHandler;
        Assert.NotNull(validate);
        using var chain = new X509Chain();
        Assert.True(validate(new MqttClientCertificateValidationEventArgs(
            certificates.Server, chain, SslPolicyErrors.RemoteCertificateChainErrors, channel)));
        Assert.False(validate(new MqttClientCertificateValidationEventArgs(
            certificates.Server, chain, SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateChainErrors, channel)));
        using var otherCaKey = RSA.Create(2048);
        using var otherCa = Certificates.CreateCa(otherCaKey);
        using var foreignServer = Certificates.CreateServer(otherCa);
        Assert.False(validate(new MqttClientCertificateValidationEventArgs(
            foreignServer, chain, SslPolicyErrors.RemoteCertificateChainErrors, channel)));
    }

    [Fact]
    public void WssRetainsConfiguredClientCertificateAndExplicitUntrustedOptIn()
    {
        using var certificates = new Certificates();
        var options = MqttBridgeService.BuildOptions(new MqttSettings
        {
            WebSocketUrl = "wss://localhost:8084/mqtt",
            Tls = new()
            {
                Enabled = false, AllowUntrustedCerts = true,
                ClientCertPath = certificates.ClientPath, CertPassword = Certificates.Password,
            },
        });
        var channel = Assert.IsType<MqttClientWebSocketOptions>(options.ChannelOptions);
        Assert.True(channel.TlsOptions.UseTls);
        Assert.NotNull(channel.TlsOptions.ClientCertificatesProvider);
        Assert.NotNull(channel.TlsOptions.CertificateValidationHandler);
        using var chain = new X509Chain();
        Assert.True(channel.TlsOptions.CertificateValidationHandler(new MqttClientCertificateValidationEventArgs(
            certificates.Server, chain, SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch, channel)));
    }

    [Theory]
    [InlineData("not-an-absolute-url")]
    [InlineData("/mqtt")]
    [InlineData("http://localhost:8083/mqtt")]
    [InlineData("https://localhost:8084/mqtt")]
    [InlineData("mqtt://localhost:1883")]
    [InlineData("ws://")]
    [InlineData("ws://localhost:invalid/mqtt")]
    [InlineData("ws://localhost:8083/raw space")]
    [InlineData("wss://localhost:8084/mqtt?invalid=%Q0")]
    [InlineData("ws://user:password@localhost:8083/mqtt")]
    [InlineData("wss://user@localhost:8084/mqtt")]
    [InlineData("wss://localhost:8084/mqtt#fragment")]
    public void InvalidWebSocketEndpointIsRejectedBeforeAnyConnection(string url)
        => Assert.ThrowsAny<ArgumentException>(() => MqttBridgeService.BuildOptions(new MqttSettings { WebSocketUrl = url }));

    private sealed class Certificates : IDisposable
    {
        public const string Password = "p2l-test-only";
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "p2l-ws-cert-" + Guid.NewGuid().ToString("N"));
        private readonly RSA _key = RSA.Create(2048);
        private readonly X509Certificate2 _ca;
        public X509Certificate2 Server { get; }
        public string CaPath => Path.Combine(_directory, "ca.pem");
        public string ClientPath => Path.Combine(_directory, "client.pfx");

        public Certificates()
        {
            Directory.CreateDirectory(_directory);
            _ca = CreateCa(_key);
            Server = CreateServer(_ca);
            File.WriteAllText(CaPath, _ca.ExportCertificatePem());
            using var clientKey = RSA.Create(2048);
            var request = new CertificateRequest("CN=p2l-client", clientKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var client = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(1));
            File.WriteAllBytes(ClientPath, client.Export(X509ContentType.Pfx, Password));
        }

        public static X509Certificate2 CreateCa(RSA key)
        {
            var request = new CertificateRequest("CN=p2l-test-ca", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
            return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
        }

        public static X509Certificate2 CreateServer(X509Certificate2 ca)
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName("localhost");
            request.CertificateExtensions.Add(names.Build());
            return request.Create(ca, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(1), RandomNumberGenerator.GetBytes(16));
        }

        public void Dispose()
        {
            Server.Dispose(); _ca.Dispose(); _key.Dispose();
            Directory.Delete(_directory, true);
        }
    }
}
