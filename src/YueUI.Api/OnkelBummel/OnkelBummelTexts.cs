using YueUI.Api.Push;

namespace YueUI.Api.OnkelBummel;

/// <summary>
/// What a message to Onkel Bummel says. A single job says what its Web Push says (<see cref="PushTexts"/>); a batch
/// (the songs of one run, the pictures painted for one song, …) becomes one message with a line per job.
/// </summary>
public static class OnkelBummelTexts
{
    /// <summary>One finished job of a group: whether it succeeded, and its own title and body.</summary>
    public sealed record Done(bool Ok, string Title, string Body);

    public static (string Title, string Body) Group(string kind, IReadOnlyList<Done> done, string language)
    {
        if (done.Count == 1)
        {
            return (done[0].Title, done[0].Body);
        }
        var de = IsGerman(language);
        var ok = done.Count(d => d.Ok);
        var noun = Noun(kind, de);
        var title = ok == done.Count
            ? (de ? $"{done.Count} {noun} fertig" : $"{done.Count} {noun} finished")
            : ok == 0
                ? (de ? $"{done.Count} {noun} fehlgeschlagen" : $"{done.Count} {noun} failed")
                : (de ? $"{ok} von {done.Count} {noun} fertig" : $"{ok} of {done.Count} {noun} finished");
        // A failed line keeps its message; ✓/✗ say which is which without reading it.
        var body = string.Join('\n', done.Select(d => $"{(d.Ok ? "✓" : "✗")} {d.Body}"));
        return (title, body);
    }

    public static (string Title, string Body) Image(Images.ImageState image, string language)
    {
        var de = IsGerman(language);
        var song = image.SongId[(image.SongId.IndexOf('/') + 1)..].Replace("song", "Song ", StringComparison.Ordinal);
        var which = $"{(string.IsNullOrWhiteSpace(image.Title) ? image.SongId : image.Title)} · {song} · {image.ModelLabel}";
        return image.Stage == "done"
            ? (de ? "Cover-Bild fertig" : "Cover picture ready", which)
            : (de ? "Cover-Bild fehlgeschlagen" : "Cover picture failed", WithMessage(which, image.Message));
    }

    public static (string Title, string Body) Speech(Speech.SpeechTake take, string language)
    {
        var de = IsGerman(language);
        var which = take.Source is { } source ? $"{source} · {take.ModelLabel}" : take.ModelLabel;
        if (take.Source is null && take.VoiceLabel is { } voice)
        {
            which += $" · {voice}";
        }
        return take.Stage == "done"
            ? (take.Source is null
                ? (de ? "Sprachaufnahme fertig" : "Speech take ready")
                : (de ? "Sprache gesäubert" : "Speech cleaned"), which)
            : (de ? "Sprachaufnahme fehlgeschlagen" : "Speech take failed", WithMessage(which, take.Message));
    }

    public static (string Title, string Body) Test(string language) => IsGerman(language)
        ? ("Tonwerk-AI", "Nachrichten über Onkel Bummel funktionieren.")
        : ("Tonwerk-AI", "Messages through Onkel Bummel work.");

    private static string Noun(string kind, bool de) => kind switch
    {
        "song" => de ? "Songs" : "songs",
        "version" => de ? "Stimmen" : "voices",
        "image" => de ? "Cover-Bilder" : "cover pictures",
        "speech" => de ? "Sprachaufnahmen" : "speech takes",
        _ => de ? "Aufträge" : "jobs",
    };

    private static bool IsGerman(string language) => language.StartsWith("de", StringComparison.OrdinalIgnoreCase);

    private static string WithMessage(string subject, string? message) =>
        string.IsNullOrWhiteSpace(message) ? subject : $"{subject}: {message}";
}
