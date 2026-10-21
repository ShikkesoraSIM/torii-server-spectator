// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Threading.Tasks;
using Moq;
using osu.Game.Online.Metadata;
using osu.Game.Users;
using osu.Server.Spectator.Hubs.Metadata;
using Xunit;

namespace osu.Server.Spectator.Tests
{
    public partial class MetadataHubTest
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task SilentSpectatorActivityHiddenFromLivePresence(bool multiplayer)
        {
            var friend = new Mock<IMetadataClient>();
            mockClients.Setup(c => c.Group(MetadataHub.FRIEND_PRESENCE_WATCHERS_GROUP(silent_user_id))).Returns(friend.Object);
            hub.Context = createUserContext(silent_user_id).Object;
            await hub.OnConnectedAsync();
            await hub.UpdateStatus(UserStatus.Online);
            mockWatchersGroup.Invocations.Clear();
            friend.Invocations.Clear();
            mockCaller.Invocations.Clear();

            UserActivity activity = multiplayer ? (UserActivity)new UserActivity.SpectatingMultiplayerGame() : new UserActivity.SpectatingUser { PlayerName = "target" };
            await hub.UpdateActivity(activity);

            mockWatchersGroup.Verify(c => c.UserPresenceUpdated(silent_user_id, It.Is<UserPresence>(p => p.Activity == null && p.Status == UserStatus.Online)), Times.Once);
            friend.Verify(c => c.FriendPresenceUpdated(silent_user_id, It.Is<UserPresence>(p => p.Activity == null && p.Status == UserStatus.Online)), Times.Once);
            mockCaller.Verify(c => c.UserPresenceUpdated(silent_user_id, It.Is<UserPresence>(p => p.Activity == activity)), Times.Once);
            using (var usage = await userStates.GetForUse(silent_user_id))
                Assert.Same(activity, usage.Item!.UserActivity);

            await hub.UpdateStatus(UserStatus.DoNotDisturb);
            mockWatchersGroup.Verify(c => c.UserPresenceUpdated(silent_user_id, It.Is<UserPresence>(p => p.Activity == null && p.Status == UserStatus.DoNotDisturb)), Times.Once);
            await hub.UpdateActivity(new UserActivity.ChoosingBeatmap());
            mockWatchersGroup.Verify(c => c.UserPresenceUpdated(silent_user_id, It.Is<UserPresence>(p => p.Activity is UserActivity.ChoosingBeatmap)), Times.Once);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task SilentSpectatorActivityHiddenFromInitialPresence(bool friends)
        {
            using (var usage = await userStates.GetForUse(silent_user_id, true))
            {
                usage.Item = new MetadataClientState("silent", silent_user_id, null)
                {
                    UserStatus = UserStatus.Online,
                    UserActivity = new UserActivity.SpectatingUser { PlayerName = "target" },
                };
            }

            if (friends)
            {
                mockDatabase.Setup(d => d.GetUserFriendsAsync(user_id)).ReturnsAsync([silent_user_id]);
                await hub.OnConnectedAsync();
                mockCaller.Verify(c => c.FriendPresenceUpdated(silent_user_id, It.Is<UserPresence>(p => p.Activity == null && p.Status == UserStatus.Online)), Times.Once);
            }
            else
            {
                await hub.BeginWatchingUserPresence();
                mockCaller.Verify(c => c.UserPresenceUpdated(silent_user_id, It.Is<UserPresence>(p => p.Activity == null && p.Status == UserStatus.Online)), Times.Once);
            }
        }

        [Fact]
        public async Task RegularSpectatorActivityStillVisible()
        {
            await hub.OnConnectedAsync();
            await hub.UpdateStatus(UserStatus.Online);
            var activity = new UserActivity.SpectatingUser { PlayerName = "target" };
            await hub.UpdateActivity(activity);
            mockWatchersGroup.Verify(c => c.UserPresenceUpdated(user_id, It.Is<UserPresence>(p => p.Activity == activity)), Times.Once);
        }
    }
}
