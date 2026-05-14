// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;

namespace osu.Server.Spectator
{
    public static class AppSettings
    {
        /// <summary>
        /// Tolerant boolean env-var parser used for every <c>bool</c> setting
        /// below. Upstream relies on <see cref="bool.TryParse"/>, which only
        /// accepts the literal strings "true" and "false". Operators following
        /// Docker/12-factor conventions naturally write <c>SAVE_REPLAYS=1</c>
        /// (or 0, yes, no, on, off), which silently fell back to the default
        /// — most painfully on <c>SAVE_REPLAYS</c> where every replay was
        /// dropped on the floor without a single log line. Recognise the
        /// common truthy/falsy spellings instead.
        /// </summary>
        private static bool parseBool(string? value, bool defaultValue)
        {
            if (string.IsNullOrEmpty(value))
                return defaultValue;
            switch (value.Trim().ToLowerInvariant())
            {
                case "1":
                case "true":
                case "yes":
                case "on":
                case "y":
                case "t":
                    return true;
                case "0":
                case "false":
                case "no":
                case "off":
                case "n":
                case "f":
                    return false;
                default:
                    return defaultValue;
            }
        }

        public static bool SaveReplays { get; }

        public static int ReplayUploaderConcurrency { get; set; } = 1;

        #region For use with FileScoreStorage

        public static string ReplaysPath { get; } = "replays";

        #endregion

        #region For use with S3ScoreStorage

        public static string S3Key { get; } = string.Empty;
        public static string S3Secret { get; } = string.Empty;
        public static string ReplaysBucket { get; } = string.Empty;

        #endregion

        public static bool TrackBuildUserCounts { get; set; }
        public static bool ClientCheckVersion { get; }
        public static int[] ClientCheckVersionExemptGroups { get; }

        public static int ServerPort { get; set; } = 80;
        public static string RedisHost { get; } = "localhost";
        public static string DataDogAgentHost { get; set; } = "localhost";

        public static string DatabaseHost { get; } = "localhost";
        public static string DatabaseUser { get; } = "osuweb";
        public static int DatabasePort { get; } = 3306;

        /// <summary>
        /// Torii: g0v0's MySQL database name. Upstream hardcoded the database
        /// in the connection string, but g0v0 ships with `osu_api` as the
        /// canonical schema name and we need to be able to override per-deploy.
        /// </summary>
        public static string DatabaseName { get; } = "osu_api";

        /// <summary>
        /// Torii: g0v0's MySQL password. Empty string = no password (the
        /// connection string omits the Password= clause entirely so MySQL
        /// falls back to whatever auth method the user account has — useful
        /// for unix-socket auth in dev).
        /// </summary>
        public static string DatabasePassword { get; } = string.Empty;

        /// <summary>
        /// Torii: gate for upstream's <c>BeatmapStatusWatcher</c>, which polls
        /// osu-web's <c>bss_process_queue</c> table to broadcast newly-processed
        /// beatmap-set updates. g0v0 doesn't have that table (its beatmap
        /// pipeline writes directly to <c>beatmaps</c> + uses redis pub/sub for
        /// updates), so the poller throws "table not found" on every tick.
        /// Defaults to <c>false</c> for Torii deploys; flip to true when running
        /// against a real osu-web schema.
        /// </summary>
        public static bool EnableBeatmapStatusPolling { get; } = false;

        public static string SharedInteropDomain { get; } = "http://localhost:8080";
        public static string SharedInteropSecret { get; } = string.Empty;

        /// <summary>
        /// Bearer token used by <see cref="Services.ToriiClientNameResolver"/> to authenticate
        /// against the Torii server's <c>/api/private/client-versions/torii-hashes</c> endpoint.
        /// The g0v0 backend rotates this secret independently of the shared-interop one because
        /// the client-versions registry is exposed to the spectator only — never to osu!web.
        /// </summary>
        public static string ClientVersionWebhookSecret { get; } = string.Empty;

