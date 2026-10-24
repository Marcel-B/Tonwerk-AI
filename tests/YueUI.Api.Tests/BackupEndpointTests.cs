using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using YueUI.Api.Backup;

namespace YueUI.Api.Tests;

public sealed class BackupEndpointTests : IDisposable
{
    private const string Run = "20260922-101500-Neon-Night";
    private const string Base = "/remote.php/dav/files/marcel/Tonwerk";

    private readonly TestApp _app = new() { NextcloudUrl = $"https://cloud.test{Base}" };

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task Without_a_Nextcloud_the_backup_is_off()
    {
        _app.NextcloudUrl = null;
        var client = _app.CreateClient();

        var status = await client.GetFromJsonAsync<BackupStatus>("/api/backup", TestApp.Json);
        var start = await client.PostAsync("/api/backup", null);

        Assert.False(status!.Configured);
        Assert.Null(status.NextRun);
        Assert.Equal(HttpStatusCode.NotImplemented, start.StatusCode);
    }

    [Fact]
    public async Task A_backup_uploads_the_database_as_a_weekday_zip_and_the_files_beside_it()
    {
        var client = _app.CreateClient();
        _app.AddSong(Run, "song1");
        Directory.CreateDirectory(Path.Combine(_app.Root, "versions"));
        File.WriteAllText(Path.Combine(_app.Root, "versions", "v1.flac"), "flac");
        File.WriteAllText(Path.Combine(_app.Root, "versions", "v1.m4a"), "stream copy");
        Directory.CreateDirectory(Path.Combine(_app.Root, "stems", "s1"));
        File.WriteAllText(Path.Combine(_app.Root, "stems", "s1", "bass drum.flac"), "stem");
        Directory.CreateDirectory(Path.Combine(_app.Root, "speech", "voices"));
        File.WriteAllText(Path.Combine(_app.Root, "speech", "voices", "me.wav"), "voice");
        Directory.CreateDirectory(Path.Combine(_app.Root, "speech", "env", "bin"));
        File.WriteAllText(Path.Combine(_app.Root, "speech", "env", "bin", "python"), "");
        Directory.CreateDirectory(Path.Combine(_app.Root, "stream", Run));
        File.WriteAllText(Path.Combine(_app.Root, "stream", Run, "song1.m4a"), "");
        Directory.CreateDirectory(Path.Combine(_app.Root, "loras", ".lora-cache"));
        File.WriteAllText(Path.Combine(_app.Root, "loras", "dubstep.safetensors"), "lora");
        File.WriteAllText(Path.Combine(_app.Root, "loras", "dubstep.log"), "log");
        File.WriteAllText(Path.Combine(_app.Root, "loras", ".lora-cache", "song-abc.npy"), "latents");
        // Something in the database to find in the copy.
        await client.PutAsJsonAsync($"/api/runs/{Run}/title", new { title = "Neon Nacht" });
        await client.PostAsJsonAsync("/api/push/subscriptions", new
        {
            endpoint = "https://web.push.apple.com/abc",
            keys = new { p256dh = "key", auth = "secret" },
        });

        var status = await Backup(client);

        Assert.True(status.Last!.Success, status.Last.Error);
        Assert.Matches("^tonwerk-(mon|tue|wed|thu|fri|sat|sun)\\.zip$", status.Last.Archive);
        Assert.Equal($"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes("marcel:app-password"))}", _app.Nextcloud.Authorization);
        var zip = new ZipArchive(new MemoryStream(_app.Nextcloud.File($"{Base}/{status.Last.Archive}")));
        Assert.Equal(["push.json", "yueui.db"], zip.Entries.Select(e => e.FullName).Order());
        var copy = Path.Combine(_app.Root, "copy.db");
        zip.GetEntry("yueui.db")!.ExtractToFile(copy);
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={copy};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT title FROM run_titles";
            Assert.Equal("Neon Nacht", command.ExecuteScalar());
        }

