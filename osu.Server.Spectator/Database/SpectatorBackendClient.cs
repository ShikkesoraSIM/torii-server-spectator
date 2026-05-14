// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace osu.Server.Spectator.Database
{
    /// <summary>
    /// HTTP transport for <see cref="ISpectatorBackendClient"/>. Talks to
    /// g0v0-server's <c>/_lio/spectator/*</c> endpoints over plain HTTP+JSON
    /// inside the docker bridge network.
    ///
    /// Mirrors the auth + retry semantics of
    /// <see cref="osu.Server.Spectator.Services.SharedInterop"/> deliberately:
    /// both client types live on opposite sides of the same shared-secret /
    /// HMAC-SHA1 trust boundary (using <c>SHARED_INTEROP_SECRET</c> +
    /// <c>X-LIO-Signature</c> header). The split is purely organisational —
    /// SharedInterop owns state-changing operations that osu-web has been
    /// running for years (create room, ensure beatmap, upload replay);
    /// SpectatorBackendClient owns the read/write operations that g0v0 is
    /// re-exposing as part of the Path 1B migration (see
    /// <c>PATH_1B_PLAN.md</c>).
    ///
    /// Why a separate client class instead of extending SharedInterop:
    /// 1. <c>/_lio/spectator/*</c> namespace is a Torii addition; mixing it
    ///    into <c>SharedInterop</c> would leak Torii-specific endpoints into
    ///    upstream-compatible code paths.
    /// 2. The two clients can have independent retry / backoff / circuit-
    ///    breaker policies if we ever need that.
    /// 3. Phases can flag-toggle one client's behaviour without affecting the
    ///    other's. <c>SharedInterop</c> can't be turned off; this one can.
    ///
    /// Serialisation: <c>Newtonsoft.Json</c> matches the rest of the
    /// spectator's wire formats (the POCOs in <c>Database/Models</c> already
    /// use <c>JsonProperty</c> attributes). System.Text.Json was considered
    /// but the existing POCO attributes would all need rewriting.
    /// </summary>
    public class SpectatorBackendClient : ISpectatorBackendClient
    {
        private readonly HttpClient httpClient;
        private readonly ILogger logger;

        private readonly string interopDomain;
        private readonly string interopSecret;

        /// <summary>
        /// Maximum retry attempts per request on transient (5XX) failures.
        /// Matches <see cref="osu.Server.Spectator.Services.SharedInterop"/>.
        /// </summary>
        private const int max_retries = 3;

        /// <summary>
        /// Delay between retries. Linear backoff (not exponential) because
        /// the network hop is intra-docker and a 5XX is much more likely to
        /// be "g0v0 just rolled" than "g0v0 is throttling" — we want to
        /// retry quickly enough that the SignalR call doesn't time out.
        /// </summary>
        private static readonly TimeSpan retry_delay = TimeSpan.FromSeconds(1);

        public SpectatorBackendClient(HttpClient httpClient, ILoggerFactory loggerFactory)
        {
            this.httpClient = httpClient;
            logger = loggerFactory.CreateLogger("SpectatorBackend");

            interopDomain = AppSettings.SharedInteropDomain;
            interopSecret = AppSettings.SharedInteropSecret;
        }

        /// <summary>
        /// Sends an HTTP request to a <c>/_lio/spectator/*</c> endpoint and
        /// returns the deserialised response body, or <c>default</c> when
        /// the response is 404 (matching the nullable-return semantics of
        /// <see cref="IDatabaseAccess"/> methods).
        ///
        /// 5XX responses are retried up to <see cref="max_retries"/> times.
        /// Any other non-success response throws
        /// <see cref="SpectatorBackendRequestFailedException"/> which is
        /// surfaced as a hub-level error to the SignalR caller.
        /// </summary>
        /// <typeparam name="TResponse">Type to deserialise the response body into. Use <c>object</c> to discard.</typeparam>
        /// <param name="method">HTTP method.</param>
        /// <param name="path">Path under <c>/_lio/spectator/</c>. Do NOT prefix with <c>/_lio/spectator/</c>.</param>
        /// <param name="body">Optional request body to JSON-serialise.</param>
        /// <returns>The deserialised response body, or <c>default(TResponse)</c> on 404.</returns>
        protected async Task<TResponse?> SendAsync<TResponse>(
            HttpMethod method,
            string path,
            object? body = null)
            where TResponse : class
        {
            int attemptsRemaining = max_retries;

            retry:

            long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string url = $"{interopDomain}/_lio/spectator/{path}{(path.Contains('?') ? "&" : "?")}timestamp={timestamp}";

            string? serialisedBody = body == null ? null : JsonConvert.SerializeObject(body);

            logger.LogDebug("Spectator backend request: {method} {url} (body: {body})", method, url, serialisedBody);

            try
            {
                string signature = hmacSign(url, interopSecret);

                var request = new HttpRequestMessage
                {
                    RequestUri = new Uri(url),
                    Method = method,
                    Headers =
                    {
                        { "X-LIO-Signature", signature },
                        { "Accept", "application/json" },
                    },
                };

                if (serialisedBody != null)
                {
                    request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(serialisedBody));
                    request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json");
                }

                var response = await httpClient.SendAsync(request);

                // 404 means "no such row" — translate to null for the C# DAO
                // contract. The g0v0 endpoints are expected to return 404 with
                // a JSON `{"code": "..._not_found"}` body for absent resources.
                if (response.StatusCode == HttpStatusCode.NotFound)
                    return null;

                if (response.IsSuccessStatusCode)
                {
                    if (typeof(TResponse) == typeof(object))
                        return default;

                    string content = await response.Content.ReadAsStringAsync();
                    return string.IsNullOrEmpty(content)
                        ? default
                        : JsonConvert.DeserializeObject<TResponse>(content);
                }

                throw await SpectatorBackendRequestFailedException.Create(url, response);
            }
            catch (Exception e)
            {
                bool allowRetry = true;

                if (e is SpectatorBackendRequestFailedException sbException)
                {
                    switch (sbException.StatusCode)
                    {
                        // Retry on potentially transient 5XX responses.
                        case HttpStatusCode.InternalServerError:
                        case HttpStatusCode.BadGateway:
                        case HttpStatusCode.ServiceUnavailable:
                        case HttpStatusCode.GatewayTimeout:
                            break;

                        default:
                            allowRetry = false;
                            break;
                    }
                }

                if (allowRetry && attemptsRemaining-- > 0)
                {
                    logger.LogError(e, "Spectator backend request to {url} failed, retrying ({remaining} remaining)", url, attemptsRemaining);
                    Thread.Sleep(retry_delay);
                    goto retry;
                }

                logger.LogError(e, "Spectator backend request to {url} failed permanently", url);
                throw;
            }
        }

        /// <summary>
        /// Sends an HTTP request that doesn't need a response body (write-only).
        /// Throws on 4XX/5XX, returns silently on success.
        /// </summary>
        protected async Task SendAsync(HttpMethod method, string path, object? body = null)
        {
            await SendAsync<object>(method, path, body);
        }

        /// <summary>
        /// HMAC-SHA1 signature over the URL using the shared interop secret.
        /// Matches <see cref="osu.Server.Spectator.Services.SharedInterop.hmacEncode"/>
        /// byte-for-byte so the same secret authenticates both client types
        /// against g0v0.
        /// </summary>
        private static string hmacSign(string input, string secret)
        {
            byte[] keyBytes = Encoding.UTF8.GetBytes(secret);
            byte[] inputBytes = Encoding.ASCII.GetBytes(input);
            byte[] hash = HMACSHA1.HashData(keyBytes, inputBytes);
            return hash.Aggregate(string.Empty, (s, b) => s + $"{b:x2}", s => s);
        }
    }

    /// <summary>
    /// Thrown when a request to g0v0's <c>/_lio/spectator/*</c> endpoint fails
    /// after all retries. Inherits from <see cref="HubException"/> so the
    /// failure surfaces as a SignalR error visible to the lazer client
    /// (rather than silently corrupting state by returning empty data).
    /// </summary>
    [Serializable]
    public class SpectatorBackendRequestFailedException : HubException
    {
        public readonly HttpStatusCode StatusCode;

        private SpectatorBackendRequestFailedException(HttpStatusCode statusCode, string message, Exception innerException)
            : base(message, innerException)
        {
            StatusCode = statusCode;
        }

        public static async Task<SpectatorBackendRequestFailedException> Create(string url, HttpResponseMessage response)
        {
            string errorMessage = $"{(int)response.StatusCode}: {response.ReasonPhrase}";

            try
            {
                string body = await response.Content.ReadAsStringAsync();
                var apiError = JsonConvert.DeserializeObject<ApiError>(body);
                if (!string.IsNullOrEmpty(apiError?.Error))
                    errorMessage = apiError.Error;
            }
            catch
            {
                // Body wasn't JSON / didn't fit the ApiError shape — keep the
                // status-line message we already built above.
            }

            return new SpectatorBackendRequestFailedException(
                response.StatusCode,
                errorMessage,
                new Exception($"Spectator backend request to {url} failed with {response.StatusCode} ({response.ReasonPhrase})."));
        }

        [Serializable]
        private class ApiError
        {
            [JsonProperty("error")]
            public string Error { get; set; } = string.Empty;

            [JsonProperty("code")]
            public string Code { get; set; } = string.Empty;
        }
    }
}
