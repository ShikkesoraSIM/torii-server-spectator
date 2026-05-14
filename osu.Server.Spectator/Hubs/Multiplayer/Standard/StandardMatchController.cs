// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using osu.Game.Online.Multiplayer;
using osu.Game.Online.Rooms;
using osu.Game.Rulesets;
using osu.Server.Spectator.Database;
using osu.Server.Spectator.Database.Models;
using osu.Server.Spectator.Extensions;

namespace osu.Server.Spectator.Hubs.Multiplayer.Standard
{
    /// <summary>
    /// Abstract class that implements the logic for a generic multiplayer room.
    /// </summary>
    [NonController]
    public abstract class StandardMatchController : IMatchController
    {
        public const int HOST_PLAYLIST_LIMIT = 50;
        public const int GUEST_PLAYLIST_LIMIT = 3;

        public MultiplayerPlaylistItem CurrentItem => room.Playlist[currentPlaylistItemIndex];

        private readonly ServerMultiplayerRoom room;
        private readonly IDatabaseFactory dbFactory;
        private readonly MultiplayerEventDispatcher eventDispatcher;

        private QueueMode queueMode;
        private int currentPlaylistItemIndex;

        protected StandardMatchController(ServerMultiplayerRoom room, IDatabaseFactory dbFactory, MultiplayerEventDispatcher eventDispatcher)
        {
            this.room = room;
            this.dbFactory = dbFactory;
            this.eventDispatcher = eventDispatcher;

            queueMode = room.Settings.QueueMode;
        }

        /// <summary>
        /// Initialises the queue from the database.
        /// </summary>
        public virtual async Task Initialise()
        {
            using (var db = dbFactory.GetInstance())
                await updatePlaylistOrder(db);

            await updateCurrentItem();
        }

        public Task<bool> UserCanJoin(int userId)
            => Task.FromResult(true);

        /// <summary>
        /// Updates the queue as a result of a change in the queueing mode.
        /// </summary>
        public virtual async Task HandleSettingsChanged()
        {
            if (queueMode == room.Settings.QueueMode)
                return;

            queueMode = room.Settings.QueueMode;

            using (var db = dbFactory.GetInstance())
            {
                // When changing to host-only mode, ensure that at least one non-expired playlist item exists by duplicating the current item.
                if (room.Settings.QueueMode == QueueMode.HostOnly && room.Playlist.All(item => item.Expired))
                    await addItem(db, CurrentItem.Clone());

                if (room.State == MultiplayerRoomState.Open)
                    await updatePlaylistOrder(db);
            }

            if (room.State == MultiplayerRoomState.Open)
                await updateCurrentItem();
        }

        /// <summary>
        /// Expires the current playlist item and advances to the next one in the order defined by the queueing mode.
        /// </summary>
        public virtual async Task HandleGameplayCompleted()
        {
            using (var db = dbFactory.GetInstance())
            {
                // Expire and let clients know that the current item has finished.
                await db.MarkPlaylistItemAsPlayedAsync(room.RoomID, CurrentItem.ID);
                room.Playlist[currentPlaylistItemIndex] = (await db.GetPlaylistItemAsync(room.RoomID, CurrentItem.ID)).ToMultiplayerPlaylistItem();

                await room.HandlePlaylistItemChanged(CurrentItem, true);
                await updatePlaylistOrder(db);

                // Torii: auto-clone the just-played item if the queue is now
                // exhausted, in ANY queue mode. Upstream restricted this to
                // HostOnly because in shared-queue modes (AllPlayers,
                // RoundRobin) the assumption is that other users queued items
                // to take over after the current one expires. On Torii's
                // smaller-server scale we routinely hit "single-host room
                // plays the only map, queue exhausts, next ready-up trips
                // `Cannot ready up while all items have been played.`" in
                // ServerMultiplayerRoom.cs because CurrentItem stays expired
                // with no successor to advance to. Auto-cloning unconditionally
                // when the queue exhausts unblocks the ready-up cycle for any
                // queue mode at the cost of attributing the new item to the
                // just-played item's owner (which is correct for HostOnly and
                // acceptable for the other modes as a stopgap).
                //
                // PATH_1B_PLAN.md Phase 6 replaces this with proper "queue
                // exhausted" state owned by g0v0 (with an optional
                // AUTO_CLONE_EXHAUSTED_QUEUE config flag), at which point this
                // patch can be reverted to the upstream HostOnly-only check.
                if (room.Playlist.All(item => item.Expired))
                    await addItem(db, CurrentItem.Clone());
            }

            await updateCurrentItem();
        }

