// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using System.Threading.Tasks;
using osu.Game.Online.Matchmaking;
using osu.Game.Online.Matchmaking.Requests;
using osu.Game.Online.Matchmaking.Responses;
using osu.Game.Online.Multiplayer;
using osu.Server.Spectator.Database.Models;
using osu.Server.Spectator.Extensions;

namespace osu.Server.Spectator.Hubs.Multiplayer
{
    public partial class MultiplayerHub : IMatchmakingServer
    {
        // Provided for backwards compatibility. Can be removed 20260727.
        public Task<MatchmakingPool[]> GetMatchmakingPools()
            => GetMatchmakingPoolsOfType(MatchmakingPoolType.QuickPlay);

        // Provided for backwards compatibility. Can be removed 20261001.
        public async Task MatchmakingJoinLobby()
        {
            using (var db = databaseFactory.GetInstance())
            {
                // Since this is only a compatibility method, we don't really care WHICH lobby is joined, as long as it's one of the active pools.
                matchmaking_pool? pool = (await db.GetActiveMatchmakingPoolsAsync()).FirstOrDefault();

                if (pool == null)
                    return;

                await MatchmakingJoinLobbyWithParams(new MatchmakingJoinLobbyRequest { PoolId = (int)pool.id });
            }
        }

        public async Task<MatchmakingPool[]> GetMatchmakingPoolsOfType(MatchmakingPoolType type)
        {
            using (var db = databaseFactory.GetInstance())
            {
                return (await db.GetActiveMatchmakingPoolsAsync())
                       .Select(p => p.ToMatchmakingPool())
                       .Where(p => p.Type == type)
                       .ToArray();
            }
        }

        public async Task<MatchmakingJoinLobbyResponse> MatchmakingJoinLobbyWithParams(MatchmakingJoinLobbyRequest request)
        {
            using (var userUsage = await GetOrCreateLocalUserState())
                await matchmakingQueueService.AddToLobbyAsync(userUsage.Item!, request.PoolId);

            return new MatchmakingJoinLobbyResponse();
        }

        public async Task MatchmakingLeaveLobby()
        {
            using (var userUsage = await GetOrCreateLocalUserState())
                await matchmakingQueueService.RemoveFromLobbyAsync(userUsage.Item!);
        }

        public async Task MatchmakingJoinQueue(int poolId)
        {
            using (var userUsage = await GetOrCreateLocalUserState())
                await matchmakingQueueService.AddToQueueAsync(userUsage.Item!, poolId);
        }

        public async Task MatchmakingLeaveQueue()
        {
            using (var userUsage = await GetOrCreateLocalUserState())
                await matchmakingQueueService.RemoveFromQueueAsync(userUsage.Item!);
        }

        public async Task MatchmakingAcceptInvitation()
        {
            using (var userUsage = await GetOrCreateLocalUserState())
                await matchmakingQueueService.AcceptInvitationAsync(userUsage.Item!);
        }

        public async Task<MatchmakingIssueDuelResponse> MatchmakingIssueDuel(MatchmakingIssueDuelRequest request)
        {
            using (var userUsage = await GetOrCreateLocalUserState())
                return await matchmakingQueueService.IssueDuelAsync(userUsage.Item!, request);
        }

        public async Task<MatchmakingAcceptDuelResponse> MatchmakingAcceptDuel(MatchmakingAcceptDuelRequest request)
        {
            using (var userUsage = await GetOrCreateLocalUserState())
                return await matchmakingQueueService.AcceptDuelAsync(userUsage.Item!, request);
        }

        public async Task MatchmakingDeclineInvitation()
        {
            using (var userUsage = await GetOrCreateLocalUserState())
                await matchmakingQueueService.DeclineInvitationAsync(userUsage.Item!);
        }

        public async Task MatchmakingToggleSelection(long playlistItemId)
        {
            using (var userUsage = await GetOrCreateLocalUserState())
            using (var roomUsage = await getLocalUserRoom(userUsage.Item!))
            {
                var room = roomUsage.Item;
                if (room == null)
                    throw new InvalidOperationException("Attempted to operate on a null room");

                await room.MatchmakingToggleSelection(Context.GetUserId(), playlistItemId);
            }
        }

        public async Task MatchmakingSkipToNextStage()
        {
            // This is only used for testing purposes right now.
            // It causes the room to skip forward with *any* user's request, which will not work well in standard usage.
            if (!AppSettings.MatchmakingRoomAllowSkip)
                throw new InvalidStateException("Skipping matchmaking rounds is not allowed.");

            using (var userUsage = await GetOrCreateLocalUserState())
            using (var roomUsage = await getLocalUserRoom(userUsage.Item!))
            {
                var room = roomUsage.Item;
                if (room == null)
                    throw new InvalidOperationException("Attempted to operate on a null room");

                room.MatchmakingSkipToNextStage(Context.GetUserId(), out _);
            }
        }

        /// <summary>
        /// torii GHOST CURSOR: el cliente manda su posicion de cursor (normalizada 0..1) mientras
        /// esta en las pantallas de ranked play y la relayeamos al resto de su room tal cual.
        /// Invocado por NOMBRE (el cliente hace SendAsync("RankedPlayCursor", x, y)) — no forma
        /// parte de la interface tipada del hub a proposito, para no tocar el contrato del paquete.
        /// </summary>
        public async Task RankedPlayCursor(float x, float y)
        {
            using (var userUsage = await GetOrCreateLocalUserState())
            {
                long? roomId = userUsage.Item?.CurrentRoomID;

                if (roomId == null)
                    return;

                await multiplayerEventDispatcher.RelayRankedPlayCursorAsync(roomId.Value, Context.GetUserId(), x, y, Context.ConnectionId);
            }
        }

        /// <summary>
        /// torii DIBUJITO: chunk de trazo de la pizarra de ranked play, mismo esquema que el ghost
        /// cursor (invocado por nombre, relay sin estado). Caps defensivos por si un cliente
        /// modificado manda cualquier cosa: chunk acotado, arrays parejos y grosor clampeado.
        /// </summary>
        public async Task RankedPlayDrawStroke(int strokeId, float[] xs, float[] ys, int colour, float thickness, bool done)
        {
            if (xs.Length == 0 || xs.Length != ys.Length || xs.Length > 64)
                return;

            using (var userUsage = await GetOrCreateLocalUserState())
            {
                long? roomId = userUsage.Item?.CurrentRoomID;

                if (roomId == null)
                    return;

                await multiplayerEventDispatcher.RelayRankedPlayDrawStrokeAsync(roomId.Value, Context.GetUserId(), strokeId, xs, ys, colour, Math.Clamp(thickness, 1f, 24f), done, Context.ConnectionId);
            }
        }

        /// <summary>torii DIBUJITO: borrar todos los trazos propios (la basurita de la paleta).</summary>
        public async Task RankedPlayDrawClear()
        {
            using (var userUsage = await GetOrCreateLocalUserState())
            {
                long? roomId = userUsage.Item?.CurrentRoomID;

                if (roomId == null)
                    return;

                await multiplayerEventDispatcher.RelayRankedPlayDrawClearAsync(roomId.Value, Context.GetUserId(), Context.ConnectionId);
            }
        }
    }
}
