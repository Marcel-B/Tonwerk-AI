using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using YueUI.Api.Images;
using YueUI.Api.Memory;

namespace YueUI.Api.Tests;

/// <summary>The engine that paints through img-o-mat, against a handler that plays its API.</summary>
public sealed class ImgOMatEngineTests
{
    private static readonly ImageModel Klein = ImageModel.Defaults.Single(m => m.Id == "klein-4b");
    private static readonly ImageModel Krea = ImageModel.Defaults.Single(m => m.Id == "krea-2-turbo");

    [Fact]
    public void The_models_say_what_img_o_mat_can()
    {
        var engine = Engine(new FakeImgOMat());

        Assert.True(engine.Installed);
        Assert.Null(engine.Unavailable(Klein));
        Assert.True(engine.Downloaded(Klein));
        Assert.Equal("braucht einen Mac mit 32 GB", engine.Unavailable(Krea));
        Assert.False(engine.Downloaded(Krea));
        Assert.True(engine.TokenFound);
    }

    [Fact]
    public void Without_img_o_mat_nothing_paints_and_it_says_why()
    {
        var engine = Engine(new FakeImgOMat { Down = true });

        Assert.False(engine.Installed);
        Assert.StartsWith("img-o-mat (http://img-o-mat:1236) does not answer", engine.Unavailable(Klein));
    }

    [Fact]
    public async Task Paints_with_priority_and_sends_a_lora_once()
    {
        var fake = new FakeImgOMat();
        var engine = Engine(fake);
        var folder = Directory.CreateTempSubdirectory("imgomat-");
        try
        {
            var lora = Path.Combine(folder.FullName, "style.safetensors");
            await File.WriteAllBytesAsync(lora, [1, 2, 3]);
            var stages = new List<string>();
            ImageJob Job(string name) => new(Klein, "a red fox", 7, 1024, 1024, Path.Combine(folder.FullName, name),
                [new ImageLoraFile(lora, 0.8)], [new ImageReferenceFile(lora, "style")]);

            var result = await engine.PaintAsync(Job("a.jpg"), (stage, _) => stages.Add(stage), CancellationToken.None);
            await engine.PaintAsync(Job("b.jpg"), (_, _) => { }, CancellationToken.None);

            Assert.Equal([0xFF, 0xD8], (await File.ReadAllBytesAsync(Path.Combine(folder.FullName, "a.jpg")))[..2]);
            Assert.Equal("a red fox, painted", result.PaintedPrompt);
            Assert.Equal(12.5, result.PaintSeconds);
            Assert.Contains("painting", stages);
            Assert.Equal(1, fake.Uploads);
            var request = fake.Painted!;
            Assert.Equal("klein-4b", (string?)request["model"]);
            Assert.Equal("1024x1024", (string?)request["size"]);
            Assert.Equal(7, (long?)request["seed"]);
            Assert.Equal(0.8, (double?)request["loras"]![0]!["strength"]);
            Assert.Equal("style", (string?)request["references"]![0]!["role"]);
            Assert.Equal(fake.Digest, (string?)request["loras"]![0]!["file"]);
            Assert.Equal("0", fake.Priority);
            Assert.Equal("Bearer tonwerk-ai", fake.Authorization);
        }
        finally
        {
            folder.Delete(true);
        }
    }

    [Fact]
    public async Task The_content_filter_keeps_its_status()
    {
        var engine = Engine(new FakeImgOMat { Refuse = (422, "Der Inhaltsfilter hat das Bild zurückgehalten.") });

        var failure = await Assert.ThrowsAsync<ImageException>(() => engine.PaintAsync(
            new ImageJob(Klein, "x", 1, 1024, 1024, Path.GetTempFileName()), (_, _) => { }, CancellationToken.None));

        Assert.Equal(422, failure.Status);
        Assert.Equal("Der Inhaltsfilter hat das Bild zurückgehalten.", failure.Message);
    }

