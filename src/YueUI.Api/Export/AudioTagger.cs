using TagLib;

namespace YueUI.Api.Export;

/// <summary>A picture to embed as the front cover.</summary>
/// <param name="MimeType"><c>image/jpeg</c> or <c>image/png</c>, the two every player shows.</param>
public sealed record CoverImage(byte[] Data, string MimeType);

/// <summary>What an exported file says about its song. Null or empty fields are left out.</summary>
/// <param name="Album">The run's title; its songs are variants of one song, numbered by <paramref name="Track"/>.</param>
/// <param name="Comment">Style and seed, so the file still says how it was made.</param>
/// <param name="AiModel">The models that made the sound (<c>YuE2</c>, <c>YuE2, Seed-VC</c>), for <see cref="AiMark"/>.</param>
public sealed record SongTags(
    string Title,
    string? Artist,
    string? Album,
    int? Year,
    int? Track,
    int? TrackCount,
    string? Genre,
    string? Lyrics,
    string? Comment,
    CoverImage? Cover,
    string? AiModel = null);

/// <summary>
/// The machine-readable note that a file is AI-made, which the EU's AI Act (Art. 50(2)) asks of every synthetic audio
/// file a generating system hands out. There is no standard field for audio yet, so the note goes into a field of each
/// format's own kind (ID3 <c>TXXX</c>, an iTunes freeform atom, a Vorbis comment) under two names: <c>AI_GENERATED</c>
/// in words, and <c>DIGITAL_SOURCE_TYPE</c> with IPTC's term for media made by a trained model, the term pictures carry
/// in their XMP (Images/yueui_image.py) and platforms read. The comment says it too, for people.
/// </summary>
public static class AiMark
{
    public const string SourceType = "http://cv.iptc.org/newscodes/digitalsourcetype/trainedAlgorithmicMedia";

    public const string Field = "AI_GENERATED";

    public const string SourceField = "DIGITAL_SOURCE_TYPE";

    /// <summary>For a file whose models are not known one by one, such as a music video.</summary>
    public const string Plain = "KI-generiert (Tonwerk-AI)";

    public static string Note(string model) => $"KI-generiert mit {model} (Tonwerk-AI)";
}

/// <summary>Writes <see cref="SongTags"/> into an MP3, M4A or FLAC file in place. Tests replace it.</summary>
public interface IAudioTagger
{
    /// <exception cref="InvalidOperationException">The file is not audio TagLib can read.</exception>
    void Write(string path, SongTags tags);

    /// <summary>
    /// Adds only the <see cref="AiMark"/> to a file the app made (a stem, a spoken or sung take, a swapped voice),
    /// keeping whatever tags it has. WAV files take it as an ID3 chunk.
    /// </summary>
    /// <exception cref="InvalidOperationException">The file is not audio TagLib can read.</exception>
    void Mark(string path, string model);
}

/// <summary>
/// TagLib# writes each format's own kind of tags: ID3v2 for MP3 (<c>USLT</c> lyrics, <c>APIC</c> cover), iTunes atoms
/// for M4A (<c>©lyr</c>, <c>covr</c>) and Vorbis comments plus a picture block for FLAC. ID3v2.3 rather than 2.4,
/// since Windows and some car radios still read only 2.3.
/// </summary>
public sealed class TagLibTagger : IAudioTagger
{
    static TagLibTagger()
    {
        TagLib.Id3v2.Tag.DefaultVersion = 3;
        TagLib.Id3v2.Tag.ForceDefaultVersion = true;
        // The lyrics frame's language; TagLib takes the server's culture, which is "ivl" under the invariant one.
        // "XXX" is ID3's "unknown": the lyrics may be English or German.
        TagLib.Id3v2.Tag.Language = "XXX";
    }

    public void Write(string path, SongTags tags)
    {
        try
        {
            using var file = TagLib.File.Create(path);
            // A FLAC from the worker may carry tags of its own; the export says only what is known here.
            file.RemoveTags(file.TagTypes & ~TagTypes.FlacMetadata);
            var tag = file.GetTag(KindOf(file), create: true);
            tag.Title = tags.Title;
            tag.Performers = Some(tags.Artist);
            tag.AlbumArtists = Some(tags.Artist);
            tag.Album = Blank(tags.Album);
            tag.Year = (uint)(tags.Year ?? 0);
            tag.Track = (uint)(tags.Track ?? 0);
            tag.TrackCount = (uint)(tags.TrackCount ?? 0);
            tag.Genres = Some(tags.Genre);
            tag.Lyrics = Blank(tags.Lyrics);
            tag.Comment = Blank(tags.Comment);
            if (Blank(tags.AiModel) is { } model)
            {
                WriteMark(file, model);
            }
            if (tags.Cover is { } cover)
            {
                // FLAC keeps pictures in blocks of its own, outside the Vorbis comment; file.Tag reaches both.
                file.Tag.Pictures =
                [
                    new Picture(new ByteVector(cover.Data)) { Type = PictureType.FrontCover, MimeType = cover.MimeType, Description = "Cover" },
                ];
            }
            file.Save();
        }
        catch (Exception exception) when (exception is CorruptFileException or UnsupportedFormatException)
        {
            throw new InvalidOperationException($"Could not tag the file: {exception.Message}", exception);
        }
    }

    public void Mark(string path, string model)
    {
        try
        {
            using var file = TagLib.File.Create(path);
            var tag = file.GetTag(KindOf(file), create: true);
            var note = AiMark.Note(model);
            if (string.IsNullOrWhiteSpace(tag.Comment))
            {
                tag.Comment = note;
            }
            else if (!tag.Comment.Contains(note, StringComparison.Ordinal))
            {
                tag.Comment = $"{note} · {tag.Comment}";
            }
            WriteMark(file, model);
            file.Save();
        }
        catch (Exception exception) when (exception is CorruptFileException or UnsupportedFormatException)
        {
            throw new InvalidOperationException($"Could not mark the file: {exception.Message}", exception);
        }
    }

    private static TagTypes KindOf(TagLib.File file) => file.MimeType switch
    {
        "taglib/mp3" or "taglib/wav" => TagTypes.Id3v2,
        "taglib/m4a" => TagTypes.Apple,
        "taglib/flac" => TagTypes.Xiph,
        var other => throw new InvalidOperationException($"Cannot tag {other}."),
    };

    private static void WriteMark(TagLib.File file, string model)
    {
        var note = AiMark.Note(model);
        switch (file.GetTag(KindOf(file), create: true))
        {
            case TagLib.Id3v2.Tag id3:
                TagLib.Id3v2.UserTextInformationFrame.Get(id3, AiMark.Field, create: true).Text = [note];
                TagLib.Id3v2.UserTextInformationFrame.Get(id3, AiMark.SourceField, create: true).Text = [AiMark.SourceType];
                break;
            case TagLib.Mpeg4.AppleTag apple:
                apple.SetDashBox("com.apple.iTunes", AiMark.Field, note);
                apple.SetDashBox("com.apple.iTunes", AiMark.SourceField, AiMark.SourceType);
                break;
            case TagLib.Ogg.XiphComment xiph:
                xiph.SetField(AiMark.Field, note);
                xiph.SetField(AiMark.SourceField, AiMark.SourceType);
                break;
        }
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string[] Some(string? value) => Blank(value) is { } text ? [text] : [];
}
