// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.IdentityModel.JsonWebTokens;
using osu.Game.Online.Multiplayer;
using osu.Game.Scoring;
using osu.Server.Spectator.Database.Models;

namespace osu.Server.Spectator.Database
{
    public interface IDatabaseAccess : IDisposable
    {
        /// <summary>
        /// Returns the database ID of the user to whom the supplied <paramref name="jwtToken"/> belongs.
        /// Will be <c>null</c> if the token does not exist, has expired or has been revoked.
        /// </summary>
        Task<int?> GetUserIdFromTokenAsync(JsonWebToken jwtToken);

        /// <summary>
        /// Whether the user with the given <paramref name="userId"/> is currently restricted.
        /// </summary>
        Task<bool> IsUserRestrictedAsync(int userId);

        /// <summary>
        /// Returns a username from a <paramref name="userId"/>.
        /// </summary>
        Task<string?> GetUsernameAsync(int userId);

        /// <summary>
        /// Returns the <see cref="multiplayer_room"/> with the given <paramref name="roomId"/>.
        /// </summary>
        Task<multiplayer_room?> GetRoomAsync(long roomId);

        /// <summary>
        /// Returns the <see cref="multiplayer_room"/> with the given <paramref name="roomId"/>.
        /// Rooms of type <see cref="database_match_type.playlists"/> are not returned by this method.
        /// </summary>
        Task<multiplayer_room?> GetRealtimeRoomAsync(long roomId);

        /// <summary>
        /// Retrieves a beatmap corresponding to the given <paramref name="beatmapId"/>.
        /// </summary>
        Task<database_beatmap?> GetBeatmapAsync(int beatmapId);

        /// <summary>
        /// Retrieves all beatmaps corresponding to the given <paramref name="beatmapSetId"/>.
        /// </summary>
        Task<database_beatmap[]> GetBeatmapsAsync(int beatmapSetId);

        /// <summary>
        /// Marks the given <paramref name="room"/> as active and accepting new players.
        /// </summary>
        Task MarkRoomActiveAsync(MultiplayerRoom room);

        /// <summary>
        /// Updates the current settings of <paramref name="room"/> in the database.
        /// </summary>
        Task UpdateRoomSettingsAsync(MultiplayerRoom room);

        /// <summary>
        /// Updates the current status of <paramref name="room"/> in the database.
        /// </summary>
        Task UpdateRoomStatusAsync(MultiplayerRoom room);

        /// <summary>
        /// Updates the current host of <paramref name="room"/> in the database.
        /// </summary>
        Task UpdateRoomHostAsync(MultiplayerRoom room);

        /// <summary>
        /// Add a new participant for the specified <paramref name="room"/> in the database.
        /// </summary>
        Task AddRoomParticipantAsync(MultiplayerRoom room, MultiplayerRoomUser user);

        /// <summary>
        /// Adds a login entry for the specified user.
        /// </summary>
        Task AddLoginForUserAsync(int userId, string? userIp);

        Task OfflineUser(int userId);

        /// <summary>
        /// Remove a new participant for the specified <paramref name="room"/> in the database.
        /// </summary>
        Task RemoveRoomParticipantAsync(MultiplayerRoom room, MultiplayerRoomUser user);

        /// <summary>
        /// Retrieves a playlist item from a room.
        /// </summary>
        /// <param name="roomId">The room.</param>
        /// <param name="playlistItemId">The playlist item.</param>
        Task<multiplayer_playlist_item> GetPlaylistItemAsync(long roomId, long playlistItemId);

        /// <summary>
        /// Creates a new playlist item.
        /// </summary>
        /// <returns>The playlist item ID.</returns>
        Task<long> AddPlaylistItemAsync(multiplayer_playlist_item item);

        /// <summary>
        /// Updates an existing playlist item.
        /// </summary>
        /// <param name="item">The new playlist item settings.</param>
        Task UpdatePlaylistItemAsync(multiplayer_playlist_item item);

        /// <summary>
        /// Removes a playlist item.
        /// </summary>
        /// <param name="roomId">The room.</param>
        /// <param name="playlistItemId">The playlist item ID to remove.</param>
        Task RemovePlaylistItemAsync(long roomId, long playlistItemId);

        /// <summary>
        /// Marks a playlist item as having been played.
        /// </summary>
        Task MarkPlaylistItemAsPlayedAsync(long roomId, long playlistItemId);

