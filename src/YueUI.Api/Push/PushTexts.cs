using YueUI.Api.Voices;
using YueUI.Api.Worker;

namespace YueUI.Api.Push;

/// <summary>What a notification says. Made here rather than in the frontend's i18n.ts, since no page may be open.</summary>
public static class PushTexts
{
    public static (string Title, string Body) Song(SongState song, string language)
    {
        var de = IsGerman(language);
        var name = string.IsNullOrWhiteSpace(song.Title) ? song.Run : song.Title;
        var which = $"{name} · Song {song.Index}";
        return song.Stage == "ready"
            ? (de ? "Song fertig" : "Song finished",
                song.Seconds is { } seconds ? $"{which} · {Duration(seconds)}" : which)
            : (de ? "Song fehlgeschlagen" : "Song failed", WithMessage(which, song.Message));
    }

    public static (string Title, string Body) Transcription(TranscriptionState transcription, string language)
    {
        var de = IsGerman(language);
        return transcription.Stage == "done"
            ? (de ? "Transkription fertig" : "Transcription finished", transcription.FileName)
            : (de ? "Transkription fehlgeschlagen" : "Transcription failed", WithMessage(transcription.FileName, transcription.Message));
    }

    public static (string Title, string Body) Version(VersionState version, string language)
    {
        var de = IsGerman(language);
        var song = version.SongId[(version.SongId.IndexOf('/') + 1)..].Replace("song", "Song ", StringComparison.Ordinal);
        var which = $"{(string.IsNullOrWhiteSpace(version.Title) ? version.Run : version.Title)} · {song} · {version.VoiceLabel}";
        return version.Stage == "done"
            ? (de ? "Stimme fertig" : "Voice finished", which)
            : (de ? "Stimme fehlgeschlagen" : "Voice failed", WithMessage(which, version.Message));
    }

    public static (string Title, string Body) Stems(StemSetState set, string language)
    {
        var de = IsGerman(language);
        var which = set.Upload
            ? set.Title
            : $"{(string.IsNullOrWhiteSpace(set.Title) ? set.Run : set.Title)} · {set.Song.Replace("song", "Song ", StringComparison.Ordinal)}";
        return set.Stage == "done"
            ? (de ? "Stems fertig" : "Stems ready", which)
            : (de ? "Stems fehlgeschlagen" : "Stems failed", WithMessage(which, set.Message));
    }

    public static (string Title, string Body) Swap(SwapState swap, string language)
    {
        var de = IsGerman(language);
        var which = $"{swap.FileName} · {swap.VoiceLabel}";
        return swap.Stage == "done"
            ? (de ? "Stimme getauscht" : "Voice swapped", which)
            : (de ? "Stimmentausch fehlgeschlagen" : "Voice swap failed", WithMessage(which, swap.Message));
    }

    public static (string Title, string Body) Video(Video.VideoState video, string language)
    {
        var de = IsGerman(language);
        var song = video.SongId[(video.SongId.IndexOf('/') + 1)..].Replace("song", "Song ", StringComparison.Ordinal);
        var which = $"{(string.IsNullOrWhiteSpace(video.Title) ? video.SongId : video.Title)} · {song}";
        return video.Stage == "done"
            ? (de ? "Video fertig" : "Video ready", which)
            : (de ? "Video fehlgeschlagen" : "Video failed", WithMessage(which, video.Message));
    }

    public static (string Title, string Body) Ace(Ace.AceJob job, string language)
    {
        var de = IsGerman(language);
        return job.Stage == "done"
            ? (de ? "ACE-Step-Song fertig" : "ACE-Step song finished", job.Title)
            : (de ? "ACE-Step-Song fehlgeschlagen" : "ACE-Step song failed", WithMessage(job.Title, job.Message));
    }

    public static (string Title, string Body) Sketch(Sketches.SketchJob job, string language)
    {
        var de = IsGerman(language);
        return job.Stage == "done"
            ? (de ? "Ausarbeitung fertig" : "Sketch planned", job.Title)
            : (de ? "Ausarbeitung fehlgeschlagen" : "Sketch failed", WithMessage(job.Title, job.Message));
    }

    public static (string Title, string Body) Lyrics(LyricsState lyrics, string language)
    {
        var de = IsGerman(language);
        return lyrics.Stage == "done"
            ? (de ? "Songtext fertig" : "Lyrics ready", de ? "Der Entwurf steht im Formular." : "The draft is in the form.")
            : (de ? "Songtext fehlgeschlagen" : "Lyrics failed", lyrics.Message ?? "");
    }

    public static (string Title, string Body) BackupFailed(string error, string language) => IsGerman(language)
        ? ("Datensicherung fehlgeschlagen", error)
        : ("Backup failed", error);

    public static (string Title, string Body) Test(string language) => IsGerman(language)
        ? ("Tonwerk", "Benachrichtigungen funktionieren.")
        : ("Tonwerk", "Notifications work.");

    private static bool IsGerman(string language) => language.StartsWith("de", StringComparison.OrdinalIgnoreCase);

    private static string WithMessage(string subject, string? message) =>
        string.IsNullOrWhiteSpace(message) ? subject : $"{subject}: {message}";

    private static string Duration(double seconds)
    {
        var total = (int)Math.Round(seconds);
        return $"{total / 60}:{total % 60:00}";
    }
}
