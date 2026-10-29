using System.Diagnostics;
using YueUI.Api.Worker;

namespace YueUI.Api.Memory;

/// <summary>The large models besides YuE2 that take turns in the memory.</summary>
public enum LargeModel
{
    /// <summary>The lyrics model in LM Studio (Lyrics/LyricsWriter.cs).</summary>
    Lyrics,

    /// <summary>Separation and Seed-VC for versions, stems and swaps (Voices/VoiceConverter.cs).</summary>
    Voices,

    /// <summary>A text-to-speech model of the speech lab (Speech/SpeechLab.cs).</summary>
    Speech,

    /// <summary>FLUX.2 Klein painting a cover (Images/ImageMaker.cs).</summary>
    Images,

    /// <summary>ACE-Step's LM and DiT making a song (Ace/AceComposer.cs).</summary>
    Ace,

    /// <summary>SoulX-Singer singing a melody with words (Singing/SingingStudio.cs).</summary>
    Singing,
}

/// <summary>
/// Which large model holds the memory: the one place the rule lives that on 24 GB only one of YuE2, the lyrics model,
/// separation with Seed-VC, a speech model, FLUX.2 Klein or ACE-Step fits at a time.
/// </summary>
/// <remarks>
/// The models besides YuE2 share one slot, taken by compare-and-swap, so two of them can never both have it. YuE2 is
/// not in the slot: its worker queues and batches songs itself and announces them through
/// <see cref="WorkerHost.ExpectSongs"/>. Against it the rule is claim, then look: <see cref="TryClaim"/> takes the slot
/// before it checks <see cref="WorkerHost.InUse"/> again, and the queue announces songs before it checks
/// <see cref="Holder"/>, so of a song and a model starting at once one always sees the other. A transcription counts
/// as the worker being in use: it runs inside the worker's process, which a model shuts down to make room.
/// <para>
/// A new model is one entry in <see cref="LargeModel"/>; whoever loads it claims the slot here and the others wait
/// without knowing about it. Changes go into the snapshot (<see cref="StatusSnapshot.Memory"/>), so the browser shows
/// the holder the server decided on instead of inferring it from the stages of everything in the works.
/// </para>
/// </remarks>
public sealed class ModelMemory(WorkerHost host, Status.StatusHub hub, ILogger<ModelMemory> logger)
{
    /// <summary>When the holder took the memory (<see cref="Stopwatch"/> ticks), for the line its release writes.</summary>
    private long _claimedAt;

    /// <summary>0 for none, otherwise the <see cref="LargeModel"/> plus one.</summary>
    private int _holder;

    /// <summary>Held while the holder goes out, so that a release and a claim racing each other publish the last state last.</summary>
    private readonly Lock _publish = new();

    /// <summary>The large model besides YuE2 that holds the memory, or null.</summary>
    public LargeModel? Holder => Volatile.Read(ref _holder) is > 0 and var code ? (LargeModel)(code - 1) : null;

    private string? _outside;

    /// <summary>
    /// Another app's picture that img-o-mat paints on this Mac (<see cref="Images.ImageServiceWatch"/>), as
    /// "app (model)", or null. It holds the memory like a holder without being one of Tonwerk's: nothing here claims
    /// while it paints, and songs wait, but it is no reason in <c>/api/idle</c>, which the LLM gate reads and would
    /// then hold back the very app that paints.
    /// </summary>
    public string? Outside => Volatile.Read(ref _outside);

    /// <summary>Whether a holder or <see cref="Outside"/> takes the memory.</summary>
    public bool Taken => Holder is not null || Outside is not null;

    public void SetOutside(string? painting)
    {
        if (Interlocked.Exchange(ref _outside, painting) is var before && before != painting)
        {
            if (painting is not null)
            {
                logger.LogInformation("img-o-mat paints for {Painting}; Tonwerk's models wait", painting);
            }
            else if (before is not null)
            {
                logger.LogInformation("img-o-mat's picture for {Painting} is done", before);
            }
        }
    }

    /// <summary>Whether <paramref name="model"/> holds the memory.</summary>
    public bool Holds(LargeModel model) => Volatile.Read(ref _holder) == Code(model);

    /// <summary>
    /// Takes the memory for <paramref name="model"/> if no other large model has it and the YuE worker is not in use.
    /// The caller still shuts an idle worker down before loading its model.
    /// </summary>
    /// <returns>False when it has to wait; also when it already holds the memory, since one model does one thing at a time.</returns>
    public bool TryClaim(LargeModel model)
    {
        if (host.InUse || Outside is not null || Interlocked.CompareExchange(ref _holder, Code(model), 0) != 0)
        {
            return false;
        }
        // Looked at again once claimed: a song announced in between sees the claim, or is seen here.
        if (host.InUse)
        {
            Interlocked.CompareExchange(ref _holder, 0, Code(model));
            return false;
        }
        Volatile.Write(ref _claimedAt, Stopwatch.GetTimestamp());
        Published();
        // Who held the memory when is what the log needs to tell why something waited.
        logger.LogInformation("{Model} takes the memory", Name(model));
        return true;
    }

    /// <summary>Waits until <see cref="TryClaim"/> succeeds; nothing announces that the memory came free, so it polls.</summary>
    public async Task ClaimAsync(LargeModel model, TimeSpan interval, TimeProvider time, CancellationToken cancellationToken)
    {
        while (!TryClaim(model))
        {
            await Task.Delay(interval, time, cancellationToken);
        }
    }

    /// <summary>Gives the memory back if <paramref name="model"/> holds it; safe to call more than once.</summary>
    public void Release(LargeModel model)
    {
        var claimedAt = Volatile.Read(ref _claimedAt);
        if (Interlocked.CompareExchange(ref _holder, 0, Code(model)) == Code(model))
        {
            Published();
            logger.LogInformation("{Model} gives the memory back after {Seconds:0} s", Name(model), Stopwatch.GetElapsedTime(claimedAt).TotalSeconds);
        }
    }

    private void Published()
    {
        lock (_publish)
        {
            hub.UpdateMemory(Holder);
        }
    }

    private static int Code(LargeModel model) => (int)model + 1;

    private static string Name(LargeModel model) => model.ToString().ToLowerInvariant();
}
