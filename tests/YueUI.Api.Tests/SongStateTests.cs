using YueUI.Api.Worker;

namespace YueUI.Api.Tests;

public class SongStateTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 23, 0, 0, 0, TimeSpan.Zero);

    private static SongState Queued() => new()
    {
        Id = "run/song1",
        Run = "run",
        Stages = [new StageTime("queued", Start)],
    };

    [Fact]
    public void A_stage_counts_the_time_it_waits_for_an_engine()
    {
        var song = Queued()
            .Entering("synth", Start).Saying("solver step 1/32", Start)
            .Saying("paused · the GPU is busy rendering", Start.AddSeconds(30))
            .Saying("paused · the GPU is busy rendering", Start.AddSeconds(40))
            .Saying("solver step 2/32", Start.AddSeconds(90))
            .Saying("waiting", Start.AddSeconds(100))
            .Entering("decode", Start.AddSeconds(110)).Saying("decoding tile 1/9", Start.AddSeconds(110));

        Assert.Equal([0, 70, 0], song.Stages.Select(s => s.Waited));
        Assert.Null(song.WaitingSince);
    }

    [Fact]
    public void A_running_wait_is_open_until_the_song_works_again()
    {
        var song = Queued().Entering("synth", Start).Saying("waiting", Start.AddSeconds(5));

        Assert.Equal(Start.AddSeconds(5), song.WaitingSince);
        Assert.Equal(0, song.Stages[^1].Waited);
    }

    [Fact]
    public void Waiting_in_the_queue_is_not_counted_twice()
    {
        var song = Queued().Saying("waiting", Start).Entering("planning", Start.AddSeconds(60));

        Assert.Null(song.WaitingSince);
        Assert.All(song.Stages, stage => Assert.Equal(0, stage.Waited));
    }

    [Fact]
    public void A_song_that_fails_while_waiting_closes_its_wait()
    {
        var song = Queued().Entering("synth", Start).Saying("waiting", Start.AddSeconds(10))
            .Entering("failed", Start.AddSeconds(25));

        Assert.Equal(15, song.Stages[1].Waited);
        Assert.Null(song.WaitingSince);
    }
}
