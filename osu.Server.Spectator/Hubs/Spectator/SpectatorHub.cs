// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;
using osu.Game.Beatmaps;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Database;
using osu.Game.Online.Spectator;
using osu.Game.Rulesets.Scoring;
using osu.Game.Scoring;
using osu.Server.Spectator.Authentication;
using osu.Server.Spectator.Database;
using osu.Server.Spectator.Database.Models;
using osu.Server.Spectator.Entities;
using osu.Server.Spectator.Extensions;
using osu.Server.Spectator.Helpers;
using StackExchange.Redis;

namespace osu.Server.Spectator.Hubs.Spectator
{
    [Authorize(ConfigureJwtBearerOptions.LAZER_CLIENT_SCHEME)]
    public class SpectatorHub : StatefulUserHub<ISpectatorClient, SpectatorClientState>, ISpectatorServer
    {
        /// <summary>
        /// Minimum beatmap status to save replays for.
        /// </summary>
        private const BeatmapOnlineStatus min_beatmap_status_for_replays = BeatmapOnlineStatus.Ranked;

        /// <summary>
        /// Maximum beatmap status to save replays for.
        /// </summary>
        private const BeatmapOnlineStatus max_beatmap_status_for_replays = BeatmapOnlineStatus.Loved;

        private readonly IDatabaseFactory databaseFactory;
        private readonly ScoreUploader scoreUploader;
        private readonly IScoreProcessedSubscriber scoreProcessedSubscriber;
        // Torii: M1PP injected redis here so it could pop the
        // `score:existed_time:{token}` stream g0v0 publishes when a play
        // begins. Without it, editPlayTime can't compute a delta and the
        // per-mode playtime column on lazer_user_statistics never advances.
        private readonly IDatabase redisDatabase;

        public SpectatorHub(
            ILoggerFactory loggerFactory,
            EntityStore<SpectatorClientState> users,
            IDatabaseFactory databaseFactory,
            ScoreUploader scoreUploader,
            IScoreProcessedSubscriber scoreProcessedSubscriber,
            IConnectionMultiplexer redis)
            : base(loggerFactory, users)
        {
            this.databaseFactory = databaseFactory;
            this.scoreUploader = scoreUploader;
            this.scoreProcessedSubscriber = scoreProcessedSubscriber;
            redisDatabase = redis.GetDatabase();
        }

        public Task BeginPlaySession(long? scoreToken, SpectatorState state) => beginPlaySession(scoreToken, state, false);

        public Task BeginPlaySessionV2(long? scoreToken, SpectatorState state) => beginPlaySession(scoreToken, state, true);

        private async Task beginPlaySession(long? scoreToken, SpectatorState state, bool usesV2)
        {
            int userId = Context.GetUserId();

            using (var usage = await GetOrCreateLocalUserState())
            {
                var clientState = (usage.Item ??= new SpectatorClientState(Context.ConnectionId, userId));

                if (usesV2 && clientState.UsesV2 && scoreToken != null && clientState.ScoreToken == scoreToken && clientState.Score != null)
                    return;

                clientState.State = state;
                clientState.ScoreToken = scoreToken;
                clientState.UsesV2 = usesV2;
                clientState.Beatmap = null;
                clientState.Score = null;

                if (state.RulesetID == null)
                    return;

                if (state.BeatmapID == null)
                    return;

                using (var db = databaseFactory.GetInstance())
                {
                    // Torii: OrFetch was M1PP's pattern. If a player begins a
                    // session on a beatmap g0v0 hasn't cached yet (any map
                    // nobody on this server has played) the bare
                    // GetBeatmapAsync returns null and `beatmap?.checksum`
                    // short-circuits the whole session — no replay, no
                    // spectate broadcast. OrFetch falls through to
                    // _lio/beatmaps/ensure to bootstrap the row + parent set.
                    database_beatmap? beatmap = await db.GetBeatmapOrFetchAsync(state.BeatmapID.Value);
                    string? username = await db.GetUsernameAsync(userId);

                    if (string.IsNullOrEmpty(username))
                        throw new ArgumentException(nameof(username));

                    if (string.IsNullOrEmpty(beatmap?.checksum))
                        return;

                    clientState.Beatmap = beatmap;
                    clientState.Score = new Score
                    {
                        ScoreInfo =
                        {
                            APIMods = state.Mods.ToArray(),
                            User = new APIUser
                            {
                                Id = userId,
                                Username = username,
                            },
                            Ruleset = LegacyHelper.GetRulesetFromLegacyID(state.RulesetID.Value).RulesetInfo,
                            BeatmapInfo = new BeatmapInfo
                            {
                                OnlineID = state.BeatmapID.Value,
                                MD5Hash = beatmap.checksum,
                                Status = beatmap.approved
                            },
                            MaximumStatistics = state.MaximumStatistics
                        }
                    };
                }
                await Clients.Group(GetGroupId(userId)).UserBeganPlaying(userId, state);
            }
        }

