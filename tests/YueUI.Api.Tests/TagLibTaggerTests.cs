using System.Diagnostics;
using YueUI.Api.Export;
using YueUI.Api.Share;

namespace YueUI.Api.Tests;

/// <summary>Against real files made by ffmpeg, where there is one: whether players find the tags is what could break.</summary>
public sealed class TagLibTaggerTests : IDisposable
{
    /// <summary>The smallest valid PNG: one transparent pixel.</summary>
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=");

    private readonly string _root = Directory.CreateTempSubdirectory("yueui-tagger-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData("mp3", "libmp3lame")]
    [InlineData("m4a", "aac")]
    [InlineData("flac", "flac")]
    public async Task Every_field_reads_back(string extension, string codec)
    {
        if (AacEncoder.FindFfmpeg() is not { } ffmpeg)
        {
            return; // Nothing to run against on a machine without ffmpeg; the Mac and CI's runner have it.
        }
        var path = Path.Combine(_root, $"song.{extension}");
        await Run(ffmpeg, "-nostdin", "-loglevel", "error", "-f", "lavfi", "-i", "sine=frequency=440:duration=1", "-c:a", codec, path);
        var tags = new SongTags(
            "Neon Nächte", "Marcel", "Neon Nächte", 2026, 2, 3, "synthwave", "[verse]\nÜber die Stadt", "YuE2 · Seed 42", new CoverImage(Png, "image/png"));

        new TagLibTagger().Write(path, tags);

        using var file = TagLib.File.Create(path);
        var tag = file.Tag;
        Assert.Equal("Neon Nächte", tag.Title);
        Assert.Equal(["Marcel"], tag.Performers);
        Assert.Equal("Neon Nächte", tag.Album);
        Assert.Equal((2026u, 2u, 3u), (tag.Year, tag.Track, tag.TrackCount));
        Assert.Equal(["synthwave"], tag.Genres);
        Assert.Equal("[verse]\nÜber die Stadt", tag.Lyrics?.Replace("\r\n", "\n").Replace('\r', '\n'));
        Assert.Equal("YuE2 · Seed 42", tag.Comment);
        var picture = Assert.Single(tag.Pictures);
        Assert.Equal(TagLib.PictureType.FrontCover, picture.Type);
        Assert.Equal("image/png", picture.MimeType);
        Assert.Equal(Png, picture.Data.Data);
        Assert.True(file.Properties.Duration > TimeSpan.FromSeconds(0.5));
        if (extension == "mp3")
        {
            Assert.Equal(3, ((TagLib.Id3v2.Tag)file.GetTag(TagLib.TagTypes.Id3v2)).Version);
        }
    }

    [Theory]
    [InlineData("mp3", "libmp3lame")]
    [InlineData("m4a", "aac")]
    [InlineData("flac", "flac")]
    public async Task A_song_says_it_is_AI_made(string extension, string codec)
    {
        if (AacEncoder.FindFfmpeg() is not { } ffmpeg)
        {
            return;
        }
        var path = Path.Combine(_root, $"song.{extension}");
        await Run(ffmpeg, "-nostdin", "-loglevel", "error", "-f", "lavfi", "-i", "sine=frequency=440:duration=1", "-c:a", codec, path);

        new TagLibTagger().Write(path, new SongTags("x", null, null, null, null, null, null, null, "Seed 42", null, "YuE2"));

        Assert.Equal(
            (AiMark.Note("YuE2"), AiMark.SourceType),
            (MarkIn(path, AiMark.Field), MarkIn(path, AiMark.SourceField)));
    }

    [Theory]
    [InlineData("wav", "pcm_s16le")]
    [InlineData("flac", "flac")]
    public async Task A_take_is_marked_and_keeps_its_tags(string extension, string codec)
    {
        if (AacEncoder.FindFfmpeg() is not { } ffmpeg)
        {
            return;
        }
        var path = Path.Combine(_root, $"take.{extension}");
        await Run(ffmpeg, "-nostdin", "-loglevel", "error", "-f", "lavfi", "-i", "sine=frequency=440:duration=1",
            "-metadata", "title=Probe", "-c:a", codec, path);

        new TagLibTagger().Mark(path, "SoulX-Singer");
        new TagLibTagger().Mark(path, "SoulX-Singer");

        Assert.Equal(AiMark.Note("SoulX-Singer"), MarkIn(path, AiMark.Field));
        Assert.Equal(AiMark.SourceType, MarkIn(path, AiMark.SourceField));
        using var file = TagLib.File.Create(path);
        // Marked twice, said once.
        Assert.Equal(AiMark.Note("SoulX-Singer"), file.Tag.Comment);
        if (extension == "flac")
        {
            Assert.Equal("Probe", file.Tag.Title);
        }
        Assert.True(file.Properties.Duration > TimeSpan.FromSeconds(0.5));
    }

    /// <summary>The mark's field as each format keeps it.</summary>
    private static string? MarkIn(string path, string field)
    {
        using var file = TagLib.File.Create(path);
        return file.MimeType switch
        {
            "taglib/mp3" or "taglib/wav" => TagLib.Id3v2.UserTextInformationFrame
                .Get((TagLib.Id3v2.Tag)file.GetTag(TagLib.TagTypes.Id3v2), field, create: false)?.Text.SingleOrDefault(),
            "taglib/m4a" => ((TagLib.Mpeg4.AppleTag)file.GetTag(TagLib.TagTypes.Apple)).GetDashBox("com.apple.iTunes", field),
            "taglib/flac" => ((TagLib.Ogg.XiphComment)file.GetTag(TagLib.TagTypes.Xiph)).GetFirstField(field),
            var other => throw new InvalidOperationException(other),
        };
    }

    [Fact]
    public void A_file_that_is_no_audio_is_refused()
    {
        var path = Path.Combine(_root, "song.flac");
        File.WriteAllBytes(path, [.. "fLaC"u8, .. new byte[96]]);

        Assert.Throws<InvalidOperationException>(() =>
            new TagLibTagger().Write(path, new SongTags("x", null, null, null, null, null, null, null, null, null)));
    }

    private static async Task Run(string tool, params string[] arguments)
    {
        var start = new ProcessStartInfo(tool) { RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start)!;
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, error);
    }
}
