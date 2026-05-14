// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Threading.Tasks;
using Microsoft.IdentityModel.JsonWebTokens;

namespace osu.Server.Spectator.Database
{
    /// <summary>
    /// HTTP-backed companion to <see cref="IDatabaseAccess"/>. Each method here
    /// has a 1:1 mapping to an HTTP endpoint under
    /// <c>${SHARED_INTEROP_DOMAIN}/_lio/spectator/*</c> served by
    /// <c>g0v0-server</c>.
    ///
    /// Path 1B migration (see <c>PATH_1B_PLAN.md</c>) replaces every
    /// <see cref="IDatabaseAccess"/> SQL implementation with an
    /// <see cref="ISpectatorBackendClient"/> HTTP call. Migration is
    /// per-surface (Phase 1: auth, Phase 2: session, … Phase 11: misc) and
    /// each surface is gated behind a <c>USE_HTTP_DAO_*</c> env flag so we can
    /// cut over one phase at a time and roll back instantly.
    ///
    /// This interface grows incrementally as phases land. When all 60 methods
    /// have migrated, <see cref="IDatabaseAccess"/> can be deleted entirely
    /// and callers can depend on <see cref="ISpectatorBackendClient"/>
    /// directly.
    ///
    /// All implementations MUST be:
    /// - Async + cancellation-safe
    /// - Idempotent for write methods (g0v0 must tolerate retries)
    /// - Return null for 404 (matching <see cref="IDatabaseAccess"/>'s nullable
    ///   return semantics — the wire-level 404 maps to C# null)
    /// - Throw <see cref="SpectatorBackendRequestFailedException"/> for any
    ///   other non-success response (fail-closed by design: the SignalR hub
    ///   call surfaces the failure rather than silently corrupting state)
    /// </summary>
    public interface ISpectatorBackendClient
    {
        // --- Phase 1 — Auth + identity (5 endpoints) ---

        /// <summary>
        /// Resolve an OAuth access-token (raw JWT string) to its current
        /// user_id by looking up <c>oauth_tokens.access_token</c> on g0v0.
        /// Returns null when the token doesn't exist or has expired.
        /// Migrated users are handled by the rows' <c>user_id</c> being
        /// rewritten directly on the DB (the JWT's <c>sub</c> claim is NOT
        /// used as the source of truth).
        ///
        /// <c>POST /_lio/spectator/auth/resolve-token</c> body
        /// <c>{token: string}</c> → <c>{user_id: int | null}</c>.
        /// </summary>
        Task<int?> ResolveUserIdFromTokenAsync(JsonWebToken jwt);

        /// <summary>
        /// Check whether a user is restricted. The contract is
        /// <c>restricted = priv != 1</c>; missing users return
        /// <c>restricted = true</c> (fail-closed).
        ///
        /// <c>GET /_lio/spectator/users/{user_id}/is-restricted</c> →
        /// <c>{restricted: bool}</c>.
        /// </summary>
        Task<bool> IsUserRestrictedAsync(int userId);

        /// <summary>
        /// Look up the user's current username. Returns null when the user
        /// doesn't exist (HTTP 404 → null per the
        /// <see cref="ISpectatorBackendClient"/> contract).
        ///
        /// <c>GET /_lio/spectator/users/{user_id}/username</c> →
        /// <c>{username: string}</c> or 404.
        /// </summary>
        Task<string?> GetUsernameAsync(int userId);

        /// <summary>
        /// Resolve a delegated/client-credentials grant to its resource-owner
        /// user_id. Stubbed on g0v0 (always returns null) because Torii's
        /// only OAuth flow is the direct password / refresh_token grant
        /// where the access_token identifies the user. Kept on the contract
        /// so future delegation flows can be added without changing callers.
        ///
        /// <c>POST /_lio/spectator/auth/resolve-delegated-token</c> body
        /// <c>{token: string}</c> → <c>{user_id: int | null}</c>.
        /// </summary>
        Task<int?> ResolveDelegatedResourceOwnerIdFromTokenAsync(JsonWebToken jwt);

        /// <summary>
        /// Look up user IDs belonging to any of the given phpbb-style
        /// group IDs. Stubbed on g0v0 (always returns empty) because g0v0
        /// doesn't model phpbb groups — equivalents are flags on
        /// <c>lazer_users</c> rows. The only caller is the version-check
        /// exemption hook, which is off by default for Torii.
        ///
        /// <c>POST /_lio/spectator/users/in-groups</c> body
        /// <c>{group_ids: int[]}</c> → <c>{user_ids: int[]}</c>.
        /// </summary>
        Task<int[]> GetUsersInGroupsAsync(int[] groupIds);
    }
}
