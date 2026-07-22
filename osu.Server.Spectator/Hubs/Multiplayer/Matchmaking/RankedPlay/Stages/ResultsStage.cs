// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using osu.Game.Online.Multiplayer;
using osu.Game.Online.Multiplayer.MatchTypes.RankedPlay;
using osu.Server.Spectator.Database.Models;

namespace osu.Server.Spectator.Hubs.Multiplayer.Matchmaking.RankedPlay.Stages
{
    public class ResultsStage : RankedPlayStageImplementation
    {
        /// <summary>
        /// Amount of time to wait for scores to arrive in the database before continuing.
        /// </summary>
        // 4s (bajo el lock timeout de 5s de EntityStore). el ResultsStage bloquea el
        // lock de la sala mientras espera, asi que pasarse de 5s hacia timeoutear los
        // ChangeState de los clientes. con el filtro de room_id el wait corta apenas
        // llegan los 2 scores reales (~1s), este cap es solo para el caso raro de un
        // score que nunca llega (quit/desync real).
        public TimeSpan ScoreRetrievalWaitTime { get; set; } = TimeSpan.FromSeconds(4);

        /// <summary>
        /// Flat bonus damage the round loser takes (el bonus amarillo por ganar la ronda).
        /// </summary>
        public int BaseDamage { get; set; } = 50_000;

        public ResultsStage(RankedPlayMatchController controller)
            : base(controller)
        {
        }

        protected override RankedPlayStage Stage => RankedPlayStage.Results;
        protected override TimeSpan Duration => TimeSpan.FromSeconds(15);

        private int? winningUserId;

        protected override async Task Begin()
        {
            // Collect all scores from the database.
            List<SoloScore> scores = [];

            using (var db = DbFactory.GetInstance())
            {
                // Wait up to 10 seconds to retrieve scores for all players, before continuing and giving them 0 score.
                using (var cts = new CancellationTokenSource(ScoreRetrievalWaitTime))
                {
                    SoloScore[] retrievedScores = [];

                    while (!cts.IsCancellationRequested)
                    {
                        // torii: la query puede traer scores ajenos al match que comparten el playlist
                        // item id (datos historicos en la db), asi que filtramos a los jugadores que
                        // estan realmente en la sala. sin esto, State.Users[score.user_id] mas abajo
                        // revienta con KeyNotFoundException y se cae todo el cierre del match.
                        retrievedScores = (await db.GetAllScoresForPlaylistItem(Room.RoomID, Room.Settings.PlaylistItemId))
                                          .Where(s => State.Users.ContainsKey((int)s.user_id))
                                          .ToArray();

                        if (retrievedScores.Length == State.Users.Count)
                            break;

                        // delay cancelable por el cap (ScoreRetrievalWaitTime): al expirar cortamos
                        // el wait en el acto en vez de comernos hasta 1s extra reteniendo el lock de
                        // la sala (que dispararia TimeoutException en operaciones concurrentes).
                        try
                        {
                            await Task.Delay(1000, cts.Token);
                        }
                        catch (OperationCanceledException)
                        {
                        }
                    }

                    scores.AddRange(retrievedScores);
                }
            }

            foreach ((int userId, RankedPlayUserInfo info) in State.Users)
            {
                // Add dummy scores for all users that did not play the map.
                if (scores.All(s => s.user_id != userId))
                    scores.Add(new SoloScore { user_id = (uint)userId });

                // arranca a todos con un damage info default (0). asi el cliente resetea.
                info.DamageInfo = Controller.Damage(userId);
            }

            int winningTotalScore = (int)scores.Select(s => s.total_score).Max();
            SoloScore[] winningScores = scores.Where(u => u.total_score == winningTotalScore).ToArray();
            winningUserId = winningScores.Length == 1 ? (int)winningScores.Single().user_id : null;

            if (winningUserId != null)
            {
                // el GANADOR de la ronda le pega al perdedor: (diferencia de score) escalada
                // por el multiplier (room + del ganador), MAS el bonus base de 50k amarillo.
                SoloScore losingScore = scores.Single(u => u.user_id != winningUserId);

                int attackDamage = winningTotalScore - (int)losingScore.total_score;
                double attackMultiplier = State.DamageMultiplier + State.Users[winningUserId.Value].DamageMultiplier;

                State.Users[(int)losingScore.user_id].DamageInfo = Controller.Damage((int)losingScore.user_id, attackDamage, attackMultiplier, BaseDamage);
                State.Users[(int)winningUserId].RoundsWon += 1;
            }

            await Controller.MatchmakingService.RecordBeatmapResult(
                Controller.PoolId,
                Room.CurrentPlaylistItem.BeatmapID,
                Room.CurrentPlaylistItem.RequiredMods.ToArray(),
                scores.Select(s => (int)s.total_score).ToArray(),
                scores.Select(s => Controller.RatingByUser[(int)s.user_id]).ToArray());

            if (!HasGameplayRoundsRemaining())
                await Controller.HandleMatchCompleted();
        }

        protected override async Task Finish()
        {
            // el GANADOR de la ronda se lleva su propio boost de multiplier (upstream): su
            // DamageMultiplier per-usuario crece +0.5, que se suma al de la sala en la formula
            // de ataque (snowball del que va ganando). sin esto quedaba en 0 toda la partida.
            if (winningUserId != null)
                State.Users[winningUserId.Value].DamageMultiplier += 0.5;

            foreach ((_, RankedPlayUserInfo userInfo) in State.Users)
                userInfo.DamageInfo = null;

            if (HasGameplayRoundsRemaining())
                await Controller.GotoStage(RankedPlayStage.RoundWarmup);
            else
                await Controller.GotoStage(RankedPlayStage.Ended);
        }

        public override async Task HandleUserLeft(MultiplayerRoomUser user)
        {
            // Allow players to leave early without incurring a loss if they know gameplay won't continue.
            if (HasGameplayRoundsRemaining())
                await KillUser(user);

            // Remain in the results stage, which will naturally transition to the ended stage once the countdown expires.
        }
    }
}
