// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using OpenSkillSharp.Models;
using OpenSkillSharp.Rating;
using osu.Server.Spectator.Database;
using osu.Server.Spectator.Database.Models;
using osu.Server.Spectator.Hubs.Multiplayer.Matchmaking.Elo;

namespace osu.Server.Spectator.Hubs.Multiplayer.Matchmaking.Queue
{
    public class MatchmakingBeatmapSelector
    {
        /// <summary>
        /// The approximate number of osu! ruleset beatmaps that should be filtered down to for relevancy purposes.
        /// </summary>
        private const double osu_beatmap_proportion = 1 / 12.0;

        /// <summary>
        /// Contains all ranked beatmaps.
        /// </summary>
        public Dictionary<int, matchmaking_pool_beatmap> GlobalBeatmaps { get; init; } = [];

        private readonly matchmaking_pool pool;
        private readonly ConcurrentDictionary<BeatmapLookupKey, matchmaking_pool_beatmap> beatmaps;
        private readonly IDatabaseFactory dbFactory;

        private readonly ConcurrentQueue<matchmaking_pool_beatmap> pendingUpdates = [];

        public MatchmakingBeatmapSelector(matchmaking_pool pool, Dictionary<BeatmapLookupKey, matchmaking_pool_beatmap> beatmaps, IDatabaseFactory dbFactory)
        {
            this.pool = pool;
            this.beatmaps = new ConcurrentDictionary<BeatmapLookupKey, matchmaking_pool_beatmap>(beatmaps);
            this.dbFactory = dbFactory;
        }

        public MatchmakingBeatmapSelector(matchmaking_pool pool, matchmaking_pool_beatmap[] beatmaps, IDatabaseFactory dbFactory)
            : this(pool, beatmaps.ToDictionary(b => new BeatmapLookupKey(b.beatmap_id, b.mods), b => b), dbFactory)
        {
        }

        /// <summary>
        /// Creates a new <see cref="MatchmakingBeatmapSelector"/>.
        /// </summary>
        /// <param name="pool">The pool.</param>
        /// <param name="dbFactory">The database factory.</param>
        public static async Task<MatchmakingBeatmapSelector> Initialise(matchmaking_pool pool, IDatabaseFactory dbFactory)
        {
            using (var db = dbFactory.GetInstance())
            {
                // Get all ranked beatmaps.
                Dictionary<int, matchmaking_pool_beatmap> globalBeatmaps =
                    (await db.GetMatchmakingGlobalPoolBeatmapsAsync(pool.ruleset_id, pool.variant_id))
                    .Select(b => new matchmaking_pool_beatmap
                    {
                        pool_id = pool.id,
                        beatmap_id = b.beatmap_id,
                        playmode = b.playmode,
                        checksum = b.checksum,
                        difficultyrating = b.difficultyrating,
                        rating = (int)Math.Round(800 + 500 * (Math.Exp(0.16 * b.difficultyrating) - 1)),
                    })
                    .ToDictionary(b => b.beatmap_id, b => b);

                // Get all beatmaps from the pool.
                // torii: indexer (last-wins) en vez de ToDictionary, que tira ArgumentException si el
                // pool junta rows duplicadas (mismo beatmap_id + mods). puede pasar si el upsert de
                // rating inserto de mas, o por data vieja. mejor tolerar el duplicado que caer el match.
                Dictionary<BeatmapLookupKey, matchmaking_pool_beatmap> poolBeatmaps = new Dictionary<BeatmapLookupKey, matchmaking_pool_beatmap>();
                foreach (var b in await db.GetMatchmakingPoolBeatmapsAsync(pool.id))
                    poolBeatmaps[new BeatmapLookupKey(b.beatmap_id, b.mods)] = b;

                // The pool may not contain all ranked beatmaps, so back-fill it.
                foreach ((int beatmapId, matchmaking_pool_beatmap beatmap) in globalBeatmaps)
                    poolBeatmaps.TryAdd(new BeatmapLookupKey(beatmapId, string.Empty), beatmap);

                return new MatchmakingBeatmapSelector(pool, poolBeatmaps, dbFactory)
                {
                    GlobalBeatmaps = globalBeatmaps
                };
            }
        }

        public async Task Update()
        {
            using (var db = dbFactory.GetInstance())
            {
                while (pendingUpdates.TryDequeue(out matchmaking_pool_beatmap? beatmap))
                    await db.UpdateMatchmakingPoolBeatmapRatingAsync(beatmap);
            }
        }