        /// <summary>
        /// HMAC secret used to sign / verify JWTs issued by the Torii (g0v0) backend.
        /// Required when <see cref="UseLegacyRsaAuth"/> is false (the default for Torii).
        /// Mirrors g0v0's <c>JWT_SECRET_KEY</c> exactly — both processes must hold the same
        /// value or every request to the spectator hub fails authentication.
        /// </summary>
        public static string JwtSecretKey { get; } = string.Empty;

        /// <summary>
        /// When true, the spectator validates lazer-client JWTs against the
        /// <c>oauth-public.key</c> RSA file (osu!web flow). When false (the Torii default),
        /// it validates HMAC-SHA256 signatures using <see cref="JwtSecretKey"/>. The
        /// difference matters because g0v0 issues HS256 tokens — it doesn't have access
        /// to osu!web's RSA private key, and matching public-key infrastructure isn't
        /// worth running for a single-tenant deploy.
        ///
        /// Default is <c>false</c> because this branch (torii-customizations) targets
        /// g0v0 deployments. Operators pointing this spectator at upstream osu!web must
        /// set <c>USE_LEGACY_RSA_AUTH=true</c> explicitly. The previous default of
        /// <c>true</c> combined with the tolerant bool env-var parser (which falls back
        /// to the default for empty strings) caused every JWT to be RSA-validated against
        /// HS256 tokens — IDX10503 "Token does not have a kid" — when the docker-compose
        /// wrote <c>USE_LEGACY_RSA_AUTH=</c> (empty).
        /// </summary>
        public static bool UseLegacyRsaAuth { get; } = false;

        /// <summary>
        /// OAuth client id assigned to the lazer client by osu!web. Used as the JWT
        /// <c>aud</c> validation parameter so tokens issued for other clients (osu!web
        /// itself, the referee panel, etc.) can't authenticate as a lazer connection.
        /// Defaults to "5" (the lazer client id on osu!web's reference deploy); g0v0
        /// uses "5" too unless the operator changed it.
        /// </summary>
        public static int OsuClientId { get; } = 5;

        public static string? SentryDsn { get; }

        public static int BanchoBotUserId { get; } = 3;

        public static int MatchmakingRoomRounds { get; set; } = 5;
        public static bool MatchmakingHeadToHeadIsBestOf { get; set; } = true;
        public static bool MatchmakingRoomAllowSkip { get; set; }

        public static TimeSpan MatchmakingLobbyUpdateRate { get; } = TimeSpan.FromSeconds(5);
        public static TimeSpan MatchmakingQueueUpdateRate { get; } = TimeSpan.FromSeconds(1);

        /// <summary>
        /// The duration for which users are temporarily banned from the matchmaking queue after declining an invitation.
        /// </summary>
        public static TimeSpan MatchmakingQueueBanDuration { get; } = TimeSpan.FromMinutes(1);

        /// <summary>
        /// The total number of beatmaps per matchmaking room.
        /// </summary>
        public static int MatchmakingPoolSize { get; } = 50;

        /// <summary>
        /// Torii: when true, replays are stored regardless of beatmap rank
        /// status (osu-web only stores them for ranked..loved). g0v0 needs
        /// this on so leaderboards on graveyarded/qualified maps still
        /// surface a watchable replay. Mirrors the M1PP setting of the
        /// same name.
        /// </summary>
        #region Path 1B HTTP-DAO migration flags (see PATH_1B_PLAN.md)

        // Each surface of IDatabaseAccess is migrated to HTTP one phase at a
        // time. Each phase's flag gates whether that surface's calls go via
        // SpectatorBackendClient (HTTP to g0v0) or the legacy DatabaseAccess
        // path (direct MySQL). Default: false (legacy SQL) on every phase, so
        // a fresh checkout of this branch keeps existing behaviour. Operators
        // flip each flag to true once the corresponding g0v0 endpoints land
        // and pass smoke tests in their environment.
        //
        // The phased rollout means at any commit the spectator can be in
        // mixed mode (some methods HTTP, some MySQL). Rollback of any phase
        // is one env-var flip, no rebuild needed.

