using Microsoft.Extensions.Options;
using YueUI.Api.Memory;
using YueUI.Api.Worker;

namespace YueUI.Api.Machines;

/// <summary>A Mac in the pickers, with what it is doing.</summary>
/// <param name="Id"><see cref="Machines.Local"/> for this one, else the helper's id.</param>
/// <param name="Reachable">Answered its last poll; this one always.</param>
/// <param name="Holder">The large model its memory holds.</param>
/// <param name="Busy">Its YuE worker generates, or a job this server handed it runs there.</param>
/// <param name="Engines">What it can run (<see cref="Machines.Engines"/>).</param>
/// <param name="Message">Why it is not reachable.</param>
public sealed record MachineInfo(
    string Id,
    string Name,
    bool Local,
    bool Reachable,
    LargeModel? Holder,
    bool Busy,
    IReadOnlyList<string> Engines,
    string? Message = null);

/// <summary>Where a job runs: here, holding <see cref="ModelMemory"/>, or on a helper, holding nothing here.</summary>
public sealed class Placement : IAsyncDisposable
{
    private readonly Action _release;
    private int _released;

    internal Placement(HelperMachine? helper, string name, Action release)
    {
        Helper = helper;
        Name = name;
        _release = release;
    }

    /// <summary>The helper it runs on; null for this Mac.</summary>
    public HelperMachine? Helper { get; }

    /// <summary>The Mac's name, shown with the job.</summary>
    public string Name { get; }

    /// <summary>Gives the memory back or tells the helper count it is done; safe to call more than once.</summary>
    public void Release()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
        {
            _release();
        }
    }

    public ValueTask DisposeAsync()
    {
        Release();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// The Macs a job can run on: this one, and the helpers in <see cref="MachineOptions.Helpers"/>. Decides where a job
/// goes when it is its turn (<see cref="PlaceAsync"/>): the one asked for, or with <see cref="Auto"/> the first that is
/// free, this one first.
/// </summary>
/// <remarks>
/// Each Mac keeps its own rule of one large model at a time: here <see cref="ModelMemory"/>, on a helper its own, which
/// its runs wait for (<see cref="HelperRuns"/>). A job on a helper claims nothing here, so it runs beside YuE2 and
/// whatever else holds this Mac's memory. Whether a helper is free comes from <see cref="MachineMonitor"/>'s last poll
/// and the jobs this server has running there, so two jobs handed over at once do not both see it free.
/// </remarks>
public sealed class Machines(
    IOptions<MachineOptions> options,
    ModelMemory memory,
    WorkerHost host,
    TimeProvider time)
{
    /// <summary>This Mac in a job's <c>machine</c>.</summary>
    public const string Local = "local";

    /// <summary>Whichever is free first; also what an empty <c>machine</c> means.</summary>
    public const string Auto = "auto";

    private readonly Lock _gate = new();
    private readonly Dictionary<string, (HelperStatus? Status, string? Message)> _polled = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _running = new(StringComparer.Ordinal);

    public IReadOnlyList<HelperMachine> Helpers =>
        [.. options.Value.Helpers.Where(h => !string.IsNullOrWhiteSpace(h.Id) && !string.IsNullOrWhiteSpace(h.Url) && h.Id is not Local and not Auto)];

    public string LocalName => string.IsNullOrWhiteSpace(options.Value.Name) ? "Dieser Mac" : options.Value.Name;

    public HelperMachine? Find(string? id) => Helpers.FirstOrDefault(h => h.Id == id);

    /// <summary>A job's <c>machine</c> as stored: null for <see cref="Auto"/>, else <see cref="Local"/> or a helper's id.</summary>
    /// <exception cref="ArgumentException">No such Mac.</exception>
    public string? Normalize(string? machine)
    {
        var trimmed = machine?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed == Auto)
        {
            return null;
        }
        if (trimmed == Local || Find(trimmed) is not null)
        {
            return trimmed;
        }
        throw new ArgumentException($"There is no Mac {trimmed}; known are {string.Join(", ", [Local, .. Helpers.Select(h => h.Id)])}.");
    }

    /// <summary><see cref="Normalize"/> for an endpoint: the problem goes into its validation errors as <c>machine</c>.</summary>
    public bool TryNormalize(string? machine, out string? normalized, IDictionary<string, string[]> errors)
    {
        try
        {
            normalized = Normalize(machine);
            return true;
        }
        catch (ArgumentException exception)
        {
            normalized = null;
            errors["machine"] = [exception.Message];
            return false;
        }
    }

    /// <summary>
    /// Whether a job for <paramref name="engine"/> has somewhere to run: on this Mac when it is asked for or it has the
    /// engine, else on a helper that has it or has not answered yet (it may well have it).
    /// </summary>
    public bool CanRun(string engine, string? machine, bool localInstalled)
    {
        if (machine == Local)
        {
            return localInstalled;
        }
        if (machine is not null)
        {
            return true;
        }
        return localInstalled || Helpers.Any(h => StatusOf(h) is not { } status || status.Engines.Contains(engine));
    }

    /// <summary>The name a job shows for where it runs.</summary>
    public string NameOf(HelperMachine? helper) => helper?.Label ?? LocalName;

    /// <summary>
    /// Waits until the job can run, and says where. On this Mac the memory is claimed and an idle YuE worker shut down;
    /// on a helper nothing is claimed here, the helper waits for its own memory.
    /// </summary>
    /// <param name="engine">What the job needs (<see cref="Engines"/>); a helper without it is passed over by <see cref="Auto"/>.</param>
    /// <param name="machine">As stored by <see cref="Normalize"/>.</param>
    /// <param name="localAvailable">Whether this Mac has the engine; when not, <see cref="Auto"/> only waits for helpers.</param>
    public async Task<Placement> PlaceAsync(
        LargeModel model,
        string engine,
        string? machine,
        TimeSpan interval,
        CancellationToken cancellationToken,
        bool localAvailable = true)
    {
        if (machine == Local || (machine is null && Helpers.Count == 0))
        {
            await memory.ClaimAsync(model, interval, time, cancellationToken);
            return await LocalAsync(model);
        }
        if (machine is not null)
        {
            var helper = Find(machine) ?? throw new HelperException($"There is no Mac {machine} any more.", StatusCodes.Status400BadRequest);
            return Remote(helper);
        }
        while (true)
        {
            if (TryPlace(model, engine, localAvailable) is { } placement)
            {
                return placement.Helper is null ? await LocalAsync(model, claimed: placement) : placement;
            }
            await Task.Delay(interval, time, cancellationToken);
        }
    }

    /// <summary>
    /// <see cref="Auto"/> without waiting: this Mac if its memory is free, else a free helper with the engine, else
    /// null. A placement here holds the memory, but the worker is not yet shut down.
    /// </summary>
    public Placement? TryPlace(LargeModel model, string engine, bool localAvailable = true)
    {
        if (localAvailable && memory.TryClaim(model))
        {
            return new Placement(null, LocalName, () => memory.Release(model));
        }
        return FreeHelper(engine) is { } helper ? Remote(helper) : null;
    }

    /// <summary>A helper that answered its last poll free, has <paramref name="engine"/>, and runs nothing for this server.</summary>
    public HelperMachine? FreeHelper(string engine)
    {
        lock (_gate)
        {
            return Helpers.FirstOrDefault(h =>
                _polled.TryGetValue(h.Id, out var polled)
                && polled.Status is { Holder: null, InUse: false } status
                && status.Engines.Contains(engine)
                && _running.GetValueOrDefault(h.Id) == 0);
        }
    }

    /// <summary>A placement on <paramref name="helper"/>, counted until released.</summary>
    public Placement Remote(HelperMachine helper)
    {
        lock (_gate)
        {
            _running[helper.Id] = _running.GetValueOrDefault(helper.Id) + 1;
        }
        return new Placement(helper, helper.Label, () =>
        {
            lock (_gate)
            {
                _running[helper.Id] = Math.Max(0, _running.GetValueOrDefault(helper.Id) - 1);
            }
        });
    }

    /// <summary>The last poll of a helper, for jobs that need more than whether it is free (ACE-Step's model files).</summary>
    public HelperStatus? StatusOf(HelperMachine helper)
    {
        lock (_gate)
        {
            return _polled.TryGetValue(helper.Id, out var polled) ? polled.Status : null;
        }
    }

    internal void Polled(HelperMachine helper, HelperStatus? status, string? message)
    {
        lock (_gate)
        {
            _polled[helper.Id] = (status, message);
        }
    }

    /// <summary>This Mac and the helpers as last polled.</summary>
    public IReadOnlyList<MachineInfo> List(IReadOnlyList<string> localEngines)
    {
        List<MachineInfo> machines = [new(Local, LocalName, true, true, memory.Holder, host.IsBusy, localEngines)];
        lock (_gate)
        {
            foreach (var helper in Helpers)
            {
                var (status, message) = _polled.TryGetValue(helper.Id, out var polled) ? polled : (null, "Not asked yet.");
                machines.Add(new MachineInfo(
                    helper.Id,
                    helper.Label,
                    false,
                    status is not null,
                    status?.Holder,
                    (status?.InUse ?? false) || _running.GetValueOrDefault(helper.Id) > 0,
                    status?.Engines ?? [],
                    status is null ? message : null));
            }
        }
        return machines;
    }

    private async Task<Placement> LocalAsync(LargeModel model, Placement? claimed = null)
    {
        // YuE2 keeps its model for ten idle minutes; the job's model needs the memory now.
        try
        {
            await host.ShutdownWorkerAsync();
        }
        catch
        {
            memory.Release(model);
            throw;
        }
        return claimed ?? new Placement(null, LocalName, () => memory.Release(model));
    }
}

