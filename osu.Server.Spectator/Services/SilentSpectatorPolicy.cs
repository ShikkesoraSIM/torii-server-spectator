// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Globalization;
using osu.Game.Users;

namespace osu.Server.Spectator.Services
{
    public sealed class SilentSpectatorPolicy
    {
        private readonly HashSet<int> userIds = new HashSet<int>();

        public SilentSpectatorPolicy(string? configuredUserIds)
        {
            foreach (string value in (configuredUserIds ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int userId) || userId <= 0)
                    throw new ArgumentException("SILENT_SPECTATOR_USER_IDS must contain positive user IDs separated by commas.", nameof(configuredUserIds));

                userIds.Add(userId);
            }
        }

        public bool IsSilent(int userId) => userIds.Contains(userId);

        public UserPresence? GetPublicPresence(int userId, UserPresence? presence)
        {
            if (!IsSilent(userId) || presence?.Activity is not (UserActivity.SpectatingUser or UserActivity.SpectatingMultiplayerGame))
                return presence;

            var publicPresence = presence.Value;
            publicPresence.Activity = null;
            return publicPresence;
        }
    }
}
