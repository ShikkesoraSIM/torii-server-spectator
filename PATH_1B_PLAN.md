# Path 1B — Spectator becomes HTTP-only client of g0v0

**Goal.** Strip every direct MySQL access out of `torii-server-spectator` and route all backend data needs through HTTP endpoints served by `g0v0-server`. End-state: the spectator is a thin SignalR/HTTP client; g0v0 owns 100 % of persistence, schema, and business logic. Matches the architecture upstream `ppy/osu-server-spectator` implicitly assumes (because the closed-source osu! backend talks to it via HTTP only).

This document is the source of truth for the migration. It supersedes `TORII_PORT.md`'s Stage 2 (Option 2 "wholesale copy" + Option 3 "hybrid DAO") and replaces them with the clean path.

> **Companion docs:**
> - `INTEGRATION_AUDIT.md` — pre-migration state and the `_lio/*` interop layer that already exists
> - `TORII_PORT.md` — history of the m1pp → upstream rebase + stages 1/3
> - `MATCHMAKING_FUTURE_WORK.md` — optional matchmaking polish (separate axis)

---

## 1. Why path 1B (and not 1A "compat views" or status-quo)

Three options were considered before locking in:

| Option | What it is | Verdict |
|---|---|---|
| **Status quo** | Spectator's `DatabaseAccess.cs` has SQL targeting g0v0 table names (`rooms`, `lazer_users`, `room_playlists`, etc.) — 41 queries / 64 DAO methods total. HTTP only used for state-changing operations via `/_lio/*`. | ❌ Spectator can't be rebased onto upstream cleanly. Every upstream merge has conflicts in `DatabaseAccess.cs`. Schema knowledge bleeds across both repos. |
| **1A: Compat views** | g0v0 grows MySQL views named after osu-web tables (`multiplayer_rooms` → `SELECT … FROM rooms`). Spectator reverts to vanilla upstream SQL. | ❌ Still direct DB coupling. Writes need INSTEAD-OF triggers which are awkward in MySQL. New columns/tables in upstream need parallel DDL in g0v0. |
| **1B: Full HTTP API surface** | g0v0 exposes every read/write the spectator needs as an HTTP endpoint. Spectator's `DatabaseAccess` becomes an HTTP client. No MySQL connection string in the spectator's deployment. | ✅ **Chosen.** Matches ppy's architecture. Spectator is thin and rebasable. g0v0 owns schema fully. Endpoints can evolve independently with proper versioning. |

The cost (~3 to 4 weeks of focused work) is real but bounded — 64 endpoints, mostly straightforward CRUD, with the natural batching outlined in §5.

---

## 2. The bug that surfaced this work

> **Symptom (user-reported):** after finishing a beatmap in a multiplayer room, the played item stays in the visible playlist. Trying to manually remove it surfaces a toast along the lines of "can't remove something that has already been played." Subsequent attempts to ready-up can throw "Cannot ready up while all items have been played."

**Trace summary (what the code does today):**

1. `StandardMatchController.HandleGameplayCompleted` (line 86) fires when gameplay ends.
2. It calls `db.MarkPlaylistItemAsPlayedAsync` which issues
   ```sql
   UPDATE room_playlists SET expired = 1, played_at = NOW(), updated_at = NOW()
   WHERE id = @PlaylistItemId AND room_id = @RoomId;
   ```
3. It re-fetches the now-expired row via `db.GetPlaylistItemAsync` and replaces `room.Playlist[currentPlaylistItemIndex]`.
4. It broadcasts `room.HandlePlaylistItemChanged(CurrentItem, true)` to all subscribers.
5. `updateCurrentItem()` is supposed to advance to the next non-expired item.

**Where it most plausibly breaks:**

- **Single-item rooms (non-HostOnly mode).** After the only item is marked expired, the playlist is `all-expired`. `updateCurrentItem()` can't pick a next item, `CurrentItem.Expired` stays true, and `ServerMultiplayerRoom.cs:527-528` then refuses ready-up with `"Cannot ready up while all items have been played."` The Torii silent-no-op patch (`8a6c26f`) handles the Remove path but doesn't address this state machine corner.
- **Client-side rendering.** The lazer client may keep the expired item visible in the "Upcoming" list rather than moving it to history; this is a client bug, not a server bug, but ends up looking like the server failed to remove the item.
- **Matchmaking controller path.** `MatchmakingMatchController.RemovePlaylistItem` (line 242) is a separate code path with different guards than `StandardMatchController`. The silent-no-op family may not be fully ported there. Needs verification.

