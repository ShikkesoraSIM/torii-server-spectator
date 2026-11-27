// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Linq;
using System.Threading.Tasks;
using osu.Game.Beatmaps;
using osu.Game.Users;
using osu.Server.Spectator.Entities;
using osu.Server.Spectator.Hubs.Metadata;
using Xunit;

namespace osu.Server.Spectator.Tests
{
    public class MsnPresenceEndpointTest
    {
        [Fact]
        public void OnlyTheTokenIsAccepted()
        {
            Assert.True(MsnPresenceEndpoint.IsAuthorised("Bearer secret", "secret"));
            Assert.False(MsnPresenceEndpoint.IsAuthorised("Bearer wrong", "secret"));
            Assert.False(MsnPresenceEndpoint.IsAuthorised("secret", "secret"));
            Assert.False(MsnPresenceEndpoint.IsAuthorised(string.Empty, "secret"));
        }

        [Fact]
        public async Task ListsOnlinePlayersAndTheirMaps()
        {
            var states = new EntityStore<MetadataClientState>();

            await add(states, 1, UserStatus.Online, new UserActivity.InSoloGame(
                new BeatmapInfo { Metadata = new BeatmapMetadata { Artist = "Camellia", Title = "Exit This Earth's Atomosphere" }, DifficultyName = "Evolution" },
                new osu.Game.Rulesets.Osu.OsuRuleset().RulesetInfo));
            await add(states, 2, UserStatus.DoNotDisturb, new UserActivity.ChoosingBeatmap());
            await add(states, 3, UserStatus.Offline, null);

            var players = MsnPresenceEndpoint.Players(states).OrderBy(p => p.user_id).ToArray();

            Assert.Equal(2, players.Length);
            Assert.Equal(1, players[0].user_id);
            Assert.Equal("online", players[0].status);
            Assert.Contains("Camellia - Exit This Earth's Atomosphere", players[0].map);
            Assert.Contains("[Evolution]", players[0].map);
            Assert.Equal(2, players[1].user_id);
            Assert.Equal("donotdisturb", players[1].status);
            Assert.Null(players[1].map);
        }

        private static async Task add(EntityStore<MetadataClientState> states, int userId, UserStatus status, UserActivity? activity)
        {
            using (var usage = await states.GetForUse(userId, true))
            {
                usage.Item = new MetadataClientState($"connection-{userId}", userId, null)
                {
                    UserStatus = status,
                    UserActivity = activity,
                };
            }
        }
    }
}
