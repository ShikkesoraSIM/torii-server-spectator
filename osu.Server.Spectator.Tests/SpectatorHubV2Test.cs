// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Moq;
using osu.Game.Online.API;
using osu.Game.Online.Spectator;
using osu.Game.Replays.Legacy;
using osu.Game.Rulesets.Scoring;
using osu.Game.Scoring;
using osu.Server.Spectator.Database.Models;
using osu.Server.Spectator.Hubs;
using osu.Server.Spectator.Hubs.Spectator;
using Xunit;

namespace osu.Server.Spectator.Tests
{
    public partial class SpectatorHubTest
    {
        private Mock<ISpectatorClient> setupV2Clients()
        {
            var receiver = new Mock<ISpectatorClient>();
            var clients = new Mock<IHubCallerClients<ISpectatorClient>>();
            clients.Setup(c => c.Group(SpectatorHub.GetGroupId(streamer_id))).Returns(receiver.Object);
            clients.Setup(c => c.All).Returns(receiver.Object);
            var context = new Mock<HubCallerContext>();
            context.SetupGet(c => c.UserIdentifier).Returns(streamer_id.ToString());
            context.SetupGet(c => c.ConnectionId).Returns("streamer");
            hub.Clients = clients.Object;
            hub.Context = context.Object;
            return receiver;
        }

        private static SpectatorState playingState() => new SpectatorState
        {
            BeatmapID = beatmap_id,
            RulesetID = 0,
            State = SpectatedUserState.Playing,
            Mods = [new APIMod { Acronym = "HR" }],
            MaximumStatistics = { [HitResult.Great] = 100 },
        };

        private static FrameDataBundle liveFrames(long score = 123456, double accuracy = 0.9876) => new FrameDataBundle(
            new FrameHeader(new ScoreInfo
            {
                TotalScore = score,
                Accuracy = accuracy,
                Combo = 42,
                MaxCombo = 50,
                Statistics = { [HitResult.Great] = 50 },
                APIMods = [new APIMod { Acronym = "HR" }],
            }, new ScoreProcessorStatistics()),
            [new LegacyReplayFrame(12000, 0, 0, ReplayButtonState.Left1)]);

        [Fact]
        public async Task V2StreamsLiveScoreAccuracyAndCombo()
        {
            var receiver = setupV2Clients();
            var state = playingState();
            var frames = liveFrames();
            await hub.BeginPlaySessionV2(1234, state);
            await hub.SendFrameDataV2(1234, frames);

            receiver.Verify(c => c.UserBeganPlaying(streamer_id, state), Times.Once);
            receiver.Verify(c => c.UserSentFrames(streamer_id, It.Is<FrameDataBundle>(b =>
                b.Header.TotalScore == 123456 && b.Header.Accuracy == 0.9876 && b.Header.Combo == 42)), Times.Once);
        }

        [Theory]
        [InlineData(SpectatedUserState.Passed)]
        [InlineData(SpectatedUserState.Failed)]
        [InlineData(SpectatedUserState.Quit)]
        public async Task V2PreservesToriiReplayAndEndState(SpectatedUserState finalState)
        {
            var receiver = setupV2Clients();
            scoreUploader.SaveReplays = true;
            mockDatabase.Setup(d => d.GetScoreFromTokenAsync(1234)).ReturnsAsync(new SoloScore { id = 456, passed = true });
            await hub.BeginPlaySessionV2(1234, playingState());
            await hub.SendFrameDataV2(1234, liveFrames());
            await hub.EndPlaySessionV2(1234, finalState);
            await uploadsCompleteAsync();

            mockScoreStorage.Verify(s => s.WriteAsync(It.Is<ScoreUploader.UploadItem>(item =>
                item.Token == 1234 && item.Score.ScoreInfo.TotalScore == 123456 && item.Score.ScoreInfo.Accuracy == 0.9876
                && item.Score.Replay.Frames.Count == 1)), Times.Once);
            receiver.Verify(c => c.UserFinishedPlaying(streamer_id, It.Is<SpectatorState>(state =>
                state.State == finalState && state.BeatmapID == beatmap_id && state.RulesetID == 0
                && state.Mods.Single().Acronym == "HR" && state.MaximumStatistics[HitResult.Great] == 100)), Times.Once);

            if (finalState != SpectatedUserState.Passed)
                mockDatabase.Verify(d => d.UpdateFailTimeAsync(It.IsAny<fail_time>()), Times.Once);
        }

