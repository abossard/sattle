using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Sattle.Cli;

namespace Sattle.Tests;

/// <summary>#102: how a live run stops. One state machine (`LiveInput.Next`, a calculation) and its edge (`LiveInput.RunUntilStopped`) over the host's own lifetime, which ASP.NET's console lifetime stops on Ctrl-C and SIGTERM.</summary>
public class LiveStopTests
{
    /// <summary>Each step as state and effects; the first stop event names the reason, and the server is stopped exactly by the deadline and by an input that ended on its own.</summary>
    [Theory]
    [InlineData("HostStopping,InputEnded", "Stopping CancelInput > Stopped None", "the server was stopped (Ctrl-C or SIGTERM)")]
    [InlineData("Deadline,HostStopping,InputEnded", "Stopping CancelInput, StopHost > Stopping None > Stopped None", "--seconds ran out")]
    [InlineData("HostStopping,Deadline,InputEnded", "Stopping CancelInput > Stopping None > Stopped None", "the server was stopped (Ctrl-C or SIGTERM)")]
    [InlineData("InputEnded,HostStopping,Deadline", "Stopped StopHost > Stopped None > Stopped None", "the input ended")]
    public void The_first_stop_event_names_the_reason_and_decides_the_effects(string events, string steps, string reason)
    {
        LiveStop state = new LiveStop.Running(); var seen = new List<string>();
        foreach (var e in events.Split(',').Select(Enum.Parse<LiveStopEvent>))
        {
            (state, var fx) = LiveInput.Next(state, e);
            seen.Add($"{state.GetType().Name} {fx}");
        }
        Assert.Equal(steps, string.Join(" > ", seen));
        Assert.Equal(reason, Assert.IsType<LiveStop.Stopped>(state).Reason);
    }

    /// <summary>The edge with the real lifetime: whatever stops the run, the input's token is cancelled or the input ended by itself, the input's counts come back, and the server is stopped; a failing input is rethrown after the server was told to stop.</summary>
    [Theory]
    [InlineData("deadline", "--seconds ran out")]
    [InlineData("signal", "the server was stopped (Ctrl-C or SIGTERM)")]
    [InlineData("input ends", "the input ended")]
    [InlineData("input fails", null)]
    public void A_live_run_stops_with_the_server_and_returns_its_counts(string how, string? reason)
    {
        var lifetime = new ApplicationLifetime(NullLogger<ApplicationLifetime>.Instance);
        var counts = new LiveCounts(3, 1, 2);
        LiveCounts Input(CancellationToken ct)
        {
            if (how == "input fails") throw new InvalidOperationException("the hub went away");
            if (how == "signal") Task.Delay(50).ContinueWith(_ => lifetime.StopApplication());
            if (how != "input ends") Assert.True(ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(10)), "the input was not cancelled");
            return counts;
        }
        var deadline = how == "deadline" ? TimeSpan.FromMilliseconds(50) : (TimeSpan?)null;
        if (reason is null) Assert.Throws<InvalidOperationException>(() => LiveInput.RunUntilStopped(lifetime, deadline, Input));
        else Assert.Equal((counts, reason), LiveInput.RunUntilStopped(lifetime, deadline, Input));
        Assert.True(lifetime.ApplicationStopping.IsCancellationRequested);
    }

    /// <summary>`--from` takes latest or earliest only; anything else (the old `checkpoint`, a typo) is refused before the server starts instead of reading from latest.</summary>
    [Theory]
    [InlineData("checkpoint")] [InlineData("Latest")] [InlineData("newest")]
    public void Serve_live_refuses_a_from_that_is_not_latest_or_earliest(string from) =>
        Assert.Equal(2, Program.Main(["serve", "--live", "--from", from]));
}
