// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using Microsoft.Extensions.Logging;
using osu.Server.Spectator.Services;

namespace osu.Server.Spectator.Database
{
    public class DatabaseFactory : IDatabaseFactory
    {
        private readonly ILoggerFactory loggerFactory;

        // Torii: DatabaseAccess needs an ISharedInterop handle to call
        // EnsureBeatmapPresentAsync (used during room/playlist setup against g0v0).
        // Upstream's DatabaseAccess doesn't, but prod's does — keeping the dep here
        // so the factory can pass it through without DatabaseAccess having to do
        // service-locator lookups.
        private readonly ISharedInterop sharedInterop;

        // Path 1B: SpectatorBackendClient handles HTTP-backed DAO methods
        // when USE_HTTP_DAO_* flags are on. DatabaseAccess routes per-method
        // based on the flag state. See PATH_1B_PLAN.md.
        private readonly ISpectatorBackendClient backend;

        public DatabaseFactory(ILoggerFactory loggerFactory, ISharedInterop sharedInterop, ISpectatorBackendClient backend)
        {
            this.loggerFactory = loggerFactory;
            this.sharedInterop = sharedInterop;
            this.backend = backend;
        }

        public IDatabaseAccess GetInstance() => new DatabaseAccess(loggerFactory, sharedInterop, backend);
    }
}
