using System.Net;
using System.Net.Http.Headers;
using YueUI.Api.Share;

namespace YueUI.Api.Tests;

public sealed class ExportEndpointTests : IDisposable
{
    private const string Run = "20260921-165850-Neon-Night";

    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3];

    private readonly TestApp _app = new();

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task An_mp3_carries_the_songs_title_lyrics_and_the_chosen_artist_genre_and_cover()
    {
        _app.AddSong(Run, "song1");
        _app.AddSong(Run, "song2", title: "Neon: Night?");

        var response = await Export("song2", "mp3", artist: "Marcel", genre: "synthwave", cover: Jpeg);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("audio/mpeg", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("Neon Night-song2.mp3", response.Content.Headers.ContentDisposition!.FileNameStar);
        Assert.Equal((AudioFormat.Mp3, ExportEndpoints.Mp3BitRate), (_app.Encoder.Format, _app.Encoder.BitRate));
        Assert.Equal(_app.AudioPath(Run, "song2"), _app.Encoder.Source);
        var tags = _app.Tagger.Tags!;
        Assert.Equal("Neon: Night?", tags.Title);
        Assert.Equal("Neon: Night?", tags.Album);
        Assert.Equal("Marcel", tags.Artist);
        Assert.Equal("synthwave", tags.Genre);
        Assert.Equal("[verse]\nLa la", tags.Lyrics);
        Assert.Equal("YuE2 · Seed 42 · Dark synthwave", tags.Comment);
        Assert.Equal((2026, 2, 2), (tags.Year, tags.Track, tags.TrackCount));
        Assert.Equal(Jpeg, tags.Cover!.Data);
        Assert.Equal("image/jpeg", tags.Cover.MimeType);
        Assert.Equal(_app.Encoder.Target, _app.Tagger.Path);
        // The temporary file goes once it is sent.
        await TestApp.WaitUntil(() => !File.Exists(_app.Encoder.Target));
    }

    [Fact]
    public async Task A_flac_without_a_loudness_change_is_the_songs_own_audio_tagged()
    {
        _app.AddSong(Run, "song1");

        var response = await Export("song1", "flac");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("audio/flac", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(await File.ReadAllBytesAsync(_app.AudioPath(Run, "song1")), await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(AudioFormat.Flac, _app.Encoder.Format);
        Assert.Null(_app.Tagger.Tags!.Cover);
        Assert.Null(_app.Tagger.Tags.Artist);
    }

    [Fact]
    public async Task An_m4a_is_aac_at_a_higher_rate_than_the_shared_one()
    {
        _app.AddSong(Run, "song1");

        var response = await Export("song1", "M4A");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("audio/mp4", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal((AudioFormat.M4a, ExportEndpoints.M4aBitRate), (_app.Encoder.Format, _app.Encoder.BitRate));
    }

    [Fact]
    public async Task A_small_file_to_share_is_aac_at_the_lowest_rate_with_the_songs_own_cover_and_lyrics()
    {
        _app.AddSong(Run, "song1");
        var cover = new ByteArrayContent(Jpeg);
        cover.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        await _app.CreateClient().PutAsync($"/api/songs/{Run}/song1/cover", new MultipartFormDataContent { { cover, "cover", "cover.jpg" } });

        var response = await Export("song1", "small");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("audio/mp4", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("Neon Night-song1.m4a", response.Content.Headers.ContentDisposition!.FileNameStar);
        Assert.Equal((AudioFormat.M4a, AacEncoder.BitRate), (_app.Encoder.Format, _app.Encoder.BitRate));
        Assert.Equal(Jpeg, _app.Tagger.Tags!.Cover!.Data);
        Assert.Equal("[verse]\nLa la", _app.Tagger.Tags.Lyrics);
    }

    [Theory]
    [InlineData("wav", null)]
    [InlineData(null, null)]
    [InlineData("mp3", new byte[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8' })]
    public async Task Other_formats_and_covers_are_refused(string? format, byte[]? cover)
    {
        _app.AddSong(Run, "song1");

        var response = await Export("song1", format, cover: cover);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(_app.Tagger.Tags);
    }

    [Fact]
    public async Task Without_ffmpeg_an_mp3_is_not_implemented()
    {
        _app.AddSong(Run, "song1");
        _app.Encoder.Available = false;

        var response = await Export("song1", "mp3");

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        Assert.Contains("ffmpeg", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_file_that_cannot_be_tagged_says_why_and_leaves_nothing_behind()
    {
        _app.AddSong(Run, "song1");
        _app.Tagger.Failure = "Could not tag the file: broken";

        var response = await Export("song1", "flac");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("broken", await response.Content.ReadAsStringAsync());
        Assert.False(File.Exists(_app.Tagger.Path));
    }

    [Theory]
    [InlineData(Run, "song3")]
    [InlineData("..", "song1")]
    public async Task A_song_that_does_not_exist_is_not_found(string run, string song)
    {
        _app.AddSong(Run, "song1");

        var response = await _app.CreateClient().PostAsync($"/api/songs/{run}/{song}/export", Form("mp3", null, null, null));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private Task<HttpResponseMessage> Export(string song, string? format, string? artist = null, string? genre = null, byte[]? cover = null) =>
        _app.CreateClient().PostAsync($"/api/songs/{Run}/{song}/export", Form(format, artist, genre, cover));

    private static MultipartFormDataContent Form(string? format, string? artist, string? genre, byte[]? cover)
    {
        var form = new MultipartFormDataContent();
        if (format is not null)
        {
            form.Add(new StringContent(format), "format");
        }
        if (artist is not null)
        {
            form.Add(new StringContent(artist), "artist");
        }
        if (genre is not null)
        {
            form.Add(new StringContent(genre), "genre");
        }
        if (cover is not null)
        {
            var content = new ByteArrayContent(cover);
            content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            form.Add(content, "cover", "cover.jpg");
        }
        return form;
    }
}
