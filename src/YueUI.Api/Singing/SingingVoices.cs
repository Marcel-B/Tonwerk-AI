using Microsoft.Extensions.Options;
using YueUI.Api.Data;

namespace YueUI.Api.Singing;

/// <summary>
/// The voices the singing page offers: the configured ones (SoulX-Singer's examples by default) and the ones made from
/// recordings of one's own, which lie next to the database (<see cref="SqliteSingingStore.VoiceAudioPath"/>).
/// </summary>
public sealed class SingingVoices(IOptions<SingingOptions> options, SqliteSingingStore store)
{
    /// <summary>The longest recording taken; the script keeps a phrase of at most 12 s of it.</summary>
    public const long MaxFileBytes = 100L * 1024 * 1024;

    /// <summary>Configured first, own ones in the order they were made.</summary>
    public IReadOnlyList<SingingVoice> All() =>
    [
        .. options.Value.ResolvedVoices,
        .. store.Voices().Select(v => new SingingVoice
        {
            Id = v.Id,
            Label = v.Label,
            Audio = store.VoiceAudioPath(v.Id),
            Metadata = store.VoiceMetadataPath(v.Id),
            Language = "English",
            Own = true,
        }),
    ];

    public SingingVoice? Find(string? id) => id is null ? null : All().FirstOrDefault(v => v.Id == id);

    /// <summary>What the page lists, the own ones with what they sing.</summary>
    public IReadOnlyList<SingingVoiceInfo> Infos() =>
    [
        .. options.Value.ResolvedVoices.Select(v => new SingingVoiceInfo(v.Id, v.Label, v.Language)),
        .. store.Voices().Select(v => v.Info),
    ];
}
