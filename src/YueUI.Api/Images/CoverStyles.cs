using System.Text.Json;
using Microsoft.Data.Sqlite;
using YueUI.Api.Data;

namespace YueUI.Api.Images;

/// <summary>
/// What a reference picture stands for in a cover; each becomes a sentence after the description
/// (<see cref="ImageMaker.WithReferences"/>). <see cref="Start"/> is the one kind Z-Image and Krea 2 take: a picture the
/// new one is painted over rather than one it looks at.
/// </summary>
public static class ReferenceRoles
{
    public const string Style = "style";
    public const string Figure = "figure";
    public const string Place = "place";
    public const string Start = "start";

    public static readonly IReadOnlyList<string> All = [Style, Figure, Place, Start];
}

/// <summary>A reference picture of a cover style.</summary>
/// <param name="Id">Its file's name in the style's folder, without the extension.</param>
public sealed record CoverStyleImage(string Id, string Role);

/// <summary>
/// A cover style: a look kept under a name for all the songs of a project. Choosing it in the cover dialog puts its
/// text into the description, picks its model and LoRAs and takes its pictures as references.
/// </summary>
/// <param name="Prompt">A sentence or two added to the description.</param>
/// <param name="Model">The <see cref="ImageModel.Id"/> it is meant for; null for whichever is chosen.</param>
public sealed record CoverStyle(
    long Id,
    string Name,
    string? Prompt,
    string? Model,
    IReadOnlyList<ImageLoraChoice> Loras,
    IReadOnlyList<CoverStyleImage> Images,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>A new style, or a change to one; null leaves a field as it is on a change.</summary>
public sealed record CoverStyleRequest(string? Name, string? Prompt, string? Model, IReadOnlyList<ImageLoraChoice>? Loras);

public sealed record CoverStyleImageUpdate(string? Role);

/// <summary>A picture handed in for one cover, to name in <see cref="ImageReferenceChoice.Id"/> with source <c>upload</c>.</summary>
public sealed record ImageReferenceUpload(string Id);

/// <summary>Pictures handed in as references, checked by their first bytes rather than by what the client says.</summary>
public static class ReferencePictures
{
    /// <summary>The clients scale a picture to about a megapixel first; a phone photo that was not still fits.</summary>
    public const long MaxBytes = 12L << 20;

    /// <summary>The extension for a JPEG, PNG or WebP picture (all of which Pillow reads for the runners); null otherwise.</summary>
    public static string? ExtensionOf(ReadOnlySpan<byte> head) =>
        head.Length >= 12 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF ? ".jpg"
        : head.Length >= 12 && head[..8].SequenceEqual((byte[])[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]) ? ".png"
        : head.Length >= 12 && head[..4].SequenceEqual("RIFF"u8) && head[8..12].SequenceEqual("WEBP"u8) ? ".webp"
        : null;

    public static string ContentType(string path) => Path.GetExtension(path) switch
    {
        ".png" => "image/png",
        ".webp" => "image/webp",
        _ => "image/jpeg",
    };

    /// <summary>A file's bytes, at most <see cref="MaxBytes"/>; null when it is larger or no picture.</summary>
    public static async Task<(byte[] Bytes, string Extension)?> ReadAsync(IFormFile file, CancellationToken cancellationToken)
    {
        if (file.Length is 0 or > MaxBytes)
        {
            return null;
        }
        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, cancellationToken);
        var bytes = buffer.ToArray();
        return ExtensionOf(bytes) is { } extension ? (bytes, extension) : null;
    }
}

/// <summary>
/// The cover styles: a row each in <c>cover_styles</c> (name, text, model, LoRAs and the pictures' roles as JSON) and
/// the pictures in <c>cover-styles/&lt;id&gt;/</c> next to the database, so the nightly backup takes them along.
/// </summary>
public sealed class CoverStyleLibrary(SqliteDatabase database, TimeProvider time, ILogger<CoverStyleLibrary> logger)
{
    /// <summary>FLUX.2 Klein reads several references, but each one costs tokens and time; four is the dialog's limit too.</summary>
    public const int MaxImages = 4;

    public const int MaxNameLength = 80;

    public const int MaxPromptLength = 600;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record Settings(string? Prompt, string? Model, List<ImageLoraChoice>? Loras, List<CoverStyleImage>? Images);

    public string Folder => Path.Combine(database.Directory, "cover-styles");

