using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using YueUI.Api.Library;
using Microsoft.Extensions.Options;
using YueUI.Api.Lyrics;
using YueUI.Api.Queue;
using YueUI.Api.Speech;
using YueUI.Api.Voices;
using YueUI.Api.Worker;

namespace YueUI.Api;

/// <summary>Starting, rendering and cancelling songs, and the live state of the worker (snapshot and event stream).</summary>
public static class WorkerEndpoints
{
    /// <summary>
    /// A comment-free "ping" event keeps idle streams open: proxies such as <c>tailscale serve</c> drop connections
    /// that stay silent, and a phone's browser notices a dead one only when it next reads.
    /// </summary>
    public static TimeSpan Heartbeat { get; set; } = TimeSpan.FromSeconds(20);

    public static RouteGroupBuilder MapWorkerEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/status", (Status.StatusHub hub) => hub.Snapshot())
            .WithName("GetStatus");
        api.MapGet("/busy", Busy)
            .WithName("GetBusy");
        // For a guard that pauses other models' work while Tonwerk needs the Mac (scripts repo, bin/yue-guard).
        api.MapGet("/idle", (Status.IdleClock clock, Monitoring.JobMetrics metrics, WorkerHost host, Memory.ModelMemory memory, Video.VideoMaker videos) =>
                clock.Read(Busy(host, memory, videos), metrics))
            .WithName("GetIdle");
        // What YuE2's synthesis took per engine, so GPU and Neural Engine can be compared without Grafana.
        api.MapGet("/stats/synthesis", (Stats.SynthesisStats stats) => stats.Overview())
            .WithName("GetSynthesisStats");
        api.MapGet("/events", (Status.StatusHub hub, CancellationToken cancellationToken) =>
            TypedResults.ServerSentEvents(Stream(hub, cancellationToken)))
            .WithName("StreamEvents")
            .Produces<Status.ServerEvents>(StatusCodes.Status200OK, "text/event-stream");

