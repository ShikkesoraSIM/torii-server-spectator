// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using osu.Game.Online;
using osu.Game.Online.Multiplayer;
using osu.Game.Online.Multiplayer.MatchTypes.RankedPlay;

namespace osu.Server.Spectator.Hubs.Multiplayer.Matchmaking.RankedPlay.Stages
{
    public class GameplayWarmupStage : RankedPlayStageImplementation
    {
        public GameplayWarmupStage(RankedPlayMatchController controller)
            : base(controller)
        {
        }

        protected override RankedPlayStage Stage => RankedPlayStage.GameplayWarmup;
        protected override TimeSpan Duration => TimeSpan.FromMinutes(2);

        protected override async Task Begin()
        {
            await continueWhenAllPlayersReady();
        }

        protected override async Task Finish()
        {
            Debug.Assert(State.ActiveUserId != null);
            Debug.Assert(Controller.LastActivatedCard != null);

            if (allPlayersReady())
                await Controller.GotoStage(RankedPlayStage.Gameplay);
            else
            {
                await Controller.RemoveCards(State.ActiveUserId.Value, [Controller.LastActivatedCard]);

                // 100k HP al que no cargo el mapa a tiempo, PERO solo si al menos un
                // jugador SI cargo (si los dos fallaron, no penalizamos a nadie). el danio
                // ahora escala con el multiplier de la ronda (upstream).
                if (Room.Users.Any(u => u.BeatmapAvailability.State == DownloadState.LocallyAvailable && u.State == MultiplayerUserState.Ready))
                {
                    foreach (var player in Room.Users.Where(u => u.BeatmapAvailability.State != DownloadState.LocallyAvailable || u.State != MultiplayerUserState.Ready))
                        Controller.Damage(player.UserID, 100_000, State.DamageMultiplier);
                }

                if (HasGameplayRoundsRemaining())
                    await Controller.GotoStage(RankedPlayStage.RoundWarmup);
                else
                    await Controller.GotoStage(RankedPlayStage.Ended);
            }
        }

        public override async Task HandleUserStateChanged(MultiplayerRoomUser user)
        {
            await continueWhenAllPlayersReady();
        }

        private async Task continueWhenAllPlayersReady()
        {
            if (allPlayersReady())
                await FinishWithCountdown(TimeSpan.FromSeconds(10));
        }

        /// <summary>
        /// Requires all players to be in the ready state, signaling they have finished viewing the beatmap details/etc.
        /// </summary>
        private bool allPlayersReady()
            => Room.Users.All(u => u.BeatmapAvailability.State == DownloadState.LocallyAvailable && u.State == MultiplayerUserState.Ready);
    }
}
