using Microsoft.AspNetCore.Mvc;
using YueUI.Api.Data;
using YueUI.Api.Logic;

namespace YueUI.Api;

/// <summary>
/// The Logic page's instrument library: named MIDI port and channel combinations - a drum machine among them with the
/// notes of its drums - and which track plays which. On the server, so that every browser (the MacBook in the studio
/// as well as the phone) sees the same setup. Routes and JSON are yue-to-logic-pro's, whose page this is.
/// </summary>
public static class InstrumentEndpoints
{
    /// <summary>Track names are the Logic template's ("Vocal 8vb"); anything longer is not one.</summary>
    public const int MaxTrackLength = 64;

    public static RouteGroupBuilder MapInstrumentEndpoints(this RouteGroupBuilder api)
    {
        var instruments = api.MapGroup("/instruments");
        instruments.MapGet("/", (SqliteInstrumentStore store) => Results.Ok(store.List()))
            .WithName("ListInstruments")
            .Produces<IReadOnlyList<Instrument>>(StatusCodes.Status200OK);
        instruments.MapPost("/", Create)
            .WithName("CreateInstrument")
            .Produces<Instrument>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);
        instruments.MapPut("/{id:long}", Update)
            .WithName("UpdateInstrument")
            .Produces<Instrument>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        instruments.MapPut("/{id:long}/note", SetNote)
            .WithName("SetInstrumentNote")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);
        instruments.MapDelete("/{id:long}", (long id, SqliteInstrumentStore store) => store.Delete(id) ? Results.NoContent() : NotFound(id))
            .WithName("DeleteInstrument")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);
        instruments.MapGet("/assignments", (SqliteInstrumentStore store) => Results.Ok(store.Assignments()))
            .WithName("ListTrackAssignments")
            .Produces<IReadOnlyDictionary<string, long>>(StatusCodes.Status200OK);
        instruments.MapPut("/assignments/{track}", Assign)
            .WithName("SetTrackAssignment")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);
        return api;
    }

    private static IResult Create([FromBody] InstrumentInput input, SqliteInstrumentStore store)
    {
        if (input.Problems() is { Count: > 0 } problems)
        {
            return Invalid(problems);
        }
        try
        {
            var instrument = store.Add(input.Cleaned());
            return Results.Created($"/api/instruments/{instrument.Id}", instrument);
        }
        catch (DuplicateInstrumentNameException exception)
        {
            return Taken(exception);
        }
    }

    private static IResult Update(long id, [FromBody] InstrumentInput input, SqliteInstrumentStore store)
    {
        if (input.Problems() is { Count: > 0 } problems)
        {
            return Invalid(problems);
        }
        try
        {
            return store.Update(id, input.Cleaned()) is { } instrument ? Results.Ok(instrument) : NotFound(id);
        }
        catch (DuplicateInstrumentNameException exception)
        {
            return Taken(exception);
        }
    }

    /// <summary>Trimmed like a song's note, so a note of blank lines counts as none.</summary>
    private static IResult SetNote(long id, NoteRequest request, SqliteInstrumentStore store)
    {
        var note = (request.Note ?? "").Trim();
        if (note.Length > LibraryEndpoints.MaxNoteLength)
        {
            return Invalid([$"note may have at most {LibraryEndpoints.MaxNoteLength} characters"]);
        }
        return store.SetNote(id, note) ? Results.NoContent() : NotFound(id);
    }

    private static IResult Assign(string track, [FromBody] TrackAssignmentInput input, SqliteInstrumentStore store)
    {
        var name = track.Trim();
        if (name.Length is 0 or > MaxTrackLength)
        {
            return Results.Problem(
                title: "Invalid track",
                detail: $"A track name has between 1 and {MaxTrackLength} characters.",
                statusCode: StatusCodes.Status400BadRequest);
        }
        if (input.InstrumentId is not { } id)
        {
            store.Unassign(name);
            return Results.NoContent();
        }
        return store.Assign(name, id) ? Results.NoContent() : NotFound(id);
    }

    private static IResult Invalid(IReadOnlyList<string> problems) =>
        Results.Problem(title: "Invalid instrument", detail: string.Join("; ", problems) + ".", statusCode: StatusCodes.Status400BadRequest);

    private static IResult Taken(DuplicateInstrumentNameException exception) =>
        Results.Problem(title: "Name taken", detail: exception.Message, statusCode: StatusCodes.Status409Conflict);

    private static IResult NotFound(long id) =>
        Results.Problem(title: "Unknown instrument", detail: $"There is no instrument with id {id}.", statusCode: StatusCodes.Status404NotFound);
}