        var uploaded = _app.Nextcloud.PutPaths().Select(p => p[(Base.Length + 1)..]).Where(p => !p.EndsWith(".zip")).Order().ToArray();
        Assert.Equal(
        [
            "loras/dubstep.safetensors",
            $"songs/{Run}/song1/audio.flac",
            $"songs/{Run}/song1/request.json",
            $"songs/{Run}/song1/result.json",
            $"songs/{Run}/song1/score.abc",
            "speech/voices/me.wav",
            "stems/s1/bass drum.flac",
            "versions/v1.flac",
        ], uploaded);
        Assert.Equal(8, status.Last.Files);
    }

    [Fact]
    public async Task Address_user_and_password_come_from_the_env_file()
    {
        _app.NextcloudUrl = null;
        _app.BackupEnvFile = Path.Combine(_app.Root, "nextcloud.env");
        File.WriteAllText(_app.BackupEnvFile, $"""
            # Tonwerk's backup
            NEXTCLOUD_WEBDAV_URL="https://cloud.test{Base}/"
            export NEXTCLOUD_USERNAME=anna
            NEXTCLOUD_APP_PASSWORD='abc=def'
            """);
        var client = _app.CreateClient();

        var status = await Backup(client);

        Assert.True(status.Last!.Success, status.Last.Error);
        Assert.Equal($"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes("anna:abc=def"))}", _app.Nextcloud.Authorization);
        Assert.Contains($"{Base}/{status.Last.Archive}", _app.Nextcloud.PutPaths());
    }

    [Fact]
    public async Task Later_backups_upload_only_new_or_changed_files()
    {
        var client = _app.CreateClient();
        var song = _app.AddSong(Run, "song1");
        await Backup(client);
        var first = _app.Nextcloud.PutPaths().Length;

        File.WriteAllText(Path.Combine(song, "score.abc"), "X:1\nK:C\n");
        _app.AddSong(Run, "song2");
        var status = await Backup(client);

        var later = _app.Nextcloud.PutPaths()[first..].Select(p => p[(Base.Length + 1)..]).Where(p => !p.EndsWith(".zip")).Order().ToArray();
        Assert.Equal(
        [
            $"songs/{Run}/song1/score.abc",
            $"songs/{Run}/song2/audio.flac",
            $"songs/{Run}/song2/request.json",
            $"songs/{Run}/song2/result.json",
            $"songs/{Run}/song2/score.abc",
        ], later);
        Assert.Equal(5, status.Last!.Files);
        Assert.Equal("X:1\nK:C\n", Encoding.UTF8.GetString(_app.Nextcloud.File($"{Base}/songs/{Run}/song1/score.abc")));
    }

    [Fact]
    public async Task A_locked_file_is_left_for_the_next_backup_while_the_others_go_on()
    {
        var client = _app.CreateClient();
        _app.AddSong(Run, "song1");
        _app.AddSong(Run, "song2");
        _app.Nextcloud.Locked.Add($"{Base}/songs/{Run}/song1/audio.flac");

        var first = await Backup(client);

        Assert.False(first.Last!.Success);
        Assert.Contains("423", first.Last.Error);
        Assert.Contains($"{Base}/songs/{Run}/song2/audio.flac", _app.Nextcloud.PutPaths());
        Assert.Equal(7, first.Last.Files);
        var metrics = await client.GetStringAsync("/metrics");
        Assert.Contains("tonwerk_backup_last_run_success 0\n", metrics);
        // Only the family's header, no sample yet.
        Assert.DoesNotContain("\ntonwerk_backup_last_success_timestamp_seconds ", metrics);

        _app.Nextcloud.Locked.Clear();
        var before = _app.Nextcloud.PutPaths().Length;
        var second = await Backup(client);

        Assert.True(second.Last!.Success, second.Last.Error);
        metrics = await client.GetStringAsync("/metrics");
        Assert.Contains("tonwerk_backup_last_run_success 1\n", metrics);
        Assert.Contains($"tonwerk_backup_last_success_timestamp_seconds {second.Last.FinishedAt.ToUnixTimeMilliseconds() / 1000.0:R}\n".Replace(",", "."), metrics);
        Assert.Equal([$"{Base}/songs/{Run}/song1/audio.flac"], _app.Nextcloud.PutPaths()[before..].Where(p => !p.EndsWith(".zip")));
    }

    [Fact]
    public async Task A_refused_backup_says_why_and_is_announced()
    {
        var client = _app.CreateClient();
        await client.PostAsJsonAsync("/api/push/subscriptions", new
        {
            endpoint = "https://web.push.apple.com/abc",
            keys = new { p256dh = "key", auth = "secret" },
            language = "de",
        });
        _app.Nextcloud.Status = HttpStatusCode.Unauthorized;

        var status = await Backup(client);

        Assert.False(status.Last!.Success);
        Assert.Contains("401", status.Last.Error);
        var (_, payload) = await _app.Push.Next();
        Assert.Equal("Datensicherung fehlgeschlagen", (string?)payload["title"]);
    }

    [Fact]
    public async Task The_nightly_backup_runs_once_a_day()
    {
        _app.BackupAt = "00:00";
        CloudBackup.CheckInterval = TimeSpan.FromMilliseconds(20);
        try
        {
            var client = _app.CreateClient();
            await TestApp.WaitUntil(() => _app.Nextcloud.PutPaths().Length > 0);
            var backup = _app.Services.GetRequiredService<CloudBackup>();
            await backup.Running;
            await Task.Delay(200);

            var status = await client.GetFromJsonAsync<BackupStatus>("/api/backup", TestApp.Json);

            Assert.True(status!.Last!.Scheduled);
            Assert.Single(_app.Nextcloud.PutPaths(), p => p.EndsWith(".zip"));
            Assert.True(status.NextRun > DateTimeOffset.UtcNow);
        }
        finally
        {
            CloudBackup.CheckInterval = TimeSpan.FromMinutes(1);
        }
    }

    [Fact]
    public async Task Only_one_backup_runs_at_a_time()
    {
        var client = _app.CreateClient();
        var backup = _app.Services.GetRequiredService<CloudBackup>();
        _app.Nextcloud.Gate = new TaskCompletionSource();

        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync("/api/backup", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync("/api/backup", null)).StatusCode);
        Assert.True((await client.GetFromJsonAsync<BackupStatus>("/api/backup", TestApp.Json))!.Running);

        _app.Nextcloud.Gate.SetResult();
        await backup.Running;
    }

    private async Task<BackupStatus> Backup(HttpClient client)
    {
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync("/api/backup", null)).StatusCode);
        await _app.Services.GetRequiredService<CloudBackup>().Running;
        return (await client.GetFromJsonAsync<BackupStatus>("/api/backup", TestApp.Json))!;
    }
}
