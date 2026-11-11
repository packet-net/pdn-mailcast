using Microsoft.Extensions.Time.Testing;

namespace Mailcast.Receiver.Tests;

/// <summary>Issue #53's "Listen now": the refusals, and the session it opens.</summary>
public class ListenNowTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Request_WellBeforeTheNextWindow_Opens_ForTheFullDuration()
    {
        var time = new FakeTimeProvider(Noon);
        var service = new ListenNowService();

        var result = service.Request(Noon, Noon + TimeSpan.FromHours(1), alreadyListening: false);

        Assert.True(result.Ok);
        Assert.Null(result.Reason);
        Assert.Equal(Noon + ListenNowService.Duration, result.Until);
        Assert.Equal(Noon + ListenNowService.Duration, service.ActiveUntil(Noon, Noon + TimeSpan.FromHours(1)));
    }

    [Fact]
    public void Request_WithinFiveMinutesOfAWindow_IsRefused()
    {
        var time = new FakeTimeProvider(Noon);
        var service = new ListenNowService();

        var result = service.Request(Noon, Noon + TimeSpan.FromMinutes(4), alreadyListening: false);

        Assert.False(result.Ok);
        Assert.Contains("wait for that instead", result.Reason, StringComparison.Ordinal);
        Assert.Null(service.ActiveUntil(Noon, Noon + TimeSpan.FromMinutes(4)));
    }

    [Fact]
    public void Request_WithExactlyFiveMinutesToTheWindow_IsLetThrough_ForTheFullDuration()
    {
        // The refusal boundary (5 minutes) is further off than a session's own duration (3
        // minutes), so an accepted request's session always fits before the window: nothing to cap.
        var time = new FakeTimeProvider(Noon);
        var service = new ListenNowService();

        var result = service.Request(Noon, Noon + ListenNowService.MinBeforeWindow, alreadyListening: false);

        Assert.True(result.Ok);
        Assert.Equal(Noon + ListenNowService.Duration, result.Until);
    }

    [Fact]
    public void AlreadyListening_IsRefused()
    {
        var time = new FakeTimeProvider(Noon);
        var service = new ListenNowService();

        var result = service.Request(Noon, Noon + TimeSpan.FromHours(1), alreadyListening: true);

        Assert.False(result.Ok);
        Assert.Contains("Already listening", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AsManyUsesAsWanted_NoDailyLimit()
    {
        var time = new FakeTimeProvider(Noon);
        var service = new ListenNowService();
        TimeSpan farAhead = TimeSpan.FromHours(6);

        for (int i = 0; i < 20; i++)
        {
            var r = service.Request(time.GetUtcNow(), time.GetUtcNow() + farAhead, alreadyListening: false);
            Assert.True(r.Ok, $"use {i + 1} should be allowed");
            Assert.Equal(time.GetUtcNow() + ListenNowService.Duration, r.Until);
            time.Advance(ListenNowService.Duration);
        }
    }

    [Fact]
    public void RequestWhileASessionIsStillOpen_ReturnsTheSameSession()
    {
        var time = new FakeTimeProvider(Noon);
        var service = new ListenNowService();
        var farWindow = Noon + TimeSpan.FromHours(1);

        var first = service.Request(Noon, farWindow, alreadyListening: false);
        time.Advance(TimeSpan.FromSeconds(30));
        var second = service.Request(time.GetUtcNow(), farWindow, alreadyListening: false);

        Assert.True(second.Ok);
        Assert.Equal(first.Until, second.Until);
    }

    [Fact]
    public void ActiveUntil_EndsOnceTheDurationHasPassed()
    {
        var time = new FakeTimeProvider(Noon);
        var service = new ListenNowService();
        var farWindow = Noon + TimeSpan.FromHours(1);
        service.Request(Noon, farWindow, alreadyListening: false);

        time.Advance(ListenNowService.Duration - TimeSpan.FromSeconds(1));
        Assert.NotNull(service.ActiveUntil(time.GetUtcNow(), farWindow));

        time.Advance(TimeSpan.FromSeconds(2));
        Assert.Null(service.ActiveUntil(time.GetUtcNow(), farWindow));
    }

    [Fact]
    public void ActiveUntil_RecomputesTheCapAgainstAFreshWindow_EndingAtOnceIfItHasMovedCloser()
    {
        // Issue #57: GB7RDG's directory, heard mid-session, can move a slot's window earlier
        // than it looked when the session was granted. ActiveUntil must still never say a time
        // past that window's opening, however it is asked afterwards.
        var time = new FakeTimeProvider(Noon);
        var service = new ListenNowService();
        var farWindow = Noon + TimeSpan.FromHours(1);
        var result = service.Request(Noon, farWindow, alreadyListening: false);
        Assert.Equal(Noon + ListenNowService.Duration, result.Until);

        time.Advance(TimeSpan.FromSeconds(10));
        // The window moves to a minute from now: still open, but capped sooner than granted.
        var closerWindow = time.GetUtcNow() + TimeSpan.FromMinutes(1);
        Assert.Equal(closerWindow, service.ActiveUntil(time.GetUtcNow(), closerWindow));

        // The window moves to right now (or the past): the session is over at once.
        Assert.Null(service.ActiveUntil(time.GetUtcNow(), time.GetUtcNow()));
        Assert.Null(service.ActiveUntil(time.GetUtcNow(), time.GetUtcNow() - TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Problem_MirrorsRequestWithoutOpeningASession()
    {
        var time = new FakeTimeProvider(Noon);
        var service = new ListenNowService();

        Assert.Null(service.Problem(Noon, Noon + TimeSpan.FromHours(1), alreadyListening: false));
        Assert.Null(service.ActiveUntil(Noon, Noon + TimeSpan.FromHours(1))); // Problem() alone never opens one

        Assert.NotNull(service.Problem(Noon, Noon + TimeSpan.FromMinutes(1), alreadyListening: false));
        Assert.NotNull(service.Problem(Noon, Noon + TimeSpan.FromHours(1), alreadyListening: true));
    }
}