        public virtual async Task HandleUserRequest(MultiplayerRoomUser user, MatchUserRequest request)
        {
            switch (request)
            {
                case RollRequest rollRequest:
                    if (rollRequest.Max < 2 || rollRequest.Max > 100)
                        throw new InvalidStateException("Invalid roll request. Max must be in [2, 100] range inclusive.");

                    uint max = rollRequest.Max ?? 100;
                    uint result = (uint)Random.Shared.Next(1, 1 + (int)max);
                    var resultEvent = new RollEvent { UserID = user.UserID, Max = max, Result = result };
                    await eventDispatcher.PostRollEventAsync(room.RoomID, resultEvent);
                    break;
            }
        }

        public virtual Task HandleUserJoined(MultiplayerRoomUser user)
        {
            return Task.CompletedTask;
        }

        public virtual Task HandleUserLeft(MultiplayerRoomUser user)
        {
            return Task.CompletedTask;
        }

        public virtual Task HandleUserStateChanged(MultiplayerRoomUser user)
        {
            return Task.CompletedTask;
        }

        private bool isHostOrReferee(MultiplayerRoomUser user)
            => user.Equals(room.Host) || user.Role == MultiplayerRoomUserRole.Referee;

        /// <summary>
        /// Add a playlist item to the room's queue.
        /// </summary>
        /// <param name="item">The item to add.</param>
        /// <param name="user">The user adding the item.</param>
        /// <exception cref="NotHostException">If the adding user is not the host in host-only mode.</exception>
        /// <exception cref="InvalidStateException">If the given playlist item is not valid.</exception>
        public virtual async Task AddPlaylistItem(MultiplayerPlaylistItem item, MultiplayerRoomUser user)
        {
            bool isHostOnly = room.Settings.QueueMode == QueueMode.HostOnly;

            if (isHostOnly && !isHostOrReferee(user))
                throw new NotHostException();

            int limit = isHostOrReferee(user) ? HOST_PLAYLIST_LIMIT : GUEST_PLAYLIST_LIMIT;

            if (room.Playlist.Count(i => i.OwnerID == user.UserID && !i.Expired) >= limit)
                throw new InvalidStateException($"Can't enqueue more than {limit} items at once.");

            if (item.Freestyle && item.AllowedMods.Any())
                throw new InvalidStateException("Cannot enqueue freestyle item with mods.");

            using (var db = dbFactory.GetInstance())
            {
                // Torii: use the OrFetch variant so adding a map that g0v0
                // hasn't cached yet (any map nobody on this server has played)
                // bootstraps the `beatmaps` row via _lio/beatmaps/ensure first.
                // Without this, the AddPlaylistItem INSERT would trip the
                // `room_playlists.beatmap_id -> beatmaps.id` FK and 500.
                var beatmap = await db.GetBeatmapOrFetchAsync(item.BeatmapID);

                if (beatmap == null)
                    throw new InvalidStateException("Attempted to add a beatmap which does not exist online.");

                if (item.BeatmapChecksum != beatmap.checksum)
                    throw new InvalidStateException("Attempted to add a beatmap which has been modified.");

                if (item.RulesetID < 0 || item.RulesetID > ILegacyRuleset.MAX_LEGACY_RULESET_ID)
                    throw new InvalidStateException("Attempted to select an unsupported ruleset.");

                if (beatmap.playmode != 0 && item.RulesetID != beatmap.playmode)
                    throw new InvalidStateException("Attempted to select an invalid beatmap and ruleset combination.");

                item.EnsureModsValid();
                item.OwnerID = user.UserID;
                item.StarRating = beatmap.difficultyrating;

                await addItem(db, item);
                if (room.State == MultiplayerRoomState.Open)
                    await updateCurrentItem();
            }
        }