**Action under Path 1B:** the bug fix is folded into Phase 5 (playlist endpoints). When the playlist lifecycle is owned by g0v0 instead of the spectator, the "what happens when the last item expires" decision lives in one place — g0v0's playlist controller — and we can implement it cleanly (auto-clone for any QueueMode? Or surface a clear "queue exhausted, host must add" state?). For now, the bug stays open with a TODO; nothing in this plan tries to patch it in-place on the spectator side.

---

## 3. Current architecture (before path 1B)

```
                       ┌──────────────────────────────┐
                       │  lazer client (torii-osu)    │
                       │  SignalR + HTTP/2 + WebSocket│
                       └─────────────┬────────────────┘
                                     │  SignalR hubs (Multiplayer, Spectator, Metadata)
                                     │  ↓
        ┌────────────────────────────▼──────────────────────────────┐
        │  torii-server-spectator (C# / ASP.NET 8)                  │
        │                                                           │
        │  Hubs/Multiplayer, Hubs/Spectator, Hubs/Metadata, Hubs/Chat
        │  Hubs/Multiplayer/Matchmaking, Hubs/Multiplayer/Standard  │
        │                                                           │
        │  ◀── HTTP /_lio/*  ──┐                                    │
        │  Database/Models     │                                    │
        │  Database/DatabaseAccess.cs  ── direct MySQL ─┐           │
        └──────────────────────┼─────────────────────────┼──────────┘
                               │                         │
                               │                         │
                       ┌───────▼─────────────────────────▼────────┐
                       │  g0v0-server (Python / FastAPI)          │
                       │                                          │
                       │  app/router/lio.py            ← /_lio/*  │
                       │  app/router/private/*.py                 │
                       │  app/router/v1/, v2/, notification/      │
                       │                                          │
                       │  app/database/* (SQLModel)               │
                       │           │                              │
                       └───────────┼──────────────────────────────┘
                                   │
                       ┌───────────▼──────────────┐
                       │  MySQL / Redis           │
                       └──────────────────────────┘
```

**Two parallel coupling paths exist today:**

