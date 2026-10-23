namespace YueUI.Api.Export;

/// <summary>
/// Puts a song's tags (title, lyrics, style and seed, its own cover) into every file that leaves the app: the shared
/// AAC and the FLAC downloads of songs and versions, as the export and the player's copy already
/// had them. The cover is chosen once per song in the library and goes along everywhere from then on.
/// </summary>
/// <remarks>
/// YuE Studio's own <c>audio.flac</c> is never tagged in place: the worker and the Logic page read it, and a render
/// writes it anew. A download gets a tagged copy in a temporary file instead. A file that cannot be tagged still goes
/// out without tags, since the audio matters more than they do.
/// </remarks>
public sealed class TaggedFiles(IAudioTagger tagger, ILogger<TaggedFiles> logger)
{
    /// <summary>Tags the file in place; false (and a warning in the log) when it could not.</summary>
    public bool TryTag(string path, SongTags tags)
    {
        try
        {
            tagger.Write(path, tags);
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Could not tag {Path}", path);
            return false;
        }
    }

    /// <summary>Adds the <see cref="AiMark"/> in place; false (and a warning in the log) when it could not.</summary>
    public bool TryMark(string path, string model)
    {
        try
        {
            tagger.Mark(path, model);
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Could not mark {Path} as AI-made", path);
            return false;
        }
    }

    /// <summary>A tagged copy of <paramref name="source"/> in a temporary file the caller deletes.</summary>
    public string Copy(string source, SongTags tags)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"yueui-tagged-{Guid.NewGuid():N}{Path.GetExtension(source)}");
        try
        {
            File.Copy(source, temp);
        }
        catch
        {
            File.Delete(temp);
            throw;
        }
        TryTag(temp, tags);
        return temp;
    }

    /// <summary>The tagged copy as a download that deletes itself once sent.</summary>
    public IResult Download(string source, SongTags tags, string contentType, string fileName)
    {
        var temp = Copy(source, tags);
        var stream = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.Delete, 1 << 16, FileOptions.DeleteOnClose);
        return Results.File(stream, contentType, fileName);
    }
}
