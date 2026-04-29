# Spectator ↔ g0v0 integration audit

End-to-end mental wire-up of every surface where the rebased spectator
talks to the g0v0 stack. Dated against branch `torii-customizations`
HEAD ≥ `10288e1`.

## Verified working ✅

### HTTP `_lio` endpoints (spectator → g0v0)

All five spectator → g0v0 calls have a matching endpoint:

| Spectator method | URL | g0v0 handler |
|---|---|---|
| `CreateRoomAsync` | `POST /_lio/multiplayer/rooms` | `lio.py:500` |
| `AddUserToRoomAsync` | `PUT /_lio/multiplayer/rooms/{room_id}/users/{user_id}` | `lio.py:637` |
| `RemoveUserFromRoomAsync` | `DELETE /_lio/multiplayer/rooms/{room_id}/users/{user_id}` | `lio.py:543` |
| `EnsureBeatmapPresentAsync` | `POST /_lio/beatmaps/ensure` | `lio.py:709` |
| `UploadReplayAsync` | `POST /_lio/scores/replay` | `lio.py:750` |

**Note**: g0v0 doesn't verify the `X-LIO-Signature` HMAC header. The
spectator computes it and sends it but g0v0 ignores. Functionally fine,
just a security sharpness issue — anyone with network access to g0v0's
:8000 port can hit `/_lio/*` directly. Defense in depth is the docker
network isolation + the fact that g0v0 binds its api to 127.0.0.1:9000
on the host.

### Redis pub/sub

| Channel | Publisher | Subscriber | Status |
|---|---|---|---|
| `osu-channel:score:processed` | g0v0 (`score.py:1850`) | spectator (`ScoreProcessedSubscriber.cs:63`) | ✅ matched |
| `chat:room:joined` | g0v0 (`room.py:148`) | g0v0 internal | n/a |
| `chat:room:left` | g0v0 (`room.py:295`) | g0v0 internal | n/a |
| `osu-channel:user:invalidate` | g0v0 admin | g0v0 cache | n/a |

The spectator never publishes on pub/sub. It only reads/writes a few
`metadata:online:{userId}` keys directly via SET/DEL commands.

### Hot-path SignalR events (server → client)

The Torii lazer client subscribes to (`OnlineMetadataClient.cs:67-75`):

| Event name | Server emitter | Status |
|---|---|---|
| `BeatmapSetsUpdated` | `MetadataBroadcaster.handleUpdates` | ⏸ poller gated off (g0v0 has no `bss_process_queue`) |
| `UserPresenceUpdated` | `MetadataHub.broadcastUserPresenceUpdate` | ✅ |
| `FriendPresenceUpdated` | same as above | ✅ |
| `DailyChallengeUpdated` | `DailyChallengeUpdater` | ✅ uses `rooms` (g0v0) |
| `MultiplayerRoomScoreSet` | `ScoreProcessedSubscriber` | ✅ via redis pub/sub |
| `UserClientNameUpdated` | `MetadataHub.broadcastUserPresenceUpdate` (raw `IHubContext`) | ✅ Torii-only event |

### Database access (DAO methods on hot paths)

These run on every `OnConnected`, every score, every multiplayer flow:

