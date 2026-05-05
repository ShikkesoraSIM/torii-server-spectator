// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using Microsoft.Extensions.DependencyInjection;
using osu.Server.Spectator.Database;
using osu.Server.Spectator.Entities;
using osu.Server.Spectator.Hubs;
using osu.Server.Spectator.Hubs.Metadata;
using osu.Server.Spectator.Hubs.Multiplayer;
using osu.Server.Spectator.Hubs.Multiplayer.Matchmaking.Queue;
using osu.Server.Spectator.Hubs.Referee;
using osu.Server.Spectator.Hubs.Spectator;
using osu.Server.Spectator.Services;
using osu.Server.Spectator.Storage;
using StackExchange.Redis;

namespace osu.Server.Spectator.Extensions
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddHubEntities(this IServiceCollection serviceCollection)
        {
            return serviceCollection.AddHttpClient()
                                    .AddSingleton<ISharedInterop, SharedInterop>()
                                    .AddSingleton<EntityStore<SpectatorClientState>>()
                                    .AddSingleton<EntityStore<MultiplayerClientState>>()
                                    .AddSingleton<EntityStore<ServerMultiplayerRoom>>()
                                    .AddSingleton<EntityStore<ConnectionState>>()
                                    .AddSingleton<EntityStore<MetadataClientState>>()
                                    .AddSingleton<EntityStore<RefereeClientState>>()
                                    .AddSingleton<GracefulShutdownManager>()
                                    .AddSingleton<MetadataBroadcaster>()
                                    // Torii: hand replays to g0v0 over the existing /_lio/scores/replay
                                    // contract — it writes the bytes into ITS OWN storage at the
                                    // canonical replays/{score_id}_{beatmap_id}_{user_id}_lazer_replay.osr
                                    // path that Score.replay_filename resolves to. The previous
                                    // FileScoreStorage wiring wrote to the spectator container's
                                    // local filesystem (a bare integer filename under
                                    // AppSettings.ReplaysPath) which g0v0 had no visibility into,
                                    // so the replay icon would appear on every score (has_replay
                                    // got flipped) but every download attempt 404'd. We don't run
                                    // any S3-compatible object store on the Torii VPS, so the
                                    // upstream S3ScoreStorage stays unused.
                                    .AddSingleton<IScoreStorage, SharedInteropScoreStorage>()
                                    .AddSingleton<ScoreUploader>()
                                    .AddSingleton<IScoreProcessedSubscriber, ScoreProcessedSubscriber>()
                                    .AddSingleton<BuildUserCountUpdater>()
                                    .AddSingleton<ChatFilters>()
                                    .AddSingleton<IDailyChallengeUpdater, DailyChallengeUpdater>()
                                    .AddHostedService<IDailyChallengeUpdater>(ctx => ctx.GetRequiredService<IDailyChallengeUpdater>())
                                    .AddSingleton<MultiplayerEventDispatcher>()
                                    .AddSingleton<IMatchmakingQueueBackgroundService, MatchmakingQueueBackgroundService>()
                                    .AddHostedService<IMatchmakingQueueBackgroundService>(ctx => ctx.GetRequiredService<IMatchmakingQueueBackgroundService>())
                                    .AddSingleton<IMultiplayerRoomController, MultiplayerRoomController>()
                                    .AddHostedService<MultiplayerRoomLifetimeBackgroundService>()
                                    // Torii client-version verification: pulls the trusted-hash
                                    // list from g0v0's /api/private/client-versions/torii-hashes
                                    // endpoint at startup + every 15 min, caches it in-memory so
                                    // the metadata hub can label every connection with the build
                                    // it's running (or `null` for unverified clients) without a
                                    // per-presence DB hit.
                                    .AddSingleton<ToriiClientNameResolver>()
                                    .AddHostedService<ToriiClientNameResolver>(ctx => ctx.GetRequiredService<ToriiClientNameResolver>());
        }

        /// <summary>
        /// Adds MySQL (<see cref="IDatabaseFactory"/>) and Redis (<see cref="IConnectionMultiplexer"/>) services.
        /// </summary>
        public static IServiceCollection AddDatabaseServices(this IServiceCollection serviceCollection)
        {
            return serviceCollection.AddSingleton<IDatabaseFactory, DatabaseFactory>()
                                    .AddSingleton<IConnectionMultiplexer, ConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(AppSettings.RedisHost));
        }
    }
}
