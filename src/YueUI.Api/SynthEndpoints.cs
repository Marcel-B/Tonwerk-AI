using Microsoft.AspNetCore.Mvc;
using YueUI.Api.Data;
using YueUI.Api.Logic;

namespace YueUI.Api;

/// <summary>
/// The Logic page's browser synthesizer: which sound each track plays in the preview, and named sounds to reuse. Only
/// the browser reads the patches; the server keeps them so every browser hears the same.
/// </summary>
public static class SynthEndpoints
{
    public static RouteGroupBuilder MapSynthEndpoints(this RouteGroupBuilder api)
    {
        var synths = api.MapGroup("/logic/synths");
        synths.MapGet("/presets", (SqliteSynthStore store) => Results.Ok(store.ListPresets()))
            .WithName("ListSynthPresets")
            .Produces<IReadOnlyList<SynthPreset>>(StatusCodes.Status200OK);
        synths.MapPut("/presets/{name}", SavePreset)
            .WithName("SaveSynthPreset")
            .Produces<SynthPreset>(StatusCodes.Status200OK)
            .Produces<SynthPreset>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest);
        synths.MapDelete("/presets/{name}", (string name, SqliteSynthStore store) =>
            store.DeletePreset(name.Trim())
                ? Results.NoContent()
                : Results.Problem(title: "Unknown sound", detail: $"There is no sound named '{name}'.", statusCode: StatusCodes.Status404NotFound))
            .WithName("DeleteSynthPreset")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);

        synths.MapGet("/tracks", (SqliteSynthStore store) => Results.Ok(store.ListTracks()))
            .WithName("ListTrackSynths")
            .Produces<IReadOnlyList<TrackSynth>>(StatusCodes.Status200OK);
        synths.MapPut("/tracks/{track}", SetTrack)
            .WithName("SetTrackSynth")
            .Produces<TrackSynth>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest);
        // Idempotent: a track without a sound of its own already plays the default.
        synths.MapDelete("/tracks/{track}", (string track, SqliteSynthStore store) =>
        {
            store.RemoveTrack(track.Trim());
            return Results.NoContent();
        })
            .WithName("DeleteTrackSynth")
            .Produces(StatusCodes.Status204NoContent);

        synths.MapGet("/mixer", (SqliteSynthStore store) => Results.Ok(store.GetMixer()))
            .WithName("GetSynthMixer")
            .Produces<MixerSettings>(StatusCodes.Status200OK);
        synths.MapPut("/mixer", ([FromBody] MixerInput input, SqliteSynthStore store) =>
            input.Problems() is { Count: > 0 } problems
                ? BadRequest("Invalid mixer", problems)
                : Results.Ok(store.SetMixer(input.Settings!.Value)))
            .WithName("SetSynthMixer")
            .Produces<MixerSettings>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest);
        return api;
    }

    private static IResult SavePreset(string name, [FromBody] SynthPresetInput input, SqliteSynthStore store)
    {
        if (input.Problems(name) is { Count: > 0 } problems)
        {
            return BadRequest("Invalid sound", problems);
        }
        var (preset, created) = store.SavePreset(name.Trim(), input.Patch!.Value);
        return created ? Results.Created($"/api/logic/synths/presets/{Uri.EscapeDataString(preset.Name)}", preset) : Results.Ok(preset);
    }

    private static IResult SetTrack(string track, [FromBody] TrackSynthInput input, SqliteSynthStore store)
    {
        if (input.Problems(track) is { Count: > 0 } problems)
        {
            return BadRequest("Invalid sound", problems);
        }
        var preset = string.IsNullOrWhiteSpace(input.Preset) ? null : input.Preset.Trim();
        return Results.Ok(store.SetTrack(track.Trim(), input.Patch!.Value, preset));
    }

    private static IResult BadRequest(string title, IReadOnlyList<string> problems) =>
        Results.Problem(title: title, detail: string.Join("; ", problems) + ".", statusCode: StatusCodes.Status400BadRequest);
}
