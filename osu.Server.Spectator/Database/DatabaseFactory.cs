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

        public DatabaseFactory(ILoggerFactory loggerFactory, ISharedInterop sharedInterop)
        {
            this.loggerFactory = loggerFactory;
            this.sharedInterop = sharedInterop;
        }

        public IDatabaseAccess GetInstance() => new DatabaseAccess(loggerFactory, sharedInterop);
    }
}
