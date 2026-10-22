using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using YueUI.Api.Images;

namespace YueUI.Api.Tests;

public sealed class CoverStyleTests : IDisposable
{
    private const string Run = "20260921-165850-Neon-Night";

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 1, 2, 3, 4];

    private readonly TestApp _app = new();
    private readonly HttpClient _client;

    public CoverStyleTests() => _client = _app.CreateClient();

    public void Dispose() => _app.Dispose();

    private static MultipartFormDataContent Picture(byte[] bytes, string role = "style")
    {
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        return new MultipartFormDataContent { { file, "file", "ref.png" }, { new StringContent(role), "role" } };
    }

    private async Task<CoverStyle> CreateAsync(string name = "Neon Noir", string? model = "klein-4b")
    {
        var response = await _client.PostAsJsonAsync("/api/cover-styles", new { name, prompt = "Neon rain, deep shadows", model });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CoverStyle>(TestApp.Json))!;
    }

    private async Task<CoverStyle> AddAsync(long id, byte[] bytes, string role = "style")
    {
        var response = await _client.PostAsync($"/api/cover-styles/{id}/images", Picture(bytes, role));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CoverStyle>(TestApp.Json))!;
    }

    [Fact]
    public async Task A_style_keeps_its_text_model_loras_and_pictures_and_goes_with_its_folder()
    {
        var style = await CreateAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync("/api/cover-styles", new { name = "neon noir" })).StatusCode);

        style = await AddAsync(style.Id, Png);
        style = await AddAsync(style.Id, Png, "figure");
        Assert.Equal(["style", "figure"], style.Images.Select(i => i.Role));
        var picture = await _client.GetAsync($"/api/cover-styles/{style.Id}/images/{style.Images[0].Id}");
        Assert.Equal("image/png", picture.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Png, await picture.Content.ReadAsByteArrayAsync());

        var changed = await _client.PutAsJsonAsync($"/api/cover-styles/{style.Id}/images/{style.Images[1].Id}", new { role = "place" });
        Assert.Equal("place", (await changed.Content.ReadFromJsonAsync<CoverStyle>(TestApp.Json))!.Images[1].Role);
        var renamed = await _client.PutAsJsonAsync($"/api/cover-styles/{style.Id}", new { name = "Noir", loras = new[] { new { name = "Urban", strength = 0.7 } } });
        style = (await renamed.Content.ReadFromJsonAsync<CoverStyle>(TestApp.Json))!;
        Assert.Equal(("Noir", "Neon rain, deep shadows", "klein-4b"), (style.Name, style.Prompt, style.Model));
        Assert.Equal([new ImageLoraChoice("Urban", 0.7)], style.Loras);
        Assert.Single((await _client.GetFromJsonAsync<List<CoverStyle>>("/api/cover-styles", TestApp.Json))!);

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/cover-styles/{style.Id}")).StatusCode);
        Assert.False(Directory.Exists(Path.Combine(_app.Root, "cover-styles", style.Id.ToString())));
    }

    [Fact]
    public async Task Only_pictures_are_taken_and_at_most_four()
    {
        var style = await CreateAsync();
        var refused = await _client.PostAsync($"/api/cover-styles/{style.Id}/images", Picture("not a picture at all"u8.ToArray()));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsync($"/api/cover-styles/{style.Id}/images", Picture(Png, "hat"))).StatusCode);
        for (var n = 0; n < CoverStyleLibrary.MaxImages; n++)
        {
            await AddAsync(style.Id, Png);
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsync($"/api/cover-styles/{style.Id}/images", Picture(Png))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/cover-styles/{style.Id}/images/..%2F..%2Fyueui")).StatusCode);
    }

    [Fact]
    public async Task References_are_copied_beside_the_picture_and_named_after_the_translation()
    {
        _app.AddSong(Run, "song1");
        var style = await AddAsync((await CreateAsync()).Id, Png, "figure");
        var upload = await _client.PostAsync("/api/image-references", Picture(Png));
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        var uploaded = (await upload.Content.ReadFromJsonAsync<ImageReferenceUpload>(TestApp.Json))!;

        var response = await _client.PostAsJsonAsync($"/api/songs/{Run}/song1/images", new
        {
            prompt = "A city at night",
            model = "klein-4b",
            references = new object[]
            {
                new { source = "style", id = $"{style.Id}/{style.Images[0].Id}", role = "figure" },
                new { source = "upload", id = uploaded.Id, role = "place" },
            },
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var image = (await response.Content.ReadFromJsonAsync<ImageState>(TestApp.Json))!;
        Assert.Equal([new ImageReferenceUsed("figure", "Neon Noir"), new ImageReferenceUsed("place", "Hochgeladen")], image.References);
        await TestApp.WaitUntil(() => _app.Images.Jobs.Count == 1);
        var job = _app.Images.Jobs[0];
        Assert.Equal("A city at night. Featuring the character from image 1. Set in the place shown in image 2.", job.Prompt);
        Assert.Equal(["figure", "place"], job.References!.Select(r => r.Role));
        Assert.All(job.References!, r => Assert.Equal(Path.Combine(_app.Root, "images"), Path.GetDirectoryName(r.Path)));
        // The upload went with the picture; deleting the style later leaves the picture its copy.
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_app.Root, "images", "uploads")));
        await _client.DeleteAsync($"/api/cover-styles/{style.Id}");
        Assert.True(File.Exists(job.References![0].Path));

        await TestApp.WaitUntil(() => _client.GetAsync($"/api/images/{image.Id}").Result.StatusCode == HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/images/{image.Id}")).StatusCode);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_app.Root, "images"), $"{image.Id}.ref*"));
    }

    [Fact]
    public async Task A_candidate_or_the_cover_can_be_a_reference_and_a_candidate_kept_in_a_style()
    {
        _app.AddSong(Run, "song1");
        var first = (await (await _client.PostAsJsonAsync($"/api/songs/{Run}/song1/images", new { prompt = "Rain", model = "klein-4b" }))
            .Content.ReadFromJsonAsync<ImageState>(TestApp.Json))!;
        await TestApp.WaitUntil(() => _client.GetAsync($"/api/images/{first.Id}").Result.StatusCode == HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.NoContent, (await _client.PutAsync($"/api/images/{first.Id}/cover", null)).StatusCode);

        var response = await _client.PostAsJsonAsync($"/api/songs/{Run}/song1/images", new
        {
            prompt = "Rain again",
            model = "flux3-klein",
            references = new object[] { new { source = "image", id = first.Id }, new { source = "cover" } },
        });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var image = (await response.Content.ReadFromJsonAsync<ImageState>(TestApp.Json))!;
        Assert.Equal(["Kandidat", "Cover"], image.References!.Select(r => r.Label));

        var style = await CreateAsync(model: null);
        var kept = await _client.PostAsync(
            $"/api/cover-styles/{style.Id}/images",
            new MultipartFormDataContent { { new StringContent(first.Id), "candidate" } });
        Assert.Equal(HttpStatusCode.Created, kept.StatusCode);
        Assert.Equal("style", (await kept.Content.ReadFromJsonAsync<CoverStyle>(TestApp.Json))!.Images.Single().Role);
    }

    [Fact]
    public async Task Z_image_paints_over_one_start_picture_and_refuses_pictures_to_look_at()
    {
        _app.AddSong(Run, "song1");
        async Task<string> Upload() =>
            (await (await _client.PostAsync("/api/image-references", Picture(Png))).Content.ReadFromJsonAsync<ImageReferenceUpload>(TestApp.Json))!.Id;

        var looked = await _client.PostAsJsonAsync($"/api/songs/{Run}/song1/images", new
        {
            prompt = "A city", model = "z-image-turbo", references = new[] { new { source = "upload", id = await Upload(), role = "style" } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, looked.StatusCode);
        var strong = await _client.PostAsJsonAsync($"/api/songs/{Run}/song1/images", new
        {
            prompt = "A city", model = "z-image-turbo", references = new[] { new { source = "upload", id = await Upload(), role = "start", strength = 0.95 } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, strong.StatusCode);
        var klein = await _client.PostAsJsonAsync($"/api/songs/{Run}/song1/images", new
        {
            prompt = "A city", model = "klein-4b", references = new[] { new { source = "upload", id = await Upload(), role = "start", strength = 0.5 } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, klein.StatusCode);

        var response = await _client.PostAsJsonAsync($"/api/songs/{Run}/song1/images", new
        {
            prompt = "A city", model = "z-image-turbo", references = new[] { new { source = "upload", id = await Upload(), role = "start", strength = 0.6 } },
        });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await TestApp.WaitUntil(() => _app.Images.Jobs.Count == 1);
        var job = _app.Images.Jobs[0];
        Assert.Equal("A city", job.Prompt);
        Assert.Equal(("start", 0.6), (job.References!.Single().Role, job.References![0].Strength));
    }

    [Fact]
    public async Task The_models_say_what_they_take()
    {
        var info = (await _client.GetFromJsonAsync<ImageInfo>("/api/images", TestApp.Json))!;
        var models = info.Models.ToDictionary(m => m.Id);
        Assert.Equal((4, false), (models["klein-4b"].References, models["klein-4b"].StartImage));
        Assert.Equal((4, false), (models["flux3-klein"].References, models["flux3-klein"].StartImage));
        Assert.Equal((0, true), (models["z-image-turbo"].References, models["z-image-turbo"].StartImage));
        Assert.Equal((0, true), (models["krea-2-turbo"].References, models["krea-2-turbo"].StartImage));
    }

    [Fact]
    public void Role_sentences_follow_the_description()
    {
        Assert.Equal("Rain", ImageMaker.WithReferences("Rain", null));
        Assert.Equal(
            "Rain! In the visual style, palette and texture of image 1.",
            ImageMaker.WithReferences("Rain!", [new ImageReferenceUsed("style", "Stil")]));
        Assert.Equal("Rain", ImageMaker.WithReferences("Rain", [new ImageReferenceUsed("start", "Hochgeladen", 0.5)]));
    }
}
