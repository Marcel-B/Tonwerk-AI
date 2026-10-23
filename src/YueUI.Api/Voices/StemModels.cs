using System.Text.Json.Serialization;

namespace YueUI.Api.Voices;

/// <summary>
/// A separation model StemMyWav offers, as its gateway lists it. Which one is chosen decides how long the separation
/// takes and which stems come back, so the page shows more than the name.
/// </summary>
/// <param name="Task">What it separates: vocals, instrumental, karaoke, 4stem, 6stem or drums.</param>
/// <param name="Stems">The files it makes, each without its .wav ending.</param>
/// <param name="RealtimeFactor">Audio length divided by computing time: 0.3 means a four-minute song takes about thirteen.</param>
/// <param name="IsDefault">The model this server separates with when none is chosen (<see cref="VoiceOptions.StemModel"/>).</param>
public sealed record StemModel(
    string Id,
    string Name,
    string? Task = null,
    IReadOnlyList<string>? Stems = null,
    double? RealtimeFactor = null,
    bool IsDefault = false);

/// <param name="Model">One of <c>GET /api/stems/models</c>; the configured one where it names none.</param>
/// <param name="Dereverb">Also splits the vocals into dry vocals and their reverb, which takes longer.</param>
/// <param name="Machine">The Mac to separate on: <c>local</c>, a helper's id, or null for whichever is free.</param>
public sealed record StemRequest(string? Model = null, bool Dereverb = false, string? Machine = null)
{
    public Dictionary<string, string[]> Validate()
    {
        var errors = new Dictionary<string, string[]>();
        // It goes into StemMyWav's query string; its ids are short slugs.
        if (Model is { } model && (model.Length is 0 or > 100 || model.Any(char.IsWhiteSpace)))
        {
            errors["model"] = ["A model as GET /api/stems/models lists it."];
        }
        return errors;
    }
}

/// <summary>One stem of a separation, stored as <see cref="File"/> in the set's folder.</summary>
/// <param name="Name">What StemMyWav called it, without the ending: vocals, instrumental, drums, vocals_dry, …</param>
/// <param name="File">The stored file's name: FLAC where ffmpeg could encode it, else the WAV as it came.</param>
/// <param name="Peaks">
/// The loudness outline for the page's waveform, read from the WAV on the server so a phone need not load every stem
/// to draw it; null when the WAV could not be read.
/// </param>
public sealed record StemFile(string Name, string File, double Seconds, IReadOnlyList<double>? Peaks);

/// <summary>
/// A song (or an uploaded recording) split into its stems by StemMyWav. The files are this app's own, in
/// <c>stems/</c> next to its database; the song's folder stays as YuE Studio wrote it.
/// <see cref="Stage"/>: queued, separating, done, failed or cancelled.
/// </summary>
public sealed record StemSetState
{
    public required string Id { get; init; }

    /// <summary>
    /// <c>run/songN</c>, the song the stems were separated from; empty for an uploaded file, which waits in the set's
    /// folder as <c>source.*</c> until it is separated.
    /// </summary>
    public required string SongId { get; init; }

    /// <summary>
    /// The run's title when the separation was asked for, for the list and the notification; an upload's file name
    /// without its ending.
    /// </summary>
    public string Title { get; init; } = "";

    public required string Model { get; init; }

    public bool Dereverb { get; init; }

    public string Stage { get; init; } = "queued";

    /// <summary>Why it failed.</summary>
    public string? Message { get; init; }

    /// <summary>Filled once it is done, in the order StemMyWav made them.</summary>
    public IReadOnlyList<StemFile> Stems { get; init; } = [];

    /// <summary>The Mac it was asked for (<c>local</c> or a helper's id, Machines/); null for whichever is free.</summary>
    public string? Machine { get; init; }

    /// <summary>The name of the Mac that runs it, once one does.</summary>
    public string? RanOn { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// When work on it began, on whichever Mac, for the queue page; null while it waits. Only in memory: a job a
    /// restart cuts off fails, one still waiting starts anew.
    /// </summary>
    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public bool Finished => Stage is "done" or "failed" or "cancelled";

    /// <summary>Separated from a file someone uploaded rather than from a song of the library.</summary>
    public bool Upload => SongId.Length == 0;

    /// <summary>Empty for an upload, which no run name matches.</summary>
    [JsonIgnore]
    public string Run => Upload ? "" : SongId[..SongId.IndexOf('/')];

    [JsonIgnore]
    public string Song => Upload ? "" : SongId[(SongId.IndexOf('/') + 1)..];
}
