// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using osu.Game.Online.Metadata;
using osu.Game.Users;
using osu.Server.Spectator.Entities;

namespace osu.Server.Spectator.Hubs.Metadata
{
    /// <summary>
    /// <c>GET /msn/presence</c>: who is signed in to osu! and which map they're playing, for torii-msn (Messenger's
    /// "now playing" line, BanchoBot's <c>!online</c> and "a contact started playing" alerts).
    /// Only answers when <c>MSN_PRESENCE_TOKEN</c> is set, to <c>Authorization: Bearer &lt;that token&gt;</c>.
    /// Players who appear offline in osu! are left out, as they are for everyone else.
    /// </summary>
    public static class MsnPresenceEndpoint
    {
        public record Player(long user_id, string? status, string? map);

        public static void MapMsnPresence(this IEndpointRouteBuilder endpoints)
        {
            string? token = Environment.GetEnvironmentVariable("MSN_PRESENCE_TOKEN");

            endpoints.MapGet("/msn/presence", (HttpContext context, EntityStore<MetadataClientState> states) =>
            {
                if (string.IsNullOrEmpty(token))
                    return Results.NotFound();
                if (!IsAuthorised(context.Request.Headers.Authorization.ToString(), token))
                    return Results.Unauthorized();

                return Results.Json(new { players = Players(states) });
            });
        }

        public static bool IsAuthorised(string authorization, string token) =>
            CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(authorization), Encoding.UTF8.GetBytes("Bearer " + token));

        public static Player[] Players(EntityStore<MetadataClientState> states) =>
            states.GetAllEntities()
                  .Select(kvp => (userId: kvp.Key, presence: kvp.Value.ToUserPresence()))
                  .Where(p => p.presence != null)
                  .Select(p => new Player(p.userId, p.presence!.Value.Status?.ToString().ToLowerInvariant(), mapOf(p.presence.Value)))
                  .ToArray();

        /// <summary>The map they're playing ("Artist - Title (Mapper) [Difficulty]"), or null when they aren't in a game.</summary>
        private static string? mapOf(UserPresence presence) => presence.Activity switch
        {
            UserActivity.InGame game => game.BeatmapDisplayTitle,
            _ => null,
        };
    }
}
