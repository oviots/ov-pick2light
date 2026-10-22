using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using P2L.CLI.Configuration;
using P2L.CLI.Infrastructure;

namespace P2L.UnitTests;

public sealed class AxiomLoggerTests
{
    private const string Token = "unit-only-axiom-token-92834";
    private const string Endpoint = "https://logs.invalid/v1/ingest/p2l-test";

    [Fact]
    public void InvalidCredentialsEndpointsAndNumericBoundsAreRejectedBeforeHttp()
    {
        Assert.Equal(3000, new AxiomLoggingOptions().RequestTimeoutMs);
        Assert.Equal(1000, new AxiomLoggingOptions().ShutdownTimeoutMs);
        Assert.Equal(LogLevel.Debug, new AxiomLoggingOptions().MinimumLevel);
        using var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var invalid = new[]
        {
            Options(token: ""), Options(token: "contains whitespace"),
            Options(endpoint: "http://logs.invalid/v1/ingest/p2l-test"),
            Options(endpoint: "https://user:password@logs.invalid/v1/ingest/p2l-test"),
            Options(endpoint: "https://logs.invalid/v1/ingest/p2l-test?token=unsafe"),
            Options(queue: 0), Options(batch: 0), Options(flush: 0),
            Options(request: 0), Options(shutdown: 0)
        };

        foreach (var options in invalid)
            Assert.ThrowsAny<ArgumentException>(() =>
            {
                using var provider = new AxiomLoggerProvider(options, http);
            });

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ProviderLevelSuppressesDebugAndEmitsStructuredScalarSchema()
    {
        using var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        await using var provider = new AxiomLoggerProvider(Options(batch: 1), http);
        var logger = provider.CreateLogger("P2L.Unit.Hardware");
        Assert.False(logger.IsEnabled(LogLevel.Debug));
        Assert.False(logger.IsEnabled(LogLevel.None));
        Assert.True(logger.IsEnabled(LogLevel.Information));
        logger.LogDebug("local-only debug");
        logger.LogInformation("device {Address} quantity {Quantity} active {Active}", "0008", 2, true);

        await Until(() => provider.SentCount == 1);
        var request = Assert.Single(handler.Requests);
        var row = Assert.Single(request.Rows);
        Assert.True(DateTimeOffset.TryParse(row.GetProperty("_time").GetString(), out _));
        Assert.True(Guid.TryParse(row.GetProperty("event_id").GetString(), out _));
        Assert.Equal("Information", row.GetProperty("level").GetString());
        Assert.Equal("P2L.Unit.Hardware", row.GetProperty("category").GetString());
        Assert.Equal("unit-bridge", row.GetProperty("service").GetString());
        Assert.Equal("test", row.GetProperty("environment").GetString());
        Assert.Equal("unit-instance", row.GetProperty("instance").GetString());
        Assert.Equal("p2l-bridge", row.GetProperty("client_id").GetString());
        Assert.True(Guid.TryParse(row.GetProperty("run_id").GetString(), out _));
        Assert.Equal(Environment.ProcessId, row.GetProperty("process_id").GetInt32());
        var properties = row.GetProperty("properties");
        Assert.Equal("0008", properties.GetProperty("Address").GetString());
        Assert.Equal(2, properties.GetProperty("Quantity").GetInt32());
        Assert.True(properties.GetProperty("Active").GetBoolean());
        Assert.Equal("application/x-ndjson", request.ContentType);
        Assert.Equal("POST", request.Method);
        Assert.Equal("timestamp-field=_time", request.Uri.Query.TrimStart('?'));
        Assert.Equal(0, provider.DroppedCount);
    }

    [Fact]
    public async Task LargeBatchesRespectWireByteCapAndDisposeFlushesEveryTailRecordOnce()
    {
        using var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        await using var provider = new AxiomLoggerProvider(Options(batch: 100, flush: 60000), http);
        var logger = provider.CreateLogger("batch");
        for (var index = 0; index < 60; index++)
        {
            var fields = Enumerable.Range(0, 12)
                .Select(n => new KeyValuePair<string, object?>("field" + n, new string('x', 850)))
                .Append(new("index", index)).ToArray();
            logger.Log(LogLevel.Information, new EventId(1), fields, null, static (_, _) => "large batch");
        }

        await Until(() => handler.Requests.Count >= 2);
        await provider.DisposeAsync();
        var requests = handler.Requests.ToArray();
        Assert.All(requests, request =>
        {
            Assert.InRange(Encoding.UTF8.GetByteCount(request.Body), 1, 256 * 1024);
            Assert.InRange(request.Rows.Length, 1, 100);
        });
        var rows = requests.SelectMany(r => r.Rows).ToArray();
        Assert.Equal(60, rows.Length);
        Assert.Equal(60, rows.Select(r => r.GetProperty("event_id").GetString()).Distinct().Count());
        Assert.Equal(Enumerable.Range(0, 60), rows.Select(r => r.GetProperty("properties").GetProperty("index").GetInt32()).Order());
        Assert.Equal(60, provider.SentCount);
        Assert.Equal(0, provider.DroppedCount);
    }

    [Fact]
    public async Task CountThresholdAndIntervalEachFlushWithoutWaitingForShutdown()
    {
        using var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        await using (var provider = new AxiomLoggerProvider(Options(batch: 2, flush: 60000), http))
        {
            var logger = provider.CreateLogger("threshold");
            logger.LogInformation("first");
            logger.LogInformation("second");
            await Until(() => provider.SentCount == 2);
            Assert.Equal(2, Assert.Single(handler.Requests).Rows.Length);
        }

        using var intervalHandler = new RecordingHandler();
        using var intervalHttp = new HttpClient(intervalHandler);
        await using var timed = new AxiomLoggerProvider(Options(batch: 100, flush: 25), intervalHttp);
        timed.CreateLogger("interval").LogInformation("partial batch");
        await Until(() => timed.SentCount == 1);
        Assert.Single(Assert.Single(intervalHandler.Requests).Rows);
    }

    [Fact]
    public async Task StalledHttpAndFullQueueNeverBlockLoggingOrUnboundedShutdown()
    {
        using var handler = new RecordingHandler(async (_, _, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("Unreachable");
        });
        using var http = new HttpClient(handler);
        var provider = new AxiomLoggerProvider(Options(queue: 2, batch: 1, request: 5000, shutdown: 100), http);
        var logger = provider.CreateLogger("backpressure");
        try
        {
            logger.LogInformation("in flight");
            await handler.FirstRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var elapsed = Stopwatch.StartNew();
            for (var i = 0; i < 200; i++) logger.LogInformation("queued {Index}", i);
            elapsed.Stop();
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(1), "Logging waited for a stalled HTTP request.");
            Assert.True(provider.DroppedCount >= 198);
            await provider.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(0, provider.SentCount);
            Assert.Equal(201, provider.DroppedCount);
            await Until(() => handler.CancelledRequests == 1);
            Assert.Equal(1, handler.CancelledRequests);
        }
        finally { await provider.DisposeAsync(); }
    }

