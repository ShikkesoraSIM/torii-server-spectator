// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using MySqlConnector;
using osu.Game.Online.Multiplayer;
using osu.Game.Scoring;
using osu.Server.Spectator.Database.Models;
using osu.Server.Spectator.Services;

namespace osu.Server.Spectator.Database
{
    public class DatabaseAccess : IDatabaseAccess
    {
        private static bool dapperMapperInstalled;
        private static readonly object dapperMapperLock = new object();

        // torii: fix del leak de conexiones. getConnectionAsync devuelve una conexion NUEVA por llamada,
        // pero varios metodos lifted de upstream hacen `var conn = await getConnectionAsync()` sin disponerla
        // (asumen el caching de openConnection que quedo comentado) -> se leakeaban al pool. trackeamos todas
        // las que abre esta instancia y las disponemos en Dispose(); el doble-dispose de las que ya se cerraron
        // con `await using` es idempotente (MySqlConnection lo tolera).
        private readonly List<MySqlConnection> openedConnections = new List<MySqlConnection>();
        private readonly ILogger<DatabaseAccess> logger;
        private readonly ISharedInterop sharedInterop;
        private readonly ISpectatorBackendClient backend;

        public DatabaseAccess(ILoggerFactory loggerFactory, ISharedInterop sharedInterop, ISpectatorBackendClient backend)
        {
            logger = loggerFactory.CreateLogger<DatabaseAccess>();
            this.sharedInterop = sharedInterop;
            this.backend = backend;
        }

        public async Task<int?> GetUserIdFromTokenAsync(JsonWebToken jwtToken)
        {
            // Path 1B Phase 1: when USE_HTTP_DAO_AUTH=true, delegate to the
            // SpectatorBackendClient which talks HTTP to g0v0's
            // /_lio/spectator/auth/resolve-token. Default (flag off) keeps the
            // legacy direct-MySQL path below for operators who haven't cut
            // over yet. See PATH_1B_PLAN.md §5 Phase 1 + §8 migration strategy.
            if (AppSettings.UseHttpDaoAuth)
                return await backend.ResolveUserIdFromTokenAsync(jwtToken);

            // Look the access_token string up directly. We deliberately do NOT trust the
            // sub claim here: when a user is migrated between IDs (account merge / id
            // transfer) we rewrite oauth_tokens.user_id to the new id but the JWT payload
            // still has the old sub baked in. The DB row keyed by access_token is the
            // source of truth for current user identity.
            var accessToken = jwtToken.EncodedToken;

            if (string.IsNullOrEmpty(accessToken))
                return null;

            await using var connection = await getConnectionAsync();
            var result = await connection.QueryFirstOrDefaultAsync<int?>(
                "SELECT user_id FROM oauth_tokens WHERE access_token = @accessToken AND expires_at > UTC_TIMESTAMP()",
                new { accessToken });

            return result;
        }

        public async Task<string?> GetUsernameAsync(int userId)
        {
            // Path 1B Phase 1: see GetUserIdFromTokenAsync.
            if (AppSettings.UseHttpDaoAuth)
                return await backend.GetUsernameAsync(userId);

            await using var connection = await getConnectionAsync();

            return await connection.QueryFirstOrDefaultAsync<string?>("SELECT username FROM lazer_users WHERE id = @UserID", new { UserID = userId });
        }

        public async Task<bool> IsUserRestrictedAsync(int userId)
        {
            // Path 1B Phase 1: see GetUserIdFromTokenAsync.
            if (AppSettings.UseHttpDaoAuth)
                return await backend.IsUserRestrictedAsync(userId);

            await using var connection = await getConnectionAsync();

            var priv = await connection.QueryFirstOrDefaultAsync<int>("SELECT priv FROM lazer_users WHERE id = @UserID", new { UserID = userId });

            // priv å€¼ä¸º 1 è¡¨ç¤ºæ­£å¸¸ç”¨æˆ·ï¼Œå…¶ä»–å€¼å¯èƒ½è¡¨ç¤ºå—é™ç”¨æˆ·
            return priv != 1;
        }

        public async Task<multiplayer_room?> GetRoomAsync(long roomId)
        {
            await using var connection = await getConnectionAsync();

            return await connection.QueryFirstOrDefaultAsync<multiplayer_room>("SELECT * FROM rooms WHERE id = @RoomID", new { RoomID = roomId });
        }

        public async Task<multiplayer_room?> GetRealtimeRoomAsync(long roomId)
        {
            await using var connection = await getConnectionAsync();

            return await connection.QueryFirstOrDefaultAsync<multiplayer_room>("SELECT * FROM rooms WHERE type != 'multiplayer_playlist_items' AND id = @RoomID", new { RoomID = roomId });
        }

        public async Task<database_beatmap?> GetBeatmapAsync(int beatmapId)
        {
            await using var connection = await getConnectionAsync();

            return await connection.QuerySingleOrDefaultAsync<database_beatmap>(
                @"SELECT
            id as beatmap_id,
            beatmapset_id,
            checksum,
            beatmap_status as approved,
            difficulty_rating as difficultyrating,
            total_length AS total_length,
            CASE
                WHEN LOWER(mode) = 'osu' THEN 0
                WHEN LOWER(mode) = 'taiko' THEN 1
                WHEN LOWER(mode) = 'fruits' THEN 2
                WHEN LOWER(mode) = 'mania' THEN 3
                WHEN LOWER(mode) = 'osurx' THEN 4
                WHEN LOWER(mode) = 'osuap' THEN 5
                WHEN LOWER(mode) = 'taikorx' THEN 6
                WHEN LOWER(mode) = 'fruitsrx' THEN 7
                ELSE 0
            END as playmode,
            14 as osu_file_version
        FROM beatmaps
        WHERE id = @BeatmapId AND deleted_at IS NULL",
                new { BeatmapId = beatmapId });
        }

        /// <summary>
        /// èŽ·å–è°±é¢ï¼Œå¦‚æžœä¸å­˜åœ¨åˆ™é€šçŸ¥ LIO æ‹‰å–ã€‚
        /// </summary>
        /// <param name="beatmapId">è°±é¢ ID</param>
        /// <returns>è°±é¢ä¿¡æ¯ï¼Œå¦‚æžœä¸å­˜åœ¨åˆ™è¿”å›ž null</returns>
        public async Task<database_beatmap?> GetBeatmapOrFetchAsync(int beatmapId)
        {
            var beatmap = await GetBeatmapAsync(beatmapId);
            if (beatmap != null) return beatmap;

            logger.LogDebug("Beatmap {BeatmapId} not found in database, requesting LIO to fetch it", beatmapId);

            try
            {
                await sharedInterop.EnsureBeatmapPresentAsync(beatmapId);
                logger.LogDebug("LIO returned success for beatmap {BeatmapId}, checking database again", beatmapId);
                return await GetBeatmapAsync(beatmapId);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "LIO request failed for beatmap {BeatmapId}: {ErrorMessage}", beatmapId, ex.Message);
                return null;
            }
        }

        public async Task<fail_time?> GetBeatmapFailTimeAsync(int beatmapId)
        {
            await using var connection = await getConnectionAsync();
            return (await connection.QuerySingleOrDefaultAsync<fail_time>(
                "SELECT * FROM failtime WHERE beatmap_id = @BeatmapId",
                new { BeatmapId = beatmapId }));
        }

