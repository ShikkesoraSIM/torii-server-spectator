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
    public class FinishCardPlayStage : RankedPlayStageImplementation
    {
        public FinishCardPlayStage(RankedPlayMatchController controller)
            : base(controller)
        {
        }

        protected override RankedPlayStage Stage => RankedPlayStage.FinishCardPlay;
        protected override TimeSpan Duration => TimeSpan.FromMinutes(2);

        protected override async Task Begin()
        {
            await continueWhenAllPlayersReady();
        }

        protected override async Task Finish()
        {
            Debug.Assert(State.ActiveUserId != null);
            Debug.Assert(Controller.LastActivatedCard != null);

            if (Room.Users.All(isPlayerReady))
            {
                Controller.FailedPickStreak = 0;
                await Controller.GotoStage(RankedPlayStage.GameplayWarmup);
            }
            else
            {
                // torii: si NADIE pudo tener el mapa, es casi seguro que el mapa esta roto (version
                // local != online), no que los dos jugadores tengan mala conexion. Lo flaggeamos: con
                // suficientes flags de matches distintos el selector lo saca solo del pool (ver
                // MatchmakingBeatmapSelector.flagged_exclusion_threshold). No penalizamos a nadie en ese
                // caso — abajo el danio 100k solo aplica cuando AL MENOS UNO si lo tuvo (anti-abuso: que
                // no te salves de un pick fuerte "no bajando" el mapa).
                if (!Room.Users.Any(isPlayerReady))
                    await Controller.FlagCurrentBeatmapUnplayable();

                // 100k HP al que no tiene el mapa a tiempo, pero solo si al menos un jugador SI
                // lo tiene (si los dos fallaron, no penalizamos a nadie). el danio escala con el
                // multiplier de la ronda. OJO: "ready" aca es SOLO tener el beatmap (mismo predicado
                // que el gate del stage); el predicado estricto (LocallyAvailable && Ready) dejaba
                // escapar de la penalidad al que ni siquiera bajo el mapa.
                if (Room.Users.Any(isPlayerReady))
                {
                    foreach (var player in Room.Users.Where(u => !isPlayerReady(u)))
                        Controller.Damage(player.UserID, 100_000, State.DamageMultiplier);
                }

                await Controller.RemoveCards(State.ActiveUserId.Value, [Controller.LastActivatedCard]);

                // torii RE-PICK JUSTO: el pick no se pudo jugar (mapa roto / sin mp3 / no descargable),
                // asi que el que pickeo NO pierde el turno ni queda con una carta menos: le damos una
                // carta de reemplazo y volvemos DIRECTO a CardPlay con el mismo ActiveUserId (el turno
                // rota en RoundWarmup — salteandolo, el pick queda en la misma persona y la ronda no se
                // consume). Cap de 3 fallos seguidos como guarda anti-loop: si algo hace fallar todo,
                // el match sigue avanzando por el camino viejo.
                if (++Controller.FailedPickStreak <= 3 && HasGameplayRoundsRemaining())
                {
                    await Controller.AddCards(State.ActiveUserId.Value, 1);
                    await Controller.GotoStage(RankedPlayStage.CardPlay);
                    return;
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
            if (Room.Users.All(isPlayerReady))
                await Finish();
        }

        /// <summary>
        /// Only requires players to have the beatmap, but not necessarily have it loaded yet.
        /// </summary>
        private bool isPlayerReady(MultiplayerRoomUser user)
            => user.BeatmapAvailability.State == DownloadState.LocallyAvailable;
    }
}
