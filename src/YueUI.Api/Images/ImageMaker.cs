using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using YueUI.Api.Data;
using YueUI.Api.Memory;
using YueUI.Api.Worker;

namespace YueUI.Api.Images;

/// <summary>
/// Paints cover candidates for songs, one at a time, with a local image model (<see cref="IImageEngine"/>). The dialog
/// lists a song's candidates; taking one makes it the song's cover (<see cref="Library.SongLibrary.SetCover"/>).
/// </summary>
/// <remarks>
/// Klein holds about 9 GB while it paints, Krea 2 about 22, so every model follows the rule YuE2 does: a picture
/// waits while songs are generated, lyrics written, a voice sung or a take spoken, an idle YuE worker is shut down
/// first, and while it holds the memory (<see cref="ModelMemory"/>) the others wait. A picture may be painted on another
/// Mac instead (<see cref="Machines.Machines"/>), which waits for its own memory. Candidates are not cover material
/// yet, so they lie in <c>images/</c> next to the database as a JPEG and a JSON each, not in the database; waiting ones survive a restart, one that was halfway is marked failed.
/// </remarks>
public sealed class ImageMaker(
    SqliteDatabase database,
    IImageEngine engine,
    IPromptTranslator translator,
    Machines.Machines machines,
    Machines.RemoteEngines remote,
    Status.StatusHub hub,
    IOptions<ImageOptions> options,
    TimeProvider time,
    ILogger<ImageMaker> logger) : BackgroundService
{
    /// <summary>Square, the size covers are drawn and exported at, and one every offered model was trained on.</summary>
    public const int Size = 1024;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Lock _gate = new();
    private readonly Dictionary<string, ImageState> _images = [];
    private string? _running;
    private CancellationTokenSource? _cancel;

    public string Folder => Path.Combine(database.Directory, "images");

    public string ImagePath(string id) => Path.Combine(Folder, $"{id}.jpg");

    /// <summary>The song's candidates, newest first.</summary>
    public IReadOnlyList<ImageState> Of(string songId)
    {
        lock (_gate)
        {
            return [.. _images.Values.Where(i => i.SongId == songId).OrderByDescending(i => i.CreatedAt)];
        }
    }

    /// <summary>Every candidate, of every song, for the storage overview.</summary>
    public IReadOnlyList<ImageState> All()
    {
        lock (_gate)
        {
            return [.. _images.Values];
        }
    }

    public ImageState? Get(string id)
    {
        lock (_gate)
        {
            return _images.GetValueOrDefault(id);
        }
    }

    /// <param name="machine">Where to paint, as <see cref="Machines.Machines.Normalize"/> stores it.</param>
    public ImageState Enqueue(string songId, string? title, ImageModel model, string prompt, long seed, string? machine = null)
    {
        var now = time.GetUtcNow();
        var image = new ImageState
        {
            Id = Guid.NewGuid().ToString("N"),
            SongId = songId,
            Title = title,
            ModelId = model.Id,
            ModelLabel = model.Label,
            Prompt = prompt,
            Seed = seed,
            Machine = machine,
            CreatedAt = now,
            UpdatedAt = now,
        };
        Save(image);
        _queue.Writer.TryWrite(image.Id);
        return image;
    }

    /// <summary>Stops the picture if it is being painted, and removes it with its file.</summary>
    public void Delete(ImageState image)
    {
        lock (_gate)
        {
            if (_running == image.Id)
            {
                _cancel?.Cancel();
            }
            _images.Remove(image.Id);
        }
        DeleteFiles(image.Id);
        hub.UpdateImage(image with { Stage = "cancelled", UpdatedAt = time.GetUtcNow() });
    }

    /// <summary>Every candidate of the run's songs, or of one song, when they are deleted.</summary>
    /// <returns>True, so that it can follow a deletion that succeeded.</returns>
    public bool DeleteOf(string run, string? song = null)
    {
        List<ImageState> gone;
        lock (_gate)
        {
            gone = [.. _images.Values.Where(i => song is null ? i.SongId.StartsWith($"{run}/", StringComparison.Ordinal) : i.SongId == $"{run}/{song}")];
        }
        foreach (var image in gone)
        {
            Delete(image);
        }
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        Load();
        try
        {
            await foreach (var id in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                await PaintAsync(id, stoppingToken);
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
            if (!Directory.Exists(Folder))
            {
                return;
            }
            foreach (var file in Directory.EnumerateFiles(Folder, "*.json"))
            {
                ImageState? image;
                try
                {
                    image = JsonSerializer.Deserialize<ImageState>(File.ReadAllText(file), Json);
                }
                catch (JsonException exception)
                {
                    logger.LogWarning(exception, "Could not read {File}", file);
                    continue;
                }
                if (image is null)
                {
                    continue;
                }
                if (image.Stage == "queued")
                {
                    Save(image);
                    _queue.Writer.TryWrite(image.Id);
                }
                else if (!image.Finished)
                {
                    Save(image with { Stage = "failed", Message = "The server restarted while the picture was painted.", UpdatedAt = time.GetUtcNow() });
                }
                else
                {
                    lock (_gate)
                    {
                        _images[image.Id] = image;
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Could not read the pictures in {Folder}", Folder);
        }
    }

    private async Task PaintAsync(string id, CancellationToken stoppingToken)
    {
        if (Get(id) is not { Finished: false } image)
        {
            return;
        }
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        lock (_gate)
        {
            _running = id;
            _cancel = cancel;
        }
        var output = Path.Combine(Folder, $"{id}.part.jpg");
        try
        {
            var model = options.Value.ResolvedModels.FirstOrDefault(m => m.Id == image.ModelId)
                ?? throw new ImageException($"The model {image.ModelId} is no longer offered.");

            // Before the memory is claimed: Claude runs in the cloud, and a slow answer should not hold the slot.
            if (image.PaintedPrompt is null && translator.Configured
                && await translator.TranslateAsync(image.Prompt, cancel.Token) is { } english)
            {
                image = Update(image, i => i with { PaintedPrompt = english });
            }

            await using var placement = await machines.PlaceAsync(
                LargeModel.Images, Machines.Engines.ImageModel(model.Id), image.Machine, options.Value.WaitInterval, cancel.Token,
                engine.Unavailable(model) is null);
            var painter = placement.Helper is { } helper ? remote.Images(helper) : engine;

            image = Update(image, i => i with { Stage = "loading", RanOn = placement.Name });
            var shown = 0.0;
            var result = await painter.PaintAsync(
                new ImageJob(model, image.PaintedPrompt ?? image.Prompt, image.Seed, Size, Size, output),
                (stage, fraction) =>
                {
                    if (stage != image.Stage || fraction - shown >= 0.1)
                    {
                        shown = fraction;
                        image = Update(image, i => i with { Stage = stage is "loading" ? "loading" : "painting", Fraction = fraction }, persist: false);
                    }
                },
                cancel.Token);
            File.Move(output, ImagePath(id), overwrite: true);
            if (Get(id) is null)
            {
                // Deleted while it was painted.
                DeleteFiles(id);
                return;
            }
            Update(image, i => i with
            {
                Stage = "done",
                Fraction = 1,
                PaintedPrompt = result.PaintedPrompt ?? i.PaintedPrompt,
                LoadSeconds = result.LoadSeconds,
                PaintSeconds = result.PaintSeconds,
                PeakMemoryGb = result.PeakMemoryGb,
            });
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            // Deleted (Delete has said so) or shutting down, in which case it stays unfinished and fails after the restart.
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "The picture {Id} with {Model} failed", id, image.ModelId);
            Update(image, i => i with { Stage = "failed", Message = exception.Message });
        }
        finally
        {
            lock (_gate)
            {
                _running = null;
                _cancel = null;
            }
            try
            {
                File.Delete(output);
            }
            catch (IOException exception)
            {
                logger.LogWarning(exception, "Could not delete {File}", output);
            }
        }
    }

    /// <summary>Keeps the change unless the picture was deleted meanwhile, and tells the browsers.</summary>
    private ImageState Update(ImageState image, Func<ImageState, ImageState> change, bool persist = true)
    {
        var updated = change(image) with { UpdatedAt = time.GetUtcNow() };
        lock (_gate)
        {
            if (!_images.ContainsKey(image.Id))
            {
                return updated;
            }
        }
        if (persist)
        {
            Save(updated);
        }
        else
        {
            lock (_gate)
            {
                _images[updated.Id] = updated;
            }
            hub.UpdateImage(updated);
        }
        return updated;
    }

    private void Save(ImageState image)
    {
        lock (_gate)
        {
            _images[image.Id] = image;
        }
        try
        {
            Directory.CreateDirectory(Folder);
            var path = Path.Combine(Folder, $"{image.Id}.json");
            File.WriteAllText($"{path}.tmp", JsonSerializer.Serialize(image, Json));
            File.Move($"{path}.tmp", path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Could not store the picture {Id}", image.Id);
        }
        hub.UpdateImage(image);
    }

    private void DeleteFiles(string id)
    {
        foreach (var path in (string[])[Path.Combine(Folder, $"{id}.json"), ImagePath(id)])
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(exception, "Could not delete {File}", path);
            }
        }
    }
}
