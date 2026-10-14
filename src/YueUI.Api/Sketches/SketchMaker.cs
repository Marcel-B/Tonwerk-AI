using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using YueUI.Api.Data;
using YueUI.Api.Push;

namespace YueUI.Api.Sketches;

/// <summary>
/// Sends written song sketches to Claude (<see cref="ISketchModel"/>) one at a time and keeps them with their plans in
/// the database. A plan takes minutes, so a request only queues it and the page asks again; a push announces the
/// result, as the phone has usually locked by then. Nothing here touches the Mac's memory: the model runs at
/// Anthropic, so a sketch waits for none of the local models and none waits for it.
/// </summary>
/// <remarks>A restart queues waiting sketches again and marks one being written failed, since its answer is lost.</remarks>
public sealed partial class SketchMaker(
    SqliteSketchStore store,
    ISketchModel model,
    IOptions<SketchOptions> options,
    PushStore pushStore,
    IPushSender pushSender,
    TimeProvider time,
    ILogger<SketchMaker> logger) : BackgroundService
{
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Lock _gate = new();
    private readonly Dictionary<string, SketchJob> _jobs = [];
    private readonly TaskCompletionSource _loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string? _running;
    private CancellationTokenSource? _cancel;

    public SketchInfo Info => new(options.Value.ResolveApiKey() is not null, options.Value.Model);

    /// <summary>Every sketch, newest first.</summary>
    public IReadOnlyList<SketchJob> Jobs()
    {
        lock (_gate)
        {
            return [.. _jobs.Values.OrderByDescending(j => j.CreatedAt)];
        }
    }

    public SketchJob? Get(string id)
    {
        lock (_gate)
        {
            return _jobs.GetValueOrDefault(id);
        }
    }

    public SketchJob Enqueue(string text, string? instruction)
    {
        var now = time.GetUtcNow();
        var job = new SketchJob
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = TitleOf(text),
            Source = text.Trim(),
            Instruction = string.IsNullOrWhiteSpace(instruction) ? null : instruction.Trim(),
            CreatedAt = now,
            UpdatedAt = now,
        };
        Save(job);
        _queue.Writer.TryWrite(job.Id);
        return job;
    }

    /// <summary>Stops the sketch if it is being written and forgets it.</summary>
    public void Delete(SketchJob job)
    {
        lock (_gate)
        {
            if (_running == job.Id)
            {
                _cancel?.Cancel();
            }
            _jobs.Remove(job.Id);
        }
        try
        {
            store.Remove(job.Id);
        }
        catch (Exception exception) when (exception is Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            logger.LogWarning(exception, "Could not remove the sketch {Id}", job.Id);
        }
    }

    /// <summary>Waits until the stored sketches are read, so that a request right after start sees them.</summary>
    public Task Loaded => _loaded.Task;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        Load();
        try
        {
            await foreach (var id in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                await WriteAsync(id, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private void Load()
    {
        try
        {
            foreach (var job in store.All())
            {
                if (job.Stage == "queued")
                {
                    Save(job);
                    _queue.Writer.TryWrite(job.Id);
                }
                else if (!job.Finished)
                {
                    Save(job with { Stage = "failed", Message = "The server restarted while Claude was writing.", UpdatedAt = time.GetUtcNow() });
                }
                else
                {
                    lock (_gate)
                    {
                        _jobs[job.Id] = job;
                    }
                }
            }
        }
        catch (Exception exception) when (exception is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Could not read the stored sketches");
        }
        finally
        {
            _loaded.TrySetResult();
        }
    }

    private async Task WriteAsync(string id, CancellationToken stoppingToken)
    {
        if (Get(id) is not { Stage: "queued" } job)
        {
            return;
        }
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        cancel.CancelAfter(options.Value.Timeout);
        lock (_gate)
        {
            _running = id;
            _cancel = cancel;
        }
        try
        {
            job = Save(job with { Stage = "writing", UpdatedAt = time.GetUtcNow() });
            var answer = await model.PlanAsync(job.Source, job.Instruction, cancel.Token);
            job = Save(job with
            {
                Stage = "done",
                Plan = answer.Plan,
                Title = string.IsNullOrWhiteSpace(answer.Plan.Title) ? job.Title : answer.Plan.Title.Trim(),
                Model = answer.Model,
                InputTokens = answer.InputTokens,
                OutputTokens = answer.OutputTokens,
                UpdatedAt = time.GetUtcNow(),
            });
            logger.LogInformation(
                "Sketch {Id} planned by {Model}: {Bars} bars, {Input} tokens in, {Output} out",
                id, answer.Model, answer.Plan.Sections.Sum(s => s.Bars.Count), answer.InputTokens, answer.OutputTokens);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception) when (exception is SketchModelException or OperationCanceledException or HttpRequestException)
        {
            var deleted = Get(id) is null;
            if (deleted)
            {
                return;
            }
            var message = exception is OperationCanceledException
                ? $"Claude did not answer within {options.Value.Timeout.TotalMinutes:0} minutes."
                : exception.Message;
            logger.LogWarning(exception, "Sketch {Id} failed", id);
            job = Save(job with { Stage = "failed", Message = message, UpdatedAt = time.GetUtcNow() });
        }
        finally
        {
            lock (_gate)
            {
                _running = null;
                _cancel = null;
            }
        }
        if (Get(id) is not null)
        {
            await NotifyAsync(job);
        }
    }

    private SketchJob Save(SketchJob job)
    {
        lock (_gate)
        {
            if (job.Stage != "queued" && !_jobs.ContainsKey(job.Id))
            {
                // Deleted meanwhile: do not bring it back.
                return job;
            }
            _jobs[job.Id] = job;
        }
        try
        {
            store.Save(job);
        }
        catch (Exception exception) when (exception is Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            logger.LogWarning(exception, "Could not store the sketch {Id}", job.Id);
        }
        return job;
    }

    private async Task NotifyAsync(SketchJob job)
    {
        foreach (var subscription in pushStore.Subscriptions)
        {
            try
            {
                var payload = PushNotifier.Payload(PushTexts.Sketch(job, subscription.Language), $"sketch-{job.Id}");
                if (await pushSender.SendAsync(subscription, payload, CancellationToken.None) == PushResult.Gone)
                {
                    pushStore.Remove(subscription.Endpoint);
                }
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Could not announce the sketch {Id}", job.Id);
            }
        }
    }

    /// <summary>The sketch's first heading, without the Markdown and the album line.</summary>
    public static string TitleOf(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('#'))
            {
                var title = Heading().Replace(line, "").Trim();
                // "Before the Silence — Musikalische Arbeitsfassung 01": the song, not the document.
                var dash = title.IndexOfAny(['—', '–']);
                return (dash > 0 ? title[..dash] : title).Trim();
            }
        }
        var first = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        return first.Length > 60 ? first[..60] : first;
    }

    [GeneratedRegex(@"^#+\s*")]
    private static partial Regex Heading();
}
