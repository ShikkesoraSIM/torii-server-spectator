// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Moq;
using osu.Game.Online.Spectator;
using osu.Server.Spectator.Hubs.Spectator;
using Xunit;

namespace osu.Server.Spectator.Tests
{
    public partial class SpectatorHubTest
    {
        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task SilentWatcherReceivesStreamWithoutNotifyingPlayer(bool usesV2, bool ongoing)
        {
            var receiver = new Mock<ISpectatorClient>();
            var streamer = new Mock<ISpectatorClient>();
            var clients = new Mock<IHubCallerClients<ISpectatorClient>>();
            clients.Setup(c => c.Caller).Returns(receiver.Object);
            clients.Setup(c => c.Group(SpectatorHub.GetGroupId(streamer_id))).Returns(receiver.Object);
            clients.Setup(c => c.User(streamer_id.ToString())).Returns(streamer.Object);
            var groups = new Mock<IGroupManager>();
            hub.Clients = clients.Object;
            hub.Groups = groups.Object;

            var state = playingState();
            hub.Context = spectatorContext(streamer_id, "streamer");
            if (ongoing)
            {
                if (usesV2)
                    await hub.BeginPlaySessionV2(1234, state);
                else
                    await hub.BeginPlaySession(1234, state);
            }

            hub.Context = spectatorContext(silent_watcher_id, "silent");
            receiver.Invocations.Clear();
            await hub.StartWatchingUser(streamer_id);
            groups.Verify(g => g.AddToGroupAsync("silent", SpectatorHub.GetGroupId(streamer_id), default), Times.Once);
            receiver.Verify(c => c.UserBeganPlaying(streamer_id, state), Times.Exactly(ongoing ? 1 : 0));

            hub.Context = spectatorContext(streamer_id, "streamer");
            if (!ongoing)
            {
                if (usesV2)
                    await hub.BeginPlaySessionV2(1234, state);
                else
                    await hub.BeginPlaySession(1234, state);
            }

            var frames = liveFrames();
            if (usesV2)
                await hub.SendFrameDataV2(1234, frames);
            else
                await hub.SendFrameData(frames);
            receiver.Verify(c => c.UserSentFrames(streamer_id, frames), Times.Once);

            hub.Context = spectatorContext(silent_watcher_id, "silent");
            await hub.EndWatchingUser(streamer_id);
            await hub.OnDisconnectedAsync(null);
            groups.Verify(g => g.RemoveFromGroupAsync("silent", SpectatorHub.GetGroupId(streamer_id), default), Times.Once);
            streamer.Verify(c => c.UserStartedWatching(It.IsAny<SpectatorUser[]>()), Times.Never);
            streamer.Verify(c => c.UserEndedWatching(It.IsAny<int>()), Times.Never);
            mockDatabase.Verify(d => d.GetUsernameAsync(silent_watcher_id), Times.Never);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task OnlyAllowListedIdsAreSilentOnDisconnect(bool silent)
        {
            int watcherId = silent ? silent_watcher_id : watcher_id;
            var streamer = new Mock<ISpectatorClient>();
            var clients = new Mock<IHubCallerClients<ISpectatorClient>>();
            clients.Setup(c => c.User(It.IsAny<string>())).Returns(streamer.Object);
            hub.Clients = clients.Object;
            hub.Groups = Mock.Of<IGroupManager>();
            hub.Context = spectatorContext(watcherId, "watcher");
            mockDatabase.Setup(d => d.GetUsernameAsync(watcherId)).ReturnsAsync("Shikkesora");

            await hub.StartWatchingUser(streamer_id);
            await hub.StartWatchingUser(streamer_id + 1);
            await hub.OnDisconnectedAsync(null);

            streamer.Verify(c => c.UserStartedWatching(It.IsAny<SpectatorUser[]>()), Times.Exactly(silent ? 0 : 2));
            streamer.Verify(c => c.UserEndedWatching(watcherId), Times.Exactly(silent ? 0 : 2));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task WatchingCanResumeAfterDisconnect(bool silent)
        {
            int watcherId = silent ? silent_watcher_id : watcher_id;
            var streamer = new Mock<ISpectatorClient>();
            var clients = new Mock<IHubCallerClients<ISpectatorClient>>();
            clients.Setup(c => c.User(streamer_id.ToString())).Returns(streamer.Object);
            hub.Clients = clients.Object;
            hub.Groups = Mock.Of<IGroupManager>();
            mockDatabase.Setup(d => d.GetUsernameAsync(watcherId)).ReturnsAsync("watcher");

            hub.Context = spectatorContext(watcherId, "first");
            await hub.StartWatchingUser(streamer_id);
            hub.Context = spectatorContext(watcherId, "second");
            await hub.OnConnectedAsync();
            await hub.StartWatchingUser(streamer_id);

            hub.Context = spectatorContext(watcherId, "first");
            await hub.OnDisconnectedAsync(null);
            hub.Context = spectatorContext(watcherId, "second");
            await hub.EndWatchingUser(streamer_id);
            await hub.OnDisconnectedAsync(null);

            streamer.Verify(c => c.UserStartedWatching(It.IsAny<SpectatorUser[]>()), Times.Exactly(silent ? 0 : 2));
            streamer.Verify(c => c.UserEndedWatching(watcherId), Times.Exactly(silent ? 0 : 2));
        }

        private static HubCallerContext spectatorContext(int userId, string connectionId)
        {
            var context = new Mock<HubCallerContext>();
            context.SetupGet(c => c.UserIdentifier).Returns(userId.ToString());
            context.SetupGet(c => c.ConnectionId).Returns(connectionId);
            return context.Object;
        }
    }
}