/// <summary>Asks every helper how it is, every <see cref="MachineOptions.PollInterval"/>; idle without helpers.</summary>
public sealed class MachineMonitor(
    Machines machines,
    HelperClient client,
    IOptions<MachineOptions> options,
    TimeProvider time,
    ILogger<MachineMonitor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        var reachable = new Dictionary<string, bool>(StringComparer.Ordinal);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                foreach (var helper in machines.Helpers)
                {
                    using var limit = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    limit.CancelAfter(TimeSpan.FromSeconds(10));
                    try
                    {
                        var status = await client.StatusAsync(helper, limit.Token);
                        machines.Polled(helper, status, null);
                        if (!reachable.GetValueOrDefault(helper.Id, false))
                        {
                            logger.LogInformation("{Machine} computes for this Mac ({Engines})", helper.Label, string.Join(", ", status.Engines));
                        }
                        reachable[helper.Id] = true;
                    }
                    catch (Exception exception) when (exception is HelperException or OperationCanceledException or System.Text.Json.JsonException
                        && !stoppingToken.IsCancellationRequested)
                    {
                        var message = exception is OperationCanceledException ? $"{helper.Label} did not answer in time." : exception.Message;
                        machines.Polled(helper, null, message);
                        if (reachable.GetValueOrDefault(helper.Id, true))
                        {
                            logger.LogWarning("{Machine} is not reachable: {Message}", helper.Label, message);
                        }
                        reachable[helper.Id] = false;
                    }
                }
                await Task.Delay(options.Value.PollInterval, time, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
