// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;

namespace osu.Server.Spectator
{
    public static class AppSettings
    {
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
        /// </summary>
        public static bool UseLegacyRsaAuth { get; } = true;

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

        static AppSettings()
        {
            SaveReplays = bool.TryParse(Environment.GetEnvironmentVariable("SAVE_REPLAYS"), out bool saveReplays) ? saveReplays : SaveReplays;
            ReplayUploaderConcurrency = int.TryParse(Environment.GetEnvironmentVariable("REPLAY_UPLOAD_THREADS"), out int uploaderConcurrency) ? uploaderConcurrency : ReplayUploaderConcurrency;
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ReplayUploaderConcurrency);

            ReplaysPath = Environment.GetEnvironmentVariable("REPLAYS_PATH") ?? ReplaysPath;
            S3Key = Environment.GetEnvironmentVariable("S3_KEY") ?? S3Key;
            S3Secret = Environment.GetEnvironmentVariable("S3_SECRET") ?? S3Secret;
            ReplaysBucket = Environment.GetEnvironmentVariable("REPLAYS_BUCKET") ?? ReplaysBucket;
            TrackBuildUserCounts = bool.TryParse(Environment.GetEnvironmentVariable("TRACK_BUILD_USER_COUNTS"), out bool trackBuildUserCounts) ? trackBuildUserCounts : TrackBuildUserCounts;
            ClientCheckVersion = bool.TryParse(Environment.GetEnvironmentVariable("CLIENT_CHECK_VERSION"), out bool clientCheckVersion) ? clientCheckVersion : ClientCheckVersion;

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

            DatabaseHost = Environment.GetEnvironmentVariable("DB_HOST") ?? DatabaseHost;
            DatabaseUser = Environment.GetEnvironmentVariable("DB_USER") ?? DatabaseUser;
            DatabasePort = int.TryParse(Environment.GetEnvironmentVariable("DB_PORT"), out int databasePort) ? databasePort : DatabasePort;
            DatabaseName = Environment.GetEnvironmentVariable("DB_NAME") ?? DatabaseName;
            DatabasePassword = Environment.GetEnvironmentVariable("DB_PASSWORD") ?? DatabasePassword;

            SharedInteropDomain = Environment.GetEnvironmentVariable("SHARED_INTEROP_DOMAIN") ?? SharedInteropDomain;
            SharedInteropSecret = Environment.GetEnvironmentVariable("SHARED_INTEROP_SECRET") ?? SharedInteropSecret;

            ClientVersionWebhookSecret = Environment.GetEnvironmentVariable("CLIENT_VERSION_WEBHOOK_SECRET") ?? ClientVersionWebhookSecret;

            JwtSecretKey = Environment.GetEnvironmentVariable("JWT_SECRET_KEY") ?? JwtSecretKey;
            UseLegacyRsaAuth = bool.TryParse(Environment.GetEnvironmentVariable("USE_LEGACY_RSA_AUTH"), out bool useLegacyRsaAuth) ? useLegacyRsaAuth : UseLegacyRsaAuth;
            OsuClientId = int.TryParse(Environment.GetEnvironmentVariable("OSU_CLIENT_ID"), out int osuClientId) ? osuClientId : OsuClientId;

            SentryDsn = Environment.GetEnvironmentVariable("SENTRY_DSN");

            BanchoBotUserId = int.TryParse(Environment.GetEnvironmentVariable("BANCHO_BOT_USER_ID"), out int banchoBotUserId) ? banchoBotUserId : BanchoBotUserId;

            MatchmakingRoomRounds = int.TryParse(Environment.GetEnvironmentVariable("MATCHMAKING_ROOM_ROUNDS"), out int mmRounds)
                ? mmRounds
                : MatchmakingRoomRounds;

            MatchmakingHeadToHeadIsBestOf = bool.TryParse(Environment.GetEnvironmentVariable("MATCHMAKING_HEAD_TO_HEAD_IS_BESTOF"), out bool mmHeadToHeadIsBestOf)
                ? mmHeadToHeadIsBestOf
                : MatchmakingHeadToHeadIsBestOf;

            MatchmakingRoomAllowSkip = bool.TryParse(Environment.GetEnvironmentVariable("MATCHMAKING_ALLOW_SKIP"), out bool mmAllowSkip)
                ? mmAllowSkip
                : MatchmakingRoomAllowSkip;

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
        }
    }
}
