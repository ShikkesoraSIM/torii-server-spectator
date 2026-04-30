// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// ReSharper disable InconsistentNaming (matches database table)

using System;
using osu.Game.Beatmaps;

namespace osu.Server.Spectator.Database.Models
{
    [Serializable]
    public class database_beatmap
    {
        public int beatmap_id { get; set; }
        public int beatmapset_id { get; set; }
        public string? checksum { get; set; }
        public BeatmapOnlineStatus approved { get; set; }
        public double difficultyrating { get; set; }
        public ushort playmode { get; set; }
        public ushort osu_file_version { get; set; } = 14;

        // Torii: needed by SpectatorHub.processFailtime to bucket the
        // exit/fail timestamp into a 100-slot histogram against `failtime`.
        // M1PP carried this field too; the rebase onto upstream lost it
        // because the upstream osu-server-spectator never reads
        // `osu_beatmaps.total_length` (failtime tracking is a Torii/M1PP
        // feature). Default 0 keeps any code path that doesn't SELECT
        // total_length safe.
        public int total_length { get; set; }
    }
}
