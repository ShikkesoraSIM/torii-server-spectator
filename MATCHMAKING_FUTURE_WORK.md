# Matchmaking — future work the spectator could opt into

This file lives in the spectator repo because the items below are
spectator-side hooks. None of them are required for the current
matchmaking feature to work; they're polish that benefits engagement
on small servers (Torii's main use case) without breaking
compatibility for downstream forks.

If you're running a different osu-server-spectator fork and don't want
any of this, simply don't apply the patches — the matchmaking surface
keeps working without them.

## 1. Live queue presence broadcast

**Idea:** When a player joins the matchmaking queue, push a live count
to redis so the website's ranking page can show "3 players queueing in
osu! Quick Play right now" and so other players of similar rating can
get a soft notification ("someone close to your skill is queueing").

**Why it matters on small servers:** The biggest engagement problem
isn't "matches don't work" — it's "I queued for 5 minutes and nobody
showed up." Surfacing presence makes "be the third player in queue, the
match starts in 30s" a real proposition.

**Implementation sketch (spectator side, ~30 lines):**

In `MatchmakingQueueBackgroundService.UpdateLoop` or wherever the
service tracks active queuers:

```csharp
// Hook: after every queue tick, publish per-pool counts.
private async Task BroadcastQueuePresence()
{
    var counts = queue
        .GroupBy(q => q.PoolId)
        .ToDictionary(g => g.Key.ToString(), g => g.Count());

    var hashEntries = counts
        .Select(kv => new HashEntry(kv.Key, kv.Value))
        .ToArray();

    var db = redis.GetDatabase();
    await db.HashSetAsync("matchmaking:queue:counts", hashEntries);
    await db.KeyExpireAsync("matchmaking:queue:counts", TimeSpan.FromMinutes(2));
}
```

**Implementation sketch (g0v0 side, ~50 lines):**

Add `GET /api/v2/matchmaking/queue-state` that reads
`matchmaking:queue:counts` from redis and returns `{pool_id → count}`.
Frontend polls every 10s when on the rankings page.

For the "soft notification" hook: g0v0 subscribes to a redis pub/sub
channel `matchmaking:queue:join` (spectator publishes
`{pool_id, user_id, rating}` on join). When g0v0 sees a join, it scans
`metadata:online:*` keys for online users with stats in the same pool
within ±200 rating, and emits a chat message via
`server.batch_message()`:

> Bot: "🎯 hey, someone of similar skill (Diamond, ~1850) just queued
> for osu! Ranked Play. Want to join? `Find Match`."

Throttle per recipient to at most once per 10 minutes via a redis SETEX
key (`matchmaking:notify:cooldown:{user_id}`).

**Why this is filed as future work, not done:** Requires a spectator
patch. Other server forks pulling our spectator may not want the
redis publish. If we want it, we should gate it behind an env flag
(e.g. `MATCHMAKING_BROADCAST_PRESENCE=true`) so it's opt-in.

## 2. Surface "Find Match" higher in the lazer client

**Current state:** Upstream osu! has matchmaking nested 3 menus deep
(Multiplayer → Lounge → Find Match). On a small server where ranked
play is the headline feature, that's too far to discover.

**Implementation:** This is a **client fork** change (`torii-osu`),
not a spectator change. To bring it up to the main menu:

1. Edit `osu.Game/Screens/Menu/MainMenu.cs` — add a button that
   navigates straight to `MatchmakingScreen` (skip the lounge step).
2. Or add a sub-button under "Multiplayer" that says "Find Match"
   directly.

**File pointers (verify against the current torii-osu base):**

- `osu.Game/Screens/Menu/MainMenu.cs` — top-level menu buttons
- `osu.Game/Screens/OnlinePlay/Matchmaking/MatchmakingScreen.cs` — the
  destination screen
- `osu.Game/Screens/OnlinePlay/Multiplayer/MultiplayerLounge.cs` — the
  lounge that currently mediates the click

The change is ~20 lines but lives in the lazer fork. Document the
patch in `torii-osu/MATCHMAKING_SURFACING.md` so subsequent rebases
don't lose it.

**Not done in this repo because:** the spectator and the website
don't have visibility into the lazer client menu structure. Tracked
here so it's not forgotten.

## 3. Per-match audience streaming

**Idea:** When a matchmaking match starts, allow non-participants to
spectate via the existing `SpectatorHub.StartWatchingUser` API — but
also broadcast a "live now" pill in the website with a deep link to
the lazer client's spectate screen.

**Implementation:**

- Spectator publishes match start to redis
  (`matchmaking:match:started` channel) with `{room_id, pool_id,
  participants, expected_duration}`.
- g0v0 exposes `GET /api/v2/matchmaking/live` returning currently-
  active matches.
- Website renders a "Watch live" panel on the rankings page below the
  beatmap rotation.

**Status:** filed; not high priority because Torii doesn't have a
critical mass of simultaneous matches yet. Revisit when the active
match count regularly hits double digits.

## 4. Pool-specific custom mod presets

**Idea:** Allow admins to define mod presets per pool ("DT-only",
"HR/HD", "no-mod") that the spectator picks instead of letting the
client send arbitrary mods.

**Implementation:** Already partially supported — `matchmaking_pool_beatmaps.mods`
column is JSON. What's missing is the spectator honouring it instead of
letting the client choose freely.

**Status:** filed; needs a small spectator patch in
`MatchmakingMatchController.PickBeatmapForRoundAsync` to copy `mods`
from the pool row into the round's playlist item. Same opt-in concern
as item 1 — gate behind a feature flag.

## 5. Tier-locked pools

**Idea:** Make some pools only joinable by users in a specific tier
(e.g. "Diamond+ pool"). Reduces stomping in casual matchmaking.

**Implementation:**

- Schema: `matchmaking_pools.min_rating` + `max_rating` columns
  (nullable).
- Spectator: `MatchmakingJoinQueue` rejects users whose rating in the
  pool's ruleset is outside the band, with a clear error toast.

**Status:** filed; non-urgent until pool count grows.

---

These are all opt-in / additive. The current matchmaking experience
works without any of them — file is here as a roadmap so the polish
items don't get lost.
