namespace YueUI.Api.Writing;

/// <summary>The writing page (<see cref="WritingDesk"/>), a chat with a model in LM Studio. Configured under <c>Writing</c>.</summary>
public sealed class WritingOptions
{
    public const string Section = "Writing";

    /// <summary>The model a chat uses when none is chosen; null takes <c>Lyrics:Model</c>.</summary>
    public string? Model { get; set; }

    /// <summary>
    /// Tokens of context the model is loaded with, which the conversation and the answer share; older messages are
    /// left out of the request once the conversation outgrows about two thirds of it.
    /// </summary>
    public int ContextLength { get; set; } = 32768;

    public double Temperature { get; set; } = 0.9;

    /// <summary>
    /// How long the model stays loaded after an answer, so the next one does not wait for 20 GB to load again. Songs
    /// wait meanwhile; a job waiting in the queue ends it early.
    /// </summary>
    public TimeSpan KeepLoaded { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>An answer gives up after this long, counted from when its model is loaded.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(20);

    /// <summary>The longest message accepted.</summary>
    public int MaxMessageCharacters { get; set; } = 12000;
}