- **HTTP `/_lio/*`** (✅ already path-1 style) — 7 endpoints in `g0v0-server/app/router/lio.py` covering room invite, room create/add-user/remove-user, beatmap-ensure, replay-upload, ruleset-hashes.
- **Direct MySQL** (❌ path 2, what we're killing) — 64 `IDatabaseAccess` methods, 41 SQL statements, 1128 lines in `Database/DatabaseAccess.cs`. Queries target g0v0's table names (`rooms`, `lazer_users`, etc.) — they're already "g0v0-flavoured" via SQL rewrites kept in the spectator repo.

The fact that the spectator's DAO already knows g0v0's schema is the path-2-disguised-as-path-1 that we're undoing.

---

## 4. Target architecture (after path 1B)

```
                       ┌──────────────────────────────┐
                       │  lazer client (torii-osu)    │
                       └─────────────┬────────────────┘
                                     │
        ┌────────────────────────────▼──────────────────────────────┐
        │  torii-server-spectator                                   │
        │                                                           │
        │  Hubs/* (unchanged)                                       │
        │                                                           │
        │  Database/DatabaseAccess.cs                               │
        │     ↓                                                     │
        │  HttpDatabaseClient   ── HTTP ──▶                         │
        │     (1 HTTP call per IDatabaseAccess method)              │
        └─────────────────────────────────┬─────────────────────────┘
                                          │
                                  ┌───────▼──────────────────────┐
                                  │  g0v0-server                 │
                                  │  app/router/lio.py           │
                                  │  app/router/lio_db.py  ←NEW  │
                                  │  app/service/spectator_db.py │
                                  │  app/database/* (unchanged)  │
                                  └──────────────────────────────┘
```

Concrete deliverables:

- **g0v0-server**: a new `app/router/lio_db.py` (or `app/router/private/spectator/*` modular split) exposing 64 endpoints. All under `/_lio/db/*` or `/_lio/spectator/*` to keep them clearly internal-interop.
- **torii-server-spectator**: `Database/DatabaseAccess.cs` becomes a thin HTTP client wrapping `Database/HttpDatabaseClient.cs`. The interface `IDatabaseAccess` stays unchanged — every method just makes one HTTP call. Calling code in hubs is untouched.
- **Auth between the two**: re-use the existing `SHARED_INTEROP_SECRET` HMAC pattern from `/_lio/*` (`X-LIO-Signature` header). g0v0 enforces the HMAC on the new endpoints too (and on the existing ones — currently it ignores it per `INTEGRATION_AUDIT.md` line 21-26).

**What stays in the spectator (NOT migrated):**

- SignalR hub logic (multiplayer state machine, gameplay flow, etc.) — that's the spectator's whole reason to exist.
- Redis read/write for `metadata:online:{userId}` — Redis is shared infra, both sides hit it directly.
- File system access for `/app/replays` — local file storage is fine.

---

## 5. The 64 endpoints, grouped into phases

Each phase is a coherent batch: the endpoints in a phase are mutually dependent or share a domain. Phases are ordered so each one unlocks an observable user-facing improvement (lighthouse milestones), not just "more endpoints done."

### Phase 1 — Auth + identity (5 endpoints)

**Lighthouse: lazer client connects and online-presence works.** Without these, nothing else can resolve a user from a JWT.

| DAO method | HTTP signature | Notes |
|---|---|---|
| `GetUserIdFromTokenAsync(JsonWebToken)` | `POST /_lio/spectator/auth/resolve-token` body `{token}` → `{user_id}` | Handles migrated-user rewrite (when `oauth_tokens.user_id != JWT.sub`) |
| `IsUserRestrictedAsync(userId)` | `GET /_lio/spectator/users/{user_id}/is-restricted` → `{restricted: bool}` | |
| `GetUsernameAsync(userId)` | `GET /_lio/spectator/users/{user_id}/username` → `{username}` | |
| `GetDelegatedResourceOwnerIdFromTokenAsync(JsonWebToken)` | `POST /_lio/spectator/auth/resolve-delegated-token` body `{token}` → `{user_id}` | Delegated/client-credentials grants |
| `GetUsersInGroupsAsync(int[] groupIds)` | `POST /_lio/spectator/users/in-groups` body `{group_ids: [int]}` → `{user_ids: [int]}` | |

### Phase 2 — User session lifecycle (4 endpoints)

**Lighthouse: presence broadcast, "currently online" panel on website, last-visit timestamps.**

| DAO method | HTTP signature | Notes |
|---|---|---|
| `AddLoginForUserAsync(userId, ip)` | `POST /_lio/spectator/users/{user_id}/logins` body `{ip}` | |
| `OfflineUser(userId)` | `POST /_lio/spectator/users/{user_id}/offline` | Sets `lazer_users.last_visit` |
| `GetUserAllowsPMs(userId)` | `GET /_lio/spectator/users/{user_id}/allows-pms` → `{allows: bool}` | |
| `GetUserPPAsync(userId, rulesetId, variant)` | `GET /_lio/spectator/users/{user_id}/pp?ruleset={r}&variant={v}` → `{pp: float}` | Used by matchmaking ranking |

### Phase 3 — Social / relationships (2 endpoints)

**Lighthouse: friend list + friend presence updates.**

| DAO method | HTTP signature | Notes |
|---|---|---|
| `GetUserFriendsAsync(userId)` | `GET /_lio/spectator/users/{user_id}/friends` → `{friend_ids: [int]}` | |
| `GetUserRelation(userId, zebraId)` | `GET /_lio/spectator/users/{user_id}/relation/{zebra_id}` → `{type, foe_count, ...}` or `404` | |

### Phase 4 — Beatmap lookup + fail-time tracking (5 endpoints)

**Lighthouse: playlist items resolve their beatmap metadata, fail-time heatmap on beatmap page populated.**

| DAO method | HTTP signature | Notes |
|---|---|---|
| `GetBeatmapAsync(beatmapId)` | `GET /_lio/spectator/beatmaps/{beatmap_id}` → `database_beatmap` payload or `404` | |
| `GetBeatmapsAsync(beatmapSetId)` | `GET /_lio/spectator/beatmapsets/{set_id}/beatmaps` → `[database_beatmap]` | |
| `GetBeatmapOrFetchAsync(beatmapId)` | `POST /_lio/spectator/beatmaps/{beatmap_id}/ensure` → `database_beatmap` | Falls back to current `/_lio/beatmaps/ensure` semantics |
| `GetBeatmapFailTimeAsync(beatmapId)` | `GET /_lio/spectator/beatmaps/{beatmap_id}/fail-time` → `fail_time` POCO | |
| `UpdateFailTimeAsync(failTime)` | `POST /_lio/spectator/beatmaps/{beatmap_id}/fail-time` body `{ruleset_id, fail_times, exit_times}` | |

### Phase 5 — Multiplayer room lifecycle (10 endpoints) — **the bug-fix phase**

**Lighthouse: full multiplayer create / join / play / results works end-to-end. The played-item-stuck bug from §2 is fixed here because g0v0 owns the playlist lifecycle now.**

| DAO method | HTTP signature | Notes |
|---|---|---|
| `GetRoomAsync(roomId)` | `GET /_lio/spectator/rooms/{room_id}` → `multiplayer_room` POCO | |
| `GetRealtimeRoomAsync(roomId)` | `GET /_lio/spectator/rooms/{room_id}/realtime` → `multiplayer_room` POCO with realtime-specific fields | |
| `MarkRoomActiveAsync(room)` | `POST /_lio/spectator/rooms/{room_id}/mark-active` body `{...room state...}` | |
| `UpdateRoomSettingsAsync(room)` | `PATCH /_lio/spectator/rooms/{room_id}/settings` body `{name, password, queue_mode, ...}` | |
| `UpdateRoomStatusAsync(room)` | `PATCH /_lio/spectator/rooms/{room_id}/status` body `{status}` | Idle/Playing |
| `UpdateRoomHostAsync(room)` | `PATCH /_lio/spectator/rooms/{room_id}/host` body `{host_id}` | |
| `AddRoomParticipantAsync(room, user)` | `PUT /_lio/spectator/rooms/{room_id}/participants/{user_id}` | (already partially covered by `_lio/multiplayer/rooms/{id}/users/{uid}` PUT — consolidate) |
| `RemoveRoomParticipantAsync(room, user)` | `DELETE /_lio/spectator/rooms/{room_id}/participants/{user_id}` | (consolidate with existing) |
| `EndMatchAsync(room)` | `POST /_lio/spectator/rooms/{room_id}/end-match` | Expires all non-expired items + closes room |
| `GetActiveDailyChallengeRoomsAsync()` | `GET /_lio/spectator/rooms/active-daily-challenges` → `[multiplayer_room]` | |

### Phase 6 — Playlist items (7 endpoints)

**Lighthouse: playlist add/edit/remove + the "played item lifecycle" works correctly. Fixes the bug from §2.**

| DAO method | HTTP signature | Notes |
|---|---|---|
| `GetPlaylistItemAsync(roomId, itemId)` | `GET /_lio/spectator/rooms/{room_id}/playlist/{item_id}` → `multiplayer_playlist_item` | |
| `GetAllPlaylistItemsAsync(roomId)` | `GET /_lio/spectator/rooms/{room_id}/playlist` → `[multiplayer_playlist_item]` | |
| `AddPlaylistItemAsync(item)` | `POST /_lio/spectator/rooms/{room_id}/playlist` body `{item}` → `{logical_id}` | |
| `UpdatePlaylistItemAsync(item)` | `PATCH /_lio/spectator/rooms/{room_id}/playlist/{item_id}` body `{item}` | |
| `RemovePlaylistItemAsync(roomId, itemId)` | `DELETE /_lio/spectator/rooms/{room_id}/playlist/{item_id}` | |
| `MarkPlaylistItemAsPlayedAsync(roomId, itemId)` | `POST /_lio/spectator/rooms/{room_id}/playlist/{item_id}/mark-played` | **Bug-fix opportunity:** g0v0 can also auto-clone for queue-exhausted single-item rooms here, so the "Cannot ready up while all items have been played" state never happens. |
| `AnyScoreTokenExistsFor(itemId)` | `GET /_lio/spectator/playlist/{item_id}/has-score-tokens` → `{exists: bool}` | |

### Phase 7 — Scores (8 endpoints)

**Lighthouse: rank/PP popup after score submission, multiplayer score linking, replay storage.**

| DAO method | HTTP signature | Notes |
|---|---|---|
| `MarkScoreHasReplay(score)` | `POST /_lio/spectator/scores/{score_id}/has-replay` | |
| `GetScoreFromTokenAsync(token)` | `GET /_lio/spectator/score-tokens/{token}/score` → `SoloScore` or `404` | |
| `GetScoreAsync(scoreId)` | `GET /_lio/spectator/scores/{score_id}` → `SoloScore` or `404` | |
| `IsScoreProcessedAsync(scoreId)` | `GET /_lio/spectator/scores/{score_id}/is-processed` → `{processed: bool}` | |
| `GetMultiplayerRoomIdForScoreAsync(scoreId)` | `GET /_lio/spectator/scores/{score_id}/room` → `{room_id, playlist_item_id}` or `404` | |
| `GetPassingScoresForPlaylistItem(roomId, itemId, afterScoreId=0)` | `GET /_lio/spectator/rooms/{room_id}/playlist/{item_id}/passing-scores?after_score_id={n}` → `[SoloScore]` | |
| `GetAllScoresForPlaylistItem(itemId)` | `GET /_lio/spectator/playlist/{item_id}/all-scores` → `[SoloScore]` | |
| `GetUserBestScoreAsync(itemId, userId)` | `GET /_lio/spectator/playlist/{item_id}/best/{user_id}` → `multiplayer_scores_high` or `404` | |
| `GetUserRankInRoomAsync(roomId, userId)` | `GET /_lio/spectator/rooms/{room_id}/rank/{user_id}` → `{rank: int}` | |

### Phase 8 — Event logging (1 endpoint)

| DAO method | HTTP signature | Notes |
|---|---|---|
| `LogRoomEventAsync(ev)` | `POST /_lio/spectator/rooms/{room_id}/events` body `{event}` | Inserts into `multiplayer_events` |

### Phase 9 — Matchmaking pools / stats / ELO (10 endpoints) — needs alembic migration first

**Prerequisite: g0v0 needs alembic migration `c4d5e6f7a8b9` to create matchmaking tables.** See `MATCHMAKING_FUTURE_WORK.md` for surrounding plans.

| DAO method | HTTP signature | Notes |
|---|---|---|
| `GetActiveMatchmakingPoolsAsync()` | `GET /_lio/spectator/matchmaking/pools/active` → `[matchmaking_pool]` | |
| `GetMatchmakingPoolAsync(poolId)` | `GET /_lio/spectator/matchmaking/pools/{pool_id}` → `matchmaking_pool` or `404` | |
| `GetMatchmakingPoolBeatmapsAsync(poolId)` | `GET /_lio/spectator/matchmaking/pools/{pool_id}/beatmaps` → `[matchmaking_pool_beatmap]` | |
| `GetMatchmakingPoolBeatmapAsync(poolId, beatmapId, mods)` | `GET /_lio/spectator/matchmaking/pools/{pool_id}/beatmaps/{beatmap_id}?mods={mods}` → `matchmaking_pool_beatmap` or `404` | |
| `UpdateMatchmakingPoolBeatmapRatingAsync(beatmap)` | `PATCH /_lio/spectator/matchmaking/pools/{pool_id}/beatmaps/{beatmap_id}/rating` body `{rating, mods}` | |
| `GetMatchmakingGlobalPoolBeatmapsAsync(rulesetId, variant)` | `GET /_lio/spectator/matchmaking/global-pool-beatmaps?ruleset={r}&variant={v}` → `[database_beatmap]` | |
| `GetMatchmakingUserStatsAsync(userId, poolId)` | `GET /_lio/spectator/matchmaking/users/{user_id}/stats?pool_id={p}` → `matchmaking_user_stats` or `404` | |
| `UpdateMatchmakingUserStatsAsync(stats)` | `PATCH /_lio/spectator/matchmaking/users/{user_id}/stats` body `{stats}` | |
| `InsertUserEloHistoryEntry(roomId, poolId, userId, opponentId, result, eloBefore, eloAfter)` | `POST /_lio/spectator/matchmaking/elo-history` body `{...}` | |

### Phase 10 — Build version tracking (5 endpoints) — currently stubbed, gated off

Implementations are stub-only in g0v0 today (table `osu_builds` doesn't exist). Either bring up a `client_versions`-equivalent table OR keep stubs forever — decide separately.

| DAO method | HTTP signature | Status |
|---|---|---|
| `GetBuildByIdAsync(buildId)` | `GET /_lio/spectator/builds/{build_id}` | Stub OK |
| `GetBuildByHashAsync(hash)` | `GET /_lio/spectator/builds/by-hash/{hash}` | Stub OK |
| `GetAllMainLazerBuildsAsync()` | `GET /_lio/spectator/builds/main-lazer` | Stub OK |
| `GetAllPlatformSpecificLazerBuildsAsync()` | `GET /_lio/spectator/builds/platform-specific` | Stub OK |
| `UpdateBuildUserCountAsync(build)` | `POST /_lio/spectator/builds/{build_id}/user-count` | Stub OK |

### Phase 11 — Chat filters + playtime (3 endpoints) — currently stubbed or low-priority

| DAO method | HTTP signature | Notes |
|---|---|---|
| `GetAllChatFiltersAsync()` | `GET /_lio/spectator/chat-filters` | g0v0 has no chat_filters table; returns empty list |
| `GetUserPlaytimeAsync(gamemode, userId)` | `GET /_lio/spectator/users/{user_id}/playtime?gamemode={g}` → `{minutes: int?}` | |
| `UpdateUserPlaytimeAsync(gamemode, userId, playTime)` | `POST /_lio/spectator/users/{user_id}/playtime` body `{gamemode, minutes}` | |

**Total: 60 endpoints across 11 phases.** (The 4 missing from the 64 are consolidated into existing `/_lio/*` endpoints — e.g. `_lio/multiplayer/rooms` covers create + the DAO's `MarkRoomActiveAsync`/`UpdateRoomSettingsAsync`/`UpdateRoomStatusAsync` partially.)

---

## 6. Per-phase deliverables

Each phase produces three artefacts:

1. **g0v0 changes** — the actual FastAPI handlers + service-layer logic + tests
2. **Spectator changes** — `Database/Http<X>Client.cs` partial classes that replace `DatabaseAccess` SQL with HTTP calls
3. **Cutover** — feature flag in spectator (`USE_HTTP_DAO_FOR_<phase>=true`) so we can switch one phase at a time and roll back instantly

The feature-flag pattern means at any commit on the migration branch we can run the spectator in mixed mode (some methods HTTP, some MySQL). Rollback is one env-var change, no rebuild.

When all phases land, the SQL implementations and the MySQL connection string are deleted from the spectator. `DatabaseAccess.cs` collapses into `HttpDatabaseAccess.cs` (`IDatabaseAccess` → 1 HTTP call → done).

---

## 7. HTTP transport contract

All endpoints share:

- **Base URL**: `${SHARED_INTEROP_DOMAIN}/_lio/spectator/...` (or just `/_lio/...` for the 7 already-existing endpoints)
- **Auth**: `X-LIO-Signature: hmac-sha1(SHARED_INTEROP_SECRET, URL_WITH_TIMESTAMP_QUERY_PARAM)`. SHA1 (not SHA256) matches the existing `SharedInterop` HMAC contract — same secret signs both clients against g0v0. The signature scope is the full request URL with a `?timestamp=<unix>` query param appended (NOT the body), again to match the existing pattern. g0v0 currently does NOT verify the signature on its `/_lio/*` endpoints (`INTEGRATION_AUDIT.md` §HTTP `_lio`); enable verification on g0v0's side before any sensitive Path 1B endpoint goes live.
- **Content-Type**: `application/json`
- **Errors**:
  - `404` for "not found" semantics (the DAO returns nullable types — `null` becomes `404` over the wire)
  - `409` for state-machine violations (e.g. trying to add a participant who's already in the room)
  - `422` for validation failures
  - `500` for unexpected internal errors
- **Idempotency**: state-changing endpoints (`PATCH`, `POST`, `DELETE`) must be safe to retry. Where ordering matters (e.g. `MarkPlaylistItemAsPlayedAsync` should fire exactly once per item), g0v0 uses optimistic locking via a `version` or `updated_at` precondition header.

### Why not gRPC

gRPC would be cleaner long-term but adds a build/codegen pipeline both sides and a new transport layer. For 60 endpoints with JSON-encoded data structures we already have (the Database/Models C# POCOs already serialise cleanly), HTTP+JSON is sufficient. If we ever need streaming server-to-spectator we'd revisit.

---

## 8. Migration strategy (incremental)

We do NOT cut over all 60 endpoints at once. Order:

1. **Land the HTTP transport machinery** in the spectator (a `HttpDatabaseClient` base class with auth + retry + error mapping). Cost: ~1-2 days. No behaviour change.
2. **Phase 1 (Auth)**: implement 5 endpoints in g0v0, add 5 HTTP partials in spectator, gate behind `USE_HTTP_DAO_AUTH=true`. Smoke test login flow. Flip the flag default to `true` after a week of soak.
3. **Phases 2-11 in order**, each on its own ~3-5 day cycle: implement → gate → smoke → flag-flip → next.
4. **Cleanup**: after all phases flipped, delete the MySQL connection logic in the spectator entirely. `DatabaseAccess.cs` becomes `HttpDatabaseAccess.cs` only.
5. **Rebase onto upstream**: with no direct DB access, the spectator can finally be rebased onto upstream `ppy/osu-server-spectator`'s next release without `DatabaseAccess.cs` conflicts. Any future upstream changes that don't touch the data access layer apply cleanly.

---

## 9. Lighthouse milestones (what "done" looks like at each cut)

| Milestone | Phases needed | User-observable change |
|---|---|---|
| **M1** — Spectator can resolve users via HTTP | 1 | None (internal). Login flow unchanged for users; spectator's debug logs show "auth resolved via HTTP". |
| **M2** — Presence + social via HTTP | 1, 2, 3 | None visible; verifies multiple endpoints in concert. |
| **M3** — Multiplayer round-trip via HTTP | 1, 4, 5 | Multiplayer create/join/leave/edit works without spectator touching MySQL. |
| **M4** — Played-item bug fixed | 1, 4, 5, 6 | The §2 bug stops reproducing. Playlist items expire and clean up correctly. Ready-up works after a played map. |
| **M5** — Score flow via HTTP | + 7 | Score submission, rank/PP popup, replay watch, leaderboards all run via HTTP. |
| **M6** — Logging via HTTP | + 8 | `multiplayer_events` populated through g0v0 service. |
| **M7** — Matchmaking ready | + 9 (and alembic migration `c4d5e6f7a8b9`) | "Find Match" button is safe to click — the matchmaking queue is wired against g0v0's matchmaking tables (existing today only as model files; need migration to actually create them in DB). |
| **M8** — Spectator is pure HTTP client | + 10, 11, cleanup | The spectator's `appsettings.json` no longer has a MySQL connection string. Rebase onto upstream becomes routine. |

---

## 10. Known open issues to fold into specific phases

| Issue | Lands in | Notes |
|---|---|---|
| Played-item-stuck bug (§2) | Phase 6 (Playlist items) | Solve in g0v0 controller — when last item expires, auto-clone for any QueueMode (not just HostOnly), or surface explicit "queue-exhausted" state to client. |
| `RemovePlaylistItem` silent-no-op may not be ported to Matchmaking controller | Phase 5 / 6 | Audit `MatchmakingMatchController.RemovePlaylistItem` vs `StandardMatchController.RemovePlaylistItem`. The silent-no-op family is the right behaviour in both. |
| `X-LIO-Signature` HMAC currently not verified by g0v0 (`INTEGRATION_AUDIT.md` line 21) | Phase 1 cutover | Enable verification before we start sending sensitive endpoints through. Defense-in-depth even though the docker network is isolated. |
| `MultiplayerEventDispatcher.PostMatchmakingRoomCreatedAsync` writes to non-existent `matchmaking_room_events` (`INTEGRATION_AUDIT.md` line 178) | Phase 9 | Folded into the matchmaking migration. |
| `BeatmapStatusWatcher` polls non-existent `bss_process_queue` | Not in 1B scope | Separately addressed via redis pub/sub from g0v0 if we want live "newly-uploaded beatmap" broadcasts. |
| `BuildUserCountUpdater` targets non-existent `osu_builds` | Phase 10 | Implement against `client_versions` (separate webhook flow) OR keep permanently stubbed. |
| **Pre-existing: `osu.Server.Spectator.Tests` does not compile** (17 errors, all `'multiplayer_room' does not contain a definition for 'user_id'`) | Cleanup pass, no specific phase | The Torii schema rename `multiplayer_rooms.user_id → rooms.host_id` updated the POCO but never updated the test fixtures. Tests have been broken since at least `8a6c26f` (verified by checking out and rebuilding). Doesn't block the production `osu.Server.Spectator.csproj` build (which is clean). When a phase touches `multiplayer_room` semantics, take the opportunity to fix the corresponding tests too. |

---

## 11. Open architecture decisions (to lock before Phase 1 starts)

1. **Endpoint base path: `/_lio/spectator/*` vs `/api/private/spectator/*`?**
   The 7 existing endpoints live at `/_lio/*` (no `spectator/` prefix). Keep new endpoints under `/_lio/spectator/*` so the spectator-specific surface is contained, and the 7 originals can be optionally moved to `/_lio/spectator/multiplayer/*` etc. for symmetry. Decision: yes, namespace it.
2. **Auth model**: HMAC (`X-LIO-Signature`) shared secret vs mTLS vs nothing-because-internal-network?
   Stick with HMAC. Easier ops than mTLS, more defensive than nothing (especially since g0v0's `:9000` is bound to 127.0.0.1, the spectator container reaches it on the bridge network, no external surface but defense-in-depth still helps).
3. **Serialisation**: System.Text.Json vs Newtonsoft on the C# side?
   The C# POCOs (`multiplayer_playlist_item`, `multiplayer_room`, etc.) already use `Newtonsoft.Json` attributes. Stay on Newtonsoft for consistency.
4. **Failure mode when g0v0 is unreachable**: fail open (return null/empty) or fail closed (500 the SignalR call)?
   Fail closed. Returning empty silently corrupts state-machine assumptions in the hubs. The user gets a connection error which is at least diagnosable.
5. **Background services** (`MatchmakingQueueBackgroundService`, `DailyChallengeUpdater`, `MetadataBroadcaster`): these poll DB on a timer. Do they switch to HTTP too?
   Yes — they share the `IDatabaseAccess` interface. The polling cadence may need adjustment (HTTP overhead is higher per call than direct SQL) but the API stays uniform.

---

## 12. Bug-fix lane (parallel to migration)

The played-item bug (§2) is real and bothering users today. We will NOT block on the full migration to fix it. Parallel lane:

- **Quick patch (this week)**: in `StandardMatchController.HandleGameplayCompleted`, if `room.Settings.QueueMode != HostOnly` and `room.Playlist.All(item => item.Expired)` after marking played, auto-clone the just-finished item the same way HostOnly mode already does. This unblocks the "ready up after the only map" case.
- **Investigate Matchmaking controller's RemovePlaylistItem path** to confirm whether the silent-no-op family is needed there too, and apply if so.
- **Then resume the migration plan above**.

The quick patch belongs in `torii-customizations` branch, not blocked by the migration commits. It can ship in tomorrow's spectator rebuild.

---

## Appendix A — Per-endpoint authority & contract examples

Two worked examples to show the level of detail we expect per endpoint when implementing.

### Example: `MarkPlaylistItemAsPlayedAsync` (Phase 6)

**Current SQL:**
```sql
UPDATE room_playlists
SET expired = 1, played_at = NOW(), updated_at = NOW()
WHERE id = @PlaylistItemId AND room_id = @RoomId;
```

**Path 1B HTTP signature:**
```
POST /_lio/spectator/rooms/{room_id}/playlist/{item_id}/mark-played
Authorization: X-LIO-Signature: <hmac>
Body: (empty)

Response 200:
{
  "item": {
    "id": 42,
    "room_id": 123,
    "beatmap_id": 7891,
    "expired": true,
    "played_at": "2026-05-14T03:11:02.123Z",
    ...
  },
  "queue_state": {
    "next_item_id": 43,
    "queue_exhausted": false,
    "auto_cloned_item_id": null
  }
}

Response 404: { "code": "playlist_item_not_found" }
Response 409: { "code": "already_expired", "played_at": "..." }
```

**g0v0 service-layer logic:**
1. Look up item by `(room_id, item_id)`. 404 if missing.
2. If already `expired = true`, return 409.
3. `UPDATE room_playlists SET expired = 1, played_at = NOW() WHERE ...`
4. Check if any non-expired items remain in this room. If 0:
   - If QueueMode == HostOnly OR (config flag `AUTO_CLONE_EXHAUSTED_QUEUE` == true): clone the played item as a new playlist row. Set `auto_cloned_item_id` in response.
   - Else: set `queue_exhausted = true` in response.
5. Pick the next item by `playlist_order` ascending where `expired = false`. Return its id as `next_item_id` (null if queue_exhausted).
6. Optionally publish `multiplayer:playlist:item-played` event on redis pub/sub for any future subscribers.

**Spectator change:**
```csharp
public async Task MarkPlaylistItemAsPlayedAsync(long roomId, long playlistItemId)
{
    var resp = await http.PostAsync($"/_lio/spectator/rooms/{roomId}/playlist/{playlistItemId}/mark-played");
    if (resp.StatusCode == HttpStatusCode.NotFound) return; // idempotent
    if (resp.StatusCode == HttpStatusCode.Conflict) return; // already expired, also idempotent
    resp.EnsureSuccessStatusCode();

    // Caller (StandardMatchController.HandleGameplayCompleted) will follow up with
    // GetPlaylistItemAsync to re-fetch the item; the queue_state hints are
    // informational for future optimisation, not required for correctness.
}
```

### Example: `GetUserFriendsAsync` (Phase 3)

**Current SQL** (g0v0 schema):
```sql
SELECT target_id FROM relationship
WHERE user_id = @userId AND type = 'friend';
```

**Path 1B HTTP signature:**
```
GET /_lio/spectator/users/{user_id}/friends
Authorization: X-LIO-Signature: <hmac>

Response 200: { "friend_ids": [123, 456, 789] }
Response 404: { "code": "user_not_found" }
```

**Spectator change:**
```csharp
public async Task<IEnumerable<int>> GetUserFriendsAsync(int userId)
{
    var resp = await http.GetAsync($"/_lio/spectator/users/{userId}/friends");
    if (resp.StatusCode == HttpStatusCode.NotFound) return Array.Empty<int>();
    resp.EnsureSuccessStatusCode();
    var payload = await resp.Content.ReadFromJsonAsync<FriendsResponse>();
    return payload?.FriendIds ?? Array.Empty<int>();
}
```

---

## Appendix B — What this does NOT change

- SignalR hubs: untouched. Their server-side logic stays in the spectator.
- Redis usage: stays direct. Both sides hit `metadata:online:*`, `osu-channel:score:processed`, etc.
- File storage: stays direct. `FileScoreStorage` writes to `/app/replays`.
- JWT validation: stays in the spectator (`Authentication/ConfigureJwtBearerOptions.cs`). HS256 is verified locally — only the resolve-user-id-from-claims step calls g0v0 over HTTP.
- Lazer client: zero changes. The client only knows about the spectator's SignalR/HTTP endpoints, not how the spectator gets its data.

---

*Last updated: 2026-05-14. Next step: lock the open architecture decisions in §11, then start Phase 1.*
