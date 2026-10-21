// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Game.Users;
using osu.Server.Spectator.Services;
using Xunit;

namespace osu.Server.Spectator.Tests
{
    public class SilentSpectatorPolicyTest
    {
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData(" , ")]
        public void DisabledUnlessConfigured(string? value)
        {
            var policy = new SilentSpectatorPolicy(value);
            Assert.False(policy.IsSilent(3));
            Assert.False(policy.IsSilent(0));
        }

        [Theory]
        [InlineData("*")]
        [InlineData("Shikkesora")]
        [InlineData("3,wrong")]
        [InlineData("0")]
        [InlineData("-1")]
        [InlineData("2147483648")]
        public void RejectsInvalidAllowList(string value)
            => Assert.Throws<ArgumentException>(() => new SilentSpectatorPolicy(value));

        [Fact]
        public void MatchesOnlyConfiguredIds()
        {
            var policy = new SilentSpectatorPolicy(" 3, 77,3 ");
            Assert.True(policy.IsSilent(3));
            Assert.True(policy.IsSilent(77));
            Assert.False(policy.IsSilent(33));
            Assert.False(policy.IsSilent(0));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void HidesSpectatingWithoutChangingOriginalPresence(bool multiplayer)
        {
            var policy = new SilentSpectatorPolicy("3");
            var activity = multiplayer ? (UserActivity)new UserActivity.SpectatingMultiplayerGame() : new UserActivity.SpectatingUser { PlayerName = "target" };
            var presence = new UserPresence { Status = UserStatus.DoNotDisturb, Activity = activity };

            var publicPresence = policy.GetPublicPresence(3, presence);
            Assert.NotEqual(presence, publicPresence!.Value);
            Assert.Null(publicPresence.Value.Activity);
            Assert.Equal(presence.Status, publicPresence.Value.Status);
            Assert.Same(activity, presence.Activity);
            Assert.Equal(presence, policy.GetPublicPresence(4, presence));
        }

        [Fact]
        public void PreservesOtherActivityAndOfflinePresence()
        {
            var policy = new SilentSpectatorPolicy("3");
            var presence = new UserPresence { Status = UserStatus.Online, Activity = new UserActivity.ChoosingBeatmap() };
            Assert.Equal(presence, policy.GetPublicPresence(3, presence));
            presence.Activity = new UserActivity.WatchingReplay { PlayerName = "target" };
            Assert.Equal(presence, policy.GetPublicPresence(3, presence));
            Assert.Null(policy.GetPublicPresence(3, null));
        }
    }
}