    public IReadOnlyList<CoverStyle> List()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, settings, created_at, updated_at FROM cover_styles ORDER BY name_key, id";
        using var reader = command.ExecuteReader();
        var styles = new List<CoverStyle>();
        while (reader.Read())
        {
            styles.Add(Read(reader));
        }
        return styles;
    }

    public CoverStyle? Get(long id)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, settings, created_at, updated_at FROM cover_styles WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    /// <returns>The new style, or null when one of that name exists.</returns>
    public CoverStyle? Create(string name, string? prompt, string? model, IReadOnlyList<ImageLoraChoice>? loras)
    {
        using var connection = database.Open();
        var now = time.GetUtcNow().ToString("O");
        try
        {
            SqliteDatabase.Execute(
                connection,
                null,
                "INSERT INTO cover_styles (name, name_key, settings, created_at, updated_at) VALUES ($name, $key, $settings, $at, $at)",
                ("$name", name),
                ("$key", name.ToUpperInvariant()),
                ("$settings", JsonSerializer.Serialize(new Settings(Blank(prompt), Blank(model), [.. loras ?? []], []), Json)),
                ("$at", now));
        }
        catch (SqliteException e) when (e.SqliteErrorCode == 19)
        {
            return null;
        }
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT last_insert_rowid()";
        return Get((long)command.ExecuteScalar()!);
    }

    /// <returns>The changed style; null when there is none; <paramref name="taken"/> when another has the name.</returns>
    public CoverStyle? Update(long id, CoverStyleRequest change, out bool taken)
    {
        taken = false;
        if (Get(id) is not { } style)
        {
            return null;
        }
        var name = change.Name?.Trim() is { Length: > 0 } given ? given : style.Name;
        var settings = new Settings(
            change.Prompt is null ? style.Prompt : Blank(change.Prompt),
            change.Model is null ? style.Model : Blank(change.Model),
            [.. change.Loras ?? style.Loras],
            [.. style.Images]);
        try
        {
            Store(id, name, settings);
        }
        catch (SqliteException e) when (e.SqliteErrorCode == 19)
        {
            taken = true;
            return style;
        }
        return Get(id);
    }

    public bool Delete(long id)
    {
        using var connection = database.Open();
        if (SqliteDatabase.Execute(connection, null, "DELETE FROM cover_styles WHERE id = $id", ("$id", id)) == 0)
        {
            return false;
        }
        try
        {
            var folder = Path.Combine(Folder, id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Could not delete the pictures of cover style {Id}", id);
        }
        return true;
    }

    /// <returns>The style with the picture; null when there is no style or it has <see cref="MaxImages"/> already.</returns>
    public async Task<CoverStyle?> AddImageAsync(long id, byte[] bytes, string extension, string role, CancellationToken cancellationToken)
    {
        if (Get(id) is not { } style || style.Images.Count >= MaxImages)
        {
            return null;
        }
        var image = new CoverStyleImage(Guid.NewGuid().ToString("N"), role);
        var folder = Path.Combine(Folder, id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(folder);
        await File.WriteAllBytesAsync(Path.Combine(folder, image.Id + extension), bytes, cancellationToken);
        Store(id, style.Name, new Settings(style.Prompt, style.Model, [.. style.Loras], [.. style.Images, image]));
        return Get(id);
    }

    public CoverStyle? SetRole(long id, string imageId, string role)
    {
        if (Get(id) is not { } style || style.Images.All(i => i.Id != imageId))
        {
            return null;
        }
        Store(id, style.Name, new Settings(
            style.Prompt, style.Model, [.. style.Loras], [.. style.Images.Select(i => i.Id == imageId ? i with { Role = role } : i)]));
        return Get(id);
    }

    public CoverStyle? RemoveImage(long id, string imageId)
    {
        if (Get(id) is not { } style || style.Images.All(i => i.Id != imageId))
        {
            return null;
        }
        if (ImagePath(id, imageId) is { } path)
        {
            File.Delete(path);
        }
        Store(id, style.Name, new Settings(style.Prompt, style.Model, [.. style.Loras], [.. style.Images.Where(i => i.Id != imageId)]));
        return Get(id);
    }

    /// <summary>The picture's file; null when the style has no such picture (which also keeps ids from leaving the folder).</summary>
    public string? ImagePath(long id, string imageId)
    {
        if (imageId.Length != 32 || !imageId.All(Uri.IsHexDigit))
        {
            return null;
        }
        var folder = Path.Combine(Folder, id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return Directory.Exists(folder) ? Directory.EnumerateFiles(folder, imageId + ".*").FirstOrDefault() : null;
    }

    private void Store(long id, string name, Settings settings)
    {
        using var connection = database.Open();
        SqliteDatabase.Execute(
            connection,
            null,
            "UPDATE cover_styles SET name = $name, name_key = $key, settings = $settings, updated_at = $at WHERE id = $id",
            ("$id", id),
            ("$name", name),
            ("$key", name.ToUpperInvariant()),
            ("$settings", JsonSerializer.Serialize(settings, Json)),
            ("$at", time.GetUtcNow().ToString("O")));
    }

    private static CoverStyle Read(SqliteDataReader reader)
    {
        var settings = JsonSerializer.Deserialize<Settings>(reader.GetString(2), Json) ?? new Settings(null, null, null, null);
        return new CoverStyle(
            reader.GetInt64(0),
            reader.GetString(1),
            settings.Prompt,
            settings.Model,
            settings.Loras ?? [],
            settings.Images ?? [],
            DateTimeOffset.Parse(reader.GetString(3), System.Globalization.CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(reader.GetString(4), System.Globalization.CultureInfo.InvariantCulture));
    }

    private static string? Blank(string? text) => text?.Trim() is { Length: > 0 } trimmed ? trimmed : null;
}
