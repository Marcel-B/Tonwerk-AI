using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using YueUI.Api.Export;

namespace YueUI.Api.Tonregal;

/// <summary>What Tonregal's ingest API takes about a song (its <c>IngestMetadata</c>).</summary>
public sealed record TonregalMetadata(
    string ExternalId,
    string Title,
    string Artist,
    string? Album,
    int? Year,
    int? Track,
    string? Genre,
    double? Bpm,
    string? Key);

/// <summary>A job in Tonregal (its <c>IngestJobDto</c>): <c>imported</c> with <see cref="LibraryPath"/> means it is filed.</summary>
public sealed record TonregalJob(int Id, string ExternalId, string State, string? LibraryPath, string? Error);

/// <summary>What a check of address and key found; the apps say it in their language by <see cref="State"/>.</summary>
/// <param name="State"><c>ok</c>, <c>key</c> (Tonregal does not know the key), <c>setup</c> (Tonregal has no ingest
/// keys), <c>unreachable</c>, <c>failed</c> (another answer, see <see cref="Detail"/>) or <c>notConfigured</c>.</param>
public sealed record TonregalCheck(string State, string? Detail)
{
    public const string Ok = "ok";
    public const string Key = "key";
    public const string Setup = "setup";
    public const string Unreachable = "unreachable";
    public const string Failed = "failed";
    public const string NotConfigured = "notConfigured";
}

/// <summary>
/// Tonregal refused or could not be reached; the message is Tonregal's own where it gave one, <see cref="Status"/> its
/// HTTP status where it answered (401: the key is not one of its <c>Tonregal:Ingest:Keys</c>).
/// </summary>
public sealed class TonregalException(string message, int? status = null) : Exception(message)
{
    public int? Status { get; } = status;
}

/// <summary>
/// Tonregal's ingest API: <c>POST /api/ingest</c> (multipart: the file, the metadata as JSON, the cover) and
/// <c>GET /api/ingest/{id}</c>, both with the key in <c>X-Api-Key</c>. The same <c>externalId</c> twice creates nothing
/// new there, so handing a song over again is harmless.
/// </summary>
public sealed class TonregalClient(IHttpClientFactory clients, IOptions<TonregalOptions> options)
{
    public const string HttpClientName = "tonregal";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <returns>The job and whether Tonregal knew the song already (200 instead of 202).</returns>
    public async Task<(TonregalJob Job, bool Repeated)> SendAsync(
        string file, string contentType, string fileName, TonregalMetadata metadata, CoverImage? cover, CancellationToken cancellationToken)
    {
        await using var audio = File.OpenRead(file);
        var audioContent = new StreamContent(audio);
        audioContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        using var form = new MultipartFormDataContent
        {
            { audioContent, "file", fileName },
            { new StringContent(JsonSerializer.Serialize(metadata, Json)), "metadata" },
        };
        if (cover is not null)
        {
            var image = new ByteArrayContent(cover.Data);
            image.Headers.ContentType = new MediaTypeHeaderValue(cover.MimeType);
            form.Add(image, "cover", cover.MimeType == "image/png" ? "cover.png" : "cover.jpg");
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Value.UploadTimeout);
        using var response = await SendAsync(HttpMethod.Post, "api/ingest", form, timeout.Token);
        return (await ReadJobAsync(response, cancellationToken), response.StatusCode == System.Net.HttpStatusCode.OK);
    }

    public async Task<TonregalJob> GetAsync(int id, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, $"api/ingest/{id}", null, cancellationToken);
        return await ReadJobAsync(response, cancellationToken);
    }

    /// <summary>
    /// Asks Tonregal for a job that cannot exist: 404 means address and key are right, since Tonregal checks the key
    /// first (401 for an unknown one, 503 while it has no ingest keys at all).
    /// </summary>
    public async Task<TonregalCheck> CheckAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.Configured)
        {
            return new TonregalCheck(TonregalCheck.NotConfigured, null);
        }
        try
        {
            using var response = await SendAsync(HttpMethod.Get, "api/ingest/0", null, cancellationToken);
            return new TonregalCheck(TonregalCheck.Ok, null);
        }
        catch (TonregalException exception)
        {
            return new TonregalCheck(
                exception.Status switch
                {
                    404 => TonregalCheck.Ok,
                    401 => TonregalCheck.Key,
                    503 => TonregalCheck.Setup,
                    null => TonregalCheck.Unreachable,
                    _ => TonregalCheck.Failed,
                },
                exception.Status == 404 ? null : exception.Message);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (settings.BaseUri is not { } baseUri || settings.ResolvedKey is not { } key)
        {
            throw new TonregalException("Tonregal is not configured.");
        }
        using var request = new HttpRequestMessage(method, new Uri(baseUri, path)) { Content = content };
        request.Headers.Add("X-Api-Key", key);
        HttpResponseMessage response;
        try
        {
            response = await clients.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new TonregalException($"Tonregal is not reachable at {baseUri}: {exception.Message}");
        }
        if (response.IsSuccessStatusCode)
        {
            return response;
        }
        using (response)
        {
            // Tonregal answers with problem details whose detail says what was wrong.
            string? problem = null;
            try
            {
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                problem = body.RootElement.ValueKind != JsonValueKind.Object ? null
                    : (body.RootElement.TryGetProperty("detail", out var detail) ? detail.GetString() : null)
                        ?? (body.RootElement.TryGetProperty("title", out var title) ? title.GetString() : null);
            }
            catch (JsonException)
            {
            }
            throw new TonregalException(
                $"Tonregal answered {(int)response.StatusCode}{(problem is null ? "" : $": {problem}")}", (int)response.StatusCode);
        }
    }

    private static async Task<TonregalJob> ReadJobAsync(HttpResponseMessage response, CancellationToken cancellationToken) =>
        await response.Content.ReadFromJsonAsync<TonregalJob>(Json, cancellationToken)
            ?? throw new TonregalException("Tonregal answered without a job.");
}