        api.MapPost("/generate", Generate)
            .WithName("Generate")
            .Produces<QueuedJob>(StatusCodes.Status202Accepted)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status501NotImplemented)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        api.MapPost("/songs/{run}/{song}/render", Render)
            .WithName("RenderSong")
            .Produces<QueuedJob>(StatusCodes.Status202Accepted)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        api.MapPost("/songs/{run}/{song}/cancel", async (string run, string song, WorkerHost host, CancellationToken cancellationToken) =>
            await host.CancelAsync($"{run}/{song}", cancellationToken)
                ? Results.Accepted()
                : Results.Problem(title: "Not in the queue", statusCode: StatusCodes.Status404NotFound))
            .WithName("CancelSong")
            .Produces(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status404NotFound);
        api.MapPost("/stop", async (WorkerHost host, CancellationToken cancellationToken) =>
        {
            await host.StopAllAsync(cancellationToken);
            return Results.Accepted();
        })
            .WithName("StopAll")
            .Produces(StatusCodes.Status202Accepted);
        api.MapPost("/worker/shutdown", async (WorkerHost host) =>
        {
            await host.ShutdownWorkerAsync();
            return Results.NoContent();
        })
            .WithName("ShutDownWorker")
            .Produces(StatusCodes.Status204NoContent);
        return api;
    }

    /// <summary>
    /// What a restart would cut off, for <c>deploy/update.sh</c>, which waits until nothing is. Waiting jobs are not
    /// counted: the queue, the voice converter, the speech lab and the cover painter keep theirs and take them up again after a restart.
    /// </summary>
    internal static BusyInfo Busy(WorkerHost host, Memory.ModelMemory memory, Video.VideoMaker videos)
    {
        List<string> reasons = [];
        if (host.IsBusy)
        {
            reasons.Add("songs");
        }
        if (host.IsTranscribing)
        {
            reasons.Add("transcription");
        }
        if (memory.Holder is { } holder)
        {
            reasons.Add(holder switch
            {
                Memory.LargeModel.Lyrics => "lyrics",
                Memory.LargeModel.Voices => "voices",
                Memory.LargeModel.Speech => "speech",
                Memory.LargeModel.Ace => "ace",
                Memory.LargeModel.Singing => "singing",
                _ => "images",
            });
        }
        if (videos.IsRendering)
        {
            reasons.Add("video");
        }
        return new BusyInfo(reasons.Count > 0, reasons);
    }

    /// <summary>
    /// While a lyrics draft or a voice conversion holds the memory the song waits in the <see cref="JobQueue"/>; the
    /// answer then carries the waiting job.
    /// </summary>
    /// <remarks>
    /// A voice is looked up now rather than when the song is ready, as for a version asked for in the library: a wrong
    /// id or an unreachable voice service is refused before the song takes its minutes.
    /// </remarks>
    private static async Task<IResult> Generate(
        GenerateRequest request,
        JobQueue queue,
        VoiceEngine voices,
        IOptions<VoiceOptions> options,
        Loras.LoraLibrary loras,
        CancellationToken cancellationToken)
    {
        var errors = request.Validate();
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }
        string? loraPath = null;
        if (request.Lora is { } lora && (loraPath = loras.PathFor(lora)) is null)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["lora"] = [$"No LoRA {lora} in {loras.Folder}."] });
        }
        SongVoice? voice = null;
        if (request.Voice is { } choice)
        {
            if (!options.Value.ConversionConfigured)
            {
                return Results.Problem(title: "Voices are not set up: neither Seed-VC and the separator (deploy/setup-mac.sh --voices) nor ChangeMyVoice and StemMyWav (Voice:BaseUrl, Voice:StemsBaseUrl and their keys).", statusCode: StatusCodes.Status501NotImplemented);
            }
            try
            {
                var found = (await voices.ListVoicesAsync(cancellationToken)).FirstOrDefault(v => v.Id == choice.VoiceId);
                if (found is null)
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["voice.voiceId"] = ["No such reference voice."] });
                }
                voice = SongVoice.From(found, choice);
            }
            catch (VoiceServiceException exception)
            {
                return Results.Problem(title: exception.Message, statusCode: (int)exception.Status);
            }
        }
        return await Send(() => queue.GenerateAsync(request, voice, loraPath, cancellationToken));
    }

    private static async Task<IResult> Render(
        string run,
        string song,
        RenderRequest? request,
        SongLibrary library,
        JobQueue queue,
        CancellationToken cancellationToken)
    {
        request ??= new RenderRequest();
        if (request.Quality is not ("draft" or "full") || request.Engines is not (null or "gpu" or "gpu+ane"))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["quality"] = ["Either 'draft' or 'full', engines 'gpu' or 'gpu+ane'."] });
        }
        if (library.SongDirectory(run, song) is not { } directory)
        {
            return Results.Problem(title: "No such song", statusCode: StatusCodes.Status404NotFound);
        }
        if (!File.Exists(Path.Combine(directory, "semantic.npy")))
        {
            return Results.Problem(title: "The song's tokens were not saved, so it cannot be rendered again.", statusCode: StatusCodes.Status409Conflict);
        }
        return await Send(() => queue.RenderAsync(run, song, library.TitleOf(run, directory), request.Quality, request.Engines, cancellationToken));
    }

    /// <summary>The worker answers asynchronously (its "started" event reaches the browsers), so a sent command is 202.</summary>
    private static async Task<IResult> Send(Func<Task<QueuedJob?>> send)
    {
        try
        {
            var queued = await send();
            return queued is null ? Results.Accepted() : Results.Accepted(value: queued);
        }
        catch (WorkerUnavailableException exception)
        {
            return Results.Problem(title: "YuE Studio not found", detail: exception.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async IAsyncEnumerable<SseItem<object>> Stream(Status.StatusHub hub, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var subscription = hub.Subscribe();
        yield return new SseItem<object>(subscription.Snapshot, "snapshot");

        while (!cancellationToken.IsCancellationRequested)
        {
            Status.ServerEvent? next;
            using (var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                heartbeat.CancelAfter(Heartbeat);
                try
                {
                    next = await subscription.Reader.ReadAsync(heartbeat.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    next = null;
                }
                catch (ChannelClosedException)
                {
                    // Dropped for not keeping up: ending the stream makes the EventSource reconnect with a new snapshot.
                    yield break;
                }
                catch (OperationCanceledException)
                {
                    yield break;
                }
            }
            yield return next is null ? new SseItem<object>(new { }, "ping") : new SseItem<object>(next.Data, next.Type);
        }
    }
}
