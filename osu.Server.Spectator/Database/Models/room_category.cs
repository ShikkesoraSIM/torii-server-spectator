// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;

namespace osu.Server.Spectator.Database.Models
{
    // ReSharper disable once InconsistentNaming
    [Serializable]
    public enum room_category
    {
        normal,
        spotlights,
        featured_artist,
        daily_challenge,

        // Torii: g0v0 has its own MySQL ENUM with UPPERCASE values; the
        // case-mismatch is handled by `CaseInsensitiveEnumHandler`. The
        // members below cover values that exist in g0v0 but not in osu-web:
        //
        //   `realtime` — every multiplayer room g0v0 creates uses this
        //   category (osu-web defaults realtime rooms to `normal` instead).
        //   Without this enum value, Dapper crashes the moment it reads any
        //   multiplayer room back: "Requested value 'REALTIME' was not found".
        //
        //   `spotlight` (singular) — g0v0 spells it without the trailing 's'
        //   that osu-web uses. Keep both so Dapper round-trips both
        //   spellings. We never write this value from spectator side.
        realtime,
        spotlight,
    }
}
