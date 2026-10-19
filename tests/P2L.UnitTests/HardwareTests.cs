using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using P2L.Core.Services;
using P2L.Hardware;
using P2L.Hardware.Protocol;
using P2L.Hardware.Transport;
using P2L.Shared.Models;

namespace P2L.UnitTests;

// These tests never start a transport or connect a socket. Reflection drives only
// pending responses, in-memory queues and a NetworkStream test double.
public sealed class HardwareTests
{
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static AioiGateway Gateway() => new("do-not-connect.invalid", 1, NullLogger.Instance);
    private static TcpTransport Transport(AioiGateway gateway) => Field<TcpTransport>(gateway, "_transport");
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static void SetField(object target, string name, object? value) => target.GetType().GetField(name, Private)!.SetValue(target, value);
    private static object Queue(TcpTransport transport) => Field<object>(transport, "_sendQueue");
    private static object Reader(TcpTransport transport) => Queue(transport).GetType().GetProperty("Reader")!.GetValue(Queue(transport))!;
    private static int QueueCount(TcpTransport transport) => (int)Reader(transport).GetType().GetProperty("Count")!.GetValue(Reader(transport))!;
    private static (AioiPacket Packet, CancellationToken CancellationToken) Read(TcpTransport transport)
    {
        var args = new object?[] { null };
        Assert.True((bool)Reader(transport).GetType().GetMethod("TryRead")!.Invoke(Reader(transport), args)!);
        var entry = args[0]!;
        return ((AioiPacket)entry.GetType().GetProperty("Packet")!.GetValue(entry)!,
            (CancellationToken)entry.GetType().GetProperty("CancellationToken")!.GetValue(entry)!);
    }
    private static void Respond(AioiGateway gateway, int seq, string response) =>
        typeof(AioiGateway).GetMethod("OnPacketReceived", Private)!.Invoke(gateway,
            [new AioiPacket { SequenceNo = seq, Command = response }]);
    private static int PendingCount(AioiGateway gateway) =>
        Field<ConcurrentDictionary<int, TaskCompletionSource<AioiDecoder.ParsedResponse>>>(gateway, "_pending").Count;

    [Theory]
    [InlineData("n1")]
    [InlineData("garbage")]
    [InlineData("o-not-an-ack")]
    [InlineData("a00011")]
    public async Task MutationRejectsNonPositiveAcknowledgement(string response)
    {
        await using var gateway = Gateway();
        var command = gateway.TerminateBlockAsync("01");
        var queued = Read(Transport(gateway));
        Respond(gateway, queued.Packet.SequenceNo, response);
        await Assert.ThrowsAsync<InvalidOperationException>(() => command);
        Assert.True(queued.CancellationToken.IsCancellationRequested);
        Assert.Equal(0, PendingCount(gateway));
    }

    [Fact]
    public async Task PositiveAckCompletesMutationAndNackStillThrowsItsSpecificException()
    {
        await using var gateway = Gateway();
        var command = gateway.TerminateBlockAsync("01");
        Respond(gateway, Read(Transport(gateway)).Packet.SequenceNo, "o");
        await command;
        command = gateway.TerminateBlockAsync("01");
        Respond(gateway, Read(Transport(gateway)).Packet.SequenceNo, "n");
        await Assert.ThrowsAsync<AioiNackException>(() => command);
    }

    [Theory]
    [InlineData("o")]
    [InlineData("a00021")]
    public async Task StatusQueryRequiresMatchingModuleStatus(string response)
    {
        await using var gateway = Gateway();
        var command = gateway.QueryModuleStatusAsync("0001");
        Respond(gateway, Read(Transport(gateway)).Packet.SequenceNo, response);
        await Assert.ThrowsAsync<InvalidOperationException>(() => command);
    }

    [Fact]
    public async Task FailedEnqueueCleansPendingResponseRegistration()
    {
        await using var gateway = Gateway();
        var queue = Queue(Transport(gateway));
        var writer = queue.GetType().GetProperty("Writer")!.GetValue(queue)!;
        Assert.True((bool)writer.GetType().GetMethod("TryComplete")!.Invoke(writer, [null])!);
        await Assert.ThrowsAsync<ChannelClosedException>(() => gateway.TerminateBlockAsync("01"));
        Assert.Equal(0, PendingCount(gateway));
    }

    [Fact]
    public async Task CallerCancellationInvalidatesQueuedCommandBeforeItCanConnectOrWrite()
    {
        await using var gateway = Gateway();
        var transport = Transport(gateway);
        using var caller = new CancellationTokenSource();
        var command = gateway.TerminateBlockAsync("01", caller.Token);
        Assert.Equal(1, QueueCount(transport));
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command);
        Assert.Equal(0, PendingCount(gateway));