        public virtual async Task EditPlaylistItem(MultiplayerPlaylistItem item, MultiplayerRoomUser user)
        {
            if (item.Freestyle && item.AllowedMods.Any())
                throw new InvalidStateException("Cannot enqueue freestyle item with mods.");

            using (var db = dbFactory.GetInstance())
            {
                // Torii: same auto-fetch as AddPlaylistItem above. EditPlaylistItem
                // changes the beatmap on an existing slot, so the new beatmap may
                // also be one g0v0 hasn't seen yet.
                var beatmap = await db.GetBeatmapOrFetchAsync(item.BeatmapID);

                if (beatmap == null)
                    throw new InvalidStateException("Attempted to add a beatmap which does not exist online.");

                if (item.BeatmapChecksum != beatmap.checksum)
                    throw new InvalidStateException("Attempted to add a beatmap which has been modified.");

                if (item.RulesetID < 0 || item.RulesetID > ILegacyRuleset.MAX_LEGACY_RULESET_ID)
                    throw new InvalidStateException("Attempted to select an unsupported ruleset.");

                if (beatmap.playmode != 0 && item.RulesetID != beatmap.playmode)
                    throw new InvalidStateException("Attempted to select an invalid beatmap and ruleset combination.");

                item.EnsureModsValid();
                item.OwnerID = user.UserID;
                item.StarRating = beatmap.difficultyrating;

                var existingItem = room.Playlist.SingleOrDefault(i => i.ID == item.ID);

                if (ReferenceEquals(existingItem, CurrentItem))
                {
                    // Mid-play edits are a real bug (would mutate the running
                    // match's target) — surface this one.
                    if (room.State != MultiplayerRoomState.Open)
                        throw new InvalidStateException("The current item in the room cannot be edited when currently being played.");
                }

                // Torii: silent no-op family (matches the RemovePlaylistItem
                // treatment further down). "Item gone" and "already played"
                // are both side effects of normal post-gameplay queue churn
                // racing the client's UI state — they're not actionable
                // errors and the toast adds noise without helping.
                // The permission-denied case stays as a real error because
                // it represents the user genuinely doing something they
                // shouldn't.
                if (existingItem == null)
                    return; // idempotent: nothing to update.

                if (existingItem.OwnerID != user.UserID && !isHostOrReferee(user))
                    throw new InvalidStateException("Attempted to change an item which is not owned by the user.");

                if (existingItem.Expired)
                    return; // history; the client's optimistic edit settles when RoomUpdated arrives.

                // Ensure the playlist order doesn't change.
                item.PlaylistOrder = existingItem.PlaylistOrder;

                await db.UpdatePlaylistItemAsync(new multiplayer_playlist_item(room.RoomID, item));
                room.Playlist[room.Playlist.IndexOf(existingItem)] = item;

                await room.HandlePlaylistItemChanged(item, existingItem.BeatmapChecksum != item.BeatmapChecksum);
            }
        }

