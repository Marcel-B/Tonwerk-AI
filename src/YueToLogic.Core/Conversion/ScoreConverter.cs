using System.Text;
using System.Text.Json.Serialization;
using YueToLogic.Core.Abc;
using YueToLogic.Core.Arrangement;
using YueToLogic.Core.Diagnostics;
using YueToLogic.Core.Midi;
using YueToLogic.Core.Model;

namespace YueToLogic.Core.Conversion;

/// <summary>Entry point for hosts: YuE2 <c>score.abc</c> text in, parsed score and MIDI bytes out.</summary>
public interface IScoreConverter
{
    ConversionResult Convert(string abcText, ConversionOptions? options = null);

    Task<ConversionResult> ConvertAsync(Stream abcStream, ConversionOptions? options = null, CancellationToken cancellationToken = default);
}

/// <summary>Serializable conversion settings, e.g. as the body of a web request.</summary>
/// <remarks>
/// This and the arrangement option types use <c>set</c> rather than <c>init</c>: the System.Text.Json source
/// generator assigns <c>default</c> to init-only properties missing from the JSON, which would discard the
/// defaults below for partial requests such as <c>{"includeChordTrack": false}</c>.
/// </remarks>
public sealed record ConversionOptions
{
    public int TicksPerQuarterNote { get; set; } = AbcParseOptions.DefaultTicksPerQuarterNote;

    public bool IncludeChordTrack { get; set; } = true;

    /// <summary>Octave shifts and generated bass/drum tracks; by default the score is rendered as written.</summary>
    public ArrangementOptions Arrangement { get; set; } = new();

    /// <summary>
    /// MIDI channel per track (1-16), keyed by track name (<c>Vocal</c>, <c>Bass</c>, ...; case-insensitive).
    /// A track without an entry gets the next free channel, drums channel 10. Set this to drive external gear
    /// that listens on a fixed channel.
    /// </summary>
    public IReadOnlyDictionary<string, int> MidiChannels { get; set; } = new Dictionary<string, int>();

    /// <summary>
    /// Program change (1-128) sent at the start of a track, keyed by track name. Without an entry the track
    /// sends none and the receiving instrument keeps the sound it is on.
    /// </summary>
    public IReadOnlyDictionary<string, int> MidiPrograms { get; set; } = new Dictionary<string, int>();

    /// <summary>
    /// Bass and drums heard in the song's recording, set by the host (from separated stems), never by a request's JSON:
    /// they are MIDI files, not settings.
    /// </summary>
    [JsonIgnore]
    public RecordedParts? Recorded { get; set; }
}

/// <param name="Success">Whether a score could be read; warnings in <see cref="Diagnostics"/> do not affect it.</param>
/// <param name="Score">The score as rendered, i.e. including octave shifts and generated tracks.</param>
/// <param name="Midi">The Standard MIDI File (type 1), or <c>null</c> if conversion failed.</param>
public sealed record ConversionResult(
    bool Success,
    ScoreDocument? Score,
    byte[]? Midi,
    IReadOnlyList<Diagnostic> Diagnostics);

public sealed class ScoreConverter(IAbcScoreParser parser, IScoreArranger arranger, IMidiRenderer renderer) : IScoreConverter
{
    public ScoreConverter()
        : this(new AbcScoreParser(), new ScoreArranger(), new MidiRenderer())
    {
    }

    public ConversionResult Convert(string abcText, ConversionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(abcText);
        options ??= new ConversionOptions();
        var optionErrors = ConversionOptionsValidator.Validate(options);
        if (optionErrors.Count > 0)
        {
            return new ConversionResult(false, null, null, optionErrors);
        }

        var parsed = parser.Parse(abcText, new AbcParseOptions { TicksPerQuarterNote = options.TicksPerQuarterNote });
        if (parsed.Score is null)
        {
            return new ConversionResult(false, null, null, parsed.Diagnostics);
        }

        var arranged = arranger.Arrange(parsed.Score, options.Arrangement, options.Recorded);
        var score = arranged.Score;

        var midi = renderer.Render(
            score,
            new MidiRenderOptions
            {
                IncludeChordTrack = options.IncludeChordTrack,
                Channels = options.MidiChannels,
                Programs = options.MidiPrograms,
            });
        return new ConversionResult(true, score, midi, [.. parsed.Diagnostics, .. arranged.Diagnostics]);
    }

    public async Task<ConversionResult> ConvertAsync(
        Stream abcStream,
        ConversionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(abcStream);
        using var reader = new StreamReader(abcStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        return Convert(text, options);
    }
}
