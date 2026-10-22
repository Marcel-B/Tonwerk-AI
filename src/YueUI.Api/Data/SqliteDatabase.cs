using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace YueUI.Api.Data;

/// <summary>
/// The SQLite file this app keeps its own state in (the songs stay in YuE Studio's folders). The file and its tables
/// are created on first use, so a server whose data folder is not writable still starts; only what needs the file fails.
/// </summary>
/// <remarks>
/// Every call opens its own connection; Microsoft.Data.Sqlite pools them, and SQLite's file lock serializes the
/// writers. The schema carries its version in <c>PRAGMA user_version</c>: a later change is a new step in
/// <see cref="EnsureCreated"/>, applied in order to a file an earlier release wrote; never edit an old step.
/// Version 1: playlists and their songs, with a first playlist (id 1) created along (<see cref="SqlitePlaylistStore"/>).
/// Version 2: titles given to runs in this app (<see cref="SqliteRunTitleStore"/>).
/// Version 3: song ratings, one to five stars (<see cref="SqliteSongRatingStore"/>).
/// Version 4: songs sung with another voice (<see cref="SqliteVersionStore"/>).
/// Version 5: the stem model a version was separated with.
/// Version 6: songs, renders and lyrics drafts waiting for their turn (<see cref="SqliteJobStore"/>).
/// Version 7: the Logic page's instruments, which track plays which, and its presets (<see cref="SqliteInstrumentStore"/>,
/// <see cref="SqliteLogicPresetStore"/>), as yue-to-logic-pro kept them, without its users.
/// Version 8: the Logic page's browser synthesizer: the sound each track plays in the preview and named sounds to reuse
/// (<see cref="SqliteSynthStore"/>).
/// Version 9: the preview's mixer (volume and pan per track, master), one row (<see cref="SqliteSynthStore"/>).
/// Version 10: songs split into stems (<see cref="SqliteStemStore"/>).
/// Version 11: the speech lab's recorded voices and takes (<see cref="SqliteSpeechStore"/>).
/// Version 12: songs' covers (<see cref="SqliteCoverStore"/>).
/// Version 13: reference voices of this app's own (<see cref="SqliteReferenceVoiceStore"/>).
/// Version 14: notes written to songs (<see cref="SqliteSongNoteStore"/>).
/// Version 15: uploaded recordings sung with another voice (<see cref="SqliteSwapStore"/>).
/// Version 21: songs asked of ACE-Step, from queued to done (<see cref="SqliteAceStore"/>).
/// Version 22: the measured loudness of audio files, a cache (<see cref="Share.Loudness"/>).
/// Version 23: songs asked for from a shortcut, waiting for their lyrics draft (<see cref="Lyrics.QuickSongs"/>).
/// Version 24: speech takes that clean an uploaded recording, and their progress (<see cref="SqliteSpeechStore"/>).
/// Version 25: what was heard in songs, tempo, key and how clearly the lyrics are sung (<see cref="SqliteSongTraitStore"/>).
/// Version 26: written song sketches and their plans (<see cref="SqliteSketchStore"/>).
/// Version 27: melodies sung by SoulX-Singer, with their notes (<see cref="SqliteSingingStore"/>).
/// Version 28: which Mac a speech take, version, stem set or voice swap was asked for and ran on (Machines/).
/// </remarks>
public sealed class SqliteDatabase(IOptions<DataOptions> options)
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = options.Value.ResolvedPath,
        Mode = SqliteOpenMode.ReadWriteCreate,
    }.ToString();

    private readonly Lock _gate = new();
    private bool _ready;

    public string Path { get; } = options.Value.ResolvedPath;

    /// <summary>The folder the file is in, where the app keeps its other files too.</summary>
    public string Directory => System.IO.Path.GetDirectoryName(Path) ?? ".";

    /// <summary>An open connection with foreign keys enforced, on a file whose schema is current.</summary>
    public SqliteConnection Open()
    {
        EnsureCreated();
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        Execute(connection, null, "PRAGMA foreign_keys = ON");
        return connection;
    }

    public static int Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        return command.ExecuteNonQuery();
    }

    private void EnsureCreated()
    {
        if (_ready)
        {
            return;
        }
        lock (_gate)
        {
            if (_ready)
            {
                return;
            }
            if (System.IO.Path.GetDirectoryName(Path) is { Length: > 0 } directory)
            {
                System.IO.Directory.CreateDirectory(directory);
            }
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var version = connection.CreateCommand();
            version.CommandText = "PRAGMA user_version";
            var current = (long)version.ExecuteScalar()!;
            if (current < 1)
            {
                Execute(
                    connection,
                    null,
                    """
                    CREATE TABLE playlists (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        name TEXT NOT NULL,
                        created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
                    );
                    CREATE TABLE playlist_songs (
                        playlist_id INTEGER NOT NULL REFERENCES playlists (id) ON DELETE CASCADE,
                        position INTEGER NOT NULL,
                        song_id TEXT NOT NULL,
                        PRIMARY KEY (playlist_id, position),
                        UNIQUE (playlist_id, song_id)
                    );
                    INSERT INTO playlists (id, name) VALUES (1, 'Playlist');
                    PRAGMA user_version = 1;
                    """);
            }
            if (current < 2)
            {
                Execute(
                    connection,
                    null,
                    """
                    CREATE TABLE run_titles (
                        run_id TEXT PRIMARY KEY,
                        title TEXT NOT NULL
                    );
                    PRAGMA user_version = 2;
                    """);
            }
            if (current < 3)
            {
                Execute(
                    connection,
                    null,
                    """
                    CREATE TABLE song_ratings (
                        song_id TEXT PRIMARY KEY,
                        rating INTEGER NOT NULL CHECK (rating BETWEEN 1 AND 5)
                    );
                    PRAGMA user_version = 3;
                    """);
            }
            if (current < 4)
            {
                Execute(
                    connection,
                    null,
                    """
                    CREATE TABLE song_versions (
                        id TEXT PRIMARY KEY,
                        song_id TEXT NOT NULL,
                        title TEXT NOT NULL,
                        voice_id TEXT NOT NULL,
                        voice_label TEXT NOT NULL,
                        semi_tone_shift INTEGER NOT NULL,
                        strength REAL NOT NULL,
                        diffusion_steps INTEGER NOT NULL,
                        keep_reverb INTEGER NOT NULL,
                        stage TEXT NOT NULL,
                        message TEXT NULL,
                        created_at TEXT NOT NULL,
                        updated_at TEXT NOT NULL
                    );
                    CREATE INDEX ix_song_versions_song ON song_versions (song_id);
                    PRAGMA user_version = 4;
                    """);
            }
            if (current < 5)
            {
                // Null for versions made before: they used the default model of the time, which is not known here.
                Execute(
                    connection,
                    null,
                    """
                    ALTER TABLE song_versions ADD COLUMN stem_model TEXT NULL;
                    PRAGMA user_version = 5;
                    """);
            }
            if (current < 6)
            {
                Execute(
                    connection,
                    null,
                    """
                    CREATE TABLE queued_jobs (
                        id TEXT PRIMARY KEY,
                        position INTEGER NOT NULL,
                        kind TEXT NOT NULL,
                        job TEXT NOT NULL,
                        payload TEXT NOT NULL
                    );
                    PRAGMA user_version = 6;
                    """);
            }
            if (current < 7)
            {
                // A preset's name is unique through name_key, the name folded by .NET: SQLite's NOCASE knows ASCII only,
                // so "Ä" and "ä" would otherwise be two presets. A synthesizer leaves the drum columns NULL.
                Execute(
                    connection,
                    null,
                    """
                    CREATE TABLE instruments (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        name TEXT NOT NULL COLLATE NOCASE UNIQUE,
                        port TEXT NOT NULL,
                        channel INTEGER NOT NULL CHECK (channel BETWEEN 1 AND 16),
                        kind TEXT NOT NULL DEFAULT 'Synth',
                        drum_kick INTEGER,
                        drum_snare INTEGER,
                        drum_closed_hihat INTEGER,
                        drum_open_hihat INTEGER,
                        drum_crash INTEGER,
                        drum_clap INTEGER,
                        created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                        updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
                    );
                    CREATE TABLE track_assignments (
                        track TEXT NOT NULL COLLATE NOCASE PRIMARY KEY,
                        instrument_id INTEGER NOT NULL REFERENCES instruments (id) ON DELETE CASCADE
                    );
                    CREATE TABLE logic_presets (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        name TEXT NOT NULL,
                        name_key TEXT NOT NULL UNIQUE,
                        form TEXT NOT NULL,
                        updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
                    );
                    PRAGMA user_version = 7;
                    """);
            }
            if (current < 8)
            {
                // A track's sound is a copy, not a reference to a preset: changing or deleting the preset leaves the
                // tracks that took it as they are. `preset` only names where the sound came from.
                Execute(
                    connection,
                    null,
                    """
                    CREATE TABLE synth_presets (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        name TEXT NOT NULL,
                        name_key TEXT NOT NULL UNIQUE,
                        patch TEXT NOT NULL,
                        updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
                    );
                    CREATE TABLE track_synths (
                        track TEXT NOT NULL COLLATE NOCASE PRIMARY KEY,
                        patch TEXT NOT NULL,
                        preset TEXT,
                        updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
                    );
                    PRAGMA user_version = 8;
                    """);
            }
            if (current < 9)
            {
                // One mixer for the page, like the one playlist: a single row whose settings the browser alone reads.
                Execute(
                    connection,
                    null,
                    """
                    CREATE TABLE logic_mixer (
                        id INTEGER PRIMARY KEY CHECK (id = 1),
                        settings TEXT NOT NULL,
                        updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
                    );
                    PRAGMA user_version = 9;
                    """);
            }
            if (current < 10)
            {
                // The stems' names, lengths and waveforms as one JSON column: they are only ever read and written whole.
                Execute(
                    connection,
                    null,
                    """
                    CREATE TABLE song_stems (
                        id TEXT PRIMARY KEY,
                        song_id TEXT NOT NULL,
                        title TEXT NOT NULL,
                        model TEXT NOT NULL,
                        dereverb INTEGER NOT NULL,
                        stage TEXT NOT NULL,
                        message TEXT NULL,
                        stems TEXT NOT NULL,
                        created_at TEXT NOT NULL,
                        updated_at TEXT NOT NULL
                    );
                    CREATE INDEX song_stems_song ON song_stems (song_id);
                    PRAGMA user_version = 10;
                    """);
            }
            if (current < 11)
            {
                // Takes keep the voice's label rather than a foreign key: a take stays worth hearing after its
                // recording is deleted.
                Execute(
                    connection,
                    null,
                    """
                    CREATE TABLE speech_voices (
                        id TEXT PRIMARY KEY,
                        label TEXT NOT NULL,
                        transcript TEXT NOT NULL,
                        seconds REAL NOT NULL,
                        created_at TEXT NOT NULL
                    );
                    CREATE TABLE speech_takes (
                        id TEXT PRIMARY KEY,
                        model_id TEXT NOT NULL,
                        model_label TEXT NOT NULL,
                        text TEXT NOT NULL,
                        voice_id TEXT,
                        voice_label TEXT,
                        stage TEXT NOT NULL,
                        message TEXT,
                        seconds REAL,
                        load_seconds REAL,
                        speak_seconds REAL,
                        peak_memory_gb REAL,
                        created_at TEXT NOT NULL,
                        updated_at TEXT NOT NULL
                    );
                    PRAGMA user_version = 11;
                    """);
            }
            if (current < 12)
            {
                // The file name is new for every cover, so a browser never shows a cached old one under the same name.
                Execute(
                    connection,
                    null,
                    """
                    CREATE TABLE song_covers (
                        song_id TEXT PRIMARY KEY,
                        file TEXT NOT NULL,
                        content_type TEXT NOT NULL,
                        updated_at TEXT NOT NULL
                    );
                    PRAGMA user_version = 12;
                    """);
            }
            if (current < 13)
            {
                // Reference voices moved here from ChangeMyVoice; their ids stay, since versions name them.
                Execute(
                    connection,
                    null,
                    """
                    CREATE TABLE reference_voices (
                        id TEXT PRIMARY KEY,
                        label TEXT NOT NULL,
                        seconds REAL NOT NULL,
                        created_at TEXT NOT NULL
                    );
                    PRAGMA user_version = 13;
                    """);
            }
            if (current < 14)
            {
                Execute(
                    connection,
                    null,
                    """
                    CREATE TABLE song_notes (
                        song_id TEXT PRIMARY KEY,
                        note TEXT NOT NULL,
                        updated_at TEXT NOT NULL
                    );
                    PRAGMA user_version = 14;
                    """);
            }
            if (current < 15)
            {
                // Keeps the voice's label, like a version: a swap stays worth hearing after its voice is deleted.
                Execute(
                    connection,
                    null,
                    """
                    CREATE TABLE voice_swaps (
                        id TEXT PRIMARY KEY,
                        file_name TEXT NOT NULL,
                        voice_id TEXT NOT NULL,
                        voice_label TEXT NOT NULL,
                        semi_tone_shift INTEGER NOT NULL,
                        strength REAL NOT NULL,
                        diffusion_steps INTEGER NOT NULL,
                        separate INTEGER NOT NULL,
                        keep_reverb INTEGER NOT NULL,
                        stem_model TEXT NULL,
                        stage TEXT NOT NULL,
                        message TEXT NULL,
                        created_at TEXT NOT NULL,
                        updated_at TEXT NOT NULL
                    );
                    PRAGMA user_version = 15;
                    """);
            }
            if (current < 16)
            {
                // The settings are the browser's (a subset of its form); the server only keeps them under a name.
                Execute(
                    connection,
                    null,
                    """
                    CREATE TABLE prompt_templates (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        name TEXT NOT NULL,
                        name_key TEXT NOT NULL UNIQUE,
                        settings TEXT NOT NULL,
                        created_at TEXT NOT NULL,
                        updated_at TEXT NOT NULL
                    );
                    PRAGMA user_version = 16;
                    """);
            }
            if (current < 17)
            {
                // Music videos: the settings they were made with; the layers and the file lie in videos/<id>/.
                Execute(
                    connection,
                    null,
                    """
                    CREATE TABLE song_videos (
                        id TEXT PRIMARY KEY,
                        song_id TEXT NOT NULL,
                        title TEXT NOT NULL,
                        format TEXT NOT NULL,
                        effect TEXT NOT NULL,
                        motion TEXT NOT NULL,
                        show_cover INTEGER NOT NULL,
                        show_title INTEGER NOT NULL,
                        stage TEXT NOT NULL,
                        message TEXT NULL,
                        bytes INTEGER NULL,
                        created_at TEXT NOT NULL,
                        updated_at TEXT NOT NULL
                    );
                    CREATE INDEX song_videos_song ON song_videos (song_id);
                    PRAGMA user_version = 17;
                    """);
            }
            if (current < 18)
            {
                // The analyzer's colour, picked from the cover; null for older videos, which used Tonwerk's.
                Execute(
                    connection,
                    null,
                    """
                    ALTER TABLE song_videos ADD COLUMN color TEXT NULL;
                    PRAGMA user_version = 18;
                    """);
            }
            if (current < 19)
            {
                // A note per instrument ("on input 3 of the mixer"), written on its own and never by the form.
                Execute(
                    connection,
                    null,
                    """
                    ALTER TABLE instruments ADD COLUMN note TEXT NULL;
                    PRAGMA user_version = 19;
                    """);
            }
            if (current < 20)
            {
                // Photo videos: the folder of pictures cut to the song and the motif they were picked by; null for
                // the cover videos made before.
                Execute(
                    connection,
                    null,
                    """
                    ALTER TABLE song_videos ADD COLUMN media TEXT NULL;
                    ALTER TABLE song_videos ADD COLUMN motif TEXT NULL;
                    PRAGMA user_version = 20;
                    """);
            }
            if (current < 21)
            {
                // ACE-Step's jobs; the job is JSON, since nothing queries its fields. The songs lie in ace-songs/.
                Execute(
                    connection,
                    null,
                    """
                    CREATE TABLE ace_jobs (
                        id TEXT PRIMARY KEY,
                        job TEXT NOT NULL,
                        created_at TEXT NOT NULL
                    );
                    PRAGMA user_version = 21;
                    """);
            }
            if (current < 22)
            {
                // loudnorm's first pass per file; size and time tell a render's new FLAC from the old one.
                Execute(
                    connection,
                    null,
                    """
                    CREATE TABLE loudness (
                        path TEXT PRIMARY KEY,
                        length INTEGER NOT NULL,
                        modified TEXT NOT NULL,
                        measured TEXT NOT NULL
                    );
                    PRAGMA user_version = 22;
                    """);
            }
            if (current < 23)
            {
                // The song a shortcut asked for, by the id of the lyrics draft it waits for; the request is JSON.
                Execute(
                    connection,
                    null,
                    """
                    CREATE TABLE quick_songs (
                        draft_id TEXT PRIMARY KEY,
                        request TEXT NOT NULL,
                        created_at TEXT NOT NULL
                    );
                    PRAGMA user_version = 23;
                    """);
            }
            if (current < 24)
            {
                // A take that cleans an uploaded recording instead of speaking a text: the recording's name, and how
                // far the cleaning is, since a long talk takes minutes.
                Execute(
                    connection,
                    null,
                    """
                    ALTER TABLE speech_takes ADD COLUMN source TEXT;
                    ALTER TABLE speech_takes ADD COLUMN progress REAL;
                    PRAGMA user_version = 24;
                    """);
            }
            if (current < 25)
            {
                // Tempo and key from the audio (or the score), clarity from the lyrics timing; each half set on its own.
                Execute(
                    connection,
                    null,
                    """
                    CREATE TABLE song_traits (
                        song_id TEXT PRIMARY KEY,
                        tempo REAL NULL,
                        song_key TEXT NULL,
                        heard_from TEXT NULL,
                        clarity REAL NULL,
                        clarity_from TEXT NULL,
                        updated_at TEXT NOT NULL
                    );
                    PRAGMA user_version = 25;
                    """);
            }
            if (current < 26)
            {
                // Written song sketches and the plans Claude made of them (Sketches/SketchMaker.cs), one JSON each.
                Execute(
                    connection,
                    null,
                    """
                    CREATE TABLE song_sketches (
                        id TEXT PRIMARY KEY,
                        sketch TEXT NOT NULL,
                        created_at TEXT NOT NULL
                    );
                    PRAGMA user_version = 26;
                    """);
            }
            if (current < 27)
            {
                // Melodies with words sung by SoulX-Singer (Singing/SingingStudio.cs), one JSON each, the notes beside it.
                Execute(
                    connection,
                    null,
                    """
                    CREATE TABLE sing_takes (
                        id TEXT PRIMARY KEY,
                        take TEXT NOT NULL,
                        notes TEXT NOT NULL,
                        created_at TEXT NOT NULL
                    );
                    PRAGMA user_version = 27;
                    """);
            }
            if (current < 28)
            {
                // Which Mac a job was asked for (null for whichever is free) and which one ran it (Machines/).
                Execute(
                    connection,
                    null,
                    """
                    ALTER TABLE speech_takes ADD COLUMN machine TEXT;
                    ALTER TABLE speech_takes ADD COLUMN ran_on TEXT;
                    ALTER TABLE song_versions ADD COLUMN machine TEXT;
                    ALTER TABLE song_versions ADD COLUMN ran_on TEXT;
                    ALTER TABLE song_stems ADD COLUMN machine TEXT;
                    ALTER TABLE song_stems ADD COLUMN ran_on TEXT;
                    ALTER TABLE voice_swaps ADD COLUMN machine TEXT;
                    ALTER TABLE voice_swaps ADD COLUMN ran_on TEXT;
                    PRAGMA user_version = 28;
                    """);
            }
            if (current < 29)
            {
                // Singing voices of one's own (Singing/SingingVoices.cs), one JSON each; the files lie in singing/voices/.
                Execute(
                    connection,
                    null,
                    """
                    CREATE TABLE sing_voices (
                        id TEXT PRIMARY KEY,
                        voice TEXT NOT NULL,
                        created_at TEXT NOT NULL
                    );
                    PRAGMA user_version = 29;
                    """);
            }
            if (current < 30)
            {
                // Conversations of the writing page (Writing/WritingDesk.cs), one JSON each with its messages.
                Execute(
                    connection,
                    null,
                    """
                    CREATE TABLE writing_chats (
                        id TEXT PRIMARY KEY,
                        chat TEXT NOT NULL,
                        created_at TEXT NOT NULL
                    );
                    PRAGMA user_version = 30;
                    """);
            }
            if (current < 31)
            {
                // What is known about the image models' LoRAs (Images/ImageLoras.cs): model, trigger, strength as JSON; the
                // files lie in image-loras/.
                Execute(
                    connection,
                    null,
                    """
                    CREATE TABLE image_loras (
                        name TEXT PRIMARY KEY,
                        settings TEXT NOT NULL,
                        created_at TEXT NOT NULL
                    );
                    PRAGMA user_version = 31;
                    """);
            }
            if (current < 32)
            {
                // Cover styles (Images/CoverStyles.cs): a text, a model, LoRAs and reference pictures under a name, as JSON; the
                // pictures lie in cover-styles/<id>/.
                Execute(
                    connection,
                    null,
                    """
                    CREATE TABLE cover_styles (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        name TEXT NOT NULL,
                        name_key TEXT NOT NULL UNIQUE,
                        settings TEXT NOT NULL,
                        created_at TEXT NOT NULL,
                        updated_at TEXT NOT NULL
                    );
                    PRAGMA user_version = 32;
                    """);
            }
            if (current < 33)
            {
                // How a painted cover was painted (Images/CoverPainting), as JSON; null for a photo.
                Execute(
                    connection,
                    null,
                    """
                    ALTER TABLE song_covers ADD COLUMN painting TEXT;
                    PRAGMA user_version = 33;
                    """);
            }
            _ready = true;
        }
    }
}
