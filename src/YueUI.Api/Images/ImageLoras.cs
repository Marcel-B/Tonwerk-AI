using System.Buffers.Binary;
using System.Text.Json;
using Microsoft.Extensions.Options;
using YueUI.Api.Data;
using YueUI.Api.Loras;

namespace YueUI.Api.Images;

/// <summary>A LoRA for the image models, as the cover dialog offers it.</summary>
/// <param name="Name">The file name without <c>.safetensors</c>; what a picture's request names.</param>
/// <param name="Model">The <see cref="ImageModel.Id"/> it was trained for; null until known (a LoRA fits one model only).</param>
/// <param name="Trigger">Words it was trained on, added to the end of the description when it paints.</param>
/// <param name="Strength">How strongly it is applied unless the dialog says otherwise; 1 is as trained.</param>
/// <param name="Source">The Hugging Face repository it was fetched from, if it was.</param>
public sealed record ImageLoraInfo(
    string Name,
    string Label,
    string? Model,
    string? Trigger,
    double Strength,
    long SizeBytes,
    DateTimeOffset ModifiedAt,
    string? Source = null);

/// <summary>A LoRA the dialog offers to fetch from Hugging Face with one tap (<see cref="ImageLoraLibrary.Suggestions"/>).</summary>
/// <param name="Repo">The repository, e.g. <c>krea/Krea-2-LoRA-neondrip</c>.</param>
/// <param name="File">The weights file in it.</param>
public sealed record ImageLoraSuggestion(
    string Name,
    string Label,
    string Model,
    string Trigger,
    double Strength,
    string Repo,
    string File,
    string License);

/// <param name="Folder">Where the files lie; files put there by hand are listed too.</param>
/// <param name="Suggestions">Those of <see cref="ImageLoraLibrary.Suggestions"/> not fetched yet.</param>
/// <param name="Fetching">Names being downloaded right now.</param>
public sealed record ImageLoraList(
    string Folder,
    IReadOnlyList<ImageLoraInfo> Loras,
    IReadOnlyList<ImageLoraSuggestion> Suggestions,
    IReadOnlyList<string> Fetching);

/// <summary>What the dialog may change about a LoRA; null leaves a field as it is (an empty text clears it).</summary>
public sealed record ImageLoraUpdate(string? Label, string? Model, string? Trigger, double? Strength);

/// <summary>A LoRA a picture asks for; <paramref name="Strength"/> null takes the LoRA's own.</summary>
public sealed record ImageLoraChoice(string Name, double? Strength = null);

/// <summary>A LoRA a picture was painted with, as it was when the picture was asked for.</summary>
public sealed record ImageLoraUsed(string Name, string Label, double Strength, string? Trigger);

/// <summary>A LoRA file for the runner, with the strength to apply it at.</summary>
public sealed record ImageLoraFile(string Path, double Strength);