        public Task SendFrameData(FrameDataBundle data) => sendFrameData(null, data, false);

        public Task SendFrameDataV2(long? scoreToken, FrameDataBundle data) => sendFrameData(scoreToken, data, true);

        private async Task sendFrameData(long? scoreToken, FrameDataBundle data, bool usesV2)
        {
            using (var usage = await GetOrCreateLocalUserState())
            {
                var clientState = usage.Item;
                var score = clientState?.Score;

                // Score may be null if the BeginPlaySession call failed but the client is still sending frame data.
                // For now it's safe to drop these frames.
                if (score == null)
                    return;

                // torii: el check va bajo el lock, asi un paquete viejo no toca otra partida.
                if (clientState!.UsesV2 != usesV2 || (usesV2 && clientState.ScoreToken != scoreToken))
                    return;

                score.ScoreInfo.Accuracy = data.Header.Accuracy;
                score.ScoreInfo.Statistics = data.Header.Statistics;
                score.ScoreInfo.MaxCombo = data.Header.MaxCombo;
                score.ScoreInfo.Combo = data.Header.Combo;
                score.ScoreInfo.TotalScore = data.Header.TotalScore;

                // null here means the frame bundle is from an old client that can't send mod data
                // can be removed (along with making property non-nullable on `FrameDataBundle`) 20250407
                if (data.Header.Mods != null)
                    score.ScoreInfo.APIMods = data.Header.Mods;

                score.Replay.Frames.AddRange(data.Frames);

                await Clients.Group(GetGroupId(Context.GetUserId())).UserSentFrames(Context.GetUserId(), data);
            }
        }

        public Task EndPlaySession(SpectatorState state) => finishPlaySession(null, state, false);

        public Task EndPlaySessionV2(long? scoreToken, SpectatedUserState finalState) => finishPlaySession(scoreToken, new SpectatorState { State = finalState }, true);

        private async Task finishPlaySession(long? expectedToken, SpectatorState state, bool usesV2)
        {
            using (var usage = await GetOrCreateLocalUserState())
            {
                var clientState = usage.Item;
                if (clientState?.State == null || clientState.UsesV2 != usesV2 || (usesV2 && clientState.ScoreToken != expectedToken))
                    return;

                if (usesV2)
                {
                    state.BeatmapID = clientState.State.BeatmapID;
                    state.RulesetID = clientState.State.RulesetID;
                    state.Mods = clientState.State.Mods;
                    state.MaximumStatistics = clientState.State.MaximumStatistics;
                    if (state.State != SpectatedUserState.Passed && state.State != SpectatedUserState.Failed && state.State != SpectatedUserState.Quit)
                        state.State = SpectatedUserState.Quit;
                }

                try
                {
                    Score? score = clientState.Score;
                    long? scoreToken = clientState.ScoreToken;

                    // torii: V1 conserva su cierre viejo; V2 avisa el fin aun sin token/replay.
                    if (score == null || scoreToken == null || clientState.Beatmap == null)
                    {
                        if (!usesV2)
                            return;
                    }
                    else
                    {
                        await processScore(clientState);

                        // torii: ambos protocolos conservan failtime y el ajuste de playtime.
                        int exitTime = (int)Math.Round((score.Replay.Frames.LastOrDefault()?.Time ?? 0) / 1000);
                        if (state.State == SpectatedUserState.Failed || state.State == SpectatedUserState.Quit)
                            await processFailtime(clientState, exitTime, state);
                        await editPlayTime(clientState, exitTime);
                    }
                }
                finally
                {
                    clientState.State = null;
                    clientState.Beatmap = null;
                    clientState.Score = null;
                    clientState.ScoreToken = null;
                    clientState.UsesV2 = false;
                }

                await endPlaySession(Context.GetUserId(), state);
            }
        }

