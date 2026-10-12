using System.Collections.Concurrent;
using YueToLogic.Core.Conversion;
using YueUI.Api.Data;
using YueUI.Api.Voices;

namespace YueUI.Api.AudioMidi;

/// <summary>
/// A song's bass and drum stems as the MIDI files the Logic page lays in place of the generated bass and drums
/// (<see cref="RecordedParts"/>). The preview converts again on every change of an option, while a stem takes Basic
/// Pitch seconds, so each file is made once per stem and tempo and kept in memory (a few KB each); a request that is
/// cancelled (the page changed an option meanwhile) leaves the conversion running for the next one.
/// </summary>
public sealed class StemParts(IAudioMidiEngine engine, SqliteStemStore store)
{
    public const string BassStem = "bass";

    public const string DrumStem = "drums";

    /// <summary>Enough for every stem a few songs have; the oldest go first.</summary>
    private const int Kept = 32;

    private readonly ConcurrentDictionary<string, Lazy<Task<byte[]>>> _files = new();

    private readonly ConcurrentQueue<string> _order = new();

    public bool Installed => engine.Installed;

    /// <summary>The stem as MIDI, on the song's sixteenth grid when the tempo is known.</summary>
    /// <exception cref="AudioMidiException">Basic Pitch is missing or failed.</exception>
    public async Task<byte[]> MidiAsync(StemSetState set, string stem, double? tempo, CancellationToken cancellationToken)
    {
        var path = store.FilePath(set, stem) ?? throw new AudioMidiException($"These stems have no {stem}.", StatusCodes.Status404NotFound);
        var key = $"{set.Id}/{stem}/{tempo}";
        var file = _files.GetOrAdd(key, _ => new Lazy<Task<byte[]>>(() => ConvertAsync(path, stem, tempo)));
        if (!_order.Contains(key))
        {
            _order.Enqueue(key);
            while (_order.Count > Kept && _order.TryDequeue(out var oldest))
            {
                _files.TryRemove(oldest, out _);
            }
        }
        try
        {
            return await file.Value.WaitAsync(cancellationToken);
        }
        catch (Exception) when (file.Value.IsFaulted)
        {
            // A failure is not kept: the next request tries again (Basic Pitch may have been installed meanwhile).
            _files.TryRemove(new KeyValuePair<string, Lazy<Task<byte[]>>>(key, file));
            throw;
        }
    }

    private async Task<byte[]> ConvertAsync(string path, string stem, double? tempo)
    {
        if (!File.Exists(path))
        {
            throw new AudioMidiException($"The {stem} stem's file is gone.", StatusCodes.Status404NotFound);
        }
        var folder = Directory.CreateTempSubdirectory("yueui-stem-part-").FullName;
        try
        {
            var output = Path.Combine(folder, $"{stem}.mid");
            var kind = stem == DrumStem ? AudioMidiKind.Drums : AudioMidiKind.Bass;
            var name = kind == AudioMidiKind.Drums ? "Drums" : "Bass";
            var result = await engine.ConvertAsync(
                new AudioMidiJob(path, output, name, new AudioMidiSettings(Mono: true, Quantize: tempo is not null), tempo ?? 120, kind),
                CancellationToken.None);
            if (result.Notes == 0)
            {
                throw new AudioMidiException($"No notes were heard in the {stem} stem.", StatusCodes.Status422UnprocessableEntity);
            }
            return await File.ReadAllBytesAsync(output);
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
