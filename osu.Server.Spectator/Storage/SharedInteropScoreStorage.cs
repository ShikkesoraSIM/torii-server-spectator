// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using osu.Game.Beatmaps;
using osu.Game.Scoring.Legacy;
using osu.Server.Spectator.Hubs;
using osu.Server.Spectator.Services;

namespace osu.Server.Spectator.Storage
{
    /// <summary>
    /// <see cref="IScoreStorage"/> backend that hands the encoded replay to
    /// <see cref="ISharedInterop.UploadReplayAsync"/>, which POSTs it to
    /// <c>/_lio/scores/replay</c> on the Torii (g0v0) server. g0v0 then writes
    /// the file into its own storage at the canonical
    /// <c>replays/{score_id}_{beatmap_id}_{user_id}_lazer_replay.osr</c>
    /// path that <c>Score.replay_filename</c> resolves to — so the
    /// score-download endpoint serves the file every client looks up.
    ///
    /// Why this exists: the original Torii port wired in <see cref="FileScoreStorage"/>,
    /// which writes to the spectator container's local filesystem (a bare
    /// integer filename under <see cref="AppSettings.ReplaysPath"/>). g0v0 has no
    /// visibility into that directory and looks up replays under a completely
    /// different path inside its own storage volume, so the replay icon would
    /// appear on the score (because <c>has_replay = true</c> got flipped) but
    /// every download attempt 404'd. POSTing through the existing
    /// <c>/_lio/scores/replay</c> contract puts the bytes where g0v0 already
    /// expects them, no compose mount changes, no path-format gymnastics.
    /// </summary>
    public class SharedInteropScoreStorage : IScoreStorage
    {
        private readonly ISharedInterop sharedInterop;
        private readonly ILogger logger;

        public SharedInteropScoreStorage(ISharedInterop sharedInterop, ILoggerFactory loggerFactory)
        {
            this.sharedInterop = sharedInterop;
            logger = loggerFactory.CreateLogger(nameof(SharedInteropScoreStorage));
        }

        public Task WriteAsync(ScoreUploader.UploadItem item)
        {
            var score = item.Score;
            // BeatmapVersion is required for correct encoding of replays for
            // beatmaps with version < 5 (see LegacyBeatmapDecoder.EARLY_VERSION_TIMING_OFFSET).
            var legacyEncoder = new LegacyScoreEncoder(score, new Beatmap { BeatmapVersion = item.Beatmap.osu_file_version });

            using var ms = new MemoryStream();
            // leaveOpen: true so SerializationWriter inside Encode doesn't close the
            // underlying stream — we still need to read the bytes back via ToArray()
            // when handing off to UploadReplayAsync below.
            legacyEncoder.Encode(ms, leaveOpen: true);

            int userId = score.ScoreInfo.UserID;
            long scoreOnlineId = score.ScoreInfo.OnlineID;
            int beatmapId = item.Beatmap.beatmap_id;

            logger.LogInformation(
                "Uploading replay for score {scoreId} (user {userId}, beatmap {beatmapId}, {bytes} bytes) to /_lio/scores/replay",
                scoreOnlineId,
                userId,
                beatmapId,
                ms.Length);

            // UploadReplayAsync is fire-and-forget on the SharedInterop side
            // (it returns void after kicking off the runCommand task), so this
            // method satisfies its IScoreStorage contract by completing as
            // soon as the encode finishes and the upload is in flight. Errors
            // surface in the spectator log via SharedInterop's runCommand
            // exception handling — same observability story FileScoreStorage
            // had, just with HTTP instead of disk I/O.
            sharedInterop.UploadReplayAsync(userId, scoreOnlineId, beatmapId, ms);

            return Task.CompletedTask;
        }
    }
}
