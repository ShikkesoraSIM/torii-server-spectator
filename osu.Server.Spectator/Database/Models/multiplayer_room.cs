// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;

// ReSharper disable InconsistentNaming (matches database table)

namespace osu.Server.Spectator.Database.Models
{
    [Serializable]
    public class multiplayer_room
    {
        public long id { get; set; }

        // Torii: g0v0's `rooms` table column is `host_id` (FK to lazer_users.id),
        // not osu-web's `user_id`. Renaming the POCO field so Dapper's
        // SELECT * → POCO column-name auto-mapping picks it up. Without this,
        // `host_id` from the DB row goes nowhere and downstream code that
        // resolves the room host (UpdateRoomHostAsync, transfer-host flows)
        // sees 0 / no host and breaks.
        public int host_id { get; set; }

        public string name { get; set; } = string.Empty;
        public string password { get; set; } = string.Empty;
        public int channel_id { get; set; }
        public DateTimeOffset starts_at { get; set; }
        public DateTimeOffset? ends_at { get; set; }
        public byte max_attempts { get; set; }
        public int participant_count { get; set; }
        public DateTimeOffset? created_at { get; set; }
        public DateTimeOffset? updated_at { get; set; }
        public DateTimeOffset? deleted_at { get; set; }
        public room_category category { get; set; }
        public database_room_status status { get; set; }
        public database_match_type type { get; set; }
        public database_queue_mode queue_mode { get; set; }
        public ushort auto_start_duration { get; set; }
        public bool auto_skip { get; set; }

        // Torii: g0v0 has no `tournament_mode` column on `rooms` (osu-web does).
        // Field kept on the POCO with a hardcoded false getter so any code that
        // reads `room.tournament_mode` still compiles and behaves like a
        // non-tournament room. If/when Torii grows real referee/tournament
        // support, both the DB column and this getter come back together.
        public bool tournament_mode => false;
    }
}