| Method | Target | Status |
|---|---|---|
| `GetUsernameAsync` | `lazer_users.username` | ✅ |
| `IsUserRestrictedAsync` | `lazer_users.priv` | ✅ |
| `GetUserIdFromTokenAsync` | `oauth_tokens.access_token` | ✅ (lookup-by-token, supports migrated users) |
| `OfflineUser` | `lazer_users.last_visit` | ✅ |
| `ToggleUserPresenceAsync` | `lazer_users.is_online` | ✅ (this commit's fix) |
| `GetUserFriendsAsync` | `relationship` | ✅ |
| `GetUserRelation` | `relationship` | ✅ |
| `GetRoomAsync` / `GetRealtimeRoomAsync` | `rooms` | ✅ |
| `UpdateRoomHostAsync` | `rooms.host_id` | ✅ |
| `UpdateRoomSettingsAsync` | `rooms` | ✅ |
| `UpdateRoomStatusAsync` | `rooms.status` | ✅ |
| `EndMatchAsync` | `rooms.ends_at` | ✅ |
| `AddRoomParticipantAsync` | `room_participated_users` | ✅ |
| `RemoveRoomParticipantAsync` | same | ✅ |
| `AddPlaylistItemAsync` | `room_playlists` | ✅ |
| `UpdatePlaylistItemAsync` | `room_playlists` | ✅ |
| `RemovePlaylistItemAsync` | `room_playlists` | ✅ |
| `MarkPlaylistItemAsPlayedAsync` | `room_playlists.played_at` | ✅ |
| `GetPlaylistItemAsync` / `GetAllPlaylistItemsAsync` | `room_playlists` JOIN `beatmaps` for checksum | ✅ (this commit's fix) |
| `GetBeatmapAsync` | `beatmaps` (with column aliases) | ✅ |
| `GetScoreAsync` / `GetScoreFromTokenAsync` | `scores` + `score_tokens` | ✅ |
| `IsScoreProcessedAsync` | `scores.processed` | ✅ |
| `MarkScoreHasReplay` | `scores.has_replay` | ✅ |
| `GetUserBestScoreAsync` (2-arg) | `playlist_best_scores` (column-aliased to upstream POCO) | ✅ |
| `GetUserRankInRoomAsync` (2-arg) | `playlist_best_scores` aggregate | ✅ |
| `GetMultiplayerRoomIdForScoreAsync` | `scores` | ✅ |
| `GetActiveDailyChallengeRoomsAsync` | `rooms` filtered by category | ✅ |
| `GetAllChatFiltersAsync` | empty stub (g0v0 has no `chat_filters`) | ✅ stubbed |
| `GetBuildByIdAsync` etc. | dummy stubs (g0v0 has no `osu_builds`) | ✅ stubbed |

### Schema POCOs

| POCO | g0v0 table | Status |
|---|---|---|
| `multiplayer_room` | `rooms` | ✅ `host_id` aligned (this commit), `tournament_mode` => false getter |
| `multiplayer_playlist_item` | `room_playlists` | ✅ checksum joined from `beatmaps` |
| `database_beatmap` | `beatmaps` (column aliases) | ✅ |
| `SoloScore` | `scores` | ✅ |
| `phpbb_zebra` | `relationship` (custom mapping in DAO) | ✅ |

### Auth + JWT

| Concern | Status |
|---|---|
| HS256 JWTs from g0v0 (no RSA key) | ✅ `USE_LEGACY_RSA_AUTH=false` path |
| `aud` claim validation | ✅ `OSU_CLIENT_ID=5` |
| Migrated-user principal rewrite | ✅ when `oauth_tokens.user_id != JWT.sub`, principal rebuilt |
| Restricted-user gate | ✅ via `IsUserRestrictedAsync` after rewrite |
| `scopes` claim requirement | ✅ dropped (g0v0 doesn't issue it) |
| Reentrant lock on broadcast | ✅ versionHash threaded from caller (no re-acquire) |

### Background services

| Service | Status |
|---|---|
| `MultiplayerRoomLifetimeBackgroundService` | ✅ runs (cleans up empty rooms after grace period) |
| `MatchmakingQueueBackgroundService` | ⏸ runs but never queues anyone (gated by no client UI) |
| `IDailyChallengeUpdater` | ✅ runs, queries `rooms` table |
| `BuildUserCountUpdater` | ⏸ gated off (`TRACK_BUILD_USER_COUNTS=false`) |
| `MetadataBroadcaster` (BeatmapStatusWatcher poller) | ⏸ gated off (`ENABLE_BEATMAP_STATUS_POLLING=false`) |
| `ToriiClientNameResolver` | ✅ runs when `CLIENT_VERSION_WEBHOOK_SECRET` set |

### Score storage

| | Status |
|---|---|
| Replay write path | ✅ `FileScoreStorage` → `/app/replays` (host bind-mount) |
| `S3ScoreStorage` | ❌ unused, intentionally — Torii has no S3 |
| Replay forwarded to g0v0 via `_lio/scores/replay` | ✅ |

### Frontend (torii-lazer-web)

The web frontend talks to g0v0's HTTP API only — **no SignalR / no
direct dependency on the spectator**. Verified via grep: zero
`signalr`/`/spectator`/`/multiplayer`/`/metadata` URLs in `src/`. So
none of the spectator changes can break the website.

The website does show the "Currently online" panel, which reads
`metadata:online:{userId}` from redis (set by the spectator's
`MetadataHub.logLogin`). That redis key flow is verified working.

## What's intentionally inert ⏸

These compile + run but never execute their hot path on Torii deploys.
Listed here so we know what to flip on if the deploy posture changes:

1. **Matchmaking queue + ranked-play stage machine.** All the matchmaking
   partial classes load, DI registers `MatchmakingQueueBackgroundService`,
   but as long as no client clicks "Find Match" the bound DAO methods
   (`GetActiveMatchmakingPoolsAsync`, `GetMatchmakingPoolAsync`,
   `GetMatchmakingUserStatsAsync`, etc.) never execute. **Their queries
   still target osu-web tables (`multiplayer_rooms`, `phpbb_users`,
   `osu_beatmaps`, `oauth_access_tokens`)** — they need retargeting
   before matchmaking can actually launch. Tracked for Stage 5.

2. **Referee hub.** Class compiles, JWT scheme registered, but
   `Startup.cs` never wires the SignalR endpoint mapping — so any
   client request hitting `/signalr/referee` 404s before reaching the
   hub code. The dropped `tournamentMode` flag in `CreateRoomAsync`
   is therefore moot until referee is wired.

3. **`BuildUserCountUpdater`.** Counts how many users are running each
   `osu_builds.hash`. g0v0 doesn't have that table; the DAO returns
   empty lists, the updater's hosted task no-ops. To turn on real
   tracking you'd need a `client_versions`-equivalent table on g0v0
   plus a non-stubbed `GetAllMainLazerBuildsAsync` etc.

4. **`BeatmapStatusWatcher` poller.** Polls `bss_process_queue` (osu-web)
   to broadcast `BeatmapSetsUpdated`. g0v0 has no equivalent queue;
   if Torii ever wants live "newly-uploaded beatmap" notifications,
   it should publish to a redis pub/sub channel from the
   `/api/v2/beatmaps/...` upload handler and have the spectator
   subscribe instead of polling.

## Known sharp edges that haven't bitten yet ⚠️

These compiled and the code path is reachable, but I haven't seen them
fire in the local sandbox yet. Worth a closer look on smoke-test:

1. **`MultiplayerEventDispatcher.PostMatchmakingRoomCreatedAsync`**
   inserts into `matchmaking_room_events` (g0v0 doesn't have that
   table — the alembic migration `c4d5e6f7a8b9` on
   `wip/matchmaking-server` creates it). It's wrapped in a
   `try/catch` that LogWarning's the failure, so even if it throws
   nothing breaks at the user level. But it'll spam warnings every
   matchmaking room creation if matchmaking is ever turned on
   without that migration.

2. **Daily-challenge score path.** `DailyChallengeUpdater` polls every
   30s for active daily-challenge rooms via `GetActiveDailyChallengeRoomsAsync`.
   The query expects `rooms.category = 'daily_challenge' AND type = 'playlists'`.
   Verify g0v0 actually populates these values when a daily-challenge
   room is created (vs hardcoding via web-side flow).

3. **`MultiplayerEventDispatcher.logToDatabase(matchmaking_room_event)`**
   targets `matchmaking_room_events` — same as #1. Wrapped in
   try/catch, logs warning on missing table.

4. **`GetUserRankInRoomAsync` semantics**. The Torii-flavoured query
   sums `total_score` per user in the room and ranks. That's correct
   for daily-challenge / quick-play (one score per user per item).
   For multi-item playlist rooms with `attempts > 1`, the upstream
   semantic is "rank by playlist-position-weighted total" — we don't
   replicate that. **The popup may show a slightly different rank
   than the website's leaderboard for those room types.** Consider
   if the discrepancy matters before shipping.

## What works DIFFERENTLY between Torii and osu-web (intentional)

The flip from osu-web schema → g0v0 schema preserves behaviour, but a
few things are inherently different:

| Concept | osu-web | Torii (g0v0) |
|---|---|---|
| Score storage | S3 bucket | Local FS bind-mount (`/app/replays`) |
| Beatmap mirror | upstream osu! CDN | BeatConnect proxy + Torii local mirror |
| Beatmap status overrides | none | `effective_rank_status` promotes graveyard→approved when `enable_all_beatmap_leaderboard=true` |
| Build verification | `osu_builds` registry | `client_versions` table (separate webhook flow) |
| Beatmap update broadcast | `bss_process_queue` poll | redis pub/sub from g0v0's submission handler (TODO if needed) |
| OAuth RSA key | yes | HS256 secret instead |
| Account migrations | rare | regular (cloudflare merges, etc.) — handled by principal-rewrite path |

## Recommended smoke-test order

After the current commit (`10288e1`), run through these in order. Any
failure → grab the spectator log and we debug:

1. **Login + presence**: lazer client connects, you appear in the
   social panel's "Currently online" list. Validates: JWT HS256,
   migrated-user rewrite, MetadataHub redis online key, presence
   broadcast.

2. **Friend presence**: have a second account logged in. Friend them.
   Both should see each other in their friend lists.

3. **Score submission**: pick any beatmap, finish the play. Watch
   spectator logs for: `RegisterForSingleScoreAsync` → score row
   appears → rank/PP popup in client.  Replay file should land in
   `./replays/`.

4. **Replay watch**: open one of your past scores, click watch.
   Replay should download from spectator and play.

5. **Multiplayer create + join**: create a room from client #1, join
   from client #2. Add a beatmap. Start match. Both load. Both play.
   Both see each other's scores on results screen.

6. **Daily challenge**: open it, play through, score submits,
   leaderboard updates.

7. **Spectate live**: client #2 watches client #1 mid-play. Frame
   data should stream over SpectatorHub.

If 1–4 work and 5–7 work, **everything that real Torii users hit
day-to-day is verified.** Matchmaking is the only deferred surface,
and it's gated behind a UI button the casual user doesn't reach.
