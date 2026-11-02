using Xunit;
using SuperSocket.ProtoBase;

namespace IFactory.Interaction.SuperClient.Tests;

public class SchedulerTests
{
    [Fact]
    public async Task AwaiterSupportsEarlyResponseAndTimeout()
    {
        var awaiter = new ImmediatePackageAwaiter<string>();
        awaiter.Write("first");
        awaiter.Write("second");
        Assert.Equal("first", await awaiter.ReadAsync(0));
        Assert.Null(await new ImmediatePackageAwaiter<string>().ReadAsync(10));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await awaiter.ReadAsync(-2));
    }
    [Fact]
    public async Task ImmediateTimeoutReleasesRegistration()
    {
        var scheduler = new ImmediatePackageScheduler<string, string>();
        var first = scheduler.RegisterAwaiter("one");
        Assert.Throws<InvalidOperationException>(() => scheduler.RegisterAwaiter("overlap"));
        Assert.Null(await first.ReadAsync(1));
        string? unmatched = null;
        scheduler.MismatchingPackageReceiveHandler = p => unmatched = p;
        await scheduler.HandlePackage("late");
        Assert.Equal("late", unmatched);
        var second = scheduler.RegisterAwaiter("two");
        await scheduler.HandlePackage("response");
        Assert.Equal("response", await second.ReadAsync(100));
    }
    [Fact]
    public async Task KeyedSchedulerRejectsDuplicatesAndRemovesTimeouts()
    {
        var scheduler = new KeyPackageScheduler<string, StringPackageInfo, StringPackageInfo>();
        var package = new StringPackageInfo { Key = "key" };
        var first = scheduler.RegisterAwaiter(package);
        Assert.Throws<InvalidOperationException>(() => scheduler.RegisterAwaiter(package));
        Assert.Null(await first.ReadAsync(1));
        var second = scheduler.RegisterAwaiter(package);
        await scheduler.HandlePackage(package);
        Assert.Same(package, await second.ReadAsync(100));
    }
    [Fact]
    public async Task KeyedResponsesMayArriveOutOfOrder()
    {
        var scheduler = new KeyPackageScheduler<string, StringPackageInfo, StringPackageInfo>();
        var a = new StringPackageInfo { Key = "a" };
        var b = new StringPackageInfo { Key = "b" };
        var first = scheduler.RegisterAwaiter(a);
        var second = scheduler.RegisterAwaiter(b);
        await scheduler.HandlePackage(b);
        await scheduler.HandlePackage(a);
        Assert.Same(a, await first.ReadAsync(100));
        Assert.Same(b, await second.ReadAsync(100));
    }
}
