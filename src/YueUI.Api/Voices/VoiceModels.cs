using System.Text.Json.Serialization;

namespace YueUI.Api.Voices;

/// <param name="VoicesConfigured">Seed-VC (or ChangeMyVoice) is set up, so reference voices can be managed.</param>
/// <param name="ConversionConfigured">The separator is set up as well, so songs can be sung with another voice.</param>
/// <param name="StemsConfigured">The separator (or StemMyWav) is set up, so songs can be split into stems, with or without voices.</param>
public sealed record VoiceInfo(bool VoicesConfigured, bool ConversionConfigured, bool StemsConfigured = false);

/// <summary>A reference voice kept by ChangeMyVoice; songs are sung with its timbre.</summary>
/// <param name="Seconds">How long the stored recording is; ChangeMyVoice keeps at most 25 seconds.</param>
public sealed record ReferenceVoice(string Id, string Label, double Seconds, DateTimeOffset? CreatedAt);

/// <param name="VoiceId">One of <c>GET /api/voices</c>.</param>
/// <param name="SemiToneShift">
/// Moves the melody, −24 to 24; under the song's own accompaniment only whole octaves (±12) stay in key.
/// </param>
/// <param name="Strength">How much of the reference's timbre comes through, 0 to 1 (Seed-VC's inference CFG rate).</param>
/// <param name="DiffusionSteps">More steps sound cleaner and take longer, 10 to 100.</param>
/// <param name="KeepReverb">
/// Mixes the original's reverb back in. The separated vocals are dry, and a dry voice sits oddly in a mix made
/// with reverb; the reverb carries a trace of the original voice, though.
/// </param>
/// <param name="Machine">The Mac to make it on: <c>local</c>, a helper's id, or null for whichever is free.</param>
public sealed record VersionRequest(
    string? VoiceId,
    int SemiToneShift = 0,
    double Strength = 0.7,
    int DiffusionSteps = 50,
    bool KeepReverb = true,
    string? Machine = null)
{
    public const int MaxSemiToneShift = 24;
    public const int MinDiffusionSteps = 10;
    public const int MaxDiffusionSteps = 100;

    public Dictionary<string, string[]> Validate()
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(VoiceId) || VoiceId.Length > 100)
        {
            errors["voiceId"] = ["A reference voice as GET /api/voices lists it."];
        }
        if (SemiToneShift is < -MaxSemiToneShift or > MaxSemiToneShift)
        {
            errors["semiToneShift"] = [$"-{MaxSemiToneShift} to {MaxSemiToneShift} semitones."];
        }
        if (Strength is < 0 or > 1 || double.IsNaN(Strength))
        {
            errors["strength"] = ["0 to 1."];
        }
        if (DiffusionSteps is < MinDiffusionSteps or > MaxDiffusionSteps)
        {
            errors["diffusionSteps"] = [$"{MinDiffusionSteps} to {MaxDiffusionSteps} steps."];
        }
        return errors;
    }
}

/// <summary>
/// A voice chosen in the form: each song of the run is sung with it as soon as it is finished, as if
/// <c>POST /api/songs/{run}/{song}/versions</c> had been asked for then. Travels with the queued job and the song's
/// state; the label is looked up when the song is asked for, like a version's.
/// </summary>
public sealed record SongVoice(
    string VoiceId,
    string VoiceLabel,
    int SemiToneShift,
    double Strength,
    int DiffusionSteps,
    bool KeepReverb)
{
    public static SongVoice From(ReferenceVoice voice, VersionRequest request) =>
        new(voice.Id, voice.Label, request.SemiToneShift, request.Strength, request.DiffusionSteps, request.KeepReverb);

    public VersionRequest ToRequest() => new(VoiceId, SemiToneShift, Strength, DiffusionSteps, KeepReverb);
}

/// <summary>
/// A song sung with another voice: its vocals separated, converted to a reference voice and mixed back under the
/// instrumental. The song itself stays; the version is a file of this app's own, next to its database.
/// <see cref="Stage"/>: queued, separating, converting, mixing, done, failed or cancelled.
/// </summary>
public sealed record VersionState
{
    public required string Id { get; init; }

    /// <summary><c>run/songN</c>, the song the version was made from.</summary>
    public required string SongId { get; init; }

