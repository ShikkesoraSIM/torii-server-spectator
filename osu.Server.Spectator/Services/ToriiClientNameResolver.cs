// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace osu.Server.Spectator.Services
{
    /// <summary>
    /// Pulls the verified-Torii hash list from the Torii server (g0v0) and caches it in
    /// memory so the metadata hub can quickly answer "is this connection running a real
    /// Torii build?" without a database hit per presence broadcast.
    /// Refreshes on a fixed interval; failures keep the previous snapshot.
    /// </summary>
    public class ToriiClientNameResolver : IHostedService
    {
        private static readonly TimeSpan refresh_interval = TimeSpan.FromMinutes(15);

        private readonly IHttpClientFactory httpClientFactory;
        private readonly ILogger<ToriiClientNameResolver> logger;
        private readonly string serverUrl;
        private readonly string secret;

        private Dictionary<string, string> hashToClientName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private CancellationTokenSource? loopCts;

        public ToriiClientNameResolver(IHttpClientFactory httpClientFactory, ILogger<ToriiClientNameResolver> logger)
        {
            this.httpClientFactory = httpClientFactory;
            this.logger = logger;
            // SHARED_INTEROP_DOMAIN already points at the g0v0 server (e.g. http://app:8000).
            // Routed through AppSettings for parity with the rest of the spectator's config —
            // raw `Environment.GetEnvironmentVariable` calls are reserved for AppSettings'
            // bootstrap path so configuration sourcing stays in one place.
            serverUrl = AppSettings.SharedInteropDomain.TrimEnd('/');
            secret = AppSettings.ClientVersionWebhookSecret;
        }

        public string? Resolve(string? versionHash)
        {
            if (string.IsNullOrWhiteSpace(versionHash))
                return null;

            // Local copy so a concurrent refresh swap doesn't produce a torn read.
            var snapshot = hashToClientName;
            return snapshot.TryGetValue(versionHash.Trim().ToLowerInvariant(), out string? name) ? name : null;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(serverUrl) || string.IsNullOrEmpty(secret))
            {
                logger.LogWarning("Torii client-name resolver disabled: SHARED_INTEROP_DOMAIN or CLIENT_VERSION_WEBHOOK_SECRET not configured.");
                return Task.CompletedTask;
            }

            loopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _ = runRefreshLoop(loopCts.Token);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            loopCts?.Cancel();
            return Task.CompletedTask;
        }

        private async Task runRefreshLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await refreshOnce(token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Torii client-name resolver refresh failed; keeping previous snapshot.");
                }

                try
                {
                    await Task.Delay(refresh_interval, token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        private async Task refreshOnce(CancellationToken token)
        {
            using var http = httpClientFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(15);

            using var request = new HttpRequestMessage(HttpMethod.Get, $"{serverUrl}/api/private/client-versions/torii-hashes");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);

            using var response = await http.SendAsync(request, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var payload = await response.Content.ReadFromJsonAsync<HashesResponse>(cancellationToken: token).ConfigureAwait(false);
            if (payload?.Hashes == null)
            {
                logger.LogWarning("Torii client-name resolver: empty payload from {url}", serverUrl);
                return;
            }

            var fresh = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in payload.Hashes)
                fresh[kv.Key.Trim().ToLowerInvariant()] = kv.Value;

            hashToClientName = fresh;
            logger.LogInformation("Torii client-name resolver: cached {count} verified hashes.", fresh.Count);
        }

        private class HashesResponse
        {
            [System.Text.Json.Serialization.JsonPropertyName("hashes")] public Dictionary<string, string>? Hashes { get; set; }
        }
    }
}