        private async Task processScore(SpectatorClientState item)
        {
            Debug.Assert(item.Score != null && item.ScoreToken != null && item.Beatmap != null);

            Score score = item.Score;
            long scoreToken = item.ScoreToken.Value;

            // Do nothing with scores on unranked beatmaps.
            // Torii: M1PP exposed an `EnableAllBeatmapLeaderboard` escape
            // hatch so g0v0 deploys can save replays for graveyard / WIP
            // maps too (Torii promotes all rank statuses for leaderboard
            // purposes). When the flag is on, the rank gate is skipped
            // entirely.
            var status = score.ScoreInfo.BeatmapInfo!.Status;
            if (!AppSettings.EnableAllBeatmapLeaderboard && (status < min_beatmap_status_for_replays || status > max_beatmap_status_for_replays))
                return;

            // if the user never hit anything, further processing that depends on the score existing can be waived because the client won't have submitted the score anyway.
            // see: https://github.com/ppy/osu/blob/a47ccb8edd2392258b6b7e176b222a9ecd511fc0/osu.Game/Screens/Play/SubmittingPlayer.cs#L281
            if (!score.ScoreInfo.Statistics.Any(s => s.Key.IsHit() && s.Value > 0))
                return;

            score.ScoreInfo.Date = DateTimeOffset.UtcNow;
            // this call is a little expensive due to reflection usage, so only run it at the end of score processing
            // even though in theory the rank could be recomputed after every replay frame.
            score.ScoreInfo.Rank = StandardisedScoreMigrationTools.ComputeRank(score.ScoreInfo);

            await scoreUploader.EnqueueAsync(scoreToken, score, item.Beatmap);
            await scoreProcessedSubscriber.RegisterForSingleScoreAsync(Context.ConnectionId, Context.GetUserId(), scoreToken);
        }

        public async Task StartWatchingUser(int userId)
        {
            Log($"Watching {userId}");

            try
            {
                SpectatorState? spectatorState;

                // send the user's state if exists
                using (var usage = await GetStateFromUser(userId))
                    spectatorState = usage.Item?.State;

                if (spectatorState != null)
                    await Clients.Caller.UserBeganPlaying(userId, spectatorState);
            }
            catch (KeyNotFoundException)
            {
                // user isn't tracked.
            }

            using (var state = await GetOrCreateLocalUserState())
            {
                var clientState = state.Item ??= new SpectatorClientState(Context.ConnectionId, Context.GetUserId());
                clientState.WatchedUsers.Add(userId);
            }

            await Groups.AddToGroupAsync(Context.ConnectionId, GetGroupId(userId));

            int watcherId = Context.GetUserId();
            string? watcherUsername;
            using (var db = databaseFactory.GetInstance())
                watcherUsername = await db.GetUsernameAsync(watcherId);

            if (watcherUsername == null)
                return;

            var watcher = new SpectatorUser
            {
                OnlineID = watcherId,
                Username = watcherUsername,
            };

            await Clients.User(userId.ToString()).UserStartedWatching([watcher]);
        }

        public async Task EndWatchingUser(int userId)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, GetGroupId(userId));

            using (var state = await GetOrCreateLocalUserState())
            {
                var clientState = state.Item ??= new SpectatorClientState(Context.ConnectionId, Context.GetUserId());
                clientState.WatchedUsers.Remove(userId);
            }

            int watcherId = Context.GetUserId();

