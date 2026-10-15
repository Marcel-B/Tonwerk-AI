using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using Microsoft.Extensions.Options;

namespace YueUI.Api.Sketches;

/// <summary>Writes a song plan from a sketch; <see cref="ClaudeSketchModel"/>, faked in tests.</summary>
public interface ISketchModel
{
    Task<SketchAnswer> PlanAsync(string sketch, string? instruction, CancellationToken cancellationToken);
}

public sealed record SketchAnswer(SketchPlan Plan, string Model, int? InputTokens, int? OutputTokens);

/// <summary>The model refused, ran out of room or answered something that is not a plan; the message says which.</summary>
public sealed class SketchModelException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Asks Claude (Anthropic's Messages API, official SDK) for a <see cref="SketchPlan"/> with structured output, so the
/// answer is JSON in <see cref="Schema"/>'s shape. Streamed, since a whole song with thinking takes minutes and an
/// unstreamed request that long risks the HTTP timeout. Nothing of the song is on the Mac's models: this is a call
/// to the cloud, made only when the person asks for it.
/// </summary>
public sealed class ClaudeSketchModel(IOptions<SketchOptions> options, IHttpClientFactory httpClients) : ISketchModel
{
    /// <summary>The named client the SDK sends through, so tests can answer in Anthropic's place.</summary>
    public const string HttpClientName = "anthropic";

    public async Task<SketchAnswer> PlanAsync(string sketch, string? instruction, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var key = settings.ResolveApiKey() ?? throw new SketchModelException("No Anthropic API key is set.");
        var client = new AnthropicClient
        {
            ApiKey = key,
            Timeout = settings.Timeout,
            HttpClient = httpClients.CreateClient(HttpClientName),
        };

        var user = new StringBuilder();
        user.AppendLine("<sketch>");
        user.AppendLine(sketch.Trim());
        user.AppendLine("</sketch>");
        if (!string.IsNullOrWhiteSpace(instruction))
        {
            user.AppendLine();
            user.AppendLine("<wishes>");
            user.AppendLine(instruction.Trim());
            user.AppendLine("</wishes>");
        }
        user.Append("Plan this song.");

        var parameters = new MessageCreateParams
        {
            Model = settings.Model,
            MaxTokens = settings.MaxTokens,
            System = SystemPrompt,
            Thinking = new ThinkingConfigAdaptive(),
            OutputConfig = new OutputConfig
            {
                Effort = ParseEffort(settings.Effort),
                Format = new JsonOutputFormat { Schema = Schema },
            },
            Messages = [new() { Role = Role.User, Content = user.ToString() }],
        };

        var text = new StringBuilder();
        string? stopReason = null;
        int? inputTokens = null;
        int? outputTokens = null;
        try
        {
            await foreach (var streamEvent in client.Messages.CreateStreaming(parameters, cancellationToken))
            {
                if (streamEvent.TryPickContentBlockDelta(out var delta) && delta.Delta.TryPickText(out var piece))
                {
                    text.Append(piece.Text);
                }
                else if (streamEvent.TryPickStart(out var start))
                {
                    inputTokens = (int)start.Message.Usage.InputTokens;
                }
                else if (streamEvent.TryPickDelta(out var messageDelta))
                {
                    stopReason = messageDelta.Delta.StopReason?.Raw();
                    outputTokens = (int)messageDelta.Usage.OutputTokens;
                }
            }
        }
        catch (AnthropicApiException error)
        {
            throw new SketchModelException($"Claude: {error.Message}", error);
        }

        switch (stopReason)
        {
            case "refusal":
                throw new SketchModelException("Claude declined to plan this sketch.");
            case "max_tokens":
                throw new SketchModelException($"Claude's answer outgrew {settings.MaxTokens} tokens (Sketch:MaxTokens).");
        }

        SketchPlan? plan;
        try
        {
            plan = JsonSerializer.Deserialize<SketchPlan>(text.ToString(), JsonSerializerOptions.Web);
        }
        catch (JsonException error)
        {
            throw new SketchModelException("Claude's answer was not a song plan.", error);
        }
        if (plan is null || plan.Sections.Count == 0)
        {
            throw new SketchModelException("Claude's answer held no sections.");
        }
        return new SketchAnswer(plan, settings.Model, inputTokens, outputTokens);
    }

    private static Effort ParseEffort(string effort) => effort.Trim().ToLowerInvariant() switch
    {
        "low" => Effort.Low,
        "medium" => Effort.Medium,
        "xhigh" => Effort.Xhigh,
        "max" => Effort.Max,
        _ => Effort.High,
    };

    /// <summary>
    /// The plan's shape for structured output. Every property is required and nothing else is allowed, as structured
    /// output asks; what may be missing is nullable instead.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, JsonElement> Schema = JsonSerializer
        .Deserialize<Dictionary<string, JsonElement>>(
            """
            {
              "type": "object",
              "additionalProperties": false,
              "required": ["title", "style", "tempo", "beatsPerBar", "key", "sections", "notes"],
              "properties": {
                "title": { "type": "string" },
                "style": { "type": "string" },
                "tempo": { "type": "number" },
                "beatsPerBar": { "type": "integer", "enum": [2, 3, 4, 5, 6] },
                "key": { "type": "string" },
                "notes": { "type": "array", "items": { "type": "string" } },
                "sections": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "additionalProperties": false,
                    "required": ["name", "tag", "parts", "energy", "lyrics", "bars"],
                    "properties": {
                      "name": { "type": "string" },
                      "tag": { "type": "string", "enum": ["intro", "verse", "pre-chorus", "chorus", "bridge", "interlude", "outro"] },
                      "parts": { "type": "array", "items": { "type": "string", "enum": ["piano", "pad", "bass", "drums", "pulse", "sparse-drums"] } },
                      "energy": { "type": "integer", "enum": [1, 2, 3] },
                      "lyrics": { "type": "array", "items": { "type": "string" } },
                      "bars": {
                        "type": "array",
                        "items": {
                          "type": "object",
                          "additionalProperties": false,
                          "required": ["chords", "vocal", "lyric", "ins", "parts"],
                          "properties": {
                            "chords": { "type": "string" },
                            "vocal": { "type": ["string", "null"] },
                            "lyric": { "type": ["string", "null"] },
                            "ins": { "type": ["string", "null"] },
                            "parts": {
                              "anyOf": [
                                { "type": "null" },
                                { "type": "array", "items": { "type": "string", "enum": ["piano", "pad", "bass", "drums", "pulse", "sparse-drums"] } }
                              ]
                            }
                          }
                        }
                      }
                    }
                  }
                }
              }
            }
            """)!;

    /// <summary>
    /// What a plan is and how YuE2 reads it. The rules about notes per syllable and section names come from YuE Studio's
    /// own guide to its score dialect (skills/yue2-music/references/abc-editing.md) and its example, where each lyric
    /// line of seven syllables has a bar of seven note onsets.
    /// </summary>
    public const string SystemPrompt =
        """
        You turn a songwriter's written sketch into a complete song plan. The plan becomes a score for YuE2, a model that
        sings lyrics to a given melody and chords, and a MIDI draft for a DAW. Code converts your plan; you write music
        only, in the notation below.

        Follow the sketch. Its tempo, metre, key, form, bar counts, chords and every melody it writes out are fixed:
        copy written melodies note for note, including rests. Where the sketch only describes a melody (register,
        contour, rhythm, phrasing), compose it in that spirit. Copy the lyrics word for word, in their order, including
        every repeated chorus. Where the sketch leaves something open, decide it musically and say so in `notes`.

        The plan:
        - `title`: the song's title.
        - `style`: YuE2's style prompt in English, comma separated, under 40 words: language of the lyrics, genre,
          main instruments, the singer's voice, mood, then the tempo as "96 BPM". Example: "English, warm piano pop,
          expressive female voice, acoustic piano, rounded bass and light drums, lyrical memorable melody, unhurried
          phrasing, 88 BPM".
        - `tempo` in quarter notes per minute, `beatsPerBar` in quarter notes (4 for 4/4), `key` like C, F#m, Bb, Dm.
        - `sections` in playing order, each with every one of its bars. Repeated sections are written out in full.
          `tag` is one of intro, verse, pre-chorus, chorus, bridge, interlude, outro (an instrumental or solo section
          is interlude). `name` is the sketch's own name for it, e.g. "Chorus 2".
        - `parts`: the accompaniment playing in the section's MIDI draft: piano, pad, bass, drums (full groove),
          pulse (kick and closed hi-hat only), sparse-drums (kick, a soft backbeat, quarter hi-hats). `energy` 1 is
          quiet and held, 2 moving, 3 full. A bar's own `parts` overrides the section's where the sketch changes the
          arrangement inside a section; otherwise it is null.
        - `lyrics`: the section's lines exactly as the sketch writes them, one string per line, empty for a section
          without words.

        Each bar:
        - `chords`: the chords of the bar separated by spaces, spread evenly over it ("F C/E" is half a bar each), or
          with an explicit start in sixteenths after @ ("Gm7 C@12"). Prefer YuE2's chord vocabulary: major (no suffix),
          m, dim, aug, 7, maj7, m7, dim7, m7b5, sus4, sus2, 6, m6, 7sus4, m(maj7), with slash basses like F/A. Richer
          symbols such as add9 or maj9 are allowed; the score keeps their core, the MIDI draft plays them.
        - `vocal`: the sung melody, or null where nobody sings. Notes separated by spaces as PITCH:LENGTH, the length
          in sixteenths; a rest is r:LENGTH. Pitches in scientific notation, C4 is middle C, sharps and flats written
          (F#4, Bb3), whatever the key. The lengths of a bar add up to exactly 4 * beatsPerBar sixteenths (16 in
          4/4). A ~ after the length ties the note into the next note of the same pitch, also across the bar line
          ("D5:4~" then "D5:8" in the next bar is one held note).
        - `lyric`: the words whose syllables start in this bar, or null. One note onset per sung syllable: count the
          syllables and give the bar exactly that many notes (a melisma is a tie, not a new note). A line sung over
          two bars puts its words in the bar where each word starts; together the bars' words are the section's
          `lyrics`.
        - `ins`: an instrumental melody (a motif, a riff, a hook answer) in the same notation, monophonic, or null.
          Use it where the sketch names one; keep it sparse.

        Singability: stepwise motion with occasional leaps, breathing rests between phrases, stressed syllables on
        strong beats or held longer, the register the sketch asks for (else roughly A3 to D5). Choruses repeat their
        melody unless the sketch asks for variation. Check every bar's sum and every syllable count before answering.
        """;
}
