// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Linq;
using osu.Game.Online.API;

namespace osu.Server.Spectator.Helpers
{
    /// <summary>
    /// Torii: ports M1PP's helper for resolving the storage `mode` string
    /// that g0v0's `lazer_user_statistics.mode` ENUM expects. The mapping
    /// is one-to-one with the lazer ruleset id for the four base rulesets,
    /// and additionally promotes Relax (RX) / Autopilot (AP) modded plays
    /// into pseudo-rulesets (`OSURX`, `TAIKORX`, `FRUITSRX`, `OSUAP`) when
    /// the corresponding feature flag is on. Used by SpectatorHub.editPlayTime
    /// to split per-mode playtime accounting the same way M1PP did.
    /// </summary>
    public static class GameModeHelper
    {
        public static string GameModeToString(int gameMode)
        {
            return gameMode switch
            {
                0 => "OSU",
                1 => "TAIKO",
                2 => "FRUITS",
                3 => "MANIA",
                _ => "Unknown"
            };
        }

        public static string GameModeToStringSpecial(int gameMode, APIMod[] mods)
        {
            // Mania doesn't get an RX/AP variant on g0v0, and if both flags
            // are off there's nothing to promote.
            if ((gameMode != 0 && gameMode != 1 && gameMode != 2) || (!AppSettings.EnableRX && !AppSettings.EnableAP))
                return GameModeToString(gameMode);

            string[] modAcronyms = mods.Select(m => m.Acronym).ToArray();

            if (AppSettings.EnableAP && modAcronyms.Contains("AP"))
                return "OSUAP";

            if (AppSettings.EnableRX && modAcronyms.Contains("RX"))
            {
                return gameMode switch
                {
                    0 => "OSURX",
                    1 => "TAIKORX",
                    2 => "FRUITSRX",
                    _ => "Unknown"
                };
            }

            return GameModeToString(gameMode);
        }
    }
}