        /// <summary>
        /// Marks the given <paramref name="room"/> as ended and no longer accepting new players or scores.
        /// </summary>
        Task EndMatchAsync(MultiplayerRoom room);

        /// <summary>
        /// Retrieves all playlist items.
        /// </summary>
        /// <param name="roomId">The room to retrieve playlist items from.</param>
        Task<multiplayer_playlist_item[]> GetAllPlaylistItemsAsync(long roomId);

        /// <summary>
        /// Mark a score as having a replay available.
        /// </summary>
        /// <param name="score">The score to mark.</param>
        Task MarkScoreHasReplay(Score score);

        /// <summary>
        /// Retrieves the <see cref="SoloScore"/> for a given score token. Will return null while the score has not yet been submitted.
        /// </summary>
        /// <param name="token">The score token.</param>
        /// <returns>The <see cref="SoloScore"/>.</returns>
        Task<SoloScore?> GetScoreFromTokenAsync(long token);

        /// <summary>
        /// Returns the <see cref="SoloScore"/> for the given ID.
        /// </summary>
        Task<SoloScore?> GetScoreAsync(long scoreId);

        /// <summary>
        /// Returns <see langword="true"/> if the score with the supplied <paramref name="scoreId"/> has been successfully processed.
        /// </summary>
        Task<bool> IsScoreProcessedAsync(long scoreId);

        /// <summary>
        /// Returns information about if the user with the supplied <paramref name="zebraId"/> has been added as a friend or blocked by the user with the supplied <paramref name="userId"/>.
        /// </summary>
        Task<phpbb_zebra?> GetUserRelation(int userId, int zebraId);

        /// <summary>
        /// Lists the specified user's friends.
        /// </summary>
        Task<IEnumerable<int>> GetUserFriendsAsync(int userId);

        /// <summary>
        /// Returns <see langword="true"/> if the user with the supplied <paramref name="userId"/> allows private messages from people not on their friends list.
        /// </summary>
        Task<bool> GetUserAllowsPMs(int userId);

        /// <summary>
        /// Returns a single build with the given ID.
        /// </summary>
        /// <param name="buildId"></param>
        /// <returns></returns>
        Task<osu_build?> GetBuildByIdAsync(int buildId);

        /// <summary>
        /// Returns all available main builds from the lazer and tachyon release streams.
        /// </summary>
        Task<IEnumerable<osu_build>> GetAllMainLazerBuildsAsync();

        /// <summary>
        /// Returns all known platform-specifc lazer and tachyon builds.
        /// </summary>
        Task<IEnumerable<osu_build>> GetAllPlatformSpecificLazerBuildsAsync();

        /// <summary>
        /// Updates the <see cref="osu_build.users"/> count of a given <paramref name="build"/>.
        /// </summary>
        Task UpdateBuildUserCountAsync(osu_build build);

        /// <summary>
        /// Retrieves all <see cref="chat_filter"/>s from the database.
        /// </summary>
        Task<IEnumerable<chat_filter>> GetAllChatFiltersAsync();

        /// <summary>
        /// Retrieves all active rooms from the <see cref="room_category.daily_challenge"/> category.
        /// </summary>
        Task<IEnumerable<multiplayer_room>> GetActiveDailyChallengeRoomsAsync();

        /// <summary>
        /// If <paramref name="scoreId"/> is associated with a multiplayer score, returns the room ID and playlist item ID which the score was set on.
        /// Otherwise, returns <see langword="null"/>.
        /// </summary>
        Task<(long roomID, long playlistItemID)?> GetMultiplayerRoomIdForScoreAsync(long scoreId);

        /// <summary>
        /// Retrieve all passing scores for a specified playlist item.
        /// </summary>
        /// <param name="roomId"></param>
        /// <param name="playlistItemId">The playlist item.</param>
        /// <param name="afterScoreId">An optional score ID to only fetch newer scores.</param>
        /// <returns></returns>
        Task<IEnumerable<SoloScore>> GetPassingScoresForPlaylistItem(long roomId, long playlistItemId, ulong afterScoreId = 0UL);

        // GetUserBestScoreAsync / GetUserRankInRoomAsync live in the upstream-surface block
        // below — the older prod-style 3-arg signatures targeting playlist_best_scores are
        // gone in favour of the upstream 2-arg `multiplayer_scores_high`-based versions
        // that ScoreProcessedSubscriber actually calls.

        /// <summary>
        /// Logs an event that happened in a multiplayer room.
        /// </summary>
        Task LogRoomEventAsync(multiplayer_realtime_room_event ev);

