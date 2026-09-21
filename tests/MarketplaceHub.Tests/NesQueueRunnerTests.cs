using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

namespace MarketplaceHub.Tests;

[TestClass]
public sealed class NesQueueRunnerTests
{
    [TestMethod]
    public async Task WakeCoalescesAndNeverRunsTwoProcessorsConcurrently()
    {
        var processor = new ConcurrencyDetectingProcessor(4);
        await using var runner = new NesQueueRunner(processor.ProcessNextAsync, TimeSpan.FromMilliseconds(5));
        runner.Start(); runner.Wake(); runner.Wake();
        await processor.Completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(1, processor.MaximumConcurrency);
        Assert.AreEqual(4, processor.CompletedSteps);
    }

    [TestMethod]
    public async Task StopCancelsTheCurrentWaitAndDoesNotStartAnotherStep()
    {
        var processor = new BlockingProcessor();
        await using var runner = new NesQueueRunner(processor.ProcessNextAsync, TimeSpan.FromMinutes(1));
        runner.Start(); runner.Wake(); await processor.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await runner.StopAsync();
        Assert.AreEqual(1, processor.CallCount);
    }

    sealed class ConcurrencyDetectingProcessor(int steps)
    {
        int remaining = steps, concurrent;
        public int MaximumConcurrency { get; private set; }
        public int CompletedSteps { get; private set; }
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<bool> ProcessNextAsync(CancellationToken token)
        {
            var current = Interlocked.Increment(ref concurrent); MaximumConcurrency = Math.Max(MaximumConcurrency, current);
            try
            {
                if (Interlocked.Decrement(ref remaining) < 0) return false;
                await Task.Delay(5, token); CompletedSteps++;
                if (CompletedSteps == steps) Completed.TrySetResult();
                return true;
            }
            finally { Interlocked.Decrement(ref concurrent); }
        }
    }

    sealed class BlockingProcessor
    {
        public int CallCount { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<bool> ProcessNextAsync(CancellationToken token)
        { CallCount++; Started.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); return false; }
    }
}
