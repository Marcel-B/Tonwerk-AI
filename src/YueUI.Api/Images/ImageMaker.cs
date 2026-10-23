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
    ImageLoraLibrary loras,
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

    /// <summary>Pictures handed in for one cover only, until a picture asks for them (and takes them along).</summary>
    public string UploadFolder => Path.Combine(Folder, "uploads");

    /// <summary>The picture's reference files, in the order of <see cref="ImageState.References"/>.</summary>
    public IReadOnlyList<string> ReferencePaths(ImageState image) =>
        [.. Enumerable.Range(0, image.References?.Count ?? 0)
            .Select(n => Directory.Exists(Folder) ? Directory.EnumerateFiles(Folder, $"{image.Id}.ref{n}.*").FirstOrDefault() : null)
            .OfType<string>()];

    /// <summary>Keeps a picture handed in as a reference for one cover.</summary>
    /// <returns>Its id, for <see cref="ImageReferenceChoice.Id"/>.</returns>
    public async Task<string> UploadAsync(byte[] bytes, string extension, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(UploadFolder);
        await File.WriteAllBytesAsync(Path.Combine(UploadFolder, id + extension), bytes, cancellationToken);
        return id;
    }

    /// <summary>An uploaded reference's file; null when there is none (which also keeps ids from leaving the folder).</summary>
    public string? UploadPath(string id) =>
        id.Length == 32 && id.All(Uri.IsHexDigit) && Directory.Exists(UploadFolder)
            ? Directory.EnumerateFiles(UploadFolder, id + ".*").FirstOrDefault()
            : null;

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
    /// <param name="used">The LoRAs, as they are now, so that a later change to one leaves a waiting picture alone.</param>
    /// <param name="references">
    /// The reference pictures, copied beside the picture now so that a style or candidate deleted meanwhile leaves it
    /// alone; an upload is moved, since nothing else uses it.
    /// </param>
    public ImageState Enqueue(
        string songId,
        string? title,
        ImageModel model,
        string prompt,
        long seed,
        string? machine = null,
        IReadOnlyList<ImageLoraUsed>? used = null,
        IReadOnlyList<(string Path, ImageReferenceUsed Used)>? references = null)
    {
        var now = time.GetUtcNow();
        var id = Guid.NewGuid().ToString("N");
        if (references is { Count: > 0 })
        {
            Directory.CreateDirectory(Folder);
            for (var n = 0; n < references.Count; n++)
            {
                var source = references[n].Path;
                var target = Path.Combine(Folder, $"{id}.ref{n}{Path.GetExtension(source)}");
                if (Path.GetDirectoryName(source) == UploadFolder)
                {
                    File.Move(source, target);
                }
                else
                {
                    File.Copy(source, target);
                }
            }
        }
        var image = new ImageState
        {
            Id = id,
            SongId = songId,
            Title = title,
            ModelId = model.Id,
            ModelLabel = model.Label,
            Prompt = prompt,
            Seed = seed,
            Machine = machine,
            Loras = used is { Count: > 0 } ? used : null,
            References = references is { Count: > 0 } ? [.. references.Select(r => r.Used)] : null,
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
            // An upload no picture asked for within a day was a dialog closed without painting.
            if (Directory.Exists(UploadFolder))
            {
                foreach (var upload in new DirectoryInfo(UploadFolder).EnumerateFiles()
                    .Where(f => f.LastWriteTimeUtc < time.GetUtcNow().UtcDateTime.AddDays(-1)))
                {
                    upload.Delete();
                }
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

            var files = (image.Loras ?? []).Select(l => new ImageLoraFile(
                loras.PathFor(l.Name) ?? throw new ImageException($"The LoRA {l.Label} is no longer there."), l.Strength)).ToList();
            var paths = ReferencePaths(image);
            if (paths.Count != (image.References?.Count ?? 0))
            {
                throw new ImageException("A reference picture is no longer there.");
            }
            var references = paths.Select((path, n) => new ImageReferenceFile(path, image.References![n].Role, image.References[n].Strength)).ToList();

            await using var placement = await machines.PlaceAsync(
                LargeModel.Images, Machines.Engines.ImageModel(model.Id), image.Machine, options.Value.WaitInterval, cancel.Token,
                engine.Unavailable(model) is null);
            var painter = placement.Helper is { } helper ? remote.Images(helper) : engine;

            var sent = SentPromptOf(image with { SentPrompt = null });
            image = Update(image, i => i with { Stage = "loading", RanOn = placement.Name, SentPrompt = sent });
            var shown = 0.0;
            var result = await painter.PaintAsync(
                new ImageJob(
                    model,
                    sent,
                    image.Seed,
                    Size,
                    Size,
                    output,
                    files,
                    references),
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

    /// <summary>
    /// The text the model is (or was) given; for a picture painted before it was kept, built again the same way, which gives
    /// the same text as long as no LoRA's trigger changed since.
    /// </summary>
    public static string SentPromptOf(ImageState image) =>
        image.SentPrompt ?? WithReferences(WithTriggers(image.PaintedPrompt ?? image.Prompt, image.Loras), image.References);

    /// <summary>
    /// The LoRAs' trigger words go last, after the translation, so that Claude cannot reword them: they were trained as
    /// written, and Krea's model cards end the description with them the same way.
    /// </summary>
    public static string WithTriggers(string prompt, IReadOnlyList<ImageLoraUsed>? used)
    {
        var triggers = (used ?? []).Select(l => l.Trigger?.Trim().TrimEnd('.', ',')).OfType<string>()
            .Where(t => t.Length > 0 && !prompt.Contains(t, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (triggers.Count == 0)
        {
            return prompt;
        }
        var text = prompt.TrimEnd();
        return $"{text}{(text.Length > 0 && !".!?".Contains(text[^1]) ? "." : "")} {string.Join(", ", triggers).TrimEnd('.')}.";
    }

    /// <summary>
    /// What each reference picture is for, as a sentence per picture after the description (and after the translation,
    /// so Claude cannot turn "image 1" into something else). FLUX.2 Klein's and FLUX 3's edit prompts name references
    /// by their place in the list the same way. A start picture needs no words: the model paints over it.
    /// </summary>
    public static string WithReferences(string prompt, IReadOnlyList<ImageReferenceUsed>? references)
    {
        var looked = (references ?? []).Where(r => r.Role != ReferenceRoles.Start).ToList();
        if (looked.Count == 0)
        {
            return prompt;
        }
        var sentences = looked.Select((r, n) => r.Role switch
        {
            ReferenceRoles.Figure => $"Featuring the character from image {n + 1}.",
            ReferenceRoles.Place => $"Set in the place shown in image {n + 1}.",
            _ => $"In the visual style, palette and texture of image {n + 1}.",
        });
        var text = prompt.TrimEnd();
        return $"{text}{(text.Length > 0 && !".!?".Contains(text[^1]) ? "." : "")} {string.Join(" ", sentences)}";
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
        var references = Directory.Exists(Folder) ? Directory.EnumerateFiles(Folder, $"{id}.ref*").ToList() : [];
        foreach (var path in (IEnumerable<string>)[Path.Combine(Folder, $"{id}.json"), ImagePath(id), .. references])
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
