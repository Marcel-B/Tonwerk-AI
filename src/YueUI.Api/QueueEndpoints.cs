using YueUI.Api.Queue;

namespace YueUI.Api;

/// <param name="Offset">Places to move by: negative towards the front.</param>
public sealed record MoveRequest(int Offset);

/// <summary>
/// The jobs waiting in the <see cref="JobQueue"/>: taking one out and changing their order. They reach the browsers
/// with the snapshot and as <c>queue</c> events.
/// </summary>
public static class QueueEndpoints
{
    public static RouteGroupBuilder MapQueueEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/queue", (JobQueue queue) => queue.Jobs)
            .WithName("ListQueue");
        api.MapDelete("/queue/{id}", (string id, JobQueue queue) =>
            queue.Cancel(id) ? Results.NoContent() : NotWaiting())
            .WithName("RemoveFromQueue")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);
        api.MapPost("/queue/{id}/move", (string id, MoveRequest request, JobQueue queue) =>
            queue.Move(id, request.Offset) ? Results.NoContent() : NotWaiting())
            .WithName("MoveInQueue")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);
        return api;
    }

    /// <summary>Also the answer for a job that started meanwhile; the worker's cancel reaches it from then on.</summary>
    private static IResult NotWaiting() => Results.Problem(title: "Not waiting in the queue", statusCode: StatusCodes.Status404NotFound);
}
