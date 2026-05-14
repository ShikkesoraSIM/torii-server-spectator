// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Threading.Tasks;
using osu.Server.Spectator.Authentication;

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
    /// This interface grows incrementally as phases land. It starts empty
    /// (Phase 0 — scaffolding only) and absorbs methods from
    /// <see cref="IDatabaseAccess"/> phase by phase. When all 60 methods have
    /// migrated, <see cref="IDatabaseAccess"/> can be deleted entirely and
    /// callers can depend on <see cref="ISpectatorBackendClient"/> directly.
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
        // Phase 1 — Auth + identity. Implementations land alongside the
        // matching g0v0 endpoints in app/router/lio.py (or split into
        // app/router/private/spectator/auth.py). Wired into DatabaseAccess
        // behind USE_HTTP_DAO_AUTH=true.

        // Task<int?> ResolveUserIdFromTokenAsync(JsonWebToken jwt);
        // Task<bool> IsUserRestrictedAsync(int userId);
        // Task<string?> GetUsernameAsync(int userId);
        // Task<int?> ResolveDelegatedTokenAsync(JsonWebToken jwt);
        // Task<int[]> GetUsersInGroupsAsync(int[] groupIds);

        // (Methods will be uncommented as Phase 1 implementations land.)
    }
}