        public async Task AdjustRating(BeatmapLookupKey key, int[] playerScores, EloRating[] playerRatings)
        {
            // Always use the most-recent databased rating values.
            matchmaking_pool_beatmap? beatmap;
            using (var db = dbFactory.GetInstance())
                beatmap = await db.GetMatchmakingPoolBeatmapAsync(pool.id, key.BeatmapId, key.Mods) ?? GlobalBeatmaps.GetValueOrDefault(key.BeatmapId);

            // torii: defensa dura. si el mapa no esta ni en la DB del pool ni en GlobalBeatmaps,
            // NO tiramos: saltamos el ajuste de rating de ESTE mapa. Antes `GlobalBeatmaps[id]`
            // tiraba KeyNotFoundException y ese throw subia hasta ResultsStage.Begin() ->
            // abortaba el results stage a mitad de camino -> un cliente quedaba colgado en
            // "gameplay in progress" y el otro caia al results generico con rank "#-1". El
            // resultado del match (quien gano, el dano) NO depende de esto: es solo la
            // calibracion de dificultad del mapa. Preferible perder un ajuste de rating de mapa
            // que tumbar el finish entero.
            if (beatmap == null)
            {
                Console.WriteLine($"[matchmaking] AdjustRating: beatmap {key.BeatmapId} (mods {key.Mods}) no esta en el pool {pool.id} ni en GlobalBeatmaps; salteo el ajuste de rating.");
                return;
            }

            PlackettLuce model = new PlackettLuce
            {
                Mu = 1500,
                Sigma = 150,
                Beta = 75,
                Tau = 1.5
            };

            double clearThreshold = pool.ruleset_id switch
            {
                0 => 550_000,
                1 => 850_000,
                2 => 850_000,
                3 => 850_000,
                _ => throw new ArgumentException("Unknown ruleset ID.")
            };

            IRating[] ratings = model.Rate(
                                         [
                                             new Team { Players = [model.Rating(beatmap.rating, beatmap.rating_sig)] },
                                             .. playerRatings.Select(p => new Team { Players = [model.Rating(p.Mu, p.Sig)] }).ToArray()
                                         ],
                                         scores:
                                         [
                                             clearThreshold,
                                             .. playerScores
                                         ])
                                     .Select(t => t.Players.Single())
                                     .ToArray();

            matchmaking_pool_beatmap newBeatmap = new matchmaking_pool_beatmap(beatmap)
            {
                rating = ratings[0].Mu,
                rating_sig = ratings[0].Sigma
            };

            // Store the beatmap back so that it can be used for subsequent lookups.
            // torii: guardar newBeatmap (el rating YA ajustado), no el viejo `beatmap`. Antes
            // guardaba el viejo, asi que el rating in-memory del selector nunca avanzaba entre
            // ciclos de refresh de DB (el mapa parecia no calibrarse). newBeatmap es lo que
            // ademas se encola para escribir a la DB abajo, asi in-memory y DB quedan iguales.
            beatmaps[key] = newBeatmap;

            // Write the beatmap to the database in the next update cycle.
            pendingUpdates.Enqueue(newBeatmap);
        }

        /// <summary>
        /// Retrieves a set of playlist items from the pool within an appropriate difficulty range for the lobby.
        /// </summary>
        /// <param name="count">The number of beatmaps to retrieve.</param>
        /// <param name="ratings">The lobby user ratings.</param>
        public matchmaking_pool_beatmap[] GetAppropriateBeatmaps(int count, EloRating[] ratings)
        {
            // torii: target = PROMEDIO de los picks del grupo, no el mas bajo. antes usaba .Min() y un
            // pick bajo (ej 4.3) le tankeaba la dificultad a todo el lobby (el de 6.6 jugaba 3.75). el
            // promedio reparte parejo entre los que quieren distinto nivel.
            double userRatingMu = ratings.Select(r => r.Mu).DefaultIfEmpty(1500).Average();

            // Logistic curve que es angosta (~70pts) en ratings bajos donde hay muchos mapas,
            // y mas ancha (~140pts) en ratings altos donde hay menos. subimos ~40% la sig
            // original (era 50/100) para dar un poco mas de variedad de estrellas sin volver
            // al overshoot: ~+-1.4 estrellas de spread en vez de +-1.
            double ratingSig = 70 + 70 / (1 + Math.Exp(-0.01 * (userRatingMu - 1800)));

            // Set de relevancia: los mapas de MAYOR peso (los mas cercanos al rating de la
            // lobby). DEBE quedar chico: `beatmaps` incluye el backfill global de FA (miles de
            // mapas de TODAS las estrellas), asi que el viejo `beatmaps.Count / 12` agarraba
            // cientos de mapas -> como no hay cientos cerca del rating, el set se llenaba de
            // mapas lejanos y el shuffle uniforme de abajo terminaba sirviendo 0.8* y 10* muy
            // lejos del nivel de los jugadores. Lo acotamos a un multiplo chico de count asi el
            // orden por peso manda y todas las opciones quedan cerca del skill real.
            int relevancySet = Math.Max(count, Math.Min(count * 8, 40));

            return beatmaps.Values.OrderByDescending(b =>
                           {
                               // torii: gaussiana ASIMETRICA. los mapas POR DEBAJO del target decaen mas rapido
                               // (sig * 0.55) que los de arriba: hay muchisimos mas mapas ranked abajo que arriba,
                               // asi que la seleccion simetrica + el shuffle terminaban sirviendo ~1 estrella por
                               // debajo del pick (6.6 -> 5.36 incluso solo). con esto el pool queda a la altura del
                               // pick (~-0.3/-0.5 en vez de -1), dejando algo de variedad hacia arriba (los 8* que gustan).
                               double delta = b.rating - userRatingMu;
                               double sig = delta < 0 ? ratingSig * 0.55 : ratingSig;
                               // The clamp attempts to ensure all beatmaps are given some chance of being selected.
                               double weight = Math.Clamp(Math.Exp(-(delta * delta) / (2 * sig * sig)), 1e-6, 1);
                               return Math.Pow(Random.Shared.NextDouble(), 1.0 / weight);
                           })
                           // Relevancy filter: keep only the nearest-rating maps (weighted).
                           .Take(relevancySet)
                           // Variety filter: shuffle those nearby maps down to the required count.
                           .OrderBy(_ => Random.Shared.NextDouble()).Take(count)
                           .ToArray();
        }

        public readonly record struct BeatmapLookupKey(int BeatmapId, string Mods);
    }
}
