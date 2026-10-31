using Moq;
using VibeRails.Services.BertV2;
using Xunit;

namespace Tests.Services.BertV2;

public sealed class IdleBertV2BgeEmbedderTests
{
    [Fact]
    public void LoadsLazilyEvictsOnlyWhenIdleAndReloadsAfterEviction()
    {
        var clock = new Clock();
        var model = new Mock<IBertV2BgeEmbedder>();
        model.Setup(m => m.GenerateEmbedding(It.IsAny<string>())).Returns([1f]);
        var loads = 0;
        using var owner = new IdleBertV2BgeEmbedder(() => { loads++; return model.Object; }, clock);
        Assert.Equal(0, loads);
        owner.GenerateEmbedding("first");
        clock.Advance(TimeSpan.FromMinutes(1));
        owner.GenerateEmbedding("second");
        clock.Advance(TimeSpan.FromMinutes(1)); // stale timer callback cannot evict active model
        Assert.Equal(1, loads);
        model.Verify(m => m.Dispose(), Times.Never);
        clock.Advance(TimeSpan.FromMinutes(1));
        model.Verify(m => m.Dispose(), Times.Once);
        owner.GenerateEmbedding("third");
        Assert.Equal(2, loads);
        owner.Dispose();
        owner.Dispose();
        model.Verify(m => m.Dispose(), Times.Exactly(2));
        Assert.Throws<ObjectDisposedException>(() => owner.GenerateEmbedding("after shutdown"));
    }

    [Fact]
    public async Task EvictionWaitsForAnInFlightInference()
    {
        var clock = new Clock();
        var model = new Mock<IBertV2BgeEmbedder>();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var advanced = new ManualResetEventSlim();
        clock.Advanced = advanced.Set;
        model.Setup(m => m.GenerateEmbedding("slow")).Returns(() =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            return [1f];
        });
        using var owner = new IdleBertV2BgeEmbedder(() => model.Object, clock);
        var inference = Task.Run(() => owner.GenerateEmbedding("slow"), TestContext.Current.CancellationToken);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        var eviction = Task.Run(() => clock.Advance(TimeSpan.FromMinutes(3)), TestContext.Current.CancellationToken);
        Assert.True(advanced.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        model.Verify(m => m.Dispose(), Times.Never);
        release.Set();
        await Task.WhenAll(inference, eviction);
        owner.GenerateEmbedding("again");
        model.Verify(m => m.Dispose(), Times.Never);
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        private Timer? timer;
        public Action? Advanced;
        public override DateTimeOffset GetUtcNow() => now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            timer = new Timer(() => callback(state));
        public void Advance(TimeSpan elapsed) { now += elapsed; Advanced?.Invoke(); timer!.Fire(); }
        private sealed class Timer(Action callback) : ITimer
        {
            private bool disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period) => !disposed;
            public void Fire() { if (!disposed) callback(); }
            public void Dispose() => disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