        /// <summary>
        /// Removes a playlist item from the room's queue.
        /// </summary>
        /// <param name="playlistItemId">The item to remove.</param>
        /// <param name="user">The user removing the item.</param>
        public virtual async Task RemovePlaylistItem(long playlistItemId, MultiplayerRoomUser user)
        {
            var item = room.Playlist.FirstOrDefault(item => item.ID == playlistItemId);

            // Torii: silent no-op family.
            //
            // The original osu! spectator code throws InvalidStateException for
            // these cases. SignalR turns each one into a user-facing error toast
            // ("The only item in the room cannot be removed.", "Attempted to
            // remove an item which has already been played."). Live data showed
            // hosts triggering these every time they post-played map and tried
            // to clean up the queue — a race between client state and server
            // state where the client thought the remove button was valid
            // (item not yet expired, queue had >1 item) and the server's
            // post-gameplay state had already advanced. None of these cases
            // are bugs the user can do anything about — they can't unexpire
            // the item, and "the only item" is by design (room must have ≥1
            // playable item). Returning silently from here lets the client's
            // optimistic UI settle on the next RoomUpdated (which carries the
            // authoritative state) without ever showing the toast.
            //
            // Genuine error cases (permission denied, removing the current
            // item mid-play) are still thrown — those are bugs in the caller's
            // intent that the user CAN act on (don't try, or wait until the
            // map ends).
            if (item == null)
            {
                // Idempotent silent — the item is genuinely gone (raced with
                // another remove, or never existed). Nothing actionable for
                // the user; the optimistic UI will settle on next RoomUpdated.
                room.Log($"RemovePlaylistItem: item {playlistItemId} not found in playlist, silent no-op");
                return;
            }

            if (ReferenceEquals(item, CurrentItem))
            {
                // Mid-play removal of the current item would corrupt the
                // running match — keep this as a hard error. Check BEFORE the
                // only-item path so a mid-play single-item room errors
                // instead of accepting an action that would break gameplay.
                if (room.State != MultiplayerRoomState.Open)
                    throw new InvalidStateException("The current item in the room cannot be removed when currently being played.");

                // Only-upcoming case: previously a silent no-op (upstream
                // invariant: room needs ≥1 playable item). The silent return
                // left users stuck — they'd click remove, nothing happened,
                // no toast, no clue what to do. Surface a CLEAR error
                // message instead so they understand AND have an obvious
                // next step ("add another, then try again"). The state-
                // machine invariant stays intact; only the UX feedback
                // changes from silent → actionable.
                if (UpcomingItems.Count() == 1)
                {
                    throw new InvalidStateException(
                        "Can't remove the only map in the queue — add another beatmap first, then this one can be removed.");
                }
            }

            // Permission check stays as a real error — user trying to remove
            // someone else's queued map without being host or referee.
            if (item.OwnerID != user.UserID && !isHostOrReferee(user))
                throw new InvalidStateException("Attempted to remove an item which is not owned by the user.");

            // Expired = already removed from the queue (in history view).
            // Silent no-op rather than error — lazer client moves expired
            // items from Queue to History; the user clicking Remove on a
            // history item is a no-op semantically.
            if (item.Expired)
            {
                room.Log($"RemovePlaylistItem: item {playlistItemId} is already expired (in history), silent no-op");
                return;
            }

            using (var db = dbFactory.GetInstance())
            {
                // Score-link guard — the item has scores attached, FK
                // constraint won't let us delete. Same silent semantics as
                // the .Expired check: the item is "history" from the user's
                // perspective.
                if (await db.AnyScoreTokenExistsFor(room.RoomID, playlistItemId))
                {
                    room.Log($"RemovePlaylistItem: item {playlistItemId} has attached scores (history-linked), silent no-op");
                    return;
                }

                await db.RemovePlaylistItemAsync(room.RoomID, playlistItemId);

                room.Playlist.Remove(item);

                // If either an item indexed earlier in the list was removed or the current item was removed, the index needs to be refreshed.
                // Importantly, this is done before the playlist order is updated since the update requires the current item.
                currentPlaylistItemIndex = room.Playlist.IndexOf(UpcomingItems.First());

                if (room.State == MultiplayerRoomState.Open)
                    await updatePlaylistOrder(db);
            }

            if (room.State == MultiplayerRoomState.Open)
                await updateCurrentItem();

            // It's important for clients to be notified of the removal AFTER settings are changed
            // so that PlaylistItemId always points to a valid item in the playlist.
            await eventDispatcher.PostPlaylistItemRemovedAsync(room.RoomID, playlistItemId);
        }

        public abstract MatchStartedEventDetail GetMatchDetails();

