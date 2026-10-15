namespace YueUI.Api.Lyrics;

/// <param name="Id">The model's key in LM Studio, what a draft request names.</param>
/// <param name="Name">LM Studio's display name; the id where there is none.</param>
/// <param name="SizeBytes">Size on disk, roughly the memory it takes; null when the server does not say.</param>
/// <param name="Loaded">Already in memory in LM Studio, so a draft uses it as it is.</param>
/// <param name="Vision">Whether it can read a photo, as LM Studio's capabilities say; null when the server does not say.</param>
/// <param name="Cloud">
/// Runs at Anthropic, not on the Mac (<see cref="ClaudeLyricsModel"/>): the lyrics leave the house, and the draft
/// neither waits for nor stops a song.
/// </param>
public sealed record LyricsModel(string Id, string Name, long? SizeBytes, bool Loaded, bool? Vision, bool Cloud = false);

/// <param name="Default">The configured model (<see cref="LyricsOptions.Model"/>), used when a request names none.</param>
public sealed record LyricsModels(string Default, IReadOnlyList<LyricsModel> Models);
