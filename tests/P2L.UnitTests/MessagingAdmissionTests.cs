using System.Text;
using System.Text.Json;
using P2L.Mqtt;

namespace P2L.UnitTests;

public sealed class MessagingAdmissionTests
{
    [Fact]
    public async Task ExecutionBudgetIncludesTimeWaitingInAdmissionQueue()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tokens = new List<bool>();
        int calls = 0;
        var queue = new CommandAdmissionQueue(2, async (_, ct) =>
        {
            if (++calls == 1) { entered.SetResult(); await release.Task; }
            else tokens.Add(ct.IsCancellationRequested);
        }, CancellationToken.None, _ => { }, executionTimeoutMs: 40);
        queue.TryEnqueue(Encoding.UTF8.GetBytes("{\"commandId\":\"one\"}"));
        await entered.Task;
        queue.TryEnqueue(Encoding.UTF8.GetBytes("{\"commandId\":\"two\"}"));
        await Task.Delay(60);
        release.SetResult();
        await queue.StopAsync();
        Assert.Equal(new[] { true }, tokens);
    }

    [Fact]
    public async Task CapacityIncludesRunningWorkAndOnlyExactIdentifiedRetryCoalesces()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var queue = new CommandAdmissionQueue(2, async (_, _) =>
        {
            Interlocked.Increment(ref calls); entered.TrySetResult(); await release.Task;
        }, CancellationToken.None, ex => throw ex);
        var one = Encoding.UTF8.GetBytes("{\"type\":\"get_snapshot\",\"commandId\":\"one\"}");
        Assert.Equal(CommandAdmission.Accepted, queue.TryEnqueue(one));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(CommandAdmission.Duplicate, queue.TryEnqueue(one));
        // A conflicting payload must reach dispatcher conflict validation, not be hidden.
        Assert.Equal(CommandAdmission.Conflict, queue.TryEnqueue(Encoding.UTF8.GetBytes("{\"type\":\"terminate_all\",\"commandId\":\"one\"}")));
        Assert.Equal(CommandAdmission.Accepted, queue.TryEnqueue(Encoding.UTF8.GetBytes("{\"type\":\"get_snapshot\",\"commandId\":\"two\"}")));
        Assert.Equal(2, queue.Count);
        Assert.Equal(CommandAdmission.Full, queue.TryEnqueue(Encoding.UTF8.GetBytes("{\"commandId\":\"three\"}")));
        Assert.Equal(1, calls);
        release.SetResult(); await queue.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, calls); Assert.Equal(0, queue.Count);
        Assert.Equal(CommandAdmission.Stopped, queue.TryEnqueue(one));
    }

    [Fact]
    public async Task LegacyReceiptEligibleRetryCoalescesBeforeFullQueueRejection()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = new CommandAdmissionQueue(1, async (_, _) => { entered.TrySetResult(); await release.Task; }, CancellationToken.None, _ => { });
        var legacy = Encoding.UTF8.GetBytes("{\"type\":\"light_on\",\"correlationId\":\"legacy-session\",\"address\":\"0001\"}");
        Assert.Equal(CommandAdmission.Accepted, queue.TryEnqueue(legacy));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var rejected = false;
        Assert.Equal(CommandAdmission.Duplicate, queue.TryEnqueue(legacy, () => rejected = true));
        Assert.False(rejected);
        release.SetResult(); await queue.StopAsync();
    }

    [Fact]
    public async Task AnonymousCommandsAreNotSilentlyCoalescedAndStopDrainsAcceptedWork()
    {
        using var stop = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0; var cancelled = 0;
        var queue = new CommandAdmissionQueue(2, async (_, ct) =>
        {
            Interlocked.Increment(ref calls); entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        }, stop.Token, ex => { if (ex is OperationCanceledException) Interlocked.Increment(ref cancelled); });
        var legacy = Encoding.UTF8.GetBytes("{\"type\":\"get_snapshot\"}");
        Assert.Equal(CommandAdmission.Accepted, queue.TryEnqueue(legacy));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(CommandAdmission.Accepted, queue.TryEnqueue(legacy));
        var drained = queue.StopAsync(); stop.Cancel(); await drained.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, calls); Assert.Equal(2, cancelled); Assert.Equal(0, queue.Count);
    }
}
