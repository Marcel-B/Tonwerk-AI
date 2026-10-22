using Microsoft.Extensions.Options;
using YueUI.Api.Writing;

namespace YueUI.Api;

/// <summary>
/// The writing page (<see cref="WritingDesk"/>): conversations with a model in LM Studio. A message answers 202 at
/// once; the answer grows in <c>writing</c> events, since a large local model writes for minutes.
/// </summary>
public static class WritingEndpoints
{
    public static RouteGroupBuilder MapWritingEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/writing", (WritingDesk desk) => desk.Overview())
            .WithName("GetWriting")
            .Produces<WritingOverview>(StatusCodes.Status200OK);
        api.MapPost("/writing", (WritingChatRequest request, WritingDesk desk) =>
        {
            if (request.Title?.Length > 200 || request.Model?.Length > 200)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["title"] = ["At most 200 characters."] });
            }
            var chat = desk.Create(request.Title, request.Model);
            return Results.Created($"/api/writing/{chat.Id}", chat);
        })
            .WithName("CreateWritingChat")
            .Produces<WritingChat>(StatusCodes.Status201Created)
            .ProducesValidationProblem();
        api.MapGet("/writing/{id}", (string id, WritingDesk desk) =>
            desk.Get(id) is { } chat ? Results.Ok(chat) : Results.NotFound())
            .WithName("GetWritingChat")
            .Produces<WritingChat>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);
        api.MapPut("/writing/{id}", (string id, WritingTitleRequest request, WritingDesk desk) =>
        {
            if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Length > 200)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["title"] = ["A title of at most 200 characters."] });
            }
            return desk.Rename(id, request.Title) is { } chat ? Results.Ok(chat) : Results.NotFound();
        })
            .WithName("RenameWritingChat")
            .Produces<WritingChat>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .Produces(StatusCodes.Status404NotFound);
        api.MapDelete("/writing/{id}", (string id, WritingDesk desk) =>
            desk.Delete(id) ? Results.NoContent() : Results.NotFound())
            .WithName("DeleteWritingChat")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound);
        api.MapPost("/writing/{id}/messages", (string id, WritingMessageRequest request, WritingDesk desk, IOptions<WritingOptions> options) =>
        {
            var limit = options.Value.MaxMessageCharacters;
            if (string.IsNullOrWhiteSpace(request.Text) || request.Text.Length > limit)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["text"] = [$"A message of at most {limit} characters."] });
            }
            if (request.Model?.Length > 200)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["model"] = ["At most 200 characters."] });
            }
            return Answer(() => desk.Send(id, request.Text, request.Model, request.Reasoning));
        })
            .WithName("SendWritingMessage")
            .Produces<WritingReply>(StatusCodes.Status202Accepted)
            .ProducesValidationProblem()
            .Produces(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        api.MapPost("/writing/{id}/retry", (string id, WritingRetryRequest? request, WritingDesk desk) =>
            Answer(() => desk.Retry(id, request?.Model, request?.Reasoning)))
            .WithName("RetryWritingMessage")
            .Produces<WritingReply>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        api.MapPost("/writing/{id}/stop", (string id, WritingDesk desk) =>
            desk.Stop(id) ? Results.NoContent() : Results.NotFound())
            .WithName("StopWritingMessage")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound);
        // The model stays loaded for a few minutes after an answer; this gives the memory back at once.
        api.MapPost("/writing/release", async (WritingDesk desk) =>
        {
            await desk.ReleaseAsync();
            return Results.NoContent();
        })
            .WithName("ReleaseWritingModel")
            .Produces(StatusCodes.Status204NoContent);
        return api;
    }

    private static IResult Answer(Func<WritingReply> start)
    {
        try
        {
            var reply = start();
            return Results.Accepted($"/api/writing/{reply.ChatId}", reply);
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound();
        }
        catch (WritingBusyException exception)
        {
            return Results.Problem(exception.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }
}