        public static bool UseHttpDaoAuth { get; }           // Phase 1: auth/identity (5 methods)
        public static bool UseHttpDaoSession { get; }        // Phase 2: user session lifecycle (4 methods)
        public static bool UseHttpDaoSocial { get; }         // Phase 3: friends/relationships (2 methods)
        public static bool UseHttpDaoBeatmap { get; }        // Phase 4: beatmap + fail-time (5 methods)
        public static bool UseHttpDaoRoom { get; }           // Phase 5: multiplayer room lifecycle (10 methods)
        public static bool UseHttpDaoPlaylist { get; }       // Phase 6: playlist items (7 methods) — also fixes the played-item-stuck bug
        public static bool UseHttpDaoScore { get; }          // Phase 7: scores (8 methods)
        public static bool UseHttpDaoEvents { get; }         // Phase 8: event logging (1 method)
        public static bool UseHttpDaoMatchmaking { get; }    // Phase 9: matchmaking pools/stats/ELO (10 methods, needs alembic c4d5e6f7a8b9)
        public static bool UseHttpDaoBuilds { get; }         // Phase 10: build version tracking (5 methods, currently stubbed)
        public static bool UseHttpDaoMisc { get; }           // Phase 11: chat filters + playtime (3 methods)

        #endregion

        public static bool EnableAllBeatmapLeaderboard { get; }

        /// <summary>
        /// Torii: enables osu! Autopilot scoring/leaderboards as a separate
        /// pseudo-ruleset (`OSUAP`). When false, AP-modded scores fall back
        /// to the base ruleset's leaderboard. Required for the playtime
        /// tracker to bucket AP scores into the right `lazer_user_statistics.mode`
        /// row.
        /// </summary>
        public static bool EnableAP { get; }

        /// <summary>
        /// Torii: enables osu!/taiko/fruits Relax scoring/leaderboards as
        /// separate pseudo-rulesets (`OSURX`/`TAIKORX`/`FRUITSRX`). Same
        /// shape as <see cref="EnableAP"/>.
        /// </summary>
        public static bool EnableRX { get; }

        static AppSettings()
        {
            SaveReplays = parseBool(Environment.GetEnvironmentVariable("SAVE_REPLAYS"), SaveReplays);
            ReplayUploaderConcurrency = int.TryParse(Environment.GetEnvironmentVariable("REPLAY_UPLOAD_THREADS"), out int uploaderConcurrency) ? uploaderConcurrency : ReplayUploaderConcurrency;
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ReplayUploaderConcurrency);

            ReplaysPath = Environment.GetEnvironmentVariable("REPLAYS_PATH") ?? ReplaysPath;
            S3Key = Environment.GetEnvironmentVariable("S3_KEY") ?? S3Key;
            S3Secret = Environment.GetEnvironmentVariable("S3_SECRET") ?? S3Secret;
            ReplaysBucket = Environment.GetEnvironmentVariable("REPLAYS_BUCKET") ?? ReplaysBucket;
            TrackBuildUserCounts = parseBool(Environment.GetEnvironmentVariable("TRACK_BUILD_USER_COUNTS"), TrackBuildUserCounts);
            ClientCheckVersion = parseBool(Environment.GetEnvironmentVariable("CLIENT_CHECK_VERSION"), ClientCheckVersion);

            ClientCheckVersionExemptGroups = Environment.GetEnvironmentVariable("CLIENT_CHECK_VERSION_EXEMPT_GROUPS")?.Split(',')
                                                        .Select(id =>
                                                        {
                                                            bool parsed = int.TryParse(id, out int result);
                                                            return (parsed, result);
                                                        })
                                                        .Where(res => res.parsed)
                                                        .Select(res => res.result)
                                                        .ToArray() ?? [];