        /// <summary>
        /// 预确保谱面存在。
        /// </summary>
        /// <remarks>
        ///  This will queue a background job to download the beatmap if it does not already exist.
        /// </remarks>
        Task<database_beatmap?> GetBeatmapOrFetchAsync(int beatmapId);

        Task<fail_time?> GetBeatmapFailTimeAsync(int beatmapId);

        Task UpdateFailTimeAsync(fail_time failTime);

        Task<int?> GetUserPlaytimeAsync(string gamemode, int userId);
        Task UpdateUserPlaytimeAsync(string gamemode, int userId, int playTime);

        // ────────────────────────────────────────────────────────────────────
        //  Upstream surface (matchmaking + ranked-play + adjacent helpers)
        // ────────────────────────────────────────────────────────────────────
        //  Method signatures lifted verbatim from `ppy/osu-server-spectator/master`
        //  (commit ca1a7e1 "Implement duels for ranked play"). The matchmaking_*
        //  tables backing these calls live behind alembic migration c4d5e6f7a8b9
        //  on the g0v0 side; until that migration is applied in prod, every
        //  matchmaking-targeted hub method will fail at the SQL layer rather
        //  than at compile time.

        /// <summary>
        /// Resolves a delegated client-credentials JWT to its resource-owner user id.
        /// Used by the referee scheme (Torii doesn't run referee, so this is dormant
        /// in practice but the interface still requires it).
        /// </summary>
        Task<int?> GetDelegatedResourceOwnerIdFromTokenAsync(JsonWebToken jwtToken);

        Task<int[]> GetUsersInGroupsAsync(int[] groupIds);

        Task<osu_build?> GetBuildByHashAsync(string hash);

        Task<bool> AnyScoreTokenExistsFor(long playlistItemId);

        Task<IEnumerable<SoloScore>> GetAllScoresForPlaylistItem(long playlistItemId);

        Task<multiplayer_scores_high?> GetUserBestScoreAsync(long playlistItemId, int userId);

        Task<int> GetUserRankInRoomAsync(long roomId, int userId);

        Task<float> GetUserPPAsync(int userId, int rulesetId, int variant);

        Task<matchmaking_pool[]> GetActiveMatchmakingPoolsAsync();

        Task<matchmaking_pool?> GetMatchmakingPoolAsync(uint poolId);

        Task<matchmaking_pool_beatmap[]> GetMatchmakingPoolBeatmapsAsync(uint poolId);

        Task<matchmaking_pool_beatmap?> GetMatchmakingPoolBeatmapAsync(uint poolId, int beatmapId, string mods);

        Task UpdateMatchmakingPoolBeatmapRatingAsync(matchmaking_pool_beatmap beatmap);

        Task<database_beatmap[]> GetMatchmakingGlobalPoolBeatmapsAsync(int rulesetId, int variant);

        Task<matchmaking_user_stats?> GetMatchmakingUserStatsAsync(int userId, uint poolId);

        Task UpdateMatchmakingUserStatsAsync(matchmaking_user_stats stats);

        Task InsertUserEloHistoryEntry(ulong roomId, uint poolId, uint userId, uint opponentId, matchmaking_room_result result, int eloBefore, int eloAfter);

        Task<int[]> GetMatchmakingPoolRatingsAsync(uint poolId);

        /// <summary>
        /// Visibility toggle on the user's web-side presence (used by the
        /// "appears offline" status). The g0v0 backend mirrors osu!web's
        /// <c>users.user_allow_presence</c> column, so the implementation just
        /// flips the equivalent field on <c>lazer_users</c>.
        /// </summary>
        Task ToggleUserPresenceAsync(int userId, bool visible);

        /// <summary>
        /// Sets / clears the room's <c>ends_at</c> column. Upstream's
        /// <see cref="EndMatchAsync"/> assumes ends_at is always set to NOW(); this
        /// overload lets matchmaking-driven flows schedule a future end-of-life.
        /// </summary>
        Task SetRoomEndDateAsync(MultiplayerRoom room, DateTimeOffset? endDate);

        /// <summary>
        /// Matchmaking-only event log overload (rooms have a separate
        /// <c>matchmaking_room_events</c> table from the legacy
        /// <c>multiplayer_realtime_room_events</c>).
        /// </summary>
        Task LogRoomEventAsync(matchmaking_room_event ev);
    }
}