/// <summary>
/// The LoRAs for the image models: <c>.safetensors</c> files in <c>image-loras/</c> next to the database (uploaded,
/// fetched from Hugging Face, or put there by hand), and what is known about each in <c>image_loras</c> (the model it
/// fits, its trigger words, its strength). A file without a row gets what its header says: trainers like ai-toolkit and
/// kohya note the base model, and FLUX.2's diffusers layer names give away its width.
/// </summary>
public sealed class ImageLoraLibrary(
    SqliteDatabase database,
    IOptions<ImageOptions> options,
    IHttpClientFactory http,
    ILogger<ImageLoraLibrary> logger)
{
    public const string HttpClientName = "huggingface";

    public const string Extension = ".safetensors";

    /// <summary>A rank 64 LoRA on every layer of Krea 2 is several hundred MB; leave room.</summary>
    public const long MaxUploadBytes = 2L << 30;

    public const int MaxPerPicture = 3;

    public const double MaxStrength = 2;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Lock _gate = new();
    private readonly HashSet<string> _fetching = [];

    public string Folder => Path.Combine(database.Directory, "image-loras");

    /// <summary>
    /// Krea's own style LoRAs for Krea 2 (huggingface.co/collections/krea/krea-2-loras, Krea 2 Community licence like the
    /// model), with the trigger phrases and the strength their model cards give. Klein, Z-Image and FLUX 3 have no
    /// official ones.
    /// </summary>
    public static IReadOnlyList<ImageLoraSuggestion> Suggestions { get; } =
    [
        Krea("retroanime", "Retro-Anime", "Purple retro anime style"),
        Krea("sunsetblur", "Sonnenuntergang verwischt", "ethereal motion blur style"),
        Krea("vintagetarot", "Vintage-Tarot", "vintage tarot style"),
        Krea("rainywindow", "Regenfenster", "rainy window style"),
        Krea("darkbrush", "Tuschpinsel", "monochrome ink wash style"),
        Krea("dotmatrix", "Punktraster", "Monochrome stippling style"),
        Krea("neondrip", "Neon Drip", "Textured abstract style"),
        Krea("kidsdrawing", "Kinderzeichnung", "naive expressive sketch style"),
        Krea("softwatercolor", "Art-déco-Aquarell", "Art Deco watercolor style"),
    ];

    private static ImageLoraSuggestion Krea(string name, string label, string trigger) => new(
        $"krea-{name}", label, "krea-2-turbo", trigger, 1, $"krea/Krea-2-LoRA-{name}", $"{name}.safetensors", "Krea 2 Community");

    public ImageLoraList List()
    {
        var loras = Loras();
        string[] fetching;
        lock (_gate)
        {
            fetching = [.. _fetching.Order(StringComparer.OrdinalIgnoreCase)];
        }
        return new ImageLoraList(
            Folder,
            loras,
            [.. Suggestions.Where(s => !loras.Any(l => l.Name.Equals(s.Name, StringComparison.OrdinalIgnoreCase)))],
            fetching);
    }

    public IReadOnlyList<ImageLoraInfo> Loras()
    {
        var folder = new DirectoryInfo(Folder);
        if (!folder.Exists)
        {
            return [];
        }
        var rows = Rows();
        return
        [
            .. folder.EnumerateFiles("*" + Extension)
                .Where(f => LoraLibrary.ValidName(Path.GetFileNameWithoutExtension(f.Name)))
                .Select(f => Describe(f, rows.GetValueOrDefault(Path.GetFileNameWithoutExtension(f.Name))))
                .OfType<ImageLoraInfo>()
                .OrderBy(l => l.Label, StringComparer.OrdinalIgnoreCase),
        ];
    }

    public ImageLoraInfo? Get(string name) =>
        PathFor(name) is { } path ? Describe(new FileInfo(path), Rows().GetValueOrDefault(name)) : null;

    /// <summary>The file of the LoRA called <paramref name="name"/>, or null if there is none (or the name is not one).</summary>
    public string? PathFor(string name)
    {
        if (!LoraLibrary.ValidName(name))
        {
            return null;
        }
        var path = Path.Combine(Folder, name + Extension);
        return File.Exists(path) ? path : null;
    }

    /// <summary>Stores an upload under <paramref name="name"/>, replacing a LoRA of that name.</summary>
    /// <returns>The stored LoRA, or null if the content is not a safetensors file.</returns>
    public async Task<ImageLoraInfo?> SaveAsync(string name, Stream content, ImageLoraUpdate settings, string? source, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Folder);
        var path = Path.Combine(Folder, name + Extension);
        // A picture might read the old file while the new one arrives: it only ever sees a complete file.
        var temporary = $"{path}.{Guid.NewGuid():N}.part";
        try
        {
            await using (var file = File.Create(temporary))
            {
                await content.CopyToAsync(file, cancellationToken);
            }
            if (ReadHeader(temporary) is null)
            {
                return null;
            }
            File.Move(temporary, path, overwrite: true);
            var detected = DetectModel(path, options.Value.ResolvedModels);
            Store(name, new Row(
                Blank(settings.Label),
                Blank(settings.Model) ?? detected,
                Blank(settings.Trigger),
                Math.Clamp(settings.Strength ?? 1, 0, MaxStrength),
                source));
            return Get(name);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    /// <summary>
    /// Downloads a suggestion from Hugging Face into the folder. It runs to the end even when the request that asked
    /// for it gives up (a phone locked), since it is the server's own download; the listing names it meanwhile.
    /// </summary>
    /// <returns>Null when it is being fetched already.</returns>
    public async Task<ImageLoraInfo?> FetchAsync(ImageLoraSuggestion suggestion)
    {
        lock (_gate)
        {
            if (!_fetching.Add(suggestion.Name))
            {
                return null;
            }
        }
        try
        {
            using var client = http.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(
                HttpMethod.Get, $"https://huggingface.co/{suggestion.Repo}/resolve/main/{Uri.EscapeDataString(suggestion.File)}");
            if (Token() is { } token)
            {
                request.Headers.Authorization = new("Bearer", token);
            }
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode)
            {
                throw new ImageException(
                    response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden
                        ? $"Hugging Face refused {suggestion.Repo}: accept its licence there and put a token into {options.Value.ResolvedTokenFile}."
                        : $"Hugging Face answered {(int)response.StatusCode} for {suggestion.Repo}.",
                    StatusCodes.Status502BadGateway);
            }
            await using var content = await response.Content.ReadAsStreamAsync();
            var settings = new ImageLoraUpdate(suggestion.Label, suggestion.Model, suggestion.Trigger, suggestion.Strength);
            return await SaveAsync(suggestion.Name, content, settings, suggestion.Repo, CancellationToken.None)
                ?? throw new ImageException($"{suggestion.Repo} sent no safetensors file.", StatusCodes.Status502BadGateway);
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "Could not fetch {Repo}", suggestion.Repo);
            throw new ImageException($"Hugging Face could not be reached: {exception.Message}", StatusCodes.Status502BadGateway);
        }
        finally
        {
            lock (_gate)
            {
                _fetching.Remove(suggestion.Name);
            }
        }
    }

    /// <returns>The changed LoRA, or null if there is none.</returns>
    public ImageLoraInfo? Update(string name, ImageLoraUpdate update)
    {
        if (Get(name) is not { } current)
        {
            return null;
        }
        var row = Rows().GetValueOrDefault(name);
        Store(name, new Row(
            update.Label is null ? row?.Label : Blank(update.Label),
            update.Model is null ? current.Model : Blank(update.Model),
            update.Trigger is null ? current.Trigger : Blank(update.Trigger),
            Math.Clamp(update.Strength ?? current.Strength, 0, MaxStrength),
            row?.Source));
        return Get(name);
    }

    public bool Delete(string name)
    {
        if (PathFor(name) is not { } path)
        {
            return false;
        }
        File.Delete(path);
        using var connection = database.Open();
        SqliteDatabase.Execute(connection, null, "DELETE FROM image_loras WHERE name = $name", ("$name", name));
        return true;
    }

    /// <summary>
    /// The model a LoRA was trained for, from what its file says: the trainer's note (ai-toolkit and kohya write
    /// <c>ss_base_model_version</c>, e.g. <c>flux2_klein_4b</c>), the file name (Civitai's often say Klein4B), or the
    /// width of FLUX.2's diffusers layers (3072 for Klein 4B, 4096 for 9B). Null when none tells.
    /// </summary>
    public static string? DetectModel(string path, IReadOnlyList<ImageModel> models)
    {
        if (ReadHeader(path) is not { } header)
        {
            return null;
        }
        using (header)
        {
            var root = header.RootElement;
            var notes = new List<string>();
            if (root.TryGetProperty("__metadata__", out var metadata) && metadata.ValueKind == JsonValueKind.Object)
            {
                foreach (var key in (string[])["ss_base_model_version", "modelspec.architecture", "base_model", "ss_sd_model_name"])
                {
                    if (metadata.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
                    {
                        notes.Add(value.GetString()!);
                    }
                }
            }
            notes.Add(Path.GetFileNameWithoutExtension(path));
            var found = notes.Select(Family).FirstOrDefault(f => f is not null);
            if (found is null)
            {
                foreach (var tensor in root.EnumerateObject())
                {
                    // FLUX.2's single blocks fuse attention and MLP into to_qkv_mlp_proj; its input width is the model's.
                    if (tensor.Name.Contains("to_qkv_mlp_proj", StringComparison.Ordinal)
                        && (tensor.Name.EndsWith("lora_down.weight", StringComparison.Ordinal) || tensor.Name.EndsWith("lora_A.weight", StringComparison.Ordinal))
                        && tensor.Value.TryGetProperty("shape", out var shape) && shape.GetArrayLength() == 2)
                    {
                        found = shape[1].GetInt32() switch
                        {
                            3072 => "klein-4b",
                            4096 => "klein-9b",
                            _ => null,
                        };
                        break;
                    }
                }
            }
            return found is not null && models.Any(m => m.Id == found) ? found : null;
        }
    }

    private static string? Family(string note)
    {
        var text = new string([.. note.ToLowerInvariant().Where(char.IsLetterOrDigit)]);
        return text switch
        {
            _ when text.Contains("klein4b") || text.Contains("f2k4b") || text.Contains("flux2klein4b") => "klein-4b",
            _ when text.Contains("klein9b") || text.Contains("f2k9b") => "klein-9b",
            _ when text.Contains("zimage") => "z-image-turbo",
            _ when text.Contains("krea2") || text.Contains("krea") => "krea-2-turbo",
            _ when text.Contains("flux3") => "flux3-klein",
            _ => null,
        };
    }

    /// <summary>
    /// The header of a safetensors file (the tensors' names and shapes plus <c>__metadata__</c>), or null when the file is
    /// not one: eight bytes of header length (little endian), then the header as a JSON object.
    /// </summary>
    public static JsonDocument? ReadHeader(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            Span<byte> prefix = stackalloc byte[8];
            if (file.ReadAtLeast(prefix, 8, throwOnEndOfStream: false) < 8)
            {
                return null;
            }
            var length = BinaryPrimitives.ReadUInt64LittleEndian(prefix);
            if (length is < 2 or > 100_000_000 || (long)length > file.Length - 8)
            {
                return null;
            }
            var bytes = new byte[length];
            file.ReadExactly(bytes);
            var json = JsonDocument.Parse(bytes);
            if (json.RootElement.ValueKind != JsonValueKind.Object)
            {
                json.Dispose();
                return null;
            }
            return json;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private ImageLoraInfo? Describe(FileInfo file, Row? row)
    {
        var name = Path.GetFileNameWithoutExtension(file.Name);
        if (row is null)
        {
            using var header = ReadHeader(file.FullName);
            if (header is null)
            {
                return null;            // half copied, or not a safetensors file
            }
        }
        var model = row?.Model ?? DetectModel(file.FullName, options.Value.ResolvedModels);
        return new ImageLoraInfo(
            name,
            row?.Label ?? name,
            model,
            row?.Trigger ?? (row is null ? TriggerOf(file.FullName) : null),
            row?.Strength ?? 1,
            file.Length,
            file.LastWriteTimeUtc,
            row?.Source);
    }

    /// <summary>A trigger word some trainers note (this app's own song LoRAs, Kohya's <c>ss_trigger</c>, diffusers' instance prompt).</summary>
    private static string? TriggerOf(string path)
    {
        if (LoraLibrary.ReadMetadata(path) is not { } metadata)
        {
            return null;
        }
        foreach (var key in (string[])["trigger_word", "ss_trigger", "instance_prompt", "trigger"])
        {
            if (metadata.TryGetValue(key, out var value) && value.Trim() is { Length: > 0 } trigger)
            {
                return trigger;
            }
        }
        return null;
    }

    private sealed record Row(string? Label, string? Model, string? Trigger, double Strength, string? Source);

    private Dictionary<string, Row> Rows()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name, settings FROM image_loras";
        using var reader = command.ExecuteReader();
        var rows = new Dictionary<string, Row>(StringComparer.Ordinal);
        while (reader.Read())
        {
            if (JsonSerializer.Deserialize<Row>(reader.GetString(1), Json) is { } row)
            {
                rows[reader.GetString(0)] = row;
            }
        }
        return rows;
    }

    private void Store(string name, Row row)
    {
        using var connection = database.Open();
        SqliteDatabase.Execute(
            connection,
            null,
            """
            INSERT INTO image_loras (name, settings, created_at) VALUES ($name, $settings, $created)
            ON CONFLICT (name) DO UPDATE SET settings = excluded.settings
            """,
            ("$name", name),
            ("$settings", JsonSerializer.Serialize(row, Json)),
            ("$created", DateTimeOffset.UtcNow.ToString("O")));
    }

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    /// <summary>Read for every download, so a token put there later works without a restart.</summary>
    private string? Token()
    {
        try
        {
            var path = options.Value.ResolvedTokenFile;
            return File.Exists(path) && File.ReadAllText(path).Trim() is { Length: > 0 } token ? token : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