    [Fact]
    public async Task Another_apps_picture_holds_the_memory_but_not_tonwerks_own()
    {
        using var app = new TestApp();
        var memory = app.Services.GetRequiredService<ModelMemory>();
        var fake = new FakeImgOMat { BusyApp = "cover-tool" };
        var watch = new ImageServiceWatch(new Factory(fake), Options(), memory, TimeProvider.System, NullLogger<ImageServiceWatch>.Instance);

        await watch.PollAsync(CancellationToken.None);
        Assert.Equal("cover-tool (klein-4b)", memory.Outside);
        Assert.True(memory.Taken);
        Assert.False(memory.TryClaim(LargeModel.Lyrics));

        fake.BusyApp = ImgOMatEngine.AppName;
        await watch.PollAsync(CancellationToken.None);
        Assert.Null(memory.Outside);
        Assert.True(memory.TryClaim(LargeModel.Lyrics));
        memory.Release(LargeModel.Lyrics);
    }

    private static IOptions<ImageOptions> Options() => Microsoft.Extensions.Options.Options.Create(new ImageOptions { Service = "http://img-o-mat:1236" });

    private static ImgOMatEngine Engine(FakeImgOMat fake) => new(new Factory(fake), Options(), NullLogger<ImgOMatEngine>.Instance);

    private sealed class Factory(FakeImgOMat fake) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(fake, disposeHandler: false);
    }

    /// <summary>img-o-mat's API as far as the engine and the watch use it.</summary>
    private sealed class FakeImgOMat : HttpMessageHandler
    {
        public bool Down { get; init; }

        public (int Status, string Message)? Refuse { get; init; }

        public string? BusyApp { get; set; }

        public JsonNode? Painted { get; private set; }

        public string? Priority { get; private set; }

        public string? Authorization { get; private set; }

        public int Uploads { get; private set; }

        public string Digest => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(new byte[] { 1, 2, 3 }));

        private readonly HashSet<string> _files = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Down)
            {
                throw new HttpRequestException("Connection refused");
            }
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/v1/models")
            {
                return Json("""
                    {"object":"list","data":[
                      {"id":"klein-4b","available":true,"unavailable":null,"downloaded":true,"tokenFound":true,"memoryGb":13},
                      {"id":"krea-2-turbo","available":false,"unavailable":"braucht einen Mac mit 32 GB","downloaded":false,"tokenFound":true,"memoryGb":28}]}
                    """);
            }
            if (path == "/v1/busy")
            {
                return Json(new JsonObject { ["busy"] = BusyApp is not null, ["app"] = BusyApp, ["model"] = "klein-4b", ["memoryGb"] = 13 }.ToJsonString());
            }
            if (path.StartsWith("/v1/files/", StringComparison.Ordinal))
            {
                var digest = path["/v1/files/".Length..];
                if (request.Method == HttpMethod.Head)
                {
                    return new HttpResponseMessage(_files.Contains(digest) ? HttpStatusCode.OK : HttpStatusCode.NotFound);
                }
                Uploads++;
                _files.Add(digest);
                return new HttpResponseMessage(HttpStatusCode.Created);
            }
            if (path == "/v1/images/generations")
            {
                Painted = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                Priority = request.Headers.GetValues("X-Img-Priority").Single();
                Authorization = request.Headers.Authorization?.ToString();
                if (Refuse is { } refusal)
                {
                    return new HttpResponseMessage((HttpStatusCode)refusal.Status)
                    {
                        Content = new StringContent(new JsonObject { ["error"] = new JsonObject { ["message"] = refusal.Message, ["code"] = refusal.Status } }.ToJsonString()),
                    };
                }
                var jpeg = Convert.ToBase64String([0xFF, 0xD8, 0xFF, 0xD9]);
                var events = new StringBuilder()
                    .Append("event: started\ndata: {\"id\":\"j1\"}\n\n")
                    .Append(": ping\n\n")
                    .Append("event: progress\ndata: {\"stage\":\"loading\",\"fraction\":0}\n\n")
                    .Append("event: progress\ndata: {\"stage\":\"painting\",\"fraction\":0.5}\n\n")
                    .Append($"event: image\ndata: {{\"index\":0,\"b64_json\":\"{jpeg}\",\"revised_prompt\":\"a red fox, painted\",\"seed\":7,\"loadSeconds\":3.0,\"paintSeconds\":12.5,\"peakMemoryGb\":13.1}}\n\n")
                    .Append("event: done\ndata: {\"id\":\"j1\",\"count\":1}\n\n");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(events.ToString(), Encoding.UTF8, "text/event-stream") };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    }
}