        public async Task UpdateFailTimeAsync(fail_time failTime)
        {
            await using var connection = await getConnectionAsync();
            await connection.ExecuteAsync(
                @"INSERT INTO failtime (beatmap_id, fail, `exit`)
                VALUES (@BeatmapId, @Fail, @Exit)
                ON DUPLICATE KEY UPDATE fail = @Fail, `exit` = @Exit",
                new { BeatmapId = failTime.beatmap_id, Fail = failTime.fail, Exit = failTime.exit });
        }

        public async Task<int?> GetUserPlaytimeAsync(string gamemode, int userId)
        {
            await using var connection = await getConnectionAsync();
            return await connection.QuerySingleOrDefaultAsync<int?>(
                "SELECT play_time FROM lazer_user_statistics WHERE user_id = @UserId AND mode = @GameMode", new { UserId = userId, GameMode = gamemode });
        }

        public async Task UpdateUserPlaytimeAsync(string gamemode, int userId, int playTime)
        {
            await using var connection = await getConnectionAsync();
            await connection.ExecuteAsync("UPDATE lazer_user_statistics SET play_time = @PlayTime WHERE user_id = @UserId AND mode = @GameMode",
                new { UserId = userId, GameMode = gamemode, PlayTime = playTime });
        }

        public async Task<database_beatmap[]> GetBeatmapsAsync(int beatmapSetId)
        {
            await using var connection = await getConnectionAsync();

            // Torii: g0v0's `beatmaps.mode` is a STRING enum
            // ('OSU'/'TAIKO'/'FRUITS'/'MANIA' uppercase in current prod data —
            // verified by directly inspecting the MySQL table). The
            // `database_beatmap.playmode` POCO field is `ushort`, so the
            // previous `mode as playmode` projection had Dapper try to parse
            // "OSU" as an int and crashed every freestyle-room state update
            // with `System.Data.DataException: Error parsing column 5
            // (playmode=OSU - String)`. That bubbled up through
            // ServerMultiplayerRoom.ensureAllUsersValidStyle → the SignalR
            // hub method invocation → the client's `client.ChangeState
            // (FinishedPlay)` await → "Score preparation failed!" toast.
            //
            // Decode using the same CASE pattern as GetBeatmapAsync above,
            // wrapped in LOWER() so it's case-insensitive (data has both
            // 'OSU' and 'osu' depending on import path / age). Hardcode
            // osu_file_version to 14 to match GetBeatmapAsync's handling
            // (we don't track it in g0v0 — see line ~126).
            return (await connection.QueryAsync<database_beatmap>(
                @"SELECT
            id as beatmap_id,
            beatmapset_id,
            checksum,
            beatmap_status as approved,
            difficulty_rating as difficultyrating,
            total_length AS total_length,
            CASE
                WHEN LOWER(mode) = 'osu' THEN 0
                WHEN LOWER(mode) = 'taiko' THEN 1
                WHEN LOWER(mode) = 'fruits' THEN 2
                WHEN LOWER(mode) = 'mania' THEN 3
                WHEN LOWER(mode) = 'osurx' THEN 4
                WHEN LOWER(mode) = 'osuap' THEN 5
                WHEN LOWER(mode) = 'taikorx' THEN 6
                WHEN LOWER(mode) = 'fruitsrx' THEN 7
                ELSE 0
            END as playmode,
            14 as osu_file_version
        FROM beatmaps
        WHERE beatmapset_id = @BeatmapSetId AND deleted_at IS NULL",
                new { BeatmapSetId = beatmapSetId })).ToArray();
        }

        public async Task MarkRoomActiveAsync(MultiplayerRoom room)
        {
            await using var connection = await getConnectionAsync();

            await connection.ExecuteAsync("UPDATE rooms SET ends_at = null WHERE id = @RoomID", new { RoomID = room.RoomID });
        }

        public async Task UpdateRoomSettingsAsync(MultiplayerRoom room)
        {
            await using var connection = await getConnectionAsync();

            await connection.ExecuteAsync("UPDATE rooms SET name = @Name, password = @Password, type = @MatchType, queue_mode = @QueueMode WHERE id = @RoomID", new
            {
                RoomID = room.RoomID,
                Name = room.Settings.Name,
                Password = room.Settings.Password,
                // needs ToString() to store as enums correctly, see https://github.com/DapperLib/Dapper/issues/813.
                MatchType = room.Settings.MatchType.ToDatabaseMatchType().ToString(),
                QueueMode = room.Settings.QueueMode.ToDatabaseQueueMode().ToString()
            });
        }

        public async Task UpdateRoomStatusAsync(MultiplayerRoom room)
        {
            await using var connection = await getConnectionAsync();

            await connection.ExecuteAsync("UPDATE rooms SET status = @Status WHERE id = @RoomID", new
            {
                RoomID = room.RoomID,
                // needs ToString() to store as enums correctly, see https://github.com/DapperLib/Dapper/issues/813.
                Status = room.State.ToDatabaseRoomStatus().ToString(),
            });
        }

        public async Task UpdateRoomHostAsync(MultiplayerRoom room)
        {
            await using var connection = await getConnectionAsync();

            Debug.Assert(room.Host != null);

            try
            {
                await connection.ExecuteAsync("UPDATE rooms SET host_id = @HostUserID WHERE id = @RoomID", new { HostUserID = room.Host.UserID, RoomID = room.RoomID });
            }
            catch (MySqlException)
            {
                // for now we really don't care about failures in this. it's updating display information each time a user joins/quits and doesn't need to be perfect.
            }
        }

        public async Task AddRoomParticipantAsync(MultiplayerRoom room, MultiplayerRoomUser user)
        {
            await using var connection = await getConnectionAsync();

            try
            {
                await using var transaction = await connection.BeginTransactionAsync();

                // the user may have previously been in the room and set some scores, so need to update their presence if existing.
                await connection.ExecuteAsync(
                    "INSERT INTO room_participated_users (room_id, user_id, joined_at, left_at) VALUES (@RoomID, @UserID, NOW(), NULL) ON DUPLICATE KEY UPDATE left_at = NULL",
                    new { RoomID = room.RoomID, UserID = user.UserID }, transaction);

                await connection.ExecuteAsync(
                    "UPDATE rooms SET participant_count = @Count WHERE id = @RoomID",
                    new { RoomID = room.RoomID, Count = room.Users.Count }, transaction);

                await transaction.CommitAsync();
            }
            catch (MySqlException)
            {
                // for now we really don't care about failures in this. it's updating display information each time a user joins/quits and doesn't need to be perfect.
            }
        }

        public Task AddLoginForUserAsync(int userId, string? userIp)
        {
            return Task.CompletedTask;
            // if (string.IsNullOrEmpty(userIp))
            //     return;
            //
            // await using var connection = await getConnectionAsync();
            //
            // try
            // {
            //     await connection.ExecuteAsync("INSERT INTO user_login_log (user_id, ip_address, login_method, login_time) VALUES (@UserID, @IP, 'spectator', UTC_TIMESTAMP())",
            //         new { UserID = userId, IP = userIp });
            // }
            // catch (MySqlException ex)
            // {
            //     logger.LogWarning(ex, "Could not log login for user {UserId}", userId);
            // }
        }

