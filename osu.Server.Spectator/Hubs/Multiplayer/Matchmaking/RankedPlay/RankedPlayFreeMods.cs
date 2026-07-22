// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Linq;
using osu.Game.Online.API;

namespace osu.Server.Spectator.Hubs.Multiplayer.Matchmaking.RankedPlay
{
    /// <summary>
    /// torii: mods opcionales que el jugador puede prender por su cuenta antes de la ronda (via
    /// AllowedMods en el playlist item). DECISION: solo HD. Es un toggle de playstyle para la gente
    /// que no puede jugar sin Hidden; NO da ventaja de score (el multiplier de HD se neutraliza al
    /// calcular el dano del card-duel). Nada de rate mods (DT/HT romperian el elo/rating de mapas que
    /// asume dificultad fija) ni mods baneados/0pp de Torii. HD esta en la blacklist mods_can_get_pp
    /// de g0v0 asi que el score de ranked no queda en 0pp.
    /// </summary>
    public static class RankedPlayFreeMods
    {
        // solo HD, para todos los rulesets (incluida mania).
        private static readonly string[] allowed = { @"HD" };

        /// <summary>Free mods permitidos para un ruleset (id legacy: 0 osu, 1 taiko, 2 catch, 3 mania).</summary>
        public static APIMod[] ForRuleset(int rulesetId)
            => allowed.Select(acronym => new APIMod { Acronym = acronym }).ToArray();
    }
}
