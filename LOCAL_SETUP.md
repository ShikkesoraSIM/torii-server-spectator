# Torii spectator — local sandbox setup

Walks the `torii-customizations` branch through a clean local boot against
your existing g0v0-server stack. Nothing here touches production. Nothing
here touches the M1PP fork. Everything lives in this repo + a separate
docker container running on host port `127.0.0.1:8016`.

## What you're going to end up with

```
your machine
├── g0v0-server (already running)         <- Python backend
│   ├── app:8000          (HTTP, exposed on 127.0.0.1:9000)
│   ├── mysql:3306        (internal only)
│   ├── redis:6379        (internal only)
│   └── osu-network       (docker bridge)
│
└── torii-server-spectator                <- this repo, NEW container
    └── spectator-rp:8016 (HTTP+WebSocket, exposed on 127.0.0.1:8016)
        joined to the same osu-network    <-- talks to mysql/redis/app
```

The lazer client connects to `https://lazer-rp.local` (or whatever you
override its server endpoints to) and that hits `127.0.0.1:8016` for
SignalR / spectator traffic.

## Setup (5 minutes)

### 1. Make sure g0v0-server is up

```sh
cd ../g0v0-server
docker compose ps
```

If `osu_api_server`, `osu_api_mysql`, `osu_api_redis` aren't running:

```sh
docker compose up -d
```

Wait until `osu_api_server` is healthy (`docker compose logs -f app`
should be quiet — startup banner, then waiting for connections).

### 2. Copy the env template + fill in secrets

```sh
cp .env.local.example .env.local
```

Then open `.env.local` and copy three values from `../g0v0-server/.env`:

| Variable | Source |
|---|---|
| `JWT_SECRET_KEY` | `../g0v0-server/.env` → `JWT_SECRET_KEY` |
| `SHARED_INTEROP_SECRET` | `../g0v0-server/.env` → `SHARED_INTEROP_SECRET` |
| `CLIENT_VERSION_WEBHOOK_SECRET` | optional — leave empty for first runs |
| `DB_PASSWORD` | empty for default local MySQL setup |

> **Why these have to match exactly**: the spectator validates JWTs
> against the same HS256 secret g0v0 uses to *issue* them. If they
> drift by a single character, every hub call gets rejected with
> `"invalid token signature"` and the lazer client can't connect to
> any hub.

### 3. Build + start the sandbox spectator

From the `torii-server-spectator` repo root:

```sh
docker compose -f docker-compose.local.yml up -d --build
```

First build takes ~3-5 minutes (pulls .NET SDK image, restores NuGet,
publishes). Subsequent rebuilds with code changes are ~30 seconds.

### 4. Watch the logs

```sh
docker compose -f docker-compose.local.yml logs -f spectator-rp
```

Healthy startup looks like:

```
torii-spectator-rp | info: Microsoft.Hosting.Lifetime[14]
torii-spectator-rp |       Now listening on: http://[::]:8016
torii-spectator-rp | info: Microsoft.Hosting.Lifetime[0]
torii-spectator-rp |       Application started. Press Ctrl+C to shut down.
```

If you see a stack trace mentioning `MySql`, `redis`, or `app:8000` —
that means the docker network isn't shared correctly. Confirm with:

```sh
docker network inspect g0v0-server_osu-network | grep torii-spectator-rp
```

The container name should appear in the inspector output.

### 5. Point your lazer client at the new spectator

Set these env vars before launching lazer (or override in the launch
script you already use to point at Torii):

```sh
export OSU_HUB_URL_OVERRIDE=http://localhost:8016
# or whatever the var your client respects is — check the Torii lazer
# client's auth code for the exact var name.
```