        public async Task OfflineUser(int userId)
        {
            await using var connection = await getConnectionAsync();
            // torii: al desconectar tambien apagamos is_online. La conexion la prende (ToggleUserPresenceAsync
            // -> is_online=1) pero antes NUNCA se apagaba, asi que quedaba en 1 para siempre y el perfil mostraba
            // "Currently online" a todos. CleanUpState ya borra el redis key de presence; espejamos eso en la columna.
            await connection.ExecuteAsync("UPDATE lazer_users SET last_visit = NOW(), is_online = 0 WHERE `id` = @userId", new { userId = userId });
        }

        public async Task RemoveRoomParticipantAsync(MultiplayerRoom room, MultiplayerRoomUser user)
        {
            await using var connection = await getConnectionAsync();

            try
            {
                await using var transaction = await connection.BeginTransactionAsync();

                await connection.ExecuteAsync(
                    "UPDATE room_participated_users SET left_at = NOW() WHERE room_id = @RoomID AND user_id = @UserID AND left_at IS NULL",
                    new { RoomID = room.RoomID, UserID = user.UserID }, transaction);

                await connection.ExecuteAsync(
                    "UPDATE rooms SET participant_count = @Count WHERE id = @RoomID",
                    new { RoomID = room.RoomID, Count = room.Users.Count }, transaction);

                await transaction.CommitAsync();
            }
            catch (MySqlException)
            {
                // for now we really don't care about failures in this. it's updating display information each time a user joins/quits and doesn't need to be perfect.
            }
        }

        public async Task<multiplayer_playlist_item> GetPlaylistItemAsync(long roomId, long playlistItemId)
        {
            await using var connection = await getConnectionAsync();

            // Torii: g0v0's `room_playlists` doesn't carry the beatmap checksum
            // directly (osu-web's table did). Pull it from `beatmaps` via JOIN
            // so the deserialised POCO has BeatmapChecksum populated for
            // version-mismatch checks at score-submit time. Same in
            // GetAllPlaylistItemsAsync below.
            return await connection.QuerySingleAsync<multiplayer_playlist_item>(
                @"SELECT rp.*, b.checksum, b.difficulty_rating AS difficultyrating
                  FROM room_playlists rp
                  LEFT JOIN beatmaps b ON b.id = rp.beatmap_id
                  WHERE rp.id = @Id AND rp.room_id = @RoomId",
                new { Id = playlistItemId, RoomId = roomId });
        }