        private async Task addItem(IDatabaseAccess db, MultiplayerPlaylistItem item)
        {
            // Add the item to the end of the list initially.
            item.PlaylistOrder = ushort.MaxValue;
            item.Expired = false;
            item.PlayedAt = null;
            item.ID = await db.AddPlaylistItemAsync(new multiplayer_playlist_item(room.RoomID, item));

            room.Playlist.Add(item);
            await eventDispatcher.PostPlaylistItemAddedAsync(room.RoomID, item);

            if (room.State == MultiplayerRoomState.Open)
                await updatePlaylistOrder(db);
        }

        public IEnumerable<MultiplayerPlaylistItem> UpcomingItems => room.Playlist.Where(i => !i.Expired).OrderBy(i => i.PlaylistOrder);

        /// <summary>
        /// Updates <see cref="CurrentItem"/> and the playlist item ID stored in the room's settings.
        /// </summary>
        private async Task updateCurrentItem()
        {
            if (room.State != MultiplayerRoomState.Open)
                throw new InvalidOperationException("Can't update current item when game is being played");

            // Pick the next non-expired playlist item by playlist order, or default to the most-recently-expired item.
            MultiplayerPlaylistItem nextItem = UpcomingItems.FirstOrDefault() ?? room.Playlist.OrderByDescending(i => i.PlayedAt).First();

            currentPlaylistItemIndex = room.Playlist.IndexOf(nextItem);

            long lastItemID = room.Settings.PlaylistItemId;
            room.Settings.PlaylistItemId = nextItem.ID;

            if (nextItem.ID != lastItemID)
                await room.HandleSettingsChanged(true);
        }

        /// <summary>
        /// Updates the order of items in the playlist according to the queueing mode.
        /// </summary>
        private async Task updatePlaylistOrder(IDatabaseAccess db)
        {
            if (room.State != MultiplayerRoomState.Open)
                throw new InvalidOperationException("Can't update playlist order when game is being played");

            List<MultiplayerPlaylistItem> orderedActiveItems;

            switch (room.Settings.QueueMode)
            {
                default:
                    orderedActiveItems = room.Playlist.Where(item => !item.Expired).OrderBy(item => item.ID).ToList();
                    break;

                case QueueMode.AllPlayersRoundRobin:
                    orderedActiveItems = new List<MultiplayerPlaylistItem>();

                    bool isFirstSet = true;
                    var firstSetOrderByUserId = new Dictionary<int, int>();

                    // Group each user's items in order of addition.
                    var userItemGroups = room.Playlist.Where(item => !item.Expired).OrderBy(item => item.ID).GroupBy(item => item.OwnerID);

                    foreach (IEnumerable<MultiplayerPlaylistItem> set in userItemGroups.Interleave())
                    {
                        // Do some post processing on the set of items to ensure that the order is consistent.
                        if (isFirstSet)
                        {
                            // For the first set, preserve the existing order of items and break ties based on the order in which items were added to the queue.
                            orderedActiveItems.AddRange(set.OrderBy(item => item.PlaylistOrder).ThenBy(item => item.ID));

                            // Store the order of items to be used for all future sets.
                            firstSetOrderByUserId = orderedActiveItems.Select((item, index) => (item, index)).ToDictionary(i => i.item.OwnerID, i => i.index);
                        }
                        else
                        {
                            // For the non-first set, preserve the same ordering of users as in the first set.
                            orderedActiveItems.AddRange(set.OrderBy(i => firstSetOrderByUserId[i.OwnerID]));
                        }

                        isFirstSet = false;
                    }

                    break;
            }

            for (int i = 0; i < orderedActiveItems.Count; i++)
            {
                var item = orderedActiveItems[i];

                if (item.PlaylistOrder == i)
                    continue;

                item.PlaylistOrder = (ushort)i;

                await db.UpdatePlaylistItemAsync(new multiplayer_playlist_item(room.RoomID, item));
                await room.HandlePlaylistItemChanged(item, false);
            }
        }
    }
}
