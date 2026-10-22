namespace YueUI.Api.Writing;

/// <summary>A conversation on the writing page (<see cref="WritingDesk"/>), kept in <c>writing_chats</c>.</summary>
public sealed record WritingChat
{
    public required string Id { get; init; }

    /// <summary>Given by hand, else the start of the first message.</summary>
    public string Title { get; init; } = "";

    /// <summary>The model of the last answer, which the next one takes unless another is chosen; null for the default.</summary>
    public string? Model { get; init; }

    /// <summary>How much the last answer was to think, for the page's picker; null for LM Studio's setting.</summary>
    public Lyrics.LyricsReasoning? Reasoning { get; init; }

    public IReadOnlyList<WritingMessage> Messages { get; init; } = [];

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>The answer being written in this chat, in the answers only, never stored.</summary>
    public WritingReply? Reply { get; init; }
}

/// <param name="Role"><c>user</c> or <c>assistant</c>.</param>
/// <param name="Model">The model that wrote an answer.</param>
/// <param name="Stopped">An answer stopped before its end; what came is kept.</param>
public sealed record WritingMessage(string Role, string Text, DateTimeOffset At, string? Model = null, bool Stopped = false);

/// <summary>
/// The answer being written, sent as <c>writing</c> events while it grows (every few hundred milliseconds, the whole
/// text so far), so that a phone that locks or reloads picks it up again.
/// </summary>
public sealed record WritingReply
{
    public required string Id { get; init; }

    public required string ChatId { get; init; }

    /// <summary><c>waiting</c> (for the memory), <c>loading</c> (the model), <c>writing</c>, <c>done</c>, <c>stopped</c> or <c>failed</c>.</summary>
    public string Stage { get; init; } = "waiting";

    public string Model { get; init; } = "";

    /// <summary>How much the model was asked to think; null leaves it to LM Studio.</summary>
    public Lyrics.LyricsReasoning? Reasoning { get; init; }

    /// <summary>The answer so far, without the model's reasoning.</summary>
    public string Text { get; init; } = "";

    /// <summary>Whether the model is thinking before it answers (or between), which takes a thinking model minutes.</summary>
    public bool Thinking { get; init; }

    public string? Message { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public bool Finished => Stage is "done" or "stopped" or "failed";
}

/// <param name="Messages">How many messages it holds.</param>
public sealed record WritingChatSummary(string Id, string Title, string? Model, int Messages, DateTimeOffset UpdatedAt)
{
    public static WritingChatSummary Of(WritingChat chat) => new(chat.Id, chat.Title, chat.Model, chat.Messages.Count, chat.UpdatedAt);
}

/// <param name="Chats">Newest first.</param>
/// <param name="Reply">The answer being written, in whichever chat.</param>
/// <param name="Model">The model a chat writes with when none is chosen (<see cref="WritingOptions.Model"/>, else <c>Lyrics:Model</c>).</param>
/// <param name="Loaded">The model kept in memory between answers, until it is released.</param>
public sealed record WritingOverview(IReadOnlyList<WritingChatSummary> Chats, WritingReply? Reply, string Model, string? Loaded);

public sealed record WritingChatRequest(string? Title = null, string? Model = null);

/// <param name="Model">An LM Studio model's key; the chat's last, else the default, when left out.</param>
/// <param name="Reasoning">How much a thinking model thinks first; left out, as set in LM Studio.</param>
public sealed record WritingMessageRequest(string Text, string? Model = null, Lyrics.LyricsReasoning? Reasoning = null);

public sealed record WritingTitleRequest(string Title);

/// <param name="Model">Another model for the new answer; the chat's own when left out.</param>
/// <param name="Reasoning">How much to think this time; left out, as set in LM Studio.</param>
public sealed record WritingRetryRequest(string? Model = null, Lyrics.LyricsReasoning? Reasoning = null);
