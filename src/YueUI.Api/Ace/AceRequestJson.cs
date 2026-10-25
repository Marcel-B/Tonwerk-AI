using System.Text.Json.Nodes;

namespace YueUI.Api.Ace;

/// <summary>
/// The page's request in acestep.cpp's terms (its <c>AceRequest</c> JSON, documented in its <c>docs/ARCHITECTURE.md</c>).
/// A field left out is the same as its default there, so only what the page set goes in.
/// </summary>
public static class AceRequestJson
{
    /// <summary>The lyrics acestep.cpp and the DiT read as "no vocals".</summary>
    public const string Instrumental = "[Instrumental]";

    /// <param name="seed">The first song's seed; acestep.cpp gives the following songs of a batch the next ones.</param>
    /// <param name="lmModel">The resolved LM file, so the job does not depend on the folder's order.</param>
    /// <param name="ditModel">The resolved DiT file.</param>
    public static JsonObject Build(AceSongRequest request, long seed, string? lmModel, string ditModel)
    {
        var task = request.Task ?? "text2music";
        var json = new JsonObject
        {
            ["caption"] = request.Caption?.Trim() ?? "",
            ["task_type"] = task,
            ["seed"] = seed,
            ["synth_model"] = ditModel,
            // Float, which ace-synth writes without its clipping normalisation; AceMastering brings it to 24 bits for
            // the FLAC. acestep.cpp's own default is a 128 kbit/s MP3.
            ["output_format"] = "wav32",
        };
        if (lmModel is not null && request.UsesLm)
        {
            json["lm_model"] = lmModel;
        }
        if (request.Instrumental == true)
        {
            json["lyrics"] = Instrumental;
        }
        else if (request.Lyrics?.Trim() is { Length: > 0 } lyrics)
        {
            json["lyrics"] = lyrics;
        }
        else if (!request.UsesLm)
        {
            // Without the LM nobody writes lyrics; acestep.cpp hands the DiT the empty ones, which sing nonsense.
            json["lyrics"] = Instrumental;
        }
        if (request.Instrumental != true && !string.IsNullOrWhiteSpace(request.Language))
        {
            json["vocal_language"] = request.Language;
        }
        Set(json, "duration", request.Duration);
        Set(json, "bpm", request.Bpm);
        if (!string.IsNullOrWhiteSpace(request.Key))
        {
            json["keyscale"] = request.Key.Trim();
        }
        if (request.TimeSignature is { } beats)
        {
            json["timesignature"] = beats.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        var batch = Math.Clamp(request.Batch ?? 1, 1, AceSongRequest.MaxBatch);
        if (batch > 1)
        {
            // Different songs from the LM; without it, variations of the source from the DiT.
            json[request.UsesLm ? "lm_batch_size" : "synth_batch_size"] = batch;
        }
        Set(json, "inference_steps", request.Steps);
        Set(json, "guidance_scale", request.Guidance);
        Set(json, "shift", request.Shift);
        if (!string.IsNullOrWhiteSpace(request.Solver))
        {
            json["solver"] = request.Solver;
        }
        Set(json, "lm_temperature", request.LmTemperature);
        Set(json, "lm_cfg_scale", request.LmCfgScale);
        Set(json, "lm_top_p", request.LmTopP);
        Set(json, "lm_top_k", request.LmTopK);
        if (!string.IsNullOrWhiteSpace(request.NegativePrompt))
        {
            json["lm_negative_prompt"] = request.NegativePrompt.Trim();
        }
        if (request.EnrichCaption is { } enrich)
        {
            json["use_cot_caption"] = enrich;
        }
        if (!string.IsNullOrWhiteSpace(request.Adapter))
        {
            json["adapter"] = request.Adapter;
            Set(json, "adapter_scale", request.AdapterScale);
        }
        if (task == "cover")
        {
            Set(json, "audio_cover_strength", request.CoverStrength);
        }
        if (task == "repaint")
        {
            Set(json, "repainting_start", request.RepaintStart);
            Set(json, "repainting_end", request.RepaintEnd);
        }
        return json;
    }

    private static void Set(JsonObject json, string key, double? value)
    {
        if (value is { } number)
        {
            json[key] = number;
        }
    }

    private static void Set(JsonObject json, string key, int? value)
    {
        if (value is { } number)
        {
            json[key] = number;
        }
    }
}