            ServerPort = int.TryParse(Environment.GetEnvironmentVariable("SERVER_PORT"), out int serverPort) ? serverPort : ServerPort;
            RedisHost = Environment.GetEnvironmentVariable("REDIS_HOST") ?? RedisHost;
            DataDogAgentHost = Environment.GetEnvironmentVariable("DD_AGENT_HOST") ?? DataDogAgentHost;

            // Accept both legacy MYSQL_* names (m1pp + g0v0 docker-compose convention)
            // and upstream's DB_* names. Prior Torii deploys typed `DB_PASS=password`
            // / `MYSQL_PASSWORD=password` in compose, so a checkout that only honoured
            // DB_PASSWORD landed empty-string and crashed every JWT validation with
            // "MySqlException: Access denied (using password: NO)" the moment the
            // OnTokenValidated callback tried to load the access_token row.
            DatabaseHost = Environment.GetEnvironmentVariable("DB_HOST") ?? Environment.GetEnvironmentVariable("MYSQL_HOST") ?? DatabaseHost;
            DatabaseUser = Environment.GetEnvironmentVariable("DB_USER") ?? Environment.GetEnvironmentVariable("MYSQL_USER") ?? DatabaseUser;
            DatabasePort = int.TryParse(Environment.GetEnvironmentVariable("DB_PORT") ?? Environment.GetEnvironmentVariable("MYSQL_PORT"), out int databasePort) ? databasePort : DatabasePort;
            DatabaseName = Environment.GetEnvironmentVariable("DB_NAME") ?? Environment.GetEnvironmentVariable("MYSQL_DATABASE") ?? DatabaseName;
            DatabasePassword = Environment.GetEnvironmentVariable("DB_PASSWORD")
                               ?? Environment.GetEnvironmentVariable("DB_PASS")
                               ?? Environment.GetEnvironmentVariable("MYSQL_PASSWORD")
                               ?? DatabasePassword;
            EnableBeatmapStatusPolling = parseBool(Environment.GetEnvironmentVariable("ENABLE_BEATMAP_STATUS_POLLING"), EnableBeatmapStatusPolling);

            SharedInteropDomain = Environment.GetEnvironmentVariable("SHARED_INTEROP_DOMAIN") ?? SharedInteropDomain;
            SharedInteropSecret = Environment.GetEnvironmentVariable("SHARED_INTEROP_SECRET") ?? SharedInteropSecret;

            ClientVersionWebhookSecret = Environment.GetEnvironmentVariable("CLIENT_VERSION_WEBHOOK_SECRET") ?? ClientVersionWebhookSecret;

            JwtSecretKey = Environment.GetEnvironmentVariable("JWT_SECRET_KEY") ?? JwtSecretKey;
            UseLegacyRsaAuth = parseBool(Environment.GetEnvironmentVariable("USE_LEGACY_RSA_AUTH"), UseLegacyRsaAuth);
            OsuClientId = int.TryParse(Environment.GetEnvironmentVariable("OSU_CLIENT_ID"), out int osuClientId) ? osuClientId : OsuClientId;

            SentryDsn = Environment.GetEnvironmentVariable("SENTRY_DSN");

            BanchoBotUserId = int.TryParse(Environment.GetEnvironmentVariable("BANCHO_BOT_USER_ID"), out int banchoBotUserId) ? banchoBotUserId : BanchoBotUserId;

            MatchmakingRoomRounds = int.TryParse(Environment.GetEnvironmentVariable("MATCHMAKING_ROOM_ROUNDS"), out int mmRounds)
                ? mmRounds
                : MatchmakingRoomRounds;

            MatchmakingHeadToHeadIsBestOf = parseBool(Environment.GetEnvironmentVariable("MATCHMAKING_HEAD_TO_HEAD_IS_BESTOF"), MatchmakingHeadToHeadIsBestOf);

            MatchmakingRoomAllowSkip = parseBool(Environment.GetEnvironmentVariable("MATCHMAKING_ALLOW_SKIP"), MatchmakingRoomAllowSkip);