        public async Task<long> AddPlaylistItemAsync(multiplayer_playlist_item item)
        {
            await using var connection = await getConnectionAsync();

            // è®¡ç®—è¯¥æˆ¿é—´å†…çš„ä¸‹ä¸€ä¸ªé€»è¾‘ idï¼Œå¹¶åœ¨åŒä¸€æ¡ INSERT ä¸­ä½¿ç”¨
            // åŒæ—¶æ˜¾å¼å†™å…¥ expired / played_atï¼Œé¿å… NOT NULL æ— é»˜è®¤å€¼çš„é—®é¢˜
            await connection.ExecuteAsync(@"
        INSERT INTO room_playlists
            (id, owner_id, room_id, beatmap_id, ruleset_id,
             allowed_mods, required_mods, freestyle, playlist_order,
             expired, played_at)
        VALUES
            (
                (SELECT COALESCE(MAX(rp.id), -1) + 1
                 FROM room_playlists rp
                 WHERE rp.room_id = @room_id),
                @owner_id, @room_id, @beatmap_id, @ruleset_id,
                @allowed_mods, @required_mods, @freestyle, @playlist_order,
                @expired, @played_at
            );",
                item);

            // è¿”å›žåˆšæ’å…¥è¡Œçš„â€œé€»è¾‘ idâ€ï¼ˆä¸æ˜¯è‡ªå¢žä¸»é”® db_idï¼‰
            // é€šè¿‡ LAST_INSERT_ID() å…³è”å–å›žé‚£ä¸€è¡Œçš„ id
            return await connection.QuerySingleAsync<long>(@"
        SELECT id FROM room_playlists WHERE db_id = LAST_INSERT_ID();");
        }

        public async Task UpdatePlaylistItemAsync(multiplayer_playlist_item item)
        {
            await using var connection = await getConnectionAsync();

            await connection.ExecuteAsync(@"
        UPDATE room_playlists SET
            beatmap_id     = @beatmap_id,
            ruleset_id     = @ruleset_id,
            required_mods  = @required_mods,
            allowed_mods   = @allowed_mods,
            freestyle      = @freestyle,
            playlist_order = @playlist_order,
            expired        = @expired,
            played_at      = @played_at,
            updated_at     = NOW()
        WHERE id = @id AND room_id = @room_id;", item);
        }

        public async Task RemovePlaylistItemAsync(long roomId, long playlistItemId)
        {
            await using var connection = await getConnectionAsync();

            await connection.ExecuteAsync(
                "DELETE FROM room_playlists WHERE id = @Id AND room_id = @RoomId",
                new { Id = playlistItemId, RoomId = roomId });
        }

        public async Task MarkPlaylistItemAsPlayedAsync(long roomId, long playlistItemId)
        {
            await using var connection = await getConnectionAsync();

            await connection.ExecuteAsync(@"
        UPDATE room_playlists
        SET expired = 1, played_at = NOW(), updated_at = NOW()
        WHERE id = @PlaylistItemId AND room_id = @RoomId;",
                new { PlaylistItemId = playlistItemId, RoomId = roomId });
        }

        public async Task EndMatchAsync(MultiplayerRoom room)
        {
            await using var connection = await getConnectionAsync();

            // Expire all non-expired items from the playlist.
            // We're not removing them because they may be linked to other tables (e.g. `multiplayer_realtime_room_events`, `multiplayer_scores_high`, etc.)
            // TODO: Re-enable this when `osu_api.multiplayer_score_links` exists. + " AND (SELECT COUNT(*) FROM multiplayer_score_links l WHERE l.playlist_item_id = p.id) = 0"
            await connection.ExecuteAsync(
                "UPDATE room_playlists p"
                + " SET p.expired = 1, played_at = NOW(), updated_at = NOW()"
                + " WHERE p.room_id = @RoomID"
                + " AND p.expired = 0",
                new { RoomID = room.RoomID });

            int totalUsers = connection.QuerySingle<int>("SELECT COUNT(*) FROM room_participated_users WHERE room_id = @RoomID", new { RoomID = room.RoomID });

            // Close the room.
            await connection.ExecuteAsync("UPDATE rooms SET participant_count = @Count, ends_at = NOW() WHERE id = @RoomID", new { RoomID = room.RoomID, Count = totalUsers, });
        }

        public async Task<multiplayer_playlist_item[]> GetAllPlaylistItemsAsync(long roomId)
        {
            await using var connection = await getConnectionAsync();

            // Torii: see GetPlaylistItemAsync — `room_playlists` doesn't store
            // the beatmap checksum in g0v0, JOIN it from `beatmaps` so room-init
            // time deserialisation produces playlist items the gameplay flow can
            // version-check against client-uploaded score blobs.
            return (await connection.QueryAsync<multiplayer_playlist_item>(
                @"SELECT rp.*, b.checksum, b.difficulty_rating AS difficultyrating
                  FROM room_playlists rp
                  LEFT JOIN beatmaps b ON b.id = rp.beatmap_id
                  WHERE rp.room_id = @RoomId",
                new { RoomId = roomId })).ToArray();
        }

        public async Task MarkScoreHasReplay(Score score)
        {
            await using var connection = await getConnectionAsync();

            await connection.ExecuteAsync("UPDATE `scores` SET `has_replay` = 1 WHERE `id` = @scoreId", new { scoreId = score.ScoreInfo.OnlineID, });
        }

        public async Task<SoloScore?> GetScoreFromTokenAsync(long token)
        {
            await using var connection = await getConnectionAsync();

            return await connection.QuerySingleOrDefaultAsync<SoloScore?>(
                "SELECT * FROM `scores` WHERE `id` = (SELECT `score_id` FROM `score_tokens` WHERE `id` = @Id)", new { Id = token });
        }

        public async Task<SoloScore?> GetScoreAsync(long id)
        {
            await using var connection = await getConnectionAsync();

            return await connection.QuerySingleOrDefaultAsync<SoloScore?>("SELECT * FROM `scores` WHERE `id` = @Id", new { Id = id });
        }

        public async Task<bool> IsScoreProcessedAsync(long scoreId)
        {
            await using var connection = await getConnectionAsync();

            return await connection.QuerySingleOrDefaultAsync<bool>("SELECT 1 FROM `scores` WHERE `id` = @ScoreId AND `processed` = '1'", new { ScoreId = scoreId });
        }

        public async Task<phpbb_zebra?> GetUserRelation(int userId, int zebraId)
        {
            await using var connection = await getConnectionAsync();

            // g0v0-server uses `relationship` (singular) instead of phpbb_zebra,
            // with type values 'FOLLOW' / 'BLOCK' (UPPERCASE — verified against
            // the live ENUM). Earlier this comparison used 'Friend' / 'Block'
            // which never matched anything because the canonical case is
            // upper. The result was that every InvitePlayer flow saw both
            // sides as "no relation" — friend privacy gates and block
            // detection were silently disabled.
            var relationship = await connection.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT * FROM `relationship` WHERE `user_id` = @UserId AND `target_id` = @ZebraId",
                new { UserId = userId, ZebraId = zebraId });

            if (relationship == null)
                return null;

            string type = ((string)relationship.type ?? string.Empty).ToUpperInvariant();
            return new phpbb_zebra
            {
                user_id = userId,
                zebra_id = zebraId,
                friend = type == "FOLLOW",
                foe = type == "BLOCK",
            };
        }

        public async Task<IEnumerable<int>> GetUserFriendsAsync(int userId)
        {
            await using var connection = await getConnectionAsync();

            // g0v0 uses `FOLLOW` (uppercase) for friend relations. The earlier
            // 'Friend' filter never matched any rows, so the lazer client
            // showed an empty friends list even when the user had real
            // follows in the relationship table. `priv = 1` keeps the
            // "regular user" gate that g0v0 already applies in its public
            // user lookup endpoints (priv=0 are placeholder/system rows).
            return await connection.QueryAsync<int>(
                @"SELECT r.target_id
                  FROM relationship r
                  JOIN lazer_users u ON r.target_id = u.id
                  WHERE r.user_id = @UserId
                    AND r.type = 'FOLLOW'
                    AND u.priv = 1", new { UserId = userId });
        }

        public async Task<bool> GetUserAllowsPMs(int userId)
        {
            await using var connection = await getConnectionAsync();

            // åœ¨g0v0-serverä¸­ï¼Œä½¿ç”¨pm_friends_onlyå­—æ®µï¼ˆfalseè¡¨ç¤ºå…è®¸æ‰€æœ‰äººå‘é€PMï¼‰
            var pmFriendsOnly = await connection.QuerySingleOrDefaultAsync<bool>("SELECT `pm_friends_only` FROM `lazer_users` WHERE `id` = @UserId", new { UserId = userId });

            // å¦‚æžœpm_friends_onlyä¸ºfalseï¼Œè¡¨ç¤ºå…è®¸æ‰€æœ‰äººå‘é€PM
            return !pmFriendsOnly;
        }

        public async Task<osu_build?> GetBuildByIdAsync(int buildId)
        {
            await using var connection = await getConnectionAsync();

            // g0v0-server doesn't have osu_builds table, return a dummy build
            return new osu_build { build_id = (uint)buildId, version = "unknown", hash = null, users = 0 };
        }

        public Task<IEnumerable<osu_build>> GetAllMainLazerBuildsAsync()
        {
            // g0v0-server doesn't have osu_builds table, return empty list
            return Task.FromResult<IEnumerable<osu_build>>(new List<osu_build>());
        }

        public Task<IEnumerable<osu_build>> GetAllPlatformSpecificLazerBuildsAsync()
        {
            // g0v0-server doesn't have osu_builds table, return empty list
            return Task.FromResult<IEnumerable<osu_build>>(new List<osu_build>());
        }

        public Task UpdateBuildUserCountAsync(osu_build build)
        {
            // g0v0-server doesn't have osu_builds table, do nothing
            return Task.CompletedTask;
        }

        public Task<IEnumerable<chat_filter>> GetAllChatFiltersAsync()
        {
            // g0v0-server doesn't have chat_filters table, return empty list
            return Task.FromResult<IEnumerable<chat_filter>>(new List<chat_filter>());
        }

        public async Task<IEnumerable<multiplayer_room>> GetActiveDailyChallengeRoomsAsync()
        {
            await using var connection = await getConnectionAsync();

            return await connection.QueryAsync<multiplayer_room>(
                "SELECT * FROM `rooms` "
                + "WHERE `category` = 'daily_challenge' "
                + "AND `type` = 'playlists' "
                + "AND `starts_at` <= NOW() "
                + "AND `ends_at` > NOW()");
        }

        public async Task<(long roomID, long playlistItemID)?> GetMultiplayerRoomIdForScoreAsync(long scoreId)
        {
            await using var connection = await getConnectionAsync();

            return await connection.QuerySingleOrDefaultAsync<(long, long)?>(
                "SELECT `room_id`, `playlist_item_id` "
                + "FROM `scores` "
                + "WHERE `id` = @scoreId",
                new { scoreId = scoreId });
        }

        public async Task<IEnumerable<SoloScore>> GetPassingScoresForPlaylistItem(long roomId, long playlistItemId, ulong afterScoreId = 0)
        {
            await using var connection = await getConnectionAsync();

            return (await connection.QueryAsync<SoloScore>(
                "SELECT `scores`.`id`, `scores`.`total_score` FROM `scores` "
                + "JOIN `playlist_best_scores` ON `playlist_best_scores`.`score_id` = `scores`.`id` "
                + "WHERE `scores`.`passed` = 1 "
                + "AND `playlist_best_scores`.`playlist_id` = @playlistItemId "
                + "AND `playlist_best_scores`.`room_id` = @roomId "
                + "AND `playlist_best_scores`.`score_id` > @afterScoreId "
                , new { playlistItemId = playlistItemId, afterScoreId = afterScoreId, roomId = roomId }));
        }

        // The older prod-style GetUserBestScoreAsync / GetUserRankInRoomAsync that targeted
        // `playlist_best_scores` were removed: nothing in the codebase actually called them
        // (ScoreProcessedSubscriber uses the upstream 2-arg `multiplayer_scores_high`-based
        // versions, which now live in the upstream-surface block below).

        public async Task LogRoomEventAsync(multiplayer_realtime_room_event ev)
        {
            await using var connection = await getConnectionAsync();
            await connection.ExecuteAsync(
                "INSERT INTO `multiplayer_events` (`room_id`, `event_type`, `playlist_item_id`, `user_id`, `event_detail`, `created_at`, `updated_at`) "
                + "VALUES (@room_id, @event_type, @playlist_item_id, @user_id, @event_detail, NOW(), NOW())",
                ev);
        }

        public void Dispose()
        {
            lock (openedConnections)
            {
                foreach (var connection in openedConnections)
                {
                    try
                    {
                        connection.Dispose();
                    }
                    catch
                    {
                        // ya disposeada (via `await using`) o en mal estado: idempotente, la ignoramos.
                    }
                }

                openedConnections.Clear();
            }
        }


        private async Task<MySqlConnection> getConnectionAsync()
        {
            // Install Dapper mapper once (thread-safe)
            if (!dapperMapperInstalled)
            {
                lock (dapperMapperLock)
                {
                    if (!dapperMapperInstalled)
                    {
                        DapperExtensions.InstallDateTimeOffsetMapper();
                        DapperExtensions.InstallToriiEnumMappers();
                        dapperMapperInstalled = true;
                    }
                }
            }

            string connectionString =
                $"Server={AppSettings.DatabaseHost};" +
                $"Port={AppSettings.DatabasePort};" +
                $"Database={AppSettings.DatabaseName};" +
                $"User ID={AppSettings.DatabaseUser};" +
                (string.IsNullOrEmpty(AppSettings.DatabasePassword)
                    ? ""
                    : $"Password={AppSettings.DatabasePassword};") +
                "ConnectionTimeout=15;" +
                "Pooling=true;" +
                "MinPoolSize=0;" +
                // Bumped from 50 → 200. Cascading failures in the matchmaking
                // background service (FK violations, room-creation rollbacks)
                // can transiently spike connection demand; 50 was tight enough
                // that ANY recoverable burst would exhaust the pool and starve
                // unrelated SignalR calls (queue join, lobby join). 200 has
                // headroom without any meaningful resource cost on local /
                // single-server deployments.
                "MaximumPoolSize=200;";

            var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync();

            lock (openedConnections)
                openedConnections.Add(connection);

            return connection;
        }

        // -------------------------------------------------------------------
        //  Upstream surface (matchmaking + ranked-play + adjacent helpers)
        // -------------------------------------------------------------------
        //  Methods lifted verbatim from ppy/osu-server-spectator master so the
        //  matchmaking partials, MultiplayerEventDispatcher, and ranked-play
        //  stage machine compile against this DAO. Table refs (multiplayer_rooms,
        //  phpbb_users, osu_beatmaps, oauth_access_tokens, etc) point at osu-web's
        //  schema and must be retargeted to g0v0's tables (rooms, lazer_users,
        //  beatmaps, oauth_tokens) before any of these matchmaking flows actually
        //  run successfully. Until then they fail at the SQL layer, never at
        //  compile time, and the rest of the spectator works exactly as it does
        //  today against g0v0.

        public async Task<int?> GetDelegatedResourceOwnerIdFromTokenAsync(JsonWebToken jwtToken)
        {
            // Path 1B Phase 1: see GetUserIdFromTokenAsync. g0v0's endpoint
            // is also a stub (always returns null) — symmetric with this
            // legacy path which also returns null. Keeping the route through
            // the backend lets future delegation logic land server-side
            // without touching the spectator.
            if (AppSettings.UseHttpDaoAuth)
                return await backend.ResolveDelegatedResourceOwnerIdFromTokenAsync(jwtToken);

            // Torii: g0v0's OAuth table is `oauth_tokens` (not osu-web's
            // `oauth_access_tokens`), columns are slightly different too:
            //   - no `revoked` flag (deletions hard-delete the row)
            //   - `scope` is a space-separated VARCHAR(100) (osu-web has a JSON
            //     array column called `scopes`)
            //   - `id` is auto-incrementing int, the JWT's `jti` claim is a
            //     string identifier we don't currently persist
            //
            // Practical impact: the only caller is the pubsub/legacy delegated-
            // auth flow which Torii doesn't run. Returning null disables the
            // delegation lookup cleanly without crashing on a missing table.
            await using var _ = await getConnectionAsync();
            return null;
        }


        public async Task<int[]> GetUsersInGroupsAsync(int[] groupIds)
        {
            // Path 1B Phase 1: see GetUserIdFromTokenAsync. g0v0's endpoint
            // is also a stub (always returns empty) — same shape as below.
            if (AppSettings.UseHttpDaoAuth)
                return await backend.GetUsersInGroupsAsync(groupIds);

            // Torii: g0v0 doesn't have a `phpbb_user_group` table — group
            // membership is tracked via the `lazer_users.is_supporter` /
            // `is_admin` / `priv` flags directly on the user row. The only
            // current caller is the version-check exemption hook (used when
            // ClientCheckVersion is enabled) which is off by default for Torii.
            // Return an empty array so the caller's `Contains()` check just
            // says "no exemptions" rather than crashing on a missing table.
            await using var _ = await getConnectionAsync();
            return [];
        }

        public async Task<database_beatmap[]> GetBeatmapsAsync(int[] beatmapIds)
        {
            // Torii: retarget `osu_beatmaps` -> g0v0's `beatmaps`. Same column
            // remapping as GetBeatmapAsync (id->beatmap_id, beatmap_status->
            // approved, mode->playmode literal, hardcoded osu_file_version=14).
            var connection = await getConnectionAsync();

            return (await connection.QueryAsync<database_beatmap>(
                @"SELECT
                    id AS beatmap_id,
                    beatmapset_id,
                    checksum,
                    beatmap_status AS approved,
                    difficulty_rating AS difficultyrating,
                    CASE
                        WHEN LOWER(mode) = 'osu' THEN 0
                        WHEN LOWER(mode) = 'taiko' THEN 1
                        WHEN LOWER(mode) = 'fruits' THEN 2
                        WHEN LOWER(mode) = 'mania' THEN 3
                        WHEN LOWER(mode) = 'osurx' THEN 4
                        WHEN LOWER(mode) = 'osuap' THEN 5
                        WHEN LOWER(mode) = 'taikorx' THEN 6
                        WHEN LOWER(mode) = 'fruitsrx' THEN 7
                        ELSE 0
                    END AS playmode,
                    14 AS osu_file_version
                  FROM beatmaps
                  WHERE id IN @BeatmapIds AND deleted_at IS NULL", new
                {
                    BeatmapIds = beatmapIds
                })).ToArray();
        }


        public async Task SetRoomEndDateAsync(MultiplayerRoom room, DateTimeOffset? endDate)
        {
            // Torii: g0v0's room table is `rooms` (not osu-web's `multiplayer_rooms`).
            // Called from MultiplayerRoomController when a user joins/leaves a tournament-mode
            // room — Torii doesn't run tournament rooms (TournamentMode is always false here),
            // so this is mostly a no-op in practice but the schema rename keeps the build
            // future-proof if/when tournament support lands.
            var connection = await getConnectionAsync();

            await connection.ExecuteAsync("UPDATE rooms SET ends_at = @EndDate WHERE id = @RoomID", new
            {
                RoomID = room.RoomID,
                EndDate = endDate
            });
        }

        public async Task<osu_build?> GetBuildByHashAsync(string hash)
        {
            // Torii: g0v0 has no `osu_builds` table — client build tracking is
            // handled out-of-band by ToriiClientNameResolver via a small webhook
            // endpoint instead. Returning null short-circuits the upstream
            // version-check pipeline (which is also disabled by default via
            // CLIENT_CHECK_VERSION=false) without crashing on a missing table.
            await using var _ = await getConnectionAsync();
            return null;
        }


        public async Task<bool> AnyScoreTokenExistsFor(long roomId, long playlistItemId)
        {
            // Torii: g0v0 doesn't have osu-web's `multiplayer_score_links`. The (item → finalised score)
            // mapping lives on `score_tokens` rows where `score_id IS NOT NULL`. Rows with `score_id IS NULL`
            // are in-progress / abandoned plays and must not block removal of the playlist item.
            //
            // CRITICAL: `score_tokens.playlist_item_id` is ROOM-SCOPED (the logical id from
            // `room_playlists.id`, not the global `room_playlists.db_id`). The same logical id
            // exists in every room. Without scoping by `room_id`, this query counts scores from
            // EVERY ROOM that ever had a playlist item with this logical id — which made removing
            // even fresh items in newer rooms fail because the older rooms' scores raced the count
            // up. Observed in prod with room 181 item 1: 69 score_tokens existed for `playlist_item_id=1`
            // globally (all from older rooms), so the guard fired and Remove silently no-op'd.
            // Filter by `room_id` so the count only sees scores for THIS room's item.
            var connection = await getConnectionAsync();

            var scoreTokenCount = await connection.QuerySingleAsync<long>(
                "SELECT COUNT(1) FROM `score_tokens` WHERE `room_id` = @roomId AND `playlist_item_id` = @playlistItemId AND `score_id` IS NOT NULL",
                new { roomId, playlistItemId });

            return scoreTokenCount > 0;
        }

        /// <summary>
        /// Retrieves ALL score data for scores on a playlist item.
        /// </summary>
        /// <remarks>
        /// This should be used sparingly as it queries full rows.
        /// </remarks>

        public async Task<IEnumerable<SoloScore>> GetAllScoresForPlaylistItem(long roomId, long playlistItemId)
        {
            // Torii: g0v0 doesn't have osu-web's `multiplayer_score_links` join table.
            // The (item → score) mapping lives on `score_tokens.playlist_item_id` instead,
            // and only rows whose `score_id` is non-null have actually been finalised.
            // OJO CRITICO: g0v0 usa `room_playlists.id` LOCAL por sala (0,1,2 por room),
            // NO un id global. Asi que `playlist_item_id` COLISIONA entre salas (el "item 0"
            // de cada match tiene el mismo id). Sin filtrar por room_id, el ResultsStage
            // juntaba scores de TODOS los matches -> maxScore con el max historico -> los dos
            // morian -> DRAW eterno, y el wait nunca cortaba (esperaba 10s siempre -> lock
            // timeout de 5s -> ChangeState error -> desconexiones). Filtrar por room_id lo
            // aisla al match actual y arregla las dos cosas de un saque.
            var connection = await getConnectionAsync();

            return (await connection.QueryAsync<SoloScore>(
                "SELECT `scores`.* FROM `scores` "
                + "JOIN `score_tokens` ON `score_tokens`.`score_id` = `scores`.`id` "
                + "WHERE `score_tokens`.`room_id` = @roomId "
                + "  AND `score_tokens`.`playlist_item_id` = @playlistItemId "
                + "  AND `score_tokens`.`score_id` IS NOT NULL", new
                {
                    roomId = roomId,
                    playlistItemId = playlistItemId
                }));
        }

        /// <summary>
        /// Retrieves the <see cref="SoloScore.id">ID</see> and <see cref="SoloScore.total_score">total score</see> for passing scores on a playlist item.
        /// Retrieves the passing score ids and total scores on a playlist item.
        /// </summary>
        /// <param name="playlistItemId">The playlist item.</param>

        public async Task<multiplayer_scores_high?> GetUserBestScoreAsync(long roomId, long playlistItemId, int userId)
        {
            // Torii: g0v0 stores per-playlist-item best scores in `playlist_best_scores`
            // (column `playlist_id`) rather than osu-web's `multiplayer_scores_high`
            // (column `playlist_item_id`). Column-aliasing in the SELECT lets Dapper map
            // back into the upstream POCO without us having to maintain a parallel type.
            //
            // The g0v0 table doesn't carry `accuracy` / `pp` / `id` / `created_at` /
            // `updated_at` — they're left at default values on the returned object. The
            // only consumer (ScoreProcessedSubscriber) just compares `score_id` against
            // the freshly-submitted score id, so the missing columns are inert.
            var connection = await getConnectionAsync();

            return await connection.QuerySingleOrDefaultAsync<multiplayer_scores_high>(
                "SELECT "
                + "  `score_id` AS `id`, "
                + "  `score_id` AS `score_id`, "
                + "  `user_id` AS `user_id`, "
                + "  `playlist_id` AS `playlist_item_id`, "
                + "  `total_score` AS `total_score`, "
                + "  `attempts` AS `attempts` "
                + "FROM `playlist_best_scores` "
                + "WHERE `room_id` = @roomId AND `playlist_id` = @playlistItemId AND `user_id` = @userId",
                new
                {
                    roomId = roomId,
                    playlistItemId = playlistItemId,
                    userId = userId
                });
        }

        public async Task<int> GetUserRankInRoomAsync(long roomId, int userId)
        {
            // Torii: g0v0's per-room ranking is computed from `playlist_best_scores`.
            // The semantic upstream wanted ("how many users beat this user's best in
            // this room") is "1 + count of users in the room with a higher total than
            // this user's max total". Multi-playlist-item rooms aggregate by user via
            // SUM, which matches how the daily-challenge / quick-play ranking surfaces
            // already work on g0v0.
            //
            // The osu-web variant additionally filters out restricted / banned users
            // via `phpbb_users.user_type = 0` + `user_warnings = 0`. On Torii that's
            // covered separately by the `IsUserRestrictedAsync` gate (admin-side
            // restriction prevents the user from connecting at all), so we don't
            // duplicate the filter at query time.
            var connection = await getConnectionAsync();

            return await connection.QuerySingleAsync<int>(
                "WITH `user_total` AS ( "
                + "    SELECT COALESCE(SUM(`total_score`), 0) AS total FROM `playlist_best_scores` "
                + "    WHERE `room_id` = @roomId AND `user_id` = @userId "
                + ") "
                + "SELECT 1 + COUNT(*) FROM ( "
                + "    SELECT `user_id`, SUM(`total_score`) AS user_total FROM `playlist_best_scores` "
                + "    WHERE `room_id` = @roomId AND `user_id` != @userId "
                + "    GROUP BY `user_id` "
                + ") opponents "
                + "WHERE opponents.user_total > (SELECT total FROM `user_total`)",
                new
                {
                    roomId = roomId,
                    userId = userId,
                });
        }

        public async Task LogRoomEventAsync(matchmaking_room_event ev)
        {
            var connection = await getConnectionAsync();

            await connection.ExecuteAsync(
                "INSERT INTO `matchmaking_room_events` (`room_id`, `event_type`, `playlist_item_id`, `user_id`, `event_detail`, `created_at`, `updated_at`) "
                + "VALUES (@room_id, @event_type, @playlist_item_id, @user_id, @event_detail, NOW(), NOW())",
                ev);
        }

        public async Task ToggleUserPresenceAsync(int userId, bool visible)
        {
            // Torii: g0v0 stores online presence on `lazer_users.is_online` rather than
            // osu-web's `phpbb_users.user_allow_viewonline`. Both are queried by the same
            // user-search / friends-online endpoints — flipping this column is what makes
            // "appears offline" actually appear offline in the website's user lookups.
            //
            // The "live currently-connected" signal on Torii is a separate redis key
            // (`metadata:online:{userId}`) which the metadata hub manages on connect /
            // disconnect; this column is just the user-controlled visibility preference.
            var connection = await getConnectionAsync();

            await connection.ExecuteAsync(
                "UPDATE `lazer_users` SET `is_online` = @visible WHERE `id` = @userId",
                new
                {
                    visible = visible,
                    userId = userId
                });
        }

        public async Task<float> GetUserPPAsync(int userId, int rulesetId, int variant)
        {
            // Torii: g0v0 keeps every ruleset's stats in a single
            // `lazer_user_statistics` table keyed by (user_id, mode), where
            // `mode` is the GameMode string-enum ('OSU','TAIKO','FRUITS', ...).
            // No per-keymode (4k/7k) stat split — mania pp is whatever's in
            // the `MANIA` row, mania-4k/7k variants of the matchmaking
            // ranking aren't supported on Torii (matchmaking itself isn't
            // wired up yet). The pp column is `pp` (osu-web called it
            // `rank_score`).
            string mode = rulesetId switch
            {
                0 => "OSU",
                1 => "TAIKO",
                2 => "FRUITS",
                3 => "MANIA",
                4 => "OSURX",
                5 => "OSUAP",
                6 => "TAIKORX",
                7 => "FRUITSRX",
                _ => throw new ArgumentOutOfRangeException(nameof(rulesetId), rulesetId, null)
            };

            var connection = await getConnectionAsync();

            return await connection.QuerySingleOrDefaultAsync<float>(
                "SELECT `pp` FROM `lazer_user_statistics` WHERE `user_id` = @userId AND `mode` = @mode",
                new
                {
                    userId = userId,
                    mode = mode
                });
        }

        public async Task<matchmaking_pool[]> GetActiveMatchmakingPoolsAsync()
        {
            var connection = await getConnectionAsync();

            return (await connection.QueryAsync<matchmaking_pool>("SELECT * FROM `matchmaking_pools` WHERE `active` = 1")).ToArray();
        }

        public async Task<matchmaking_pool?> GetMatchmakingPoolAsync(uint poolId)
        {
            var connection = await getConnectionAsync();

            return await connection.QuerySingleOrDefaultAsync<matchmaking_pool>("SELECT * FROM `matchmaking_pools` WHERE `id` = @PoolId", new
            {
                PoolId = poolId
            });
        }

        public async Task<matchmaking_pool_beatmap[]> GetMatchmakingPoolBeatmapsAsync(uint poolId)
        {
            // Torii: matchmaking is dormant (no UI, no migration applied) so
            // this currently returns an empty set in practice, but retarget
            // the JOIN to g0v0's `beatmaps` table (`id` column, no `playmode`
            // -- decode mode enum to int via CASE, same as GetBeatmapAsync).
            var connection = await getConnectionAsync();

            return (await connection.QueryAsync<matchmaking_pool_beatmap>(
                @"SELECT p.*,
                         CASE
                             WHEN b.mode = 'osu' THEN 0
                             WHEN b.mode = 'taiko' THEN 1
                             WHEN b.mode = 'fruits' THEN 2
                             WHEN b.mode = 'mania' THEN 3
                             ELSE 0
                         END AS playmode,
                         b.checksum,
                         b.difficulty_rating AS difficultyrating
                  FROM `matchmaking_pool_beatmaps` p
                  JOIN `beatmaps` b ON p.beatmap_id = b.id
                  WHERE p.pool_id = @PoolId", new
            {
                PoolId = poolId
            })).ToArray();
        }

        public async Task<matchmaking_pool_beatmap?> GetMatchmakingPoolBeatmapAsync(uint poolId, int beatmapId, string mods)
        {
            var connection = await getConnectionAsync();

            return await connection.QuerySingleOrDefaultAsync<matchmaking_pool_beatmap>(
                @"SELECT p.*,
                         CASE
                             WHEN b.mode = 'osu' THEN 0
                             WHEN b.mode = 'taiko' THEN 1
                             WHEN b.mode = 'fruits' THEN 2
                             WHEN b.mode = 'mania' THEN 3
                             ELSE 0
                         END AS playmode,
                         b.checksum,
                         b.difficulty_rating AS difficultyrating
                  FROM `matchmaking_pool_beatmaps` p
                  JOIN `beatmaps` b ON p.beatmap_id = b.id
                  WHERE p.pool_id = @PoolId
                    AND p.beatmap_id = @BeatmapId
                    -- `mods` es una columna JSON. Comparar contra el parametro string directo
                    -- (`p.mods = '[]'`) NUNCA matchea en MySQL: el `[]` guardado es JSON y el
                    -- parametro es texto, y `=` no auto-castea -> 0 filas para TODOS los mapas
                    -- curados (guardan mods json `[]`). Eso hacia caer el lookup a null en cada
                    -- ronda -> GlobalBeatmaps[id] (que no tiene curados) tiraba y abortaba el
                    -- results stage. CAST normaliza ambos lados a JSON (ademas tolera orden de
                    -- claves / whitespace en mods reales tipo `[{""acronym"":""HD""}]`).
                    AND p.mods = CAST(@Mods AS JSON)", new
            {
                PoolId = poolId,
                BeatmapId = beatmapId,
                Mods = mods
            });
        }

        public async Task FlagMatchmakingBeatmapAsync(uint poolId, int beatmapId)
        {
            var conn = await getConnectionAsync();

            // upsert: primer flag inserta con count=1, los siguientes incrementan. UNIQUE(pool_id, beatmap_id).
            await conn.ExecuteAsync(
                @"INSERT INTO `matchmaking_flagged_beatmaps` (pool_id, beatmap_id, flagged_count, last_flagged_at)
                  VALUES (@PoolId, @BeatmapId, 1, NOW())
                  ON DUPLICATE KEY UPDATE flagged_count = flagged_count + 1, last_flagged_at = NOW()", new
                {
                    PoolId = poolId,
                    BeatmapId = beatmapId
                });
        }

        public async Task<int[]> GetFlaggedMatchmakingBeatmapIdsAsync(uint poolId, int threshold)
        {
            var conn = await getConnectionAsync();

            return (await conn.QueryAsync<int>(
                "SELECT beatmap_id FROM `matchmaking_flagged_beatmaps` WHERE pool_id = @PoolId AND flagged_count >= @Threshold", new
                {
                    PoolId = poolId,
                    Threshold = threshold
                })).ToArray();
        }

        public async Task UpdateMatchmakingPoolBeatmapRatingAsync(matchmaking_pool_beatmap beatmap)
        {
            var conn = await getConnectionAsync();

            await conn.ExecuteAsync("INSERT INTO `matchmaking_pool_beatmaps` (pool_id, beatmap_id, mods, rating, rating_sig, selection_count) "
                                    + "VALUES (@PoolId, @BeatmapId, @Mods, @Rating, @RatingSig, @SelectionCount) "
                                    + "ON DUPLICATE KEY UPDATE rating = @Rating, rating_sig = @RatingSig", new
            {
                PoolId = beatmap.pool_id,
                BeatmapId = beatmap.beatmap_id,
                // el pool global (backfill FA) arma beatmaps sin mods -> "" rompe la
                // columna JSON de mysql al escribir el rating. normalizamos a "[]".
                Mods = string.IsNullOrEmpty(beatmap.mods) ? "[]" : beatmap.mods,
                Rating = beatmap.rating,
                RatingSig = beatmap.rating_sig,
                // selection_count no tiene default en la DB -> sin esto el INSERT de un
                // mapa nuevo (backfill FA) tira "doesn't have a default value" y tumba
                // el background service -> StopHost -> se reinicia el server ENTERO.
                SelectionCount = beatmap.selection_count
            });
        }

        public async Task<database_beatmap[]> GetMatchmakingGlobalPoolBeatmapsAsync(int rulesetId, int variant)
        {
            var connection = await getConnectionAsync();

            string variantString = string.Empty;

            if (rulesetId == 3)
                variantString = "AND b.diff_size = @Variant";

            // - From the featured artist listing
            // - Non-converted beatmaps
            // - Ranked status
            // - Between 1 and 4 minutes in length
            // - With the correct keymode (if mania)
            // Torii: retarget osu-web's `osu_beatmaps` + `osu_beatmapsets`
            // to g0v0's `beatmaps` + `beatmapsets`. Column remapping:
            //   beatmap_id -> id
            //   playmode -> mode (string enum, decoded via CASE)
            //   difficultyrating -> difficulty_rating
            //   approved -> beatmap_status (g0v0's column has the same int
            //     enum semantics, so the BETWEEN range still works)
            //   hit_length -> total_length (g0v0 keeps only `total_length`;
            //     this very slightly inflates length-clipping, but the matchmaking
            //     pool selector treats this as a soft cap so it's fine)
            //   s.track_id -> beatmapsets.track_id (same column on g0v0)
            //   s.download_disabled_url -> beatmapsets has no equivalent;
            //     drop the filter (Torii doesn't honour upstream's "download
            //     disabled" concept anyway).
            // The mania variant filter on `b.diff_size` doesn't exist on g0v0
            // (no per-key-mode column); the matchmaker simply ignores variant
            // for mania pool selection.
            return (await connection.QueryAsync<database_beatmap>(
                @"SELECT b.id AS beatmap_id,
                         b.beatmapset_id,
                         b.checksum,
                         b.beatmap_status AS approved,
                         b.difficulty_rating AS difficultyrating,
                         CASE
                             WHEN b.mode = 'osu' THEN 0
                             WHEN b.mode = 'taiko' THEN 1
                             WHEN b.mode = 'fruits' THEN 2
                             WHEN b.mode = 'mania' THEN 3
                             ELSE 0
                         END AS playmode,
                         14 AS osu_file_version
                  FROM `beatmaps` b
                  JOIN `beatmapsets` s ON s.id = b.beatmapset_id
                  WHERE s.track_id IS NOT NULL
                    AND b.mode = (CASE @RulesetId
                                  WHEN 0 THEN 'osu'
                                  WHEN 1 THEN 'taiko'
                                  WHEN 2 THEN 'fruits'
                                  WHEN 3 THEN 'mania'
                                  ELSE 'osu' END)
                    AND b.deleted_at IS NULL
                    AND b.beatmap_status BETWEEN 1 AND 2
                    AND b.total_length BETWEEN 60 AND 300",
                new
                {
                    RulesetId = rulesetId,
                    Variant = variant
                })).ToArray();
        }

        public async Task<matchmaking_user_stats?> GetMatchmakingUserStatsAsync(int userId, uint poolId)
        {
            var connection = await getConnectionAsync();

            return await connection.QuerySingleOrDefaultAsync<matchmaking_user_stats>("SELECT * FROM `matchmaking_user_stats` WHERE `user_id` = @UserId AND `pool_id` = @PoolId", new
            {
                UserId = userId,
                PoolId = poolId
            });
        }

        public async Task UpdateMatchmakingUserStatsAsync(matchmaking_user_stats stats)
        {
            var connection = await getConnectionAsync();

            await connection.ExecuteAsync("INSERT INTO `matchmaking_user_stats` (`user_id`, `pool_id`, `first_placements`, `total_points`, `elo_data`, `rating`, `plays`, `created_at`, `updated_at`) "
                                          + "VALUES (@UserId, @PoolId, @FirstPlacements, @TotalPoints, @EloData, @Rating, @Plays, NOW(), NOW()) "
                                          + "ON DUPLICATE KEY UPDATE "
                                          + "`first_placements` = @FirstPlacements, "
                                          + "`total_points` = @TotalPoints, "
                                          + "`elo_data` = @EloData, "
                                          + "`rating` = @Rating, "
                                          + "`plays` = @Plays, "
                                          + "`updated_at` = NOW()", new
            {
                UserId = stats.user_id,
                PoolId = stats.pool_id,
                FirstPlacements = stats.first_placements,
                TotalPoints = stats.total_points,
                EloData = stats.elo_data,
                // el rating int se deriva SIEMPRE del mu de OpenSkill (cubre seed y match).
                Rating = (int)Math.Round(stats.EloData.Rating.Mu),
                Plays = stats.plays
            });
        }

        public async Task InsertUserEloHistoryEntry(ulong roomId, uint poolId, uint userId, uint opponentId, matchmaking_room_result result, int eloBefore, int eloAfter)
        {
            var connection = await getConnectionAsync();

            await connection.ExecuteAsync("INSERT INTO `matchmaking_user_elo_history` (room_id, pool_id, user_id, opponent_id, result, elo_before, elo_after, created_at, updated_at) "
                                          + "VALUES (@RoomId, @PoolId, @UserId, @OpponentId, @Result, @EloBefore, @EloAfter, NOW(), NOW())", new
            {
                RoomId = roomId,
                PoolId = poolId,
                UserId = userId,
                OpponentId = opponentId,
                Result = result.ToString(),
                EloBefore = eloBefore,
                EloAfter = eloAfter
            });
        }

        public async Task<int[]> GetMatchmakingPoolRatingsAsync(uint poolId)
        {
            var connection = await getConnectionAsync();

            return (await connection.QueryAsync<int>("SELECT rating FROM matchmaking_user_stats WHERE pool_id = @PoolId AND plays > 0", new
            {
                PoolId = poolId
            })).ToArray();
        }
    }
}
