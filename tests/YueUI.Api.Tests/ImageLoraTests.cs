using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using YueUI.Api.Images;

namespace YueUI.Api.Tests;

public sealed class ImageLoraTests : IDisposable
{
    private const string Run = "20260921-165850-Neon-Night";

    private readonly TestApp _app = new();
    private readonly HttpClient _client;

    public ImageLoraTests() => _client = _app.CreateClient();

    public void Dispose() => _app.Dispose();

    private string Folder => Path.Combine(_app.Root, "image-loras");

    /// <summary>A safetensors header with the given metadata and tensors (no tensor bytes).</summary>
    private static byte[] Safetensors(string metadata = "{}", string tensors = "")
    {
        var header = Encoding.UTF8.GetBytes($$"""{"__metadata__":{{metadata}}{{tensors}}}""");
        return [.. BitConverter.GetBytes((ulong)header.Length), .. header];
    }

    private void Put(string name, byte[] bytes)
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllBytes(Path.Combine(Folder, name + ".safetensors"), bytes);
    }

    [Fact]
    public async Task Files_put_there_by_hand_are_listed_with_the_model_their_header_or_name_gives_away()
    {
        // As ai-toolkit on Civitai writes it, as Marcel's MetroRose is named, and RealSkin's diffusers layers without metadata.
        Put("71TJT8", Safetensors("""{"ss_base_model_version":"flux2_klein_4b"}"""));
        Put("MetroRose-Flux.2-Klein-4B_c1-st5000", Safetensors());
        Put("RealSkin", Safetensors("{}", ""","transformer.single_transformer_blocks.0.attn.to_qkv_mlp_proj.lora_down.weight":{"dtype":"BF16","shape":[64,3072],"data_offsets":[0,0]}"""));
        Put("mystery", Safetensors("""{"trigger_word":"chrome"}"""));
        File.WriteAllText(Path.Combine(Folder, "broken.safetensors"), "not one");

        var list = (await _client.GetFromJsonAsync<ImageLoraList>("/api/image-loras", TestApp.Json))!;

        Assert.Equal(Folder, list.Folder);
        Assert.Equal(["71TJT8", "MetroRose-Flux.2-Klein-4B_c1-st5000", "mystery", "RealSkin"], list.Loras.Select(l => l.Name));
        Assert.Equal(["klein-4b", "klein-4b", null, "klein-4b"], list.Loras.Select(l => l.Model));
        Assert.Equal("chrome", list.Loras[2].Trigger);
        Assert.All(list.Loras, l => Assert.Equal(1, l.Strength));
        Assert.Equal(9, list.Suggestions.Count);
        Assert.All(list.Suggestions, s => Assert.Equal("krea-2-turbo", s.Model));
    }

    [Fact]
    public async Task A_lora_paints_with_its_file_strength_and_trigger_after_the_description()
    {
        _app.AddSong(Run, "song1");
        Put("Urban", Safetensors("""{"ss_base_model_version":"flux2_klein_4b"}"""));
        var changed = await _client.PutAsJsonAsync("/api/image-loras/Urban", new { label = "Urban Cybercore", trigger = "urbancyber" });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);

        var response = await _client.PostAsJsonAsync($"/api/songs/{Run}/song1/images", new
        {
            prompt = "A city at night",
            model = "klein-4b",
            loras = new[] { new { name = "Urban", strength = 0.8 } },
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var image = (await response.Content.ReadFromJsonAsync<ImageState>(TestApp.Json))!;
        Assert.Equal([new ImageLoraUsed("Urban", "Urban Cybercore", 0.8, "urbancyber")], image.Loras);
        await TestApp.WaitUntil(() => _app.Images.Jobs.Count == 1);
        var job = _app.Images.Jobs[0];
        Assert.Equal("A city at night. urbancyber.", job.Prompt);
        Assert.Equal([new ImageLoraFile(Path.Combine(Folder, "Urban.safetensors"), 0.8)], job.Loras);
    }

    [Fact]
    public async Task A_lora_for_another_model_or_flux3_is_refused()
    {
        _app.AddSong(Run, "song1");
        Put("Urban", Safetensors("""{"ss_base_model_version":"flux2_klein_4b"}"""));
        Put("mystery", Safetensors());

        async Task<string> Refusal(string model, string lora)
        {
            var response = await _client.PostAsJsonAsync($"/api/songs/{Run}/song1/images", new { prompt = "x", model, loras = new[] { new { name = lora } } });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            return await response.Content.ReadAsStringAsync();
        }

        Assert.Contains("made for another model", await Refusal("z-image-turbo", "Urban"));
        Assert.Contains("takes no LoRAs", await Refusal("flux3-klein", "Urban"));
        Assert.Contains("choose the model it was made for first", await Refusal("klein-4b", "mystery"));
        Assert.Contains("no LoRA", await Refusal("klein-4b", "gone"));
        Assert.Empty(_app.Images.Jobs);
    }

    [Fact]
    public async Task A_suggestion_is_fetched_from_hugging_face_with_its_trigger()
    {
        _app.HuggingFace.Files["/krea/Krea-2-LoRA-neondrip/resolve/main/neondrip.safetensors"] = Safetensors();

        var response = await _client.PostAsync("/api/image-loras/suggestions/krea-neondrip", null);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var lora = (await response.Content.ReadFromJsonAsync<ImageLoraInfo>(TestApp.Json))!;
        Assert.Equal(("krea-neondrip", "Neon Drip", "krea-2-turbo", "Textured abstract style", "krea/Krea-2-LoRA-neondrip"),
            (lora.Name, lora.Label, lora.Model, lora.Trigger, lora.Source));
        var list = (await _client.GetFromJsonAsync<ImageLoraList>("/api/image-loras", TestApp.Json))!;
        Assert.DoesNotContain(list.Suggestions, s => s.Name == "krea-neondrip");

        var missing = await _client.PostAsync("/api/image-loras/suggestions/krea-retroanime", null);
        Assert.Equal(HttpStatusCode.BadGateway, missing.StatusCode);
        Assert.False(File.Exists(Path.Combine(Folder, "krea-retroanime.safetensors")));
    }

    [Fact]
    public async Task An_upload_takes_its_model_and_can_be_deleted()
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Safetensors());
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", "pencil.safetensors");
        form.Add(new StringContent("z-image-turbo"), "model");
        form.Add(new StringContent("pencil sketch"), "trigger");

        var response = await _client.PostAsync("/api/image-loras", form);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var lora = (await response.Content.ReadFromJsonAsync<ImageLoraInfo>(TestApp.Json))!;
        Assert.Equal(("pencil", "z-image-turbo", "pencil sketch"), (lora.Name, lora.Model, lora.Trigger));
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PutAsJsonAsync("/api/image-loras/pencil", new { model = "flux3-klein" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync("/api/image-loras/pencil")).StatusCode);
        Assert.Empty((await _client.GetFromJsonAsync<ImageLoraList>("/api/image-loras", TestApp.Json))!.Loras);
    }

    [Fact]
    public void Triggers_end_the_description_once()
    {
        Assert.Equal("A fox. neon, chrome.", ImageMaker.WithTriggers("A fox", [new("a", "a", 1, "neon."), new("b", "b", 1, "chrome")]));
        Assert.Equal("A neon fox.", ImageMaker.WithTriggers("A neon fox.", [new("a", "a", 1, "neon")]));
        Assert.Equal("A fox.", ImageMaker.WithTriggers("A fox.", null));
    }
}
