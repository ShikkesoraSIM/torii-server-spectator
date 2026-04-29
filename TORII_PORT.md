# Torii spectator port — status

This branch (`torii-customizations`) is a fresh port of Torii-specific changes
on top of upstream `ppy/osu-server-spectator`. It replaces the older M1PP-fork
+ wholesale-lift approach (which fought upstream's modern matchmaking
architecture and dragged 22 stub methods worth of porting work).

## Stage 1 — DONE: build clean (0 errors / 0 warnings)

The five files below are the foundational Torii integration. With them
applied, upstream's matchmaking + ranked-play subsystems already compile and
the metadata hub broadcasts the verified-Torii client-name event end-to-end.

| File | Change | Why |
|---|---|---|
| `AppSettings.cs` | + `ClientVersionWebhookSecret`, `JwtSecretKey`, `UseLegacyRsaAuth`, `OsuClientId` | Surface env-vars Torii needs without breaking upstream's existing config story. Defaults match upstream when toggles are off, so vanilla deploys aren't affected. |
| `Authentication/ConfigureJwtBearerOptions.cs` | HS256 path + migrated-user principal rewrite + `IsUserRestrictedAsync` gate | g0v0 issues HS256 JWTs (no access to osu!web's RSA private key). Migrated users on Torii have `oauth_tokens.user_id` diverge from the JWT `sub`, which would otherwise break every hub method that resolves the user from `Context.UserIdentifier`. |
| `Hubs/Metadata/MetadataHub.cs` | + `IConnectionMultiplexer redis`, `ToriiClientNameResolver`, `IHubContext<MetadataHub>` ctor deps; redis online key on connect; `UserClientNameUpdated` broadcast alongside every presence update; initial seed of client-names on `BeginWatchingUserPresence` | g0v0/web reads `metadata:online:{userId}` to know who's connected without re-implementing SignalR group introspection. The `UserClientNameUpdated` SignalR event drives the verified-Torii badge in the lazer client. |
| `Hubs/Spectator/ScoreProcessedSubscriber.cs` | retry loop in `RegisterForSingleScoreAsync` (max 750ms, 60ms steps) | Lazer fires `EndPlaySession` immediately after `submitScore`, so the spectator can race g0v0's `/scores` POST handler — without this retry the rank/PP popup silently never fires for the play. |
| `Extensions/ServiceCollectionExtensions.cs` | DI register `ToriiClientNameResolver` (singleton + hosted service); swap `S3ScoreStorage` → `FileScoreStorage` | Resolver is what the metadata hub queries. FileScoreStorage replays land at `AppSettings.ReplaysPath` (`/app/replays` inside the container, bind-mounted from the host). |
| `Services/ToriiClientNameResolver.cs` | new file (M1PP-only) | Pulls the trusted-hash list from g0v0's `/api/private/client-versions/torii-hashes` every 15 min and caches in-memory. Cheap O(1) reads on every presence broadcast vs a per-presence DB hit. |

## Stage 2 — REMAINING: g0v0 schema adapter

The big remaining piece is `Database/DatabaseAccess.cs`. Upstream targets
osu-web's schema (`multiplayer_rooms`, `phpbb_users`, `oauth_access_tokens`,
`osu_beatmaps`, `osu_logins`, etc). Torii (g0v0) has its own:

| upstream table | g0v0 table |
|---|---|
| `multiplayer_rooms` | `rooms` |
| `multiplayer_rooms_high` | `room_participated_users` |
| `multiplayer_playlist_items` | `room_playlists` |
| `multiplayer_realtime_room_events` | `multiplayer_events` |
| `phpbb_users` | `lazer_users` |
| `phpbb_zebra` | `relationship` |
| `oauth_access_tokens` | `oauth_tokens` |
| `osu_beatmaps` | `beatmaps` |
| `osu_logins` | `user_login_log` |

Plus column renames (`multiplayer_rooms.user_id` → `rooms.host_id`) and
columns g0v0 has that osu-web doesn't (`beatmap.total_length`).

**Options for stage 2** (in order of preference):

1. **Quirurgical SQL rewrites** in upstream's `DatabaseAccess.cs`. Each query
   targeting a renamed table gets its FROM/INTO/UPDATE clause swapped. Column
   references inside SELECT lists / joins likewise. Slow but preserves all
   the new matchmaking method implementations upstream wrote.

2. **Wholesale copy of prod's DatabaseAccess** (which is g0v0-adapted) onto
   this branch + stub the 21 new matchmaking methods upstream added with
   `NotImplementedException`. Faster route to "everything except matchmaking
   works against g0v0", but the matchmaking methods need real impls before
   ranked-play can launch — and those impls have to be written from scratch
   against the matchmaking_* tables.

3. **Hybrid DAO**. Keep upstream's IDatabaseAccess, but have a `G0v0DatabaseAccess`
   class that wraps a prod-style implementation and translates calls. Cleanest
   long-term but adds a layer.

`ISharedInterop` / `SharedInterop` have a similar problem — prod's
`SharedInterop` has `EnsureBeatmapPresentAsync` (used by the database adapter)
which upstream doesn't. Same options apply.

## Stage 3 — REMAINING: docker-compose.yml + cutover

- `docker-compose.yml`: copy from prod (`spectator-m1pp/docker-compose.yml`),
  verify env vars (`SHARED_INTEROP_DOMAIN`, `JWT_SECRET_KEY`,
  `CLIENT_VERSION_WEBHOOK_SECRET`, `OSU_CLIENT_ID`, etc.), keep the replays
  bind mount (`./replays:/app/replays`).
- Sandbox container `spectator-rp` on a different port (e.g. `:8016`) +
  Caddy subdomain `lazer-rp.shikkesora.com`. Verify against the existing prod
  spectator side-by-side before cutover.
- Cutover plan: stop old spectator, repoint prod to the new image, watch logs
  ~10min, instant rollback by restarting the old container if anything breaks.

## What's deliberately NOT ported (covered by upstream natively)

- M1PP's `Hubs/Multiplayer/HeadToHead.cs` / `TeamVersus.cs` /
  `MatchTypeImplementation.cs` / `MultiplayerQueue.cs` / `MultiplayerHubContext.cs`
  — replaced by upstream's modern `IMatchController` + `Standard/*`
  + `IMultiplayerRoomController` + `MultiplayerRoomController` family.
- Skip-functionality (the `b4aed4b` commit on prod): upstream now has
  `MatchStartCountdown` with its own skip semantics.
- Score storage: upstream's `FileScoreStorage` covers what M1PP's
  `ServerScoreStorage.cs` did.

## Recovery: prod's 6 unique commits

Prod was 6 commits ahead of `M1PPosu/master`, none of those commits were
pushed anywhere. They're now safely in
`/c/Users/megablackito/Favorites/toriiserverlocal/spectator-prod.bundle`
and as `prod-eu/master` in the `osu-server-spectator-m1pp` local clone — no
data loss risk anymore even if prod gets nuked.
