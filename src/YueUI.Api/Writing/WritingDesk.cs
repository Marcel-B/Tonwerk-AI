using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using YueUI.Api.Data;
using YueUI.Api.Lyrics;
using YueUI.Api.Memory;

namespace YueUI.Api.Writing;

/// <summary>A reply is being written already; one model, one answer at a time.</summary>
public sealed class WritingBusyException(string message) : Exception(message);

/// <summary>
/// The writing page: conversations with a model in LM Studio for ideas, rhymes, variants and finished lyrics, at
/// Marcel's wish for the two large models of the 32 GB Mac (Gemma 4 31B for quality, Qwen 3.6 35B A3B for speed).
/// One answer at a time, streamed into <c>writing</c> events.
/// </summary>
/// <remarks>
/// The model takes the memory songs need, so an answer claims <see cref="LargeModel.Lyrics"/> as a lyrics draft
/// does and waits for it while YuE2 or another model holds it. Between answers the model stays loaded for
/// <see cref="WritingOptions.KeepLoaded"/>: loading 20 GB for every message of a conversation would take longer than
/// many answers. The memory is given back when that time is up, when a job waits in the queue, or when the page asks.
/// </remarks>
public sealed partial class WritingDesk(
    SqliteWritingStore store,
    LmStudio lmStudio,
    ModelMemory memory,
    Status.StatusHub hub,
    IOptions<WritingOptions> options,
    IOptions<LyricsOptions> lyricsOptions,
    TimeProvider time,
    ILogger<WritingDesk> logger) : IAsyncDisposable
{
    /// <summary>How often a waiting answer asks for the memory, and the kept model looks whether it may stay.</summary>
    public static TimeSpan CheckInterval { get; set; } = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan PublishInterval = TimeSpan.FromMilliseconds(400);

    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _model = new(1, 1);
    private Dictionary<string, WritingChat>? _chats;
    private WritingReply? _reply;
    private CancellationTokenSource? _cancel;
    private LoadedModel? _loaded;
    private bool _claimed;
    private DateTimeOffset _keepUntil;
    private ITimer? _keeper;

    public string DefaultModel => string.IsNullOrWhiteSpace(options.Value.Model) ? lyricsOptions.Value.Model : options.Value.Model.Trim();

    /// <summary>The model kept loaded between answers.</summary>
    public string? Loaded => _loaded?.Model;

    public WritingReply? Reply
    {
        get
        {
            lock (_gate)
            {
                return _reply;
            }
        }
    }

    /// <summary>Whether an answer is being written (or waits for the memory), which a restart would cut off.</summary>
    public bool IsReplying => Reply is { Finished: false };

    public WritingOverview Overview()
    {
        lock (_gate)
        {
            return new WritingOverview(
                [.. Chats().Values.OrderByDescending(c => c.UpdatedAt).Select(WritingChatSummary.Of)],
                _reply,
                DefaultModel,
                Loaded);
        }
    }

    public WritingChat? Get(string id)
    {
        lock (_gate)
        {
            return Chats().TryGetValue(id, out var chat) ? WithReply(chat) : null;
        }
    }

    public WritingChat Create(string? title, string? model)
    {
        var now = time.GetUtcNow();
        var chat = new WritingChat
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Title = title?.Trim() ?? "",
            Model = string.IsNullOrWhiteSpace(model) ? null : model.Trim(),
            CreatedAt = now,
            UpdatedAt = now,
        };
        lock (_gate)
        {
            Save(chat);
        }
        return chat;
    }

    public WritingChat? Rename(string id, string title)
    {
        lock (_gate)
        {
            if (!Chats().TryGetValue(id, out var chat))
            {
                return null;
            }
            return WithReply(Save(chat with { Title = title.Trim() }));
        }
    }

    public bool Delete(string id)
    {
        lock (_gate)
        {
            if (!Chats().Remove(id))
            {
                return false;
            }
            if (_reply is { Finished: false } reply && reply.ChatId == id)
            {
                _cancel?.Cancel();
            }
        }
        try
        {
            store.Remove(id);
        }
        catch (Exception exception) when (exception is Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            logger.LogWarning(exception, "Could not remove the conversation {Id}", id);
        }
        return true;
    }

    /// <summary>Adds the message to the chat and has the model answer it in the background.</summary>
    /// <exception cref="WritingBusyException">An answer is being written.</exception>
    /// <exception cref="KeyNotFoundException">There is no such chat.</exception>
    public WritingReply Send(string chatId, string text, string? model)
    {
        lock (_gate)
        {
            var chat = Ready(chatId);
            chat = Save(chat with
            {
                Messages = [.. chat.Messages, new WritingMessage("user", text.Trim(), time.GetUtcNow())],
                Title = chat.Title.Length > 0 ? chat.Title : TitleOf(text),
            });
            return StartLocked(chat, model);
        }
    }

    /// <summary>Drops the last answer and has the model write it again, for another variant.</summary>
    /// <exception cref="WritingBusyException">An answer is being written.</exception>
    /// <exception cref="KeyNotFoundException">There is no such chat, or nothing to answer in it.</exception>
    public WritingReply Retry(string chatId, string? model)
    {
        lock (_gate)
        {
            var chat = Ready(chatId);
            var messages = chat.Messages.ToList();
            if (messages.Count > 0 && messages[^1].Role == "assistant")
            {
                messages.RemoveAt(messages.Count - 1);
            }
            if (messages.Count == 0)
            {
                throw new KeyNotFoundException("There is nothing to answer.");
            }
            return StartLocked(Save(chat with { Messages = messages }), model);
        }
    }

    public bool Stop(string chatId)
    {
        lock (_gate)
        {
            if (_reply is not { Finished: false } reply || reply.ChatId != chatId)
            {
                return false;
            }
            _cancel?.Cancel();
            return true;
        }
    }

    /// <summary>Unloads the kept model and gives the memory back, unless an answer is being written.</summary>
    public async Task ReleaseAsync()
    {
        if (IsReplying || !await _model.WaitAsync(0))
        {
            return;
        }
        try
        {
            if (!IsReplying)
            {
                await ReleaseLockedAsync();
            }
        }
        finally
        {
            _model.Release();
        }
    }

    private WritingChat Ready(string chatId)
    {
        if (!Chats().TryGetValue(chatId, out var chat))
        {
            throw new KeyNotFoundException($"There is no conversation {chatId}.");
        }
        if (_reply is { Finished: false })
        {
            throw new WritingBusyException("The model is still answering.");
        }
        return chat;
    }

    private WritingReply StartLocked(WritingChat chat, string? model)
    {
        var chosen = !string.IsNullOrWhiteSpace(model) ? model.Trim() : chat.Model ?? DefaultModel;
        var reply = new WritingReply
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            ChatId = chat.Id,
            Model = chosen,
            Stage = "waiting",
            UpdatedAt = time.GetUtcNow(),
        };
        _reply = reply;
        _cancel = new CancellationTokenSource();
        hub.Publish("writing", reply);
        var token = _cancel.Token;
        _ = Task.Run(() => AnswerAsync(reply, chat, token));
        return reply;
    }

    private async Task AnswerAsync(WritingReply reply, WritingChat chat, CancellationToken stopped)
    {
        var settings = options.Value;
        var text = new StringBuilder();
        var lastPublish = DateTimeOffset.MinValue;
        await _model.WaitAsync(CancellationToken.None);
        try
        {
            if (!_claimed)
            {
                await memory.ClaimAsync(LargeModel.Lyrics, CheckInterval, time, stopped);
                _claimed = true;
                KeepWatching();
            }
            if (_loaded is { } kept && kept.Model != reply.Model)
            {
                await kept.DisposeAsync();
                _loaded = null;
            }
            if (_loaded is null)
            {
                reply = Publish(reply with { Stage = "loading" });
                _loaded = await lmStudio.OpenAsync(reply.Model, settings.ContextLength, stopped);
            }
            reply = Publish(reply with { Stage = "writing" });
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(stopped);
            limit.CancelAfter(settings.Timeout);
            var (messages, promptTokens) = Conversation(chat, _loaded.Context);
            var request = new JsonObject
            {
                ["messages"] = messages,
                ["temperature"] = settings.Temperature,
                ["max_tokens"] = Math.Max(1024, _loaded.Context - promptTokens),
            };
            var answer = await LmStudio.StreamChatAsync(_loaded, request, (piece, thinking) =>
            {
                if (!thinking)
                {
                    text.Append(piece);
                }
                var now = time.GetUtcNow();
                if (now - lastPublish >= PublishInterval)
                {
                    lastPublish = now;
                    var shown = Visible(text.ToString());
                    reply = Publish(reply with { Text = shown.Text, Thinking = thinking || shown.Thinking });
                }
            }, limit.Token);
            var final = Visible(answer.Content).Text;
            if (final.Length == 0)
            {
                throw new LyricsUnavailableException(answer.FinishReason == "length"
                    ? "The model spent all its tokens thinking and wrote no answer."
                    : "The model returned no answer.");
            }
            Finish(reply with { Stage = "done", Text = final, Thinking = false }, final, stopped: false);
        }
        catch (OperationCanceledException)
        {
            // Stopped by hand (or deleted, or over time): what came is kept, the model stays loaded.
            var partial = Visible(text.ToString()).Text;
            Finish(reply with { Stage = "stopped", Text = partial, Thinking = false }, partial, stopped: true);
        }
        catch (Exception exception)
        {
            if (exception is not LyricsUnavailableException)
            {
                logger.LogError(exception, "The writing page's answer failed");
            }
            if (_loaded is { } broken)
            {
                await broken.DisposeAsync();
                _loaded = null;
            }
            Finish(reply with { Stage = "failed", Message = exception.Message, Thinking = false }, null, stopped: false);
        }
        finally
        {
            _keepUntil = time.GetUtcNow() + settings.KeepLoaded;
            _model.Release();
            if (_claimed && _loaded is null)
            {
                await ReleaseAsync();
            }
        }
    }

    /// <summary>
    /// The system prompt and the conversation, the oldest messages left out once it outgrows two thirds of the
    /// context, at about three characters a token.
    /// </summary>
    internal static (JsonArray Messages, int PromptTokens) Conversation(WritingChat chat, int context)
    {
        var budget = context * 2 / 3;
        var tokens = SystemPrompt.Length / 3;
        var kept = new List<WritingMessage>();
        foreach (var message in chat.Messages.Reverse())
        {
            var cost = message.Text.Length / 3 + 8;
            if (kept.Count > 0 && tokens + cost > budget)
            {
                break;
            }
            tokens += cost;
            kept.Insert(0, message);
        }
        var messages = new JsonArray(new JsonObject { ["role"] = "system", ["content"] = SystemPrompt });
        foreach (var message in kept)
        {
            messages.Add(new JsonObject { ["role"] = message.Role, ["content"] = message.Text });
        }
        return (messages, tokens + 64);
    }

    internal const string SystemPrompt =
        """
        You are a songwriting partner. The songwriter makes songs with YuE2, a model that sings given lyrics to a style
        description, and comes to you for ideas, themes, titles, images, rhymes, lines, variants and finished lyrics.

        Answer in the language the songwriter writes in, usually German. German lyrics use natural word order, correct
        umlauts (ä, ö, ü, ß), short words rather than long compounds and rhymes that sound natural when sung.

        Be a collaborator: concrete suggestions rather than advice, several options when asked for variants (numbered,
        so they can be referred to), and only what was asked for when a passage is to be revised. Keep explanations
        short; the songwriter wants material.

        When you write lyrics for a song, use YuE2's form: sections marked [Verse], [Pre-Chorus], [Chorus], [Bridge] or
        [Outro] on a line of their own, an empty line between sections, about four lines a section of 6 to 10
        syllables each, lines of a section alike in length, no stage directions, no chords. Plain text, no Markdown
        around the lyrics.
        """;

    private void Finish(WritingReply reply, string? text, bool stopped)
    {
        lock (_gate)
        {
            if (Chats().TryGetValue(reply.ChatId, out var chat))
            {
                Save(chat with
                {
                    Model = reply.Model,
                    Messages = text is { Length: > 0 }
                        ? [.. chat.Messages, new WritingMessage("assistant", text, time.GetUtcNow(), reply.Model, stopped)]
                        : chat.Messages,
                });
            }
            _reply = reply with { UpdatedAt = time.GetUtcNow() };
            _cancel?.Dispose();
            _cancel = null;
            hub.Publish("writing", _reply);
        }
    }

    private WritingReply Publish(WritingReply reply)
    {
        reply = reply with { UpdatedAt = time.GetUtcNow() };
        lock (_gate)
        {
            _reply = reply;
            hub.Publish("writing", reply);
        }
        return reply;
    }

    /// <summary>Looks every few seconds whether the kept model may stay: not past its time, nobody waiting.</summary>
    private void KeepWatching()
    {
        _keeper ??= time.CreateTimer(_ => _ = CheckKeptAsync(), null, CheckInterval, CheckInterval);
    }

    private async Task CheckKeptAsync()
    {
        if (!_claimed || IsReplying)
        {
            return;
        }
        // Songs, renders and drafts wait in the queue; they need the memory more than an idle chat.
        if (time.GetUtcNow() >= _keepUntil || hub.Queue.Count > 0)
        {
            await ReleaseAsync();
        }
    }

    private async Task ReleaseLockedAsync()
    {
        if (_loaded is { } loaded)
        {
            logger.LogInformation("Unloading {Model}, which the writing page kept", loaded.Model);
            _loaded = null;
            await loaded.DisposeAsync();
        }
        if (_claimed)
        {
            _claimed = false;
            memory.Release(LargeModel.Lyrics);
        }
        _keeper?.Dispose();
        _keeper = null;
    }

    public async ValueTask DisposeAsync()
    {
        _keeper?.Dispose();
        if (_loaded is { } loaded)
        {
            await loaded.DisposeAsync();
        }
    }

    private WritingChat WithReply(WritingChat chat) =>
        _reply is { } reply && reply.ChatId == chat.Id ? chat with { Reply = reply } : chat;

    private Dictionary<string, WritingChat> Chats()
    {
        if (_chats is null)
        {
            _chats = [];
            try
            {
                foreach (var chat in store.All())
                {
                    _chats[chat.Id] = chat;
                }
            }
            catch (Exception exception) when (exception is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(exception, "Could not read the writing page's conversations");
            }
        }
        return _chats;
    }

    private WritingChat Save(WritingChat chat)
    {
        chat = chat with { Reply = null, UpdatedAt = time.GetUtcNow() };
        Chats()[chat.Id] = chat;
        try
        {
            store.Save(chat);
        }
        catch (Exception exception) when (exception is Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            logger.LogWarning(exception, "Could not store the conversation {Id}", chat.Id);
        }
        return chat;
    }

    /// <summary>The answer without reasoning a model writes inline, and whether it is inside it right now.</summary>
    internal static (string Text, bool Thinking) Visible(string content)
    {
        var open = content.LastIndexOf("<think>", StringComparison.Ordinal);
        var thinking = open >= 0 && content.IndexOf("</think>", open, StringComparison.Ordinal) < 0;
        return (Thought().Replace(content, "").Trim(), thinking);
    }

    internal static string TitleOf(string text)
    {
        var line = text.Trim().Split('\n')[0].Trim();
        return line.Length > 60 ? line[..60].TrimEnd() + " …" : line;
    }

    [GeneratedRegex(@"<think>.*?(</think>|$)", RegexOptions.Singleline)]
    private static partial Regex Thought();
}