    /// <summary>The run's title when the version was asked for, for the notification.</summary>
    public string Title { get; init; } = "";

    public required string VoiceId { get; init; }

    public required string VoiceLabel { get; init; }

    public int SemiToneShift { get; init; }

    public double Strength { get; init; }

    public int DiffusionSteps { get; init; }

    public bool KeepReverb { get; init; }

    /// <summary>
    /// StemMyWav's model that separated the vocals, taken from <see cref="VoiceOptions.StemModel"/> when the version
    /// is asked for, so a version can be told apart after the setting changed. Null for versions made before it was kept.
    /// </summary>
    public string? StemModel { get; init; }

    public string Stage { get; init; } = "queued";

    /// <summary>
    /// Progress of the conversion, 0–1, estimated from the time ChangeMyVoice expects; 1 once that time has passed,
    /// since the service reports no progress of its own. Not stored.
    /// </summary>
    public double Fraction { get; init; }

    /// <summary>How long ChangeMyVoice expects the conversion to take, for when it takes longer; not stored.</summary>
    public double EstimatedSeconds { get; init; }

    /// <summary>Why it failed.</summary>
    public string? Message { get; init; }

    /// <summary>The Mac it was asked for (<c>local</c> or a helper's id, Machines/); null for whichever is free.</summary>
    public string? Machine { get; init; }

    /// <summary>The name of the Mac that runs it, once one does.</summary>
    public string? RanOn { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public bool Finished => Stage is "done" or "failed" or "cancelled";

    [JsonIgnore]
    public string Run => SongId[..SongId.IndexOf('/')];

    [JsonIgnore]
    public VoiceConversion Conversion => new(VoiceId, VoiceLabel, SemiToneShift, Strength, DiffusionSteps, SongId);
}

/// <summary>What Seed-VC needs to sing a track with a reference voice, whether a song's version or an uploaded file.</summary>
/// <param name="Subject">What is converted, for the log: a song id or a file name.</param>
public sealed record VoiceConversion(string VoiceId, string VoiceLabel, int SemiToneShift, double Strength, int DiffusionSteps, string Subject);

/// <summary>
/// An uploaded recording sung again with a reference voice, on the voices page: a bare vocal track goes straight to
/// Seed-VC, a whole song is separated first and the new vocals mixed back under its instrumental, as for a version.
/// The upload and the result are this app's own files, in <c>swaps/&lt;id&gt;/</c> next to its database.
/// <see cref="Stage"/>: queued, separating, converting, mixing, done, failed or cancelled.
/// </summary>
public sealed record SwapState
{
    public required string Id { get; init; }

    /// <summary>The uploaded file's name, to tell swaps apart and to name the download.</summary>
    public required string FileName { get; init; }

    public required string VoiceId { get; init; }

    public required string VoiceLabel { get; init; }

    public int SemiToneShift { get; init; }

    public double Strength { get; init; }

    public int DiffusionSteps { get; init; }

    /// <summary>The upload is a whole song: its vocals are separated, converted and mixed back under the rest.</summary>
    public bool Separate { get; init; }

    /// <summary>Only with <see cref="Separate"/>: the original's reverb goes back into the mix.</summary>
    public bool KeepReverb { get; init; }

    /// <summary>The separation model, taken from <see cref="VoiceOptions.StemModel"/> when asked for; null for a bare vocal track.</summary>
    public string? StemModel { get; init; }

    public string Stage { get; init; } = "queued";

    /// <inheritdoc cref="VersionState.Fraction"/>
    public double Fraction { get; init; }

    /// <inheritdoc cref="VersionState.EstimatedSeconds"/>
    public double EstimatedSeconds { get; init; }

    /// <summary>Why it failed.</summary>
    public string? Message { get; init; }

    /// <summary>The Mac it was asked for (<c>local</c> or a helper's id, Machines/); null for whichever is free.</summary>
    public string? Machine { get; init; }

    /// <summary>The name of the Mac that runs it, once one does.</summary>
    public string? RanOn { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public bool Finished => Stage is "done" or "failed" or "cancelled";

    [JsonIgnore]
    public VoiceConversion Conversion => new(VoiceId, VoiceLabel, SemiToneShift, Strength, DiffusionSteps, FileName);
}