        // All queued entries are cancelled. A send loop must discard them before
        // reaching EnsureConnectedAsync, so it never attempts DNS/TCP.
        var entry = Read(transport);
        Assert.True(entry.CancellationToken.IsCancellationRequested);
        await transport.EnqueueAsync(entry.Packet.Command, entry.Packet.SequenceNo, CancellationToken.None);
        var replacement = Read(transport);
        Assert.False(replacement.CancellationToken.IsCancellationRequested);
        // Re-queue through the original cancelled envelope without an external write.
        var queue = Queue(transport);
        var writer = queue.GetType().GetProperty("Writer")!.GetValue(queue)!;
        var entryType = queue.GetType().BaseType!.GetGenericArguments()[0];
        var cancelledEntry = Activator.CreateInstance(entryType, entry.Packet, entry.CancellationToken)!;
        Assert.True((bool)writer.GetType().GetMethod("TryWrite")!.Invoke(writer, [cancelledEntry])!);
        writer.GetType().GetMethod("TryComplete")!.Invoke(writer, [null]);
        await ((Task)typeof(TcpTransport).GetMethod("SendLoopAsync", Private)!.Invoke(transport, [CancellationToken.None])!).WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(0, QueueCount(transport));
    }

    [Fact]
    public async Task ResponseDeadlineIncludesTimeWaitingForQueueCapacity()
    {
        await using var gateway = Gateway();
        var transport = Transport(gateway);
        for (int i = 0; i < 512; i++) await transport.EnqueueAsync("Az0000", i);
        await Assert.ThrowsAsync<TimeoutException>(() => gateway.TerminateBlockAsync("01").WaitAsync(TimeSpan.FromSeconds(7)));
        Assert.Equal(512, QueueCount(transport));
        Assert.Equal(0, PendingCount(gateway));
    }

    [Fact]
    public async Task FailedInitializationCanRetryAndConcurrentCallersWaitForItsAck()
    {
        await using var gateway = Gateway();
        var first = gateway.EnsureInitializedAsync();
        Respond(gateway, Read(Transport(gateway)).Packet.SequenceNo, "n1");
        await Assert.ThrowsAsync<InvalidOperationException>(() => first);
        Assert.False(gateway.IsInitialized);

        first = gateway.EnsureInitializedAsync();
        var second = gateway.EnsureInitializedAsync();
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        Assert.Equal(1, QueueCount(Transport(gateway)));
        Respond(gateway, Read(Transport(gateway)).Packet.SequenceNo, "o");
        await Task.WhenAll(first, second);
        Assert.True(gateway.IsInitialized);
        Assert.Equal(0, QueueCount(Transport(gateway)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MixedBlocksAreRejectedBeforeAnyHardwareCommand(bool overwrite)
    {
        await using var gateway = Gateway();
        PickingInstruction[] instructions = [new() { Address = "0001", BlockNo = "01", Quantity = 1 }, new() { Address = "0002", BlockNo = "02", Quantity = 1 }];
        await Assert.ThrowsAsync<ArgumentException>(() => overwrite ? gateway.OverwriteBatchAsync(instructions) : gateway.InstructBatchAsync(instructions));
        Assert.Equal(0, QueueCount(Transport(gateway)));
    }

    [Fact]
    public async Task OversizedBatchIsRejectedBeforeModeCommandsAreQueued()
    {
        await using var gateway = Gateway();
        var instructions = Enumerable.Range(1, 113).Select(i => new PickingInstruction { Address = i.ToString("D4"), BlockNo = "01", Quantity = 1 }).ToArray();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => gateway.InstructBatchAsync(instructions));
        Assert.Equal(0, QueueCount(Transport(gateway)));
    }

    [Theory]
    [InlineData("\u0002xxx0001o\u0003")]
    [InlineData("\u00020019999o\u0003")]
    [InlineData("\u0002001-001o\u0003")]
    [InlineData("\u00020010000\u0003")]
    [InlineData("\u00020010001oX")]
    [InlineData("\u00020000007")]
    public void ParserResynchronizesInvalidOrOldConnectionPrefix(string invalidPrefix)
    {
        var good = new AioiPacket { SequenceNo = 17, Command = "t000100" }.ToBytes();
        var bytes = Encoding.ASCII.GetBytes(invalidPrefix).Concat(good).ToArray();
        var packet = AioiPacket.TryParse(bytes, out int consumed);
        Assert.NotNull(packet);
        Assert.Equal(17, packet.SequenceNo);
        Assert.Equal("t000100", packet.Command);
        Assert.Equal(bytes.Length, consumed);
    }

    [Fact]
    public void ParserRetainsFragmentedFramesAndSeparatesCoalescedPackets()
    {
        var first = new AioiPacket { SequenceNo = 17, Command = "t000100" }.ToBytes();
        for (int i = 0; i < first.Length; i++)
        {
            Assert.Null(AioiPacket.TryParse(first.AsSpan(0, i), out int used));
            Assert.Equal(0, used);
        }
        var second = new AioiPacket { SequenceNo = 18, Command = "o" }.ToBytes();
        var both = first.Concat(second).ToArray();
        Assert.Equal(17, AioiPacket.TryParse(both, out int consumed)!.SequenceNo);
        Assert.Equal(first.Length, consumed);
        Assert.Equal(18, AioiPacket.TryParse(both.AsSpan(consumed), out _)!.SequenceNo);
        var garbage = Enumerable.Repeat((byte)'x', 100_000).ToArray();
        Assert.Null(AioiPacket.TryParse(garbage, out consumed));
        Assert.Equal(garbage.Length, consumed);
    }

    [Fact]
    public async Task HardwareAckWaitsForDurableHandlerAndIsWithheldOnFailure()
    {
        await using var gateway = Gateway();
        var stream = RecordingNetworkStream.Create();
        SetField(Transport(gateway), "_stream", stream);
        Action<ConfirmationEvent> failure = _ => throw new IOException("Simulated durable-store failure");
        gateway.Confirmed += failure;
        Respond(gateway, 0, "t000100");
        Assert.Empty(stream.Writes);
        gateway.Confirmed -= failure;
        bool persisted = false;
        gateway.Confirmed += _ => { Assert.Empty(stream.Writes); persisted = true; };
        Respond(gateway, 0, "t000100");
        Assert.True(persisted);
        var ack = AioiPacket.TryParse(Assert.Single(stream.Writes), out _)!;
        Assert.Equal("O", ack.Command);
        Assert.Equal(0, ack.SequenceNo);
    }

    [Theory]
    [InlineData("t000199")]
    [InlineData("t000100<abcde>")]
    [InlineData("t000100<00001")]
    [InlineData("tX00100")]
    [InlineData("t000100x")]
    [InlineData("t000000")]
    public async Task MalformedInterruptNeverBecomesAPickedItem(string command)
    {
        await using var gateway = Gateway();
        var stream = RecordingNetworkStream.Create();
        SetField(Transport(gateway), "_stream", stream);
        var decoded = AioiDecoder.Parse(command, 0);
        Assert.Equal(AioiDecoder.ResponseKind.Unknown, decoded.Kind);
        Assert.Null(decoded.Event);
        int received = 0;
        gateway.Confirmed += _ => received++;
        Respond(gateway, 0, command);
        Assert.Equal(0, received);
        Assert.Empty(stream.Writes);
    }

    [Theory]
    [InlineData("t000100")]
    [InlineData("t000101")]
    [InlineData("t000110")]
    [InlineData("t000100<00012>")]
    [InlineData("t000100<   12>")]
    public void DocumentedInterruptFormsAreStillAccepted(string command)
    {
        var decoded = AioiDecoder.Parse(command, 0);
        Assert.Equal(AioiDecoder.ResponseKind.Interrupt, decoded.Kind);
        Assert.NotNull(decoded.Event);
    }

    [Fact]
    public async Task ThreeFailedHeartbeatsReconnectWithoutResetOrRelightAndValidReplyRestoresHealth()
    {
        await using var gateway = Gateway();
        await using var heartbeat = new HeartbeatService(gateway, NullLogger<HeartbeatService>.Instance);
        var transport = Transport(gateway);
        var stream = RecordingNetworkStream.Create();
        SetField(transport, "_stream", stream);
        var failed = typeof(HeartbeatService).GetMethod("OnProbeFailed", Private)!;
        failed.Invoke(heartbeat, null);
        failed.Invoke(heartbeat, null);
        Assert.False(stream.Disposed);
        failed.Invoke(heartbeat, null);
        Assert.True(stream.Disposed);
        Assert.Equal(1, Field<int>(gateway, "_protocolFaulted"));
        Assert.Equal(0, QueueCount(transport));
        var probe = gateway.QueryModuleStatusAsync("0000");
        Respond(gateway, Read(transport).Packet.SequenceNo, "a0000?");
        await probe;
        Assert.Equal(0, Field<int>(gateway, "_protocolFaulted"));
    }

    private sealed class RecordingNetworkStream : NetworkStream
    {
        // Never called: the test double is allocated uninitialized and overrides
        // every operation used by transport. It owns no Socket or file handle.
        private RecordingNetworkStream() : base(null!) { }
        public List<byte[]> Writes = null!;
        public bool Disposed;
        public static RecordingNetworkStream Create()
        {
            var stream = (RecordingNetworkStream)RuntimeHelpers.GetUninitializedObject(typeof(RecordingNetworkStream));
            stream.Writes = [];
            return stream;
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Writes.Add(buffer.ToArray());
            return ValueTask.CompletedTask;
        }
        protected override void Dispose(bool disposing) => Disposed = true;
    }
}
