using System.Threading.Channels;
using Microsoft.Extensions.Options;
using YueUI.Api.Push;
using YueUI.Api.Voices;
using YueUI.Api.Worker;

namespace YueUI.Api.OnkelBummel;

/// <summary>
/// Sends a message to Onkel Bummel when a long job finishes: a song batch as one message once its last song is done,
/// and transcriptions, voice versions, stems, voice swaps, videos, ACE-Step songs, cover pictures, speech takes, sung
/// takes and lyrics drafts likewise. <see cref="Post"/> takes what does not pass the hub (sketches, a failed backup).
/// </summary>
/// <remarks>
/// It listens to <see cref="Status.StatusHub"/> like <see cref="PushNotifier"/>: only the change to finished counts,
/// cancelled jobs stay silent but still complete their group. Jobs are grouped (a run's songs, the versions made for
/// a run, the pictures painted for one song, the speech lab's takes), and a group is sent when none of its jobs is
/// unfinished any more, so a batch of ten songs is one message, not ten. Sending is decoupled from reading and a
/// failure is only logged: Onkel Bummel being down never touches a job.
/// </remarks>
public sealed class OnkelBummelNotifier(
    Status.StatusHub hub,
    OnkelBummelClient client,
    IOptions<OnkelBummelOptions> options,
    ILogger<OnkelBummelNotifier> logger,
    Lyrics.QuickSongs? quick = null) : BackgroundService
{
    private readonly Dictionary<string, Tracked> _jobs = [];
    private readonly Dictionary<string, List<Tracked>> _pending = [];
    private readonly Channel<OnkelBummelMessage> _outbox =
        Channel.CreateUnbounded<OnkelBummelMessage>(new UnboundedChannelOptions { SingleReader = true });

    private string Language => options.Value.Language;

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(WatchAsync(stoppingToken), DeliverAsync(stoppingToken));

    /// <summary>Queues a message of a job that does not pass the hub; nothing happens while Onkel Bummel is off.</summary>
    /// <param name="text">Title and body in a language (<c>de</c>, <c>en</c>).</param>
    public void Post(string kind, bool ok, Func<string, (string Title, string Body)> text, string? path = null)
    {
        if (options.Value.Active)
        {
            _outbox.Writer.TryWrite(Message(kind, [new Tracked(kind, Guid.NewGuid().ToString("N"), "", true, false, ok, text, path)]));
        }
    }

    private async Task WatchAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // A dropped subscriber's channel completes; subscribing again starts from a fresh snapshot.
            using var subscription = hub.Subscribe(checkStudio: false);
            Remember(subscription.Snapshot);
            try
            {
                await foreach (var item in subscription.Reader.ReadAllAsync(stoppingToken))
                {
                    if (Track(item.Data) is { } job)
                    {
                        Observe(job, announce: true);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private void Remember(StatusSnapshot snapshot)
    {
        IEnumerable<object?> all =
        [
            .. snapshot.Songs, .. snapshot.Transcriptions ?? [], .. snapshot.Versions ?? [], .. snapshot.Stems ?? [],
            .. snapshot.Swaps ?? [], .. snapshot.Videos ?? [], .. snapshot.Ace ?? [], .. snapshot.Images ?? [],
            .. snapshot.Speech ?? [], .. snapshot.Singing ?? [], snapshot.Lyrics,
        ];
        var seen = new HashSet<string>();
        foreach (var job in all.Select(Track).OfType<Tracked>())
        {
            seen.Add(job.Key);
            Observe(job, announce: false);
        }
        // A job that vanished unfinished (a worker restart forgets its songs) must not hold its group back for good.
        foreach (var gone in _jobs.Values.Where(j => !j.Finished && !seen.Contains(j.Key)).ToList())
        {
            _jobs.Remove(gone.Key);
            Flush(gone.Kind, gone.Group);
        }
    }

    private static Tracked? Track(object? data) => data switch
    {
        SongState song => new Tracked("song", song.Id, song.Run, song.Finished, song.Stage == "cancelled", song.Stage == "ready",
            null, $"songs/{song.Id}", song),
        TranscriptionState transcription => new Tracked("transcription", transcription.Id, transcription.Id, transcription.Finished,
            transcription.Stage == "cancelled", transcription.Stage == "done", null, "transcribe", transcription),
        VersionState version => new Tracked("version", version.Id, version.Run, version.Finished, version.Stage == "cancelled",
            version.Stage == "done", null, $"songs/{version.SongId}", version),
        StemSetState set => new Tracked("stems", set.Id, set.Id, set.Finished, set.Stage == "cancelled", set.Stage == "done",
            null, set.Upload ? "voices" : $"voices/{set.SongId}", set),
        SwapState swap => new Tracked("swap", swap.Id, swap.Id, swap.Finished, swap.Stage == "cancelled", swap.Stage == "done",
            null, "voices", swap),
        Video.VideoState video => new Tracked("video", video.Id, video.Id, video.Finished, video.Stage == "cancelled",
            video.Stage == "done", null, $"songs/{video.SongId}", video),
        Ace.AceJob job => new Tracked("ace", job.Id, job.Id, job.Finished, job.Stage == "cancelled", job.Stage == "done",
            null, "songs", job),
        Images.ImageState image => new Tracked("image", image.Id, image.SongId, image.Finished, image.Stage == "cancelled",
            image.Stage == "done", null, $"songs/{image.SongId}", image),
        // The lab's takes of one text are spoken one model after the other: one message once the lab is through.
        Speech.SpeechTake take => new Tracked("speech", take.Id, "lab", take.Finished, take.Stage == "cancelled", take.Stage == "done",
            null, "lab", take),
        Singing.SingTake take => new Tracked("singing", take.Id, take.Id, take.Finished, take.Stage == "cancelled", take.Stage == "done",
            null, "sing", take),
        LyricsState lyrics => new Tracked("lyrics", lyrics.Id, lyrics.Id, lyrics.Finished, false, lyrics.Stage == "done",
            null, "create", lyrics),
        _ => null,
    };

    private void Observe(Tracked job, bool announce)
    {
        var was = _jobs.TryGetValue(job.Key, out var before) && before.Finished;
        _jobs[job.Key] = job;
        if (!announce || !job.Finished || was)
        {
            return;
        }
        // A shortcut's draft is announced by its song; only its failure is news of its own.
        if (!job.Cancelled && !(job.Kind == "lyrics" && job.Ok && quick?.Owns(job.Id) == true))
        {
            var group = GroupKey(job.Kind, job.Group);
            if (!_pending.TryGetValue(group, out var list))
            {
                _pending[group] = list = [];
            }
            list.Add(job);
        }
        Flush(job.Kind, job.Group);
    }

    /// <summary>Sends what the group has gathered once none of its jobs is still running or waiting.</summary>
    private void Flush(string kind, string group)
    {
        if (_jobs.Values.Any(j => j.Kind == kind && j.Group == group && !j.Finished))
        {
            return;
        }
        if (_pending.Remove(GroupKey(kind, group), out var done) && done.Count > 0 && options.Value.Active)
        {
            _outbox.Writer.TryWrite(Message(kind, done));
        }
    }

    private OnkelBummelMessage Message(string kind, IReadOnlyList<Tracked> done)
    {
        var language = Language;
        var texts = done.Select(j => { var (title, body) = TextOf(j, language); return new OnkelBummelTexts.Done(j.Ok, title, body); }).ToList();
        var (title, body) = OnkelBummelTexts.Group(kind, texts, language);
        var failed = done.Any(j => !j.Ok);
        var path = (done.FirstOrDefault(j => j.Ok) ?? done[0]).Path;
        Uri? click = path is not null && options.Value.LinkUri is { } link ? new Uri(link, $"ui/#/{path}") : null;
        // New for every message (a song rendered again finishes again under its id), kept across its own tries.
        return new OnkelBummelMessage(
            $"tonwerk-{kind}-{Guid.NewGuid():N}",
            title,
            body,
            failed ? 4 : 3,
            [failed ? "warning" : "white_check_mark", "tonwerk"],
            click);
    }

    private static (string Title, string Body) TextOf(Tracked job, string language) => job.State switch
    {
        SongState song => PushTexts.Song(song, language),
        TranscriptionState transcription => PushTexts.Transcription(transcription, language),
        VersionState version => PushTexts.Version(version, language),
        StemSetState set => PushTexts.Stems(set, language),
        SwapState swap => PushTexts.Swap(swap, language),
        Video.VideoState video => PushTexts.Video(video, language),
        Ace.AceJob ace => PushTexts.Ace(ace, language),
        Images.ImageState image => OnkelBummelTexts.Image(image, language),
        Speech.SpeechTake take => OnkelBummelTexts.Speech(take, language),
        Singing.SingTake take => PushTexts.Singing(take, language),
        LyricsState lyrics => PushTexts.Lyrics(lyrics, language),
        _ => job.Text?.Invoke(language) ?? ("Tonwerk-AI", ""),
    };

    private static string GroupKey(string kind, string group) => $"{kind}:{group}";

    private async Task DeliverAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var message in _outbox.Reader.ReadAllAsync(stoppingToken))
            {
                await SendAsync(message, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task SendAsync(OnkelBummelMessage message, CancellationToken stoppingToken)
    {
        var delays = options.Value.RetryDelays;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await client.SendAsync(message, stoppingToken);
                logger.LogInformation("Sent \"{Title}\" to Onkel Bummel", message.Title);
                return;
            }
            catch (OnkelBummelException exception) when (exception.Transient && attempt < delays.Length)
            {
                logger.LogInformation("Onkel Bummel did not take \"{Title}\", trying again: {Error}", message.Title, exception.Message);
                await Task.Delay(delays[attempt], stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning("Could not send \"{Title}\" to Onkel Bummel: {Error}", message.Title, exception.Message);
                return;
            }
        }
    }

    /// <param name="Group">Jobs of one group are sent as one message once none of them is unfinished.</param>
    /// <param name="Text">The text of a job that is not a hub state (<see cref="Post"/>).</param>
    /// <param name="Path">The web page that shows it, after <c>#/</c>.</param>
    private sealed record Tracked(
        string Kind, string Id, string Group, bool Finished, bool Cancelled, bool Ok,
        Func<string, (string Title, string Body)>? Text, string? Path, object? State = null)
    {
        public string Key => $"{Kind}:{Id}";
    }
}