            await Clients.User(userId.ToString()).UserEndedWatching(watcherId);
        }

        protected override async Task CleanUpState(ItemUsage<SpectatorClientState> state)
        {
            Debug.Assert(state.Item != null);

            if (state.Item.State != null)
                await endPlaySession(state.Item.UserId, state.Item.State);

            foreach (int watchedUserId in state.Item.WatchedUsers)
                await Clients.User(watchedUserId.ToString()).UserEndedWatching(state.Item.UserId);

            await base.CleanUpState(state);
        }

        public static string GetGroupId(int userId) => $"watch:{userId}";

        private async Task endPlaySession(int userId, SpectatorState state)
        {
            // Ensure that the state is no longer playing (e.g. if client crashes).
            if (state.State == SpectatedUserState.Playing)
                state.State = SpectatedUserState.Quit;

            await Clients.Group(GetGroupId(userId)).UserFinishedPlaying(userId, state);
        }

        /// <summary>
        /// Torii: M1PP-parity. Each beatmap has a `failtime` row containing
        /// two 100-int histograms (`fail`, `exit`) representing how often
        /// players failed or quit at each percent of the song. On a Failed
        /// or Quit end-of-session we read the existing histogram, bump the
        /// bucket corresponding to the relative exit time, and write it back.
        /// Used by song-select to draw the red bars on the timeline.
        /// </summary>
        private async Task processFailtime(SpectatorClientState item, int exitTime, SpectatorState state)
        {
            using (var db = databaseFactory.GetInstance())
            {
                var failTime = await db.GetBeatmapFailTimeAsync(item.Beatmap!.beatmap_id);
                int[]? target;

                if (failTime == null)
                {
                    failTime = new fail_time
                    {
                        beatmap_id = item.Beatmap!.beatmap_id,
                        exit = BlobHelper.IntArrayToBlob(new int[100]),
                        fail = BlobHelper.IntArrayToBlob(new int[100]),
                    };
                }

                switch (state.State)
                {
                    case SpectatedUserState.Failed:
                        target = BlobHelper.ParseBlobToIntArray(failTime.fail);
                        break;

                    case SpectatedUserState.Quit:
                        target = BlobHelper.ParseBlobToIntArray(failTime.exit);
                        break;

                    default:
                        return;
                }

                // Guard against zero-length maps (failtime falls through to
                // bucket 0 anyway, but we don't want a div-by-zero blowing
                // up the EndPlaySession finally block).
                int totalLength = item.Beatmap.total_length > 0 ? item.Beatmap.total_length : 1;
                int index = Math.Clamp((int)((double)exitTime / totalLength * 100), 0, 99);
                target[index] += 1;
                byte[] blob = BlobHelper.IntArrayToBlob(target);
                failTime.fail = state.State == SpectatedUserState.Failed ? blob : failTime.fail;
                failTime.exit = state.State == SpectatedUserState.Quit ? blob : failTime.exit;
                await db.UpdateFailTimeAsync(failTime);
            }
        }

        /// <summary>
        /// Torii: M1PP-parity. g0v0's score-submit pipeline optimistically
        /// counts the full beatmap runtime against the user's per-mode
        /// playtime when the play starts (publishing
        /// `score:existed_time:{token}` with the assumed duration). This
        /// method consumes that stream entry, computes the actual exit
        /// delta, and corrects `lazer_user_statistics.play_time` so a quit
        /// 30 seconds in doesn't get counted as 4 minutes of practice.
        /// No-op if the stream is empty (passed completion path or upstream
        /// race) — the optimistic count is then taken at face value.
        /// </summary>
        private async Task editPlayTime(SpectatorClientState item, int exitTime)
        {
            string key = $"score:existed_time:{item.ScoreToken}";
            var messages = await redisDatabase.StreamRangeAsync(key, "-", "+", 1);
            if (messages.Length == 0)
                return;

            var message = messages[0];
            int beforeTime = (int)message["time"];
            await redisDatabase.KeyDeleteAsync(key);
            string gamemode = GameModeHelper.GameModeToStringSpecial(item.Score!.ScoreInfo.RulesetID, item.Score.ScoreInfo.APIMods);

            using (var db = databaseFactory.GetInstance())
            {
                int? playTime = await db.GetUserPlaytimeAsync(gamemode, Context.GetUserId());

                if (playTime == null)
                    return;

                // g0v0 optimistically charged the full `beforeTime` against play_time at submit;
                // refund the unplayed portion. Clamp to 0 so a stale stream entry / wipe race
                // can't push play_time negative (it's stored unsigned downstream).
                int unplayed = Math.Max(0, beforeTime - exitTime);
                int corrected = Math.Max(0, playTime.Value - unplayed);
                await db.UpdateUserPlaytimeAsync(gamemode, Context.GetUserId(), corrected);
            }
        }
    }
}
