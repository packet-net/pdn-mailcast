using Microsoft.Extensions.Time.Testing;

namespace Mailcast.Receiver.Tests;

/// <summary>Issue #53's "Listen now": the daily allowance, the refusals, and the session it opens.</summary>
public class ListenNowTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Request_WellBeforeTheNextWindow_Opens_ForTheFullDuration()
    {
        var time = new FakeTimeProvider(Noon);
        var service = new ListenNowService(time, null);

        var result = service.Request(Noon, Noon + TimeSpan.FromHours(1), alreadyListening: false);

        Assert.True(result.Ok);
        Assert.Null(result.Reason);
        Assert.Equal(Noon + ListenNowService.Duration, result.Until);
        Assert.Equal(Noon + ListenNowService.Duration, service.ActiveUntil(Noon));
    }

    [Fact]
    public void Request_WithinFiveMinutesOfAWindow_IsRefused()
    {
        var time = new FakeTimeProvider(Noon);
        var service = new ListenNowService(time, null);

        var result = service.Request(Noon, Noon + TimeSpan.FromMinutes(4), alreadyListening: false);

        Assert.False(result.Ok);
        Assert.Contains("wait for that instead", result.Reason, StringComparison.Ordinal);
        Assert.Null(service.ActiveUntil(Noon));
    }

    [Fact]
    public void Request_WithExactlyFiveMinutesToTheWindow_IsLetThrough_ForTheFullDuration()
    {
        // The refusal boundary (5 minutes) is further off than a session's own duration (3
        // minutes), so an accepted request's session always fits before the window: nothing to cap.
        var time = new FakeTimeProvider(Noon);
        var service = new ListenNowService(time, null);

        var result = service.Request(Noon, Noon + ListenNowService.MinBeforeWindow, alreadyListening: false);

        Assert.True(result.Ok);
        Assert.Equal(Noon + ListenNowService.Duration, result.Until);
    }

    [Fact]
    public void AlreadyListening_IsRefused_WithoutSpendingAUse()
    {
        var time = new FakeTimeProvider(Noon);
        var service = new ListenNowService(time, null);

        var result = service.Request(Noon, Noon + TimeSpan.FromHours(1), alreadyListening: true);

        Assert.False(result.Ok);
        Assert.Contains("Already listening", result.Reason, StringComparison.Ordinal);
        Assert.Equal(ListenNowService.MaxPerDay, service.UsesLeft(Noon));
    }

    [Fact]
    public void AtMostThreeUsesADay_ThenRefused_UntilTheNextUtcDay()
    {
        var time = new FakeTimeProvider(Noon);
        var service = new ListenNowService(time, null);
        TimeSpan farAhead = TimeSpan.FromHours(6);

        for (int i = 0; i < ListenNowService.MaxPerDay; i++)
        {
            time.SetUtcNow(Noon.AddMinutes(i * 10));
            var r = service.Request(time.GetUtcNow(), time.GetUtcNow() + farAhead, alreadyListening: false);
            Assert.True(r.Ok, $"use {i + 1} should be allowed");
            // Let the session end before the next request, so each counts as a separate use.
            time.Advance(ListenNowService.Duration);
        }

        var refused = service.Request(time.GetUtcNow(), time.GetUtcNow() + farAhead, alreadyListening: false);
        Assert.False(refused.Ok);
        Assert.Contains($"used {ListenNowService.MaxPerDay} times today", refused.Reason, StringComparison.Ordinal);
        Assert.Equal(0, service.UsesLeft(time.GetUtcNow()));

        // A new UTC day: the allowance is back.
        time.SetUtcNow(Noon.AddDays(1));
        Assert.Equal(ListenNowService.MaxPerDay, service.UsesLeft(time.GetUtcNow()));
        Assert.True(service.Request(time.GetUtcNow(), time.GetUtcNow() + farAhead, alreadyListening: false).Ok);
    }

    [Fact]
    public void RequestWhileASessionIsStillOpen_ReturnsTheSameSession_WithoutSpendingAnotherUse()
    {
        var time = new FakeTimeProvider(Noon);
        var service = new ListenNowService(time, null);
        var farWindow = Noon + TimeSpan.FromHours(1);

        var first = service.Request(Noon, farWindow, alreadyListening: false);
        time.Advance(TimeSpan.FromSeconds(30));
        var second = service.Request(time.GetUtcNow(), farWindow, alreadyListening: false);

        Assert.True(second.Ok);
        Assert.Equal(first.Until, second.Until);
        Assert.Equal(ListenNowService.MaxPerDay - 1, service.UsesLeft(Noon));
    }

    [Fact]
    public void ActiveUntil_EndsOnceTheDurationHasPassed()
    {
        var time = new FakeTimeProvider(Noon);
        var service = new ListenNowService(time, null);
        service.Request(Noon, Noon + TimeSpan.FromHours(1), alreadyListening: false);

        time.Advance(ListenNowService.Duration - TimeSpan.FromSeconds(1));
        Assert.NotNull(service.ActiveUntil(time.GetUtcNow()));

        time.Advance(TimeSpan.FromSeconds(2));
        Assert.Null(service.ActiveUntil(time.GetUtcNow()));
    }

    [Fact]
    public void Count_IsPersisted_AcrossARestart()
    {
        using var dir = new TempDirectory();
        var time = new FakeTimeProvider(Noon);
        var farWindow = Noon + TimeSpan.FromHours(1);
        var first = new ListenNowService(time, dir.Path);
        first.Request(Noon, farWindow, alreadyListening: false);
        time.Advance(ListenNowService.Duration);
        first.Request(time.GetUtcNow(), farWindow, alreadyListening: false);

        var reopened = new ListenNowService(time, dir.Path);

        Assert.Equal(ListenNowService.MaxPerDay - 2, reopened.UsesLeft(time.GetUtcNow()));
    }

    [Fact]
    public void Problem_MirrorsRequestWithoutSpendingAUse()
    {
        var time = new FakeTimeProvider(Noon);
        var service = new ListenNowService(time, null);

        Assert.Null(service.Problem(Noon, Noon + TimeSpan.FromHours(1), alreadyListening: false));
        Assert.Equal(ListenNowService.MaxPerDay, service.UsesLeft(Noon)); // Problem() alone never spends a use

        Assert.NotNull(service.Problem(Noon, Noon + TimeSpan.FromMinutes(1), alreadyListening: false));
        Assert.NotNull(service.Problem(Noon, Noon + TimeSpan.FromHours(1), alreadyListening: true));
    }
}
