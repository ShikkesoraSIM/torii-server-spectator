// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Data;
using System.Threading;
using Dapper;
using osu.Server.Spectator.Database.Models;

namespace osu.Server.Spectator.Database
{
    public static class DapperExtensions
    {
        /// <summary>
        /// Case-insensitive enum reader for Torii. g0v0 stores room enums in
        /// MySQL ENUM columns with UPPERCASE canonical values
        /// (REALTIME, HEAD_TO_HEAD, HOST_ONLY, IDLE, ...) while the upstream
        /// osu-server-spectator POCOs declare them lowercase (head_to_head,
        /// host_only, ...). Dapper's default enum coercion is case-sensitive
        /// — readers fail with "Requested value 'REALTIME' was not found"
        /// the moment a multiplayer room round-trips the DB. This handler
        /// papers over the case difference on read and emits the C# spelling
        /// on write (MySQL ENUM is already case-insensitive on insert, so
        /// writes work either way).
        /// </summary>
        private class CaseInsensitiveEnumHandler<T> : SqlMapper.TypeHandler<T> where T : struct, Enum
        {
            public override T Parse(object value)
            {
                string raw = value?.ToString() ?? string.Empty;
                if (Enum.TryParse<T>(raw, ignoreCase: true, out var parsed))
                    return parsed;
                throw new ArgumentException($"Cannot map '{raw}' to enum {typeof(T).Name}");
            }

            public override void SetValue(IDbDataParameter parameter, T value)
            {
                parameter.Value = value.ToString();
            }
        }

        // see https://stackoverflow.com/questions/12510299/get-datetime-as-utc-with-dapper
        public class DateTimeOffsetTypeHandler : SqlMapper.TypeHandler<DateTimeOffset>
        {
            public override void SetValue(IDbDataParameter parameter, DateTimeOffset value)
            {
                switch (parameter.DbType)
                {
                    case DbType.DateTime:
                    case DbType.DateTime2:
                    case DbType.AnsiString: // Seems to be some MySQL type mapping here
                        parameter.Value = value.UtcDateTime;
                        break;

                    case DbType.DateTimeOffset:
                    case DbType.String: // TODO: i don't know why it's coming in as a string but let's just try and handle it for now?
                        parameter.Value = value;
                        break;

                    default:
                        throw new InvalidOperationException("Must be DateTime or DateTimeOffset object to be mapped.");
                }
            }

            public override DateTimeOffset Parse(object value)
            {
                switch (value)
                {
                    case DateTime time:
                        return new DateTimeOffset(DateTime.SpecifyKind(time, DateTimeKind.Utc), TimeSpan.Zero);

                    case DateTimeOffset dto:
                        return dto;

                    default:
                        throw new InvalidOperationException("Must be DateTime or DateTimeOffset object to be mapped.");
                }
            }
        }

        private static int dateTimeOffsetMapperInstalled;

        public static void InstallDateTimeOffsetMapper()
        {
            // Assumes SqlMapper.ResetTypeHandlers() is never called.
            if (Interlocked.CompareExchange(ref dateTimeOffsetMapperInstalled, 1, 0) == 0)
            {
                // First remove the default type map between typeof(DateTimeOffset) => DbType.DateTimeOffset (not valid for MySQL)
                SqlMapper.RemoveTypeMap(typeof(DateTimeOffset));
                SqlMapper.RemoveTypeMap(typeof(DateTimeOffset?));

                // This handles nullable value types automatically e.g. DateTimeOffset?
                SqlMapper.AddTypeHandler(typeof(DateTimeOffset), new DateTimeOffsetTypeHandler());
            }
        }

        private static int toriiEnumMappersInstalled;

        /// <summary>
        /// Registers case-insensitive enum readers for the room-related enums
        /// that g0v0 stores in UPPERCASE MySQL ENUM columns. Idempotent and
        /// thread-safe; intended to be called once at process start (lazily on
        /// first DB connection like <see cref="InstallDateTimeOffsetMapper"/>).
        /// </summary>
        public static void InstallToriiEnumMappers()
        {
            if (Interlocked.CompareExchange(ref toriiEnumMappersInstalled, 1, 0) != 0)
                return;

            SqlMapper.AddTypeHandler(typeof(room_category), new CaseInsensitiveEnumHandler<room_category>());
            SqlMapper.AddTypeHandler(typeof(database_match_type), new CaseInsensitiveEnumHandler<database_match_type>());
            SqlMapper.AddTypeHandler(typeof(database_queue_mode), new CaseInsensitiveEnumHandler<database_queue_mode>());
            SqlMapper.AddTypeHandler(typeof(database_room_status), new CaseInsensitiveEnumHandler<database_room_status>());
        }
    }
}