            MatchmakingLobbyUpdateRate = int.TryParse(Environment.GetEnvironmentVariable("MATCHMAKING_LOBBY_UPDATE_RATE"), out int mmLobbyUpdateRate)
                ? TimeSpan.FromSeconds(mmLobbyUpdateRate)
                : MatchmakingLobbyUpdateRate;

            MatchmakingQueueUpdateRate = int.TryParse(Environment.GetEnvironmentVariable("MATCHMAKING_QUEUE_UPDATE_RATE"), out int mmQueueUpdateRate)
                ? TimeSpan.FromSeconds(mmQueueUpdateRate)
                : MatchmakingQueueUpdateRate;

            MatchmakingQueueBanDuration = int.TryParse(Environment.GetEnvironmentVariable("MATCHMAKING_QUEUE_BAN_DURATION"), out int mmQueueBanDuration)
                ? TimeSpan.FromSeconds(mmQueueBanDuration)
                : MatchmakingQueueBanDuration;

            MatchmakingPoolSize = int.TryParse(Environment.GetEnvironmentVariable("MATCHMAKING_POOL_SIZE"), out int mmPoolSize)
                ? mmPoolSize
                : MatchmakingPoolSize;

            // Path 1B per-phase HTTP-DAO migration flags. All default false
            // (legacy SQL) until the corresponding g0v0 endpoints land and
            // the operator opts in. See PATH_1B_PLAN.md for the per-phase
            // surface description.
            UseHttpDaoAuth = parseBool(Environment.GetEnvironmentVariable("USE_HTTP_DAO_AUTH"), UseHttpDaoAuth);
            UseHttpDaoSession = parseBool(Environment.GetEnvironmentVariable("USE_HTTP_DAO_SESSION"), UseHttpDaoSession);
            UseHttpDaoSocial = parseBool(Environment.GetEnvironmentVariable("USE_HTTP_DAO_SOCIAL"), UseHttpDaoSocial);
            UseHttpDaoBeatmap = parseBool(Environment.GetEnvironmentVariable("USE_HTTP_DAO_BEATMAP"), UseHttpDaoBeatmap);
            UseHttpDaoRoom = parseBool(Environment.GetEnvironmentVariable("USE_HTTP_DAO_ROOM"), UseHttpDaoRoom);
            UseHttpDaoPlaylist = parseBool(Environment.GetEnvironmentVariable("USE_HTTP_DAO_PLAYLIST"), UseHttpDaoPlaylist);
            UseHttpDaoScore = parseBool(Environment.GetEnvironmentVariable("USE_HTTP_DAO_SCORE"), UseHttpDaoScore);
            UseHttpDaoEvents = parseBool(Environment.GetEnvironmentVariable("USE_HTTP_DAO_EVENTS"), UseHttpDaoEvents);
            UseHttpDaoMatchmaking = parseBool(Environment.GetEnvironmentVariable("USE_HTTP_DAO_MATCHMAKING"), UseHttpDaoMatchmaking);
            UseHttpDaoBuilds = parseBool(Environment.GetEnvironmentVariable("USE_HTTP_DAO_BUILDS"), UseHttpDaoBuilds);
            UseHttpDaoMisc = parseBool(Environment.GetEnvironmentVariable("USE_HTTP_DAO_MISC"), UseHttpDaoMisc);

            EnableAllBeatmapLeaderboard = parseBool(Environment.GetEnvironmentVariable("ENABLE_ALL_BEATMAP_LEADERBOARD"), EnableAllBeatmapLeaderboard);
            // Accept both ENABLE_AP and ENABLE_OSU_AP (M1PP used the latter, upstream-style is the former).
            EnableAP = parseBool(Environment.GetEnvironmentVariable("ENABLE_AP") ?? Environment.GetEnvironmentVariable("ENABLE_OSU_AP"), EnableAP);
            EnableRX = parseBool(Environment.GetEnvironmentVariable("ENABLE_RX") ?? Environment.GetEnvironmentVariable("ENABLE_OSU_RX"), EnableRX);
        }
    }
}