    [Theory]
    [InlineData("503")]
    [InlineData("429")]
    [InlineData("read-failure")]
    public async Task AnySendOrReceiptFailureIsSkippedWithoutResending(string failure)
    {
        using var handler = new RecordingHandler((_, _, _) => Task.FromResult(failure == "read-failure"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new FailingReadStream()) }
            : Response((HttpStatusCode)int.Parse(failure))));
        using var http = new HttpClient(handler);
        await using var provider = new AxiomLoggerProvider(Options(batch: 1), http);
        provider.CreateLogger("skip-failure").LogWarning("synthetic export failure");
        await Until(() => provider.DroppedCount == 1);
        await provider.DisposeAsync();
        Assert.Single(handler.Requests);
        Assert.Equal(0, provider.SentCount);
        Assert.Equal(1, provider.DroppedCount);
    }

    [Fact]
    public async Task AuthenticationFailureIsDroppedOnceAndShutdownDoesNotDoubleCount()
    {
        using var handler = new RecordingHandler((_, _, _) => Task.FromResult(Response(HttpStatusCode.Unauthorized)));
        using var http = new HttpClient(handler);
        var provider = new AxiomLoggerProvider(Options(batch: 1, shutdown: 50), http);
        try
        {
            provider.CreateLogger("failure").LogError("will fail");
            await Until(() => provider.DroppedCount == 1);
            await provider.DisposeAsync();
            Assert.Single(handler.Requests);
            Assert.Equal(0, provider.SentCount);
            Assert.Equal(1, provider.DroppedCount);
        }
        finally { await provider.DisposeAsync(); }
    }

    [Fact]
    public async Task RequestTimeoutCancelsTheSingleAttemptWithoutRetry()
    {
        using var handler = new RecordingHandler(async (_, _, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("Unreachable");
        });
        using var http = new HttpClient(handler);
        await using var provider = new AxiomLoggerProvider(Options(batch: 1, request: 30), http);
        provider.CreateLogger("timeout").LogInformation("bounded request");
        await Until(() => provider.DroppedCount == 1 && handler.CancelledRequests == 1);
        Assert.Single(handler.Requests);
        Assert.Equal(1, handler.CancelledRequests);
        Assert.Equal(0, provider.SentCount);
    }

    [Fact]
    public async Task PartialSuccessfulIngestCountsFailuresWithoutResendingAcceptedEvents()
    {
        using var handler = new RecordingHandler((_, _, _) => Task.FromResult(
            Response(HttpStatusCode.OK, "{\"ingested\":1,\"failed\":1}")));
        using var http = new HttpClient(handler);
        await using var provider = new AxiomLoggerProvider(Options(batch: 2), http);
        var logger = provider.CreateLogger("partial");
        logger.LogInformation("accepted entry");
        logger.LogInformation("rejected entry");
        await Until(() => provider.SentCount == 1 && provider.DroppedCount == 1);
        await provider.DisposeAsync();
        Assert.Single(handler.Requests);
        Assert.Equal(2, Assert.Single(handler.Requests).Rows.Length);
    }

    [Fact]
    public async Task ConfiguredAndSensitiveStateSecretsAreAbsentFromMessagePropertiesAndException()
    {
        const string brokerSecret = "unit-only-broker-secret-832";
        const string password = "state-password-not-configured-492";
        using var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        await using var provider = new AxiomLoggerProvider(Options(batch: 1), http, [brokerSecret]);
        var exception = new IOException($"authorization={Token}; broker={brokerSecret}; password={password}; short=abc; long=abcdef");
        provider.CreateLogger("secrets").LogWarning(exception,
            "token {ApiToken}; password {Password}; broker {Endpoint}; qty {Quantity}; overlaps {PasswordShort}/{PasswordLong}",
            Token, password, brokerSecret, 3, "abc", "abcdef");
        await Until(() => provider.SentCount == 1);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("Bearer " + Token, request.Authorization);
        foreach (var secret in new[] { Token, brokerSecret, password }) Assert.DoesNotContain(secret, request.Body);
        Assert.DoesNotContain("[REDACTED]def", request.Body); // Long dynamic secrets must be removed before their prefixes.
        var row = Assert.Single(request.Rows);
        Assert.Contains("[REDACTED]", row.GetProperty("message").GetString());
        Assert.Contains("[REDACTED]", row.GetProperty("exception").GetString());
        Assert.DoesNotContain("abcdef", row.GetProperty("message").GetString());
        Assert.DoesNotContain("abcdef", row.GetProperty("exception").GetString());
        Assert.Equal("[REDACTED]", row.GetProperty("properties").GetProperty("PasswordLong").GetString());
        Assert.Equal("[REDACTED]", row.GetProperty("properties").GetProperty("Password").GetString());
        Assert.Equal(3, row.GetProperty("properties").GetProperty("Quantity").GetInt32());
    }

    [Fact]
    public async Task OversizedRecordsAndThrowingFormattersCannotThrowOrPoisonTheWorker()
    {
        using var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        await using var provider = new AxiomLoggerProvider(Options(batch: 1), http);
        var logger = provider.CreateLogger("bad-record");
        var oversized = Enumerable.Range(0, 24)
            .Select(n => new KeyValuePair<string, object?>("field" + n, new string('x', 1024))).ToArray();
        var error = Record.Exception(() =>
        {
            logger.Log(LogLevel.Information, default, oversized, null, static (_, _) => "oversized state");
            logger.Log(LogLevel.Warning, default, 1, null, static (_, _) => throw new InvalidOperationException("formatter failure"));
        });
        Assert.Null(error);
        Assert.Equal(2, provider.DroppedCount);
        logger.LogInformation("worker still healthy");
        await Until(() => provider.SentCount == 1);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task DisabledInvalidExtensionsSkipRegistrationAndOwnedDiFactoryFlushesOnDispose()
    {
        foreach (var enabled in new[] { false, true })
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Axiom:Enabled"] = enabled.ToString(),
                ["Axiom:ApiToken"] = enabled ? "" : Token,
                ["Axiom:Endpoint"] = Endpoint
            }).Build();
            var services = new ServiceCollection();
            services.AddLogging(builder => { builder.ClearProviders(); builder.AddAxiom(config); });
            await using var skipped = services.BuildServiceProvider();
            Assert.DoesNotContain(skipped.GetServices<ILoggerProvider>(), p => p is AxiomLoggerProvider);
        }

        using var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var cloud = new AxiomLoggerProvider(Options(batch: 100, flush: 60000), http);
        var owned = new ServiceCollection();
        owned.AddLogging(builder => builder.ClearProviders());
        owned.AddSingleton<ILoggerProvider>(_ => cloud);
        var container = owned.BuildServiceProvider();
        var logger = container.GetRequiredService<ILoggerFactory>().CreateLogger("shutdown");
        logger.LogInformation("tail must flush through DI ownership");
        await container.DisposeAsync();
        await cloud.DisposeAsync();
        cloud.Dispose();
        logger.LogInformation("late log is ignored");
        Assert.Equal(1, cloud.SentCount);
        Assert.Equal(0, cloud.DroppedCount);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(null, "p2l-bridge")]
    [InlineData("primary-bridge-01", "primary-bridge-01")]
    public async Task ClientIdentityComesFromPrimaryMqttConfigAndIgnoresBackupAndAxiomOverrides(string? configured, string expected)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Axiom:Enabled"] = "true",
            ["Axiom:ApiToken"] = Token,
            ["Axiom:Endpoint"] = Endpoint,
            ["Axiom:ClientId"] = "ignored-cloud-override",
            ["Axiom:BatchSize"] = "1",
            ["Mqtt:ClientId"] = configured,
            ["Mqtt:BackupBroker:ClientId"] = "ignored-backup-bridge"
        }).Build();
        var options = AxiomLoggingOptions.FromConfiguration(config);
        Assert.Equal(expected, options.ClientId);
        Assert.Equal(LogLevel.Debug, options.MinimumLevel);

        using var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        await using var provider = new AxiomLoggerProvider(options, http);
        provider.CreateLogger("P2L.Mqtt.Transport.MqttChannel").LogDebug("identity through primary or backup");
        await Until(() => provider.SentCount == 1);
        var row = Assert.Single(Assert.Single(handler.Requests).Rows);
        Assert.Equal(expected, row.GetProperty("client_id").GetString());
        Assert.DoesNotContain("ignored-cloud-override", row.GetRawText());
        Assert.DoesNotContain("ignored-backup-bridge", row.GetRawText());
        Assert.Equal(Environment.ProcessId, row.GetProperty("process_id").GetInt32());
    }

    [Fact]
    public async Task RunIdentityIsStableAcrossEveryCategoryAndChangesForARecreatedProvider()
    {
        using var firstHandler = new RecordingHandler();
        using var firstHttp = new HttpClient(firstHandler);
        await using (var first = new AxiomLoggerProvider(Options(batch: 100, flush: 60000, client: "warehouse-primary"), firstHttp))
        {
            foreach (var category in new[] { "P2L.Hardware.Transport.TcpTransport", "P2L.Mqtt.MqttCommandDispatcher", "P2L.Core.PickingEngine", "P2L.CLI.Commands.ServeCommand" })
                first.CreateLogger(category).LogInformation("same provider {Category}", category);
            await first.DisposeAsync();
        }

        var firstRows = firstHandler.Requests.SelectMany(r => r.Rows).ToArray();
        Assert.Equal(4, firstRows.Length);
        Assert.Equal(4, firstRows.Select(row => row.GetProperty("category").GetString()).Distinct().Count());
        var firstRun = Assert.Single(firstRows.Select(row => row.GetProperty("run_id").GetString()).Distinct());
        Assert.True(Guid.TryParse(firstRun, out _));
        Assert.All(firstRows, row =>
        {
            Assert.Equal("warehouse-primary", row.GetProperty("client_id").GetString());
            Assert.Equal(Environment.ProcessId, row.GetProperty("process_id").GetInt32());
        });
        Assert.Equal(4, firstRows.Select(row => row.GetProperty("event_id").GetString()).Distinct().Count());

        using var nextHandler = new RecordingHandler();
        using var nextHttp = new HttpClient(nextHandler);
        await using var next = new AxiomLoggerProvider(Options(batch: 1, client: "warehouse-primary"), nextHttp);
        next.CreateLogger("P2L.Core.PickingEngine").LogError("new provider in the same process");
        await Until(() => next.SentCount == 1);
        var nextRow = Assert.Single(Assert.Single(nextHandler.Requests).Rows);
        Assert.True(Guid.TryParse(nextRow.GetProperty("run_id").GetString(), out _));
        Assert.NotEqual(firstRun, nextRow.GetProperty("run_id").GetString());
        Assert.Equal("warehouse-primary", nextRow.GetProperty("client_id").GetString());
        Assert.Equal(Environment.ProcessId, nextRow.GetProperty("process_id").GetInt32());
    }

    [Fact]
    public async Task ActualAxiomRegistrationKeepsTheSameConfiguredCategoryRulesAsLocalFile()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Logging:LogLevel:Default"] = "Information",
            ["Logging:LogLevel:P2L.Hardware"] = "Debug",
            ["Logging:LogLevel:P2L.Mqtt"] = "Debug",
            ["Axiom:Enabled"] = "true",
            ["Axiom:ApiToken"] = Token,
            ["Axiom:Endpoint"] = Endpoint,
            ["Axiom:BatchSize"] = "100",
            ["Axiom:FlushIntervalMs"] = "60000",
            ["Mqtt:ClientId"] = "parity-primary"
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddConfiguration(config.GetSection("Logging"));
            builder.AddAxiom(config);
        });
        var registration = Assert.Single(services, d => d.ServiceType == typeof(ILoggerProvider));
        Assert.NotNull(registration.ImplementationFactory);
        using var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var cloud = new AxiomLoggerProvider(AxiomLoggingOptions.FromConfiguration(config), http);
        // Keep the real extension's filter configuration, but never construct its real HTTP client.
        services[services.IndexOf(registration)] = ServiceDescriptor.Singleton<ILoggerProvider>(_ => cloud);
        var directory = Path.Combine(Path.GetTempPath(), "p2l-axiom-parity-" + Guid.NewGuid().ToString("N"));
        using var local = new RotatingFileLoggerProvider(directory, 1024 * 1024, LogLevel.Debug);
        services.AddSingleton<ILoggerProvider>(_ => local);
        await using var container = services.BuildServiceProvider();
        try
        {
            var factory = container.GetRequiredService<ILoggerFactory>();
            var core = factory.CreateLogger("P2L.Core.PickingEngine");
            var hardware = factory.CreateLogger("P2L.Hardware.Transport.TcpTransport");
            var mqtt = factory.CreateLogger("P2L.Mqtt.MqttCommandDispatcher");
            Assert.False(core.IsEnabled(LogLevel.Debug));
            Assert.True(hardware.IsEnabled(LogLevel.Debug));
            Assert.True(mqtt.IsEnabled(LogLevel.Debug));
            core.LogDebug("suppressed-core-debug");
            hardware.LogDebug("hardware-debug seq={Seq} addr={Address}", 17, "0008");
            mqtt.LogDebug("mqtt-debug session={Session}", "session-01");
            core.LogInformation("core-info quantity={Quantity}", 2);
            core.LogError(new IOException("synthetic diagnostic failure"), "core-error session={Session}", "session-01");
            await container.DisposeAsync();

            var rows = handler.Requests.SelectMany(r => r.Rows).ToArray();
            Assert.Equal(4, rows.Length);
            Assert.DoesNotContain(rows, row => row.GetProperty("message").GetString()!.Contains("suppressed-core-debug"));
            var hardwareRow = Assert.Single(rows, row => row.GetProperty("category").GetString() == "P2L.Hardware.Transport.TcpTransport");
            Assert.Equal("Debug", hardwareRow.GetProperty("level").GetString());
            Assert.Equal(17, hardwareRow.GetProperty("properties").GetProperty("Seq").GetInt32());
            Assert.Equal("0008", hardwareRow.GetProperty("properties").GetProperty("Address").GetString());
            var mqttRow = Assert.Single(rows, row => row.GetProperty("category").GetString() == "P2L.Mqtt.MqttCommandDispatcher");
            Assert.Equal("Debug", mqttRow.GetProperty("level").GetString());
            Assert.Equal("session-01", mqttRow.GetProperty("properties").GetProperty("Session").GetString());
            var info = Assert.Single(rows, row => row.GetProperty("level").GetString() == "Information");
            Assert.Equal(2, info.GetProperty("properties").GetProperty("Quantity").GetInt32());
            var error = Assert.Single(rows, row => row.GetProperty("level").GetString() == "Error");
            Assert.Contains("synthetic diagnostic failure", error.GetProperty("exception").GetString());
            Assert.Equal("session-01", error.GetProperty("properties").GetProperty("Session").GetString());
            var run = Assert.Single(rows.Select(row => row.GetProperty("run_id").GetString()).Distinct());
            Assert.True(Guid.TryParse(run, out _));
            Assert.All(rows, row =>
            {
                Assert.Equal("parity-primary", row.GetProperty("client_id").GetString());
                Assert.Equal(Environment.ProcessId, row.GetProperty("process_id").GetInt32());
            });

            var file = string.Join('\n', Directory.GetFiles(directory).Select(File.ReadAllText));
            Assert.DoesNotContain("suppressed-core-debug", file);
            foreach (var marker in new[] { "hardware-debug", "mqtt-debug", "core-info", "core-error", "synthetic diagnostic failure" })
                Assert.Contains(marker, file);
            Assert.Equal(4, cloud.SentCount);
            Assert.Equal(0, cloud.DroppedCount);
        }
        finally
        {
            await container.DisposeAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static AxiomLoggingOptions Options(string endpoint = Endpoint, string token = Token,
        int queue = 256, int batch = 8, int flush = 20, int request = 1000,
        int shutdown = 1500, string client = "p2l-bridge") => new()
    {
        Enabled = true, Endpoint = endpoint, ApiToken = token, MinimumLevel = LogLevel.Information,
        ClientId = client, Service = "unit-bridge", Environment = "test", InstanceName = "unit-instance",
        QueueCapacity = queue, BatchSize = batch, FlushIntervalMs = flush, RequestTimeoutMs = request,
        ShutdownTimeoutMs = shutdown
    };

    private static async Task Until(Func<bool> condition)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            if (deadline.Elapsed > TimeSpan.FromSeconds(8)) throw new TimeoutException("Synthetic cloud logger condition was not reached.");
            await Task.Delay(10);
        }
    }

    private static HttpResponseMessage Accepted(int count) =>
        Response(HttpStatusCode.OK, "{\"ingested\":" + count + ",\"failed\":0}");

    private static HttpResponseMessage Response(HttpStatusCode status, string body = "{}") => new(status)
    { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class FailingReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("Synthetic receipt read failure");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromException<int>(new IOException("Synthetic receipt read failure"));
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed record CapturedRequest(string Body, string? Authorization, string? ContentType, string Method, Uri Uri)
    {
        public JsonElement[] Rows => Body.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => { using var json = JsonDocument.Parse(line); return json.RootElement.Clone(); }).ToArray();
    }

    private sealed class RecordingHandler(Func<CapturedRequest, int, CancellationToken, Task<HttpResponseMessage>>? response = null)
        : HttpMessageHandler
    {
        private int _attempts, _cancelled;
        public readonly ConcurrentQueue<CapturedRequest> Requests = new();
        public readonly TaskCompletionSource FirstRequest = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CancelledRequests => Volatile.Read(ref _cancelled);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var snapshot = new CapturedRequest(await request.Content!.ReadAsStringAsync(ct),
                request.Headers.Authorization?.ToString(), request.Content.Headers.ContentType?.MediaType,
                request.Method.Method, request.RequestUri!);
            Requests.Enqueue(snapshot);
            var attempt = Interlocked.Increment(ref _attempts);
            FirstRequest.TrySetResult();
            try { return response is null ? Accepted(snapshot.Rows.Length) : await response(snapshot, attempt, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                Interlocked.Increment(ref _cancelled);
                throw;
            }
        }
    }
}