        [Fact]
        public async Task V2IgnoresFramesAndEndFromOldSession()
        {
            var receiver = setupV2Clients();
            await hub.BeginPlaySessionV2(1234, playingState());
            await hub.BeginPlaySessionV2(5678, playingState());
            await hub.SendFrameDataV2(1234, liveFrames());
            await hub.EndPlaySessionV2(1234, SpectatedUserState.Passed);
            receiver.Verify(c => c.UserSentFrames(It.IsAny<int>(), It.IsAny<FrameDataBundle>()), Times.Never);
            receiver.Verify(c => c.UserFinishedPlaying(It.IsAny<int>(), It.IsAny<SpectatorState>()), Times.Never);

            await hub.SendFrameDataV2(5678, liveFrames(234567, 0.95));
            await hub.EndPlaySessionV2(5678, SpectatedUserState.Passed);
            receiver.Verify(c => c.UserSentFrames(streamer_id, It.Is<FrameDataBundle>(b => b.Header.TotalScore == 234567 && b.Header.Accuracy == 0.95)), Times.Once);
            receiver.Verify(c => c.UserFinishedPlaying(streamer_id, It.IsAny<SpectatorState>()), Times.Once);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task V1AndV2CallsCannotCrossSessions(bool startsWithV2)
        {
            var receiver = setupV2Clients();
            if (startsWithV2)
            {
                await hub.BeginPlaySessionV2(1234, playingState());
                await hub.SendFrameData(liveFrames());
                await hub.EndPlaySession(new SpectatorState { State = SpectatedUserState.Quit });
            }
            else
            {
                await hub.BeginPlaySession(1234, playingState());
                await hub.SendFrameDataV2(1234, liveFrames());
                await hub.EndPlaySessionV2(1234, SpectatedUserState.Quit);
            }

            receiver.Verify(c => c.UserSentFrames(It.IsAny<int>(), It.IsAny<FrameDataBundle>()), Times.Never);
            receiver.Verify(c => c.UserFinishedPlaying(It.IsAny<int>(), It.IsAny<SpectatorState>()), Times.Never);
        }

        [Fact]
        public async Task V2DuplicateBeginDoesNotLoseReplayFrames()
        {
            var receiver = setupV2Clients();
            scoreUploader.SaveReplays = true;
            mockDatabase.Setup(d => d.GetScoreFromTokenAsync(1234)).ReturnsAsync(new SoloScore { id = 456, passed = true });
            await hub.BeginPlaySessionV2(1234, playingState());
            await hub.SendFrameDataV2(1234, liveFrames());
            await hub.BeginPlaySessionV2(1234, playingState());
            await hub.EndPlaySessionV2(1234, SpectatedUserState.Passed);
            await uploadsCompleteAsync();

            receiver.Verify(c => c.UserBeganPlaying(streamer_id, It.IsAny<SpectatorState>()), Times.Once);
            mockScoreStorage.Verify(s => s.WriteAsync(It.Is<ScoreUploader.UploadItem>(item => item.Score.Replay.Frames.Count == 1)), Times.Once);
        }

        [Fact]
        public async Task V2DuplicateEndIsIgnored()
        {
            var receiver = setupV2Clients();
            await hub.BeginPlaySessionV2(1234, playingState());
            await hub.EndPlaySessionV2(1234, SpectatedUserState.Passed);
            await hub.EndPlaySessionV2(1234, SpectatedUserState.Passed);
            receiver.Verify(c => c.UserFinishedPlaying(streamer_id, It.IsAny<SpectatorState>()), Times.Once);
        }

        [Fact]
        public async Task V2NullTokenCanStreamAndFinishWithoutUploading()
        {
            var receiver = setupV2Clients();
            await hub.BeginPlaySessionV2(null, playingState());
            await hub.SendFrameDataV2(null, liveFrames());
            await hub.EndPlaySessionV2(null, SpectatedUserState.Passed);

            receiver.Verify(c => c.UserSentFrames(streamer_id, It.IsAny<FrameDataBundle>()), Times.Once);
            receiver.Verify(c => c.UserFinishedPlaying(streamer_id, It.Is<SpectatorState>(s => s.State == SpectatedUserState.Passed)), Times.Once);
            mockScoreStorage.Verify(s => s.WriteAsync(It.IsAny<ScoreUploader.UploadItem>()), Times.Never);
        }

        [Fact]
        public async Task V2FramesWithoutBeginAreIgnored()
        {
            var receiver = setupV2Clients();
            await hub.SendFrameDataV2(null, liveFrames());
            await hub.EndPlaySessionV2(null, SpectatedUserState.Quit);
            receiver.Verify(c => c.UserSentFrames(It.IsAny<int>(), It.IsAny<FrameDataBundle>()), Times.Never);
            receiver.Verify(c => c.UserFinishedPlaying(It.IsAny<int>(), It.IsAny<SpectatorState>()), Times.Never);
        }

        [Fact]
        public async Task V2InvalidEndStateBecomesQuit()
        {
            var receiver = setupV2Clients();
            await hub.BeginPlaySessionV2(1234, playingState());
            await hub.EndPlaySessionV2(1234, SpectatedUserState.Playing);
            receiver.Verify(c => c.UserFinishedPlaying(streamer_id, It.Is<SpectatorState>(s => s.State == SpectatedUserState.Quit)), Times.Once);
        }

        [Fact]
        public async Task V2InvalidNewBeatmapDoesNotReuseOldReplay()
        {
            var receiver = setupV2Clients();
            await hub.BeginPlaySessionV2(1234, playingState());
            var invalidState = playingState();
            invalidState.BeatmapID = -123;
            await hub.BeginPlaySessionV2(5678, invalidState);
            await hub.SendFrameDataV2(5678, liveFrames());
            receiver.Verify(c => c.UserSentFrames(It.IsAny<int>(), It.IsAny<FrameDataBundle>()), Times.Never);
        }
    }
}
