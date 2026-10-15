using Microsoft.AspNetCore.Mvc;
using YueUI.Api.Push;

namespace YueUI.Api;

public sealed record PushKey(string PublicKey);

/// <param name="P256dh">The browser's public key, base64url.</param>
/// <param name="Auth">The browser's authentication secret, base64url.</param>
public sealed record PushKeys(string? P256dh, string? Auth);

/// <summary>What <c>PushSubscription.toJSON()</c> gives, plus the language the notifications should speak.</summary>
public sealed record PushSubscriptionRequest(string? Endpoint, PushKeys? Keys, string? Language);

public sealed record PushEndpointRequest(string? Endpoint);

/// <summary>
/// Web Push subscriptions: a browser (on iOS only the app on the home screen) subscribes with this server's VAPID key
/// and is then notified by <see cref="PushNotifier"/> when songs, transcriptions and lyrics finish.
/// </summary>
public static class PushEndpoints
{
    public static RouteGroupBuilder MapPushEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/push", (PushStore store) => new PushKey(store.PublicKey))
            .WithName("GetPushKey");

        api.MapPost("/push/subscriptions", (PushSubscriptionRequest request, PushStore store) =>
        {
            if (!IsPushEndpoint(request.Endpoint)
                || string.IsNullOrWhiteSpace(request.Keys?.P256dh) || string.IsNullOrWhiteSpace(request.Keys.Auth))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["endpoint"] = ["A push subscription as PushSubscription.toJSON() gives it: an https endpoint and the keys p256dh and auth."],
                });
            }
            var language = request.Language is "en" ? "en" : "de";
            store.Add(request.Endpoint!, request.Keys.P256dh, request.Keys.Auth, language);
            return Results.NoContent();
        })
            .WithName("Subscribe")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem();

        api.MapDelete("/push/subscriptions", ([FromBody] PushEndpointRequest request, PushStore store) =>
        {
            if (request.Endpoint is not null)
            {
                store.Remove(request.Endpoint);
            }
            return Results.NoContent();
        })
            .WithName("Unsubscribe")
            .Produces(StatusCodes.Status204NoContent);

        // Checks the whole way to the phone without generating a song first.
        api.MapPost("/push/test", async (PushEndpointRequest request, PushStore store, IPushSender sender, CancellationToken cancellationToken) =>
        {
            if (request.Endpoint is null || store.Find(request.Endpoint) is not { } subscription)
            {
                return Results.Problem(title: "Not subscribed", statusCode: StatusCodes.Status404NotFound);
            }
            var payload = PushNotifier.Payload(PushTexts.Test(subscription.Language), "test");
            switch (await sender.SendAsync(subscription, payload, cancellationToken))
            {
                case PushResult.Delivered:
                    return Results.NoContent();
                case PushResult.Gone:
                    store.Remove(subscription.Endpoint);
                    return Results.Problem(title: "The push service no longer knows this subscription.", statusCode: StatusCodes.Status410Gone);
                default:
                    return Results.Problem(title: "The push service did not accept the message.", statusCode: StatusCodes.Status502BadGateway);
            }
        })
            .WithName("SendTestPush")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status410Gone)
            .ProducesProblem(StatusCodes.Status502BadGateway);
        return api;
    }

    /// <summary>The server posts to this address, so it must at least be one a push service would have.</summary>
    private static bool IsPushEndpoint(string? endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && !uri.IsLoopback;
}