Or if you've got a config file the client reads, swap `lazer-api.shikkesora.com`
for `localhost:8016` for spectator+multiplayer hubs only (leave the
HTTP API at `localhost:9000` since that's where g0v0 itself runs).

---

## Smoke-test checklist

Run through these in order. Each one tests a different surface that
the rebase touched. Stop at the first failure and capture the spectator
log output (`docker compose -f docker-compose.local.yml logs spectator-rp`).

### A. Connection + auth (Stage 1 — JWT HS256 + migrated users)

- [ ] Lazer client connects to the spectator without auth errors.
- [ ] `Microsoft.AspNetCore.Authentication.JwtBearer was configured` log
      line appears at startup (means HS256 path picked up `JWT_SECRET_KEY`).
- [ ] After login: log line `User XYZ connected to MetadataHub`.
- [ ] Manual: try a user with **migrated id** (if you have one in the
      DB where `oauth_tokens.user_id != JWT.sub`) and confirm the log
      line `Token revoked or expired` does NOT appear (the principal-
      rewrite path takes over).

### B. Online presence + Torii badge (Stage 1 — MetadataHub broadcast)

- [ ] Open lazer client #1 logged in as user A.
- [ ] Open lazer client #2 logged in as user B.
- [ ] On client #2, friend user A.
- [ ] Both clients should see each other's presence update in the friend
      panel, with the verified-Torii badge if the client hash matches an
      entry in g0v0's `client_versions` table (otherwise no badge —
      that's expected for an unverified build).
- [ ] On client #1, toggle "appears offline" → client #2 sees user A
      drop off the friend list. Toggle back → user A reappears.
- [ ] Check Redis: `docker compose -f ../g0v0-server/docker-compose.yml
      exec redis redis-cli get metadata:online:<userA-id>` should return
      `"metadata"` while user A is connected, and be empty after they
      disconnect (Stage 3 cleanup).

### C. Score submission (Stage 1 — ScoreProcessedSubscriber retry +
###    Stage 3 — score-best/rank against playlist_best_scores)

- [ ] Play any beatmap on client #1, finish the play (don't quit).
- [ ] Spectator log shows: `RegisterForSingleScoreAsync` → score row
      eventually appears (if the retry hits, you'll see
      `Score row appeared after Xms wait for token` at Information level).
- [ ] After ~2 seconds, the rank/PP popup appears in the lazer client
      with a non-zero rank if you weren't first.
- [ ] Replay file gets written: check `./replays/` in this repo —
      should have a new `.osr` for the score id.

### D. Multiplayer (Stage 2 — DAO targeting g0v0's `rooms` table)

- [ ] Create a multiplayer room from client #1.
- [ ] Join it from client #2.
- [ ] Both can see each other in the room.
- [ ] Add a beatmap → both see it appear in the playlist.
- [ ] Start match → both go through countdown → both load → both play.
- [ ] After the match: both end up on the results screen with each
      other's scores visible.

### E. Daily challenge (Stage 2 — playlist + score flows)

- [ ] Open daily challenge from the main menu.
- [ ] Play through it. Score submits. Leaderboard updates.

### F. Replay playback (Stage 1 — FileScoreStorage swap)

- [ ] In lazer, find one of the scores you just submitted.
- [ ] Watch replay → should download from the spectator and play
      back. The fact that this works means `FileScoreStorage` is
      writing to `/app/replays` correctly inside the container and
      the bind-mount to `./replays` is round-tripping.

### G. Clean disconnect (Stage 3 — CleanUpState cleanup)

- [ ] Close the lazer client cleanly (sign out, then close window).
- [ ] In spectator log: `User X disconnected` line appears.
- [ ] Redis key `metadata:online:<userId>` is gone (verify with
      `redis-cli get metadata:online:<userId>` — should return nil).
- [ ] g0v0's `lazer_users.last_visit` for that user is updated to
      ~now (open Adminer at `http://127.0.0.1:8081`, login with
      g0v0 MySQL credentials, query `SELECT id, username, last_visit
      FROM lazer_users WHERE id = X`).

---

## What's expected to NOT work

These deliberately fail in the local sandbox — they're tracked for
Stage 4 and don't block any of the smoke tests above.

- ⛔ **Matchmaking queue** — opening the matchmaking lobby in lazer.
  The `MatchmakingQueueBackgroundService` registers and picks up queue
  events, but the matchmaking_pool / matchmaking_user_stats SQL fails
  because g0v0 needs alembic migration `c4d5e6f7a8b9` applied first.
  Avoid clicking "Find Match" until Stage 4 is done.

- ⛔ **Referee hub endpoints** — Torii doesn't run referee, the hub
  class is compiled but the HTTP endpoints aren't mapped. Calling them
  via SignalR results in a 404.

- ⚠️ **Build-version count tracking** — `BuildUserCountUpdater` is
  gated behind `TRACK_BUILD_USER_COUNTS=1`. Leave the env var unset
  (which is the default) and the hosted service no-ops. If you set it,
  it'll fail on the `osu_builds` table that g0v0 doesn't have. The DAO
  has stub returns so the failure is graceful (empty result lists)
  rather than crashing.

- ⚠️ **Client-version verification** — `ClientCheckVersion` is gated
  behind `CLIENT_CHECK_VERSION=1`. Leave it off. If you flip it on,
  the SignalR `OnConnectedAsync` filter rejects unverified hashes.

---

## Troubleshooting

### "Token expired or revoked" on every request

JWT_SECRET_KEY in `.env.local` doesn't match `../g0v0-server/.env`.
Copy it character-for-character.

### "Cannot connect to MySQL: Unknown server host 'mysql'"

The compose stack didn't join the right network. Confirm:

```sh
docker network ls | grep osu-network
# should show: g0v0-server_osu-network
```

If the network name is different (e.g. compose project was renamed),
edit `docker-compose.local.yml` → `networks.g0v0-network.name`.

### "Address already in use: 0.0.0.0:8016"

Another process is on port 8016. Either kill it or change `SERVER_PORT`
+ the host port mapping in `docker-compose.local.yml`.

### Spectator container starts but lazer immediately disconnects

Most common cause: lazer is hardcoded to https + the spectator is
listening on http only. Either:
  - Run a TLS reverse proxy (Caddy / nginx) in front of the spectator
    on a self-signed cert mapped to `lazer-rp.local`
  - Or rebuild lazer with the http override env vars set.

### How to wipe the sandbox and start fresh

```sh
docker compose -f docker-compose.local.yml down
rm -rf ./replays
docker compose -f docker-compose.local.yml up -d --build
```

---

## Once smoke tests pass

Don't deploy yet. The cutover plan in `TORII_PORT.md` runs the same
container alongside prod first (different host port, different subdomain
behind Caddy), validates against real users for 24-48h, THEN repoints
prod traffic. We'll write that piece when we're confident the local
sandbox is solid.
