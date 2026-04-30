// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;

namespace osu.Server.Spectator
{
    /// <summary>
    /// Torii: ports M1PP's blob helper. The `failtime` table on g0v0 stores
    /// fail/exit histograms as a varbinary(400) — a packed array of 100 ints
    /// (4 bytes each, little-endian). These helpers serialise/deserialise
    /// that format so SpectatorHub.processFailtime can read-modify-write a
    /// single bucket per finished play.
    /// </summary>
    public static class BlobHelper
    {
        public static int[] ParseBlobToIntArray(byte[] blob)
        {
            int[] result = new int[blob.Length / 4];

            for (int i = 0; i < blob.Length; i += 4)
            {
                result.SetValue(BitConverter.ToInt32(blob, i), i / 4);
            }

            return result;
        }

        public static byte[] IntArrayToBlob(int[] array)
        {
            byte[] result = new byte[array.Length * 4];

            for (int i = 0; i < array.Length; i++)
            {
                byte[] intBytes = BitConverter.GetBytes(array[i]);
                Array.Copy(intBytes, 0, result, i * 4, 4);
            }

            return result;
        }
    }
}
