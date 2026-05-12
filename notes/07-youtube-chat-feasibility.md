# YouTube chat parallel integration — feasibility writeup

**Date**: 2026-05-12
**Status**: Feasibility analysis. Not yet a spec or plan; not committed to a release. Promote to a real spec under `docs/superpowers/specs/` if/when scoped for a slice.
**Motivation**: FrostPrime — the streamer this mod is being aimed at — wants YouTube chat participation alongside Twitch (per ArmadilloTea in FrostPrime's Discord, 2026-05-12). This file captures the research Surfinite already did via a Claude desktop conversation, plus the architectural fit with our existing `Ti/Chat/` layer, so a future session can pick this up without re-deriving the landscape.

## TL;DR

- **Reading YouTube chat from a mod** is doable with a ~200-LOC custom scraper of the `youtubei` internal endpoint (the same one YouTube's own chat UI uses). No quota, no OAuth.
- **Posting back to YouTube** requires OAuth + Google app verification. Pragmatic decision: **read-only on YouTube**, echo receipts to Twitch only.
- **Architectural fit is clean**: a `YouTubeChatService : IChatService` slots into the existing `Ti/Chat/` surface alongside `TwitchIrcChatService`. An aggregator pattern wraps both into a single `IChatService` the existing `VoteCoordinator` consumes unchanged.
- **Real risks**: (a) the scraped endpoint shape can break when YouTube changes it (typically every 6–12 months per the libraries that do this in other languages); (b) latency asymmetry — YouTube chat lags Twitch by 2–5 seconds, so a 30s vote window under-represents YouTube votes if both share one window.
- **Estimated effort**: 1–2 weeks for a working v1, mostly spent on the scraper, latency handling, and video-ID discovery UX.

## Why a custom scraper, not the official API

YouTube's Data API v3 has a **10,000 unit/day quota** by default, and `liveChatMessages.list` costs **5 units per call**. Polling chat every ~3 seconds eats the daily quota in one stream. Quota extensions exist but require Google approval and are not granted reliably for hobby projects. Also: posting messages back requires OAuth with sensitive scopes that need Google app verification (multi-week process) for >100 users — meaning a mod end users install with the official API path is essentially impossible without enterprise-tier paperwork.

The community workaround — used by `pytchat` (Python), `chat-downloader` (Python), `youtube-chat` (Node.js), and most indie chat overlays — is to **scrape the same internal JSON endpoint that YouTube's own chat popout uses**:

1. `GET https://www.youtube.com/live_chat?v={VIDEO_ID}` — returns an HTML page with embedded `ytInitialData` JSON and an `INNERTUBE_API_KEY` constant.
2. Extract `INNERTUBE_API_KEY` + the initial continuation token from `ytInitialData`.
3. `POST https://www.youtube.com/youtubei/v1/live_chat/get_live_chat?key={API_KEY}` with body `{"continuation": "..."}`.
4. Response contains structured JSON: message text, `authorChannelId` (the dedup key), author display name, member/mod badges, Super Chat amounts (we don't use these), and a new continuation token + `timeoutMs` for the next poll.
5. Loop, respecting `timeoutMs`.

No auth, no quota. Read-only by design (no `SendMessage`-style endpoint on this path). Library implementations in other languages confirm this works at scale.

The fragility: the regex that extracts `INNERTUBE_API_KEY`, the JSON shape, and occasionally the URL pattern change when YouTube redesigns. Cross-language scraper libraries publish a new version every 6–12 months on average to keep up. We'd need to plan for the same cadence — but the impact is contained because all the fragile parsing lives in one file behind an `IChatService` boundary.

**No .NET library exists** that's worth pulling in. Searched 2026-05-12; the active Python/JS ones don't have a maintained C# equivalent. So this is a roll-our-own task.

## Architectural fit with current `Ti/Chat/`

Our `IChatService` interface ([IChatService.cs](../src/Ti/Chat/IChatService.cs)) is well-shaped for this:

| Need | Existing surface | Notes |
|---|---|---|
| Read incoming messages | `event MessageReceived` raises `ChatMessage` | YT scraper raises this on each polled message |
| Read-only marker | `CanSend` property already exists | YouTubeChatService returns `false` here |
| Send back | `SendMessageAsync` | Throws `NotSupportedException` or no-ops on the YT impl |
| Connect | `ConnectAsync(string channel, ChatCredentials?, CancellationToken)` | "channel" semantically becomes a YouTube channel ID; credentials param ignored |
| Disconnect | `Disconnect()` | Stop the poll loop, dispose HttpClient |
| State machine | `ChatConnectionState` enum + `ConnectionStateChanged` event | Reuse `Connecting` / `ConnectedReadOnly` / `Reconnecting` / `Disposed`; `AuthenticationFailed` doesn't really apply (no auth), but `JoinFailed` matches the "no live broadcast for this channel" case |
| Voter dedup | `ChatMessage.VoterKey => UserId ?? $"login:{Login}"` | Set `UserId = $"yt:{channelId}"` so YouTube voters never collide with Twitch user IDs |

`ChatMessage`'s schema ([ChatMessage.cs](../src/Ti/Chat/ChatMessage.cs)) maps cleanly. The `IsSubscriber` / `IsModerator` / `IsVip` flags are Twitch-flavored but YouTube has equivalent concepts (channel members, moderators, no VIP) — we'd map sensibly: `IsSubscriber ← isMember`, `IsModerator ← isModerator`, `IsVip ← false`.

### Aggregator for dual-platform votes

Currently `VoteCoordinator` takes one `IChatService`. To support Twitch + YouTube simultaneously without rewriting it:

- Build a `MultiChatService : IChatService` that wraps N child services.
- Forward `MessageReceived` from all children (merged tally).
- Forward outgoing `SendMessageAsync` only to children whose `CanSend == true` (so receipts go to Twitch only).
- `State` is the "best" state across children (`ConnectedReadWrite` if any is, else `ConnectedReadOnly` if any is, else the worst observed state — semantics TBD).
- `IsConnected` = any child connected.

`VoteCoordinator` doesn't change. `ModEntry` wires `MultiChatService` containing a `TwitchIrcChatService` + a `YouTubeChatService`, hands the multi to `VoteCoordinator`. Single voting tally, dedup keys are platform-prefixed so same-name voters on both platforms count separately (intentional per design note below).

## Decisions log (resolved 2026-05-12)

All ten design decisions below were resolved in conversation 2026-05-12 between Surfinite and Claude before promoting this feasibility writeup to spec. The reasoning is captured alongside each decision for the spec author (next session) and for future-Surfinite if any decision needs to be revisited.

- **D1 — Cross-platform vote-counting**: **count twice**. Same human voting on both Twitch and YouTube counts as two votes. Cross-platform identity is fundamentally unfixable for anonymous chat (matching display names doesn't prove same human; differing display names doesn't disprove). **Future optional heuristic** (not v1): if same display name on both platforms, prefer the Twitch vote and drop the YouTube vote. Simpler than picking-latest, which would require timestamp alignment across two chat clocks with different latencies — and YouTube's 2–5s lag makes that unreliable anyway. Voter dedup keys are `(Platform, UserId)` tuples; pragmatic implementation: prefix YouTube IDs with `"yt:"` so `ChatMessage.VoterKey` stays a single string.

- **D2 — Vote-window timing**: **ignore for v1**. Single shared 30s window. YouTube under-represents because of its 2–5s lag. Acceptable for v1; document as a known limit. Revisit if FrostPrime's YT viewers complain.

- **D3 — Outgoing-receipt policy**: **read-only YouTube**. No receipts posted to YouTube. All chat receipts go to Twitch only. Posting to YouTube would require OAuth + Google app verification (multi-week process, incompatible with "mod end users install"). Streamer's YouTube viewers see the in-game tally label but no Twitch-style chat receipts.

- **D4 — Video ID discovery**: **channel ID + auto-discovery**. Streamer configures `youtubeChannelId` once in settings JSON. At mod start (and on reconnect), the mod fetches `youtube.com/channel/{ID}/live` and follows the redirect to find the active video ID. No redirect = no live broadcast = log Warn and retry every ~60s (matches D7). Adds one extra scraped endpoint to maintain, but the manual-video-ID alternative's per-stream JSON edit was rejected as worse UX.

- **D5 — Members-only chat**: **don't support in v1**. Anonymous scraping works for public live chat only. Members-only chat needs authenticated session cookies (brittle, security-sensitive, real onboarding hurdle). Document as a known limit. Streamers running members-only mode can disable that restriction during mod-using streams.

- **D6 — Settings JSON schema additions**: **add just `youtubeChannelId`** (optional, nullable string). Missing or `null` = YouTube disabled, only Twitch runs. Non-empty value = YT enabled, mod auto-discovers the active live broadcast via the channel `/live` redirect (per D4). Validation: `ModSettings.Load` treats malformed values (whitespace-only, control chars) as `Malformed` (same pattern as `oauthToken`); a missing field is fine and means "YT disabled" (NOT a malformed-settings condition). Everything else (retry cadence per D7, polling intervals from YouTube's `timeoutMs` response, receipt-on-state-change behavior per D8) is hardcoded for v1. Escape-hatch field `youtubeVideoIdOverride` was considered and rejected — if YouTube changes the `/live` redirect format and breaks auto-discovery, we ship a code fix rather than asking streamers to find video IDs themselves. (Resolved 2026-05-12 retroactively after the parallel spec-drafting session noticed the gap.)

- **D7 — YouTube failure mode**: **silent degradation + periodic retry**. If YT can't connect (no live broadcast / endpoint broken / network error), log at Warn level, keep retrying every ~60s in the background. Votes count Twitch only until YT recovers. Mod stays loaded, Twitch keeps working. Matches the temporary-disconnect semantics of `TwitchIrcChatService` and the spirit of v4 spec Decision 21.

- **D8 — Streamer status feedback**: **Twitch chat receipt at startup + on state changes**. The existing `slay-the-streamer-2 v… connected` receipt is extended to also report YouTube state, e.g., `… Twitch connected; YouTube: no live broadcast found`. When YT later connects mid-session, a second receipt fires (`YouTube connected: tracking chat from <channel>`). Fits the Twitch-only-receipts model from D3.

- **D9 — Voter dedup keying**: **prefix YT IDs with `"yt:"`**. The `ChatMessage.VoterKey` is `UserId ?? $"login:{Login}"`. For YouTube messages, set `UserId = $"yt:{channelId}"` so YouTube voters cannot collide with Twitch user IDs (which are bare numeric strings). No schema change to `ChatMessage`; just discipline on how we populate the field in `YouTubeChatService`. (Decision implicit but worth documenting because it's load-bearing for D1.)

- **D10 — Receipt wording**: **Twitch chat receipts unchanged** (merged tally invisibly) BUT **in-game vote-tally label MUST show separate per-platform tallies** when YT is enabled. Visual format: separate lines for each platform (e.g., `Twitch: 0=1, 1=3, 2=0` / `YouTube: 0=0, 1=2, 2=1`). Visual-combining design is explicitly deferred to a later iteration. **Implication**: `VoteSession` needs to track votes by `(platform, optionIndex)`, not just `optionIndex`. `VoteTallyLabel` needs split rendering. This is the only decision that pushes complexity back into `Ti/Voting/` — every other YT integration is contained in `Ti/Chat/`.

## (Original analysis below — for reference; the Decisions log above supersedes individual recommendations)

These are not code-shaped; they're scoping choices Surfinite needs to make before the implementation phase.

### D1: Cross-platform vote-counting policy

Same human voting on both Twitch and YouTube: does that count as 1 vote or 2?

- **Count 2** (recommended): policing identity across platforms is impossible. The `(Platform, UserId)` prefix scheme naturally gives 2 votes to one person if they use both. Matches what most cross-platform polls do.
- **Count 1**: requires the streamer to pre-link Twitch + YouTube identities. Hard UX, brittle, probably not worth it.

### D2: Vote-window timing under latency asymmetry

YouTube chat is 2–5+ seconds behind Twitch end-to-end. Our 30s vote window currently starts when the streamer clicks. Options:

- **Single shared window, extended duration** (simple): bump window to ~33–35s when YouTube is enabled. Twitch viewers see "30s" but their votes have a few extra seconds to land. Mildly penalises Twitch latency-wise but trivial to implement.
- **Per-platform windows that close at different times** (clean but stateful): each platform has its own close timer; merge tallies at the end. More state, more complex receipt UX (which "close" receipt fires when?).
- **Adaptive window**: extend by the observed YouTube-vs-Twitch delay. Detection is non-trivial.

Recommendation: shared window + small extension. Document the asymmetry. Revisit if streamer feedback flags it.

### D3: Outgoing-receipt policy

YouTube has no easy posting path. Options:

- **Read-only YouTube; receipts via Twitch only** (recommended): chat receipts (`Vote: ...`, `Chat chose ...`, etc.) fire in the Twitch channel only. YouTube viewers see the in-game tally label but no Twitch-style chat receipts. Clear and simple.
- **Skip outgoing entirely if YouTube is the only configured chat**: degraded mode with no chat receipts at all; in-game tally is the only feedback. Acceptable for a YouTube-only streamer who chooses not to wire up Twitch.
- **Cross-post Twitch receipts onto YouTube via official-API + OAuth**: heavy lift (Google app verification), brittle (quota), and breaks the "mod end users can install" goal. Not recommended.

Recommendation: ship D3-option-1. Document D3-option-2 as a fallback the streamer can choose. Skip D3-option-3 for v1.

### D4: Video ID discovery UX

Twitch settings = channel name (stable across streams). YouTube settings = ??? The active video ID changes every stream.

Options:

- **Channel ID + auto-discovery** (recommended): streamer configures their YouTube channel ID once. At mod-start (and on reconnect), GET `https://www.youtube.com/channel/{ID}/live` and follow the redirect to find the active video ID. If no redirect → no live broadcast → log warn, retry every N seconds.
- **Manual video ID per stream**: streamer pastes a video ID into settings before each stream. Annoying but simple.
- **Combined**: prefer manual video ID if present, fall back to channel-ID auto-discovery.

Recommendation: channel ID + auto-discovery. The streamer configures once; the mod handles the rest. Adds a second scraped endpoint to our fragility surface but it's small.

### D5: Members-only chat support

Scraping works for public live chat. Members-only chat requires being signed in (cookies). YouTube channels with members-only mode would not be readable by our scraper.

- **Don't support members-only for v1** (recommended): document as a known limit. Most streamers run public chat anyway.
- **Support via streamer's session cookie**: requires the streamer to extract and paste a cookie. Brittle, security-sensitive, off the table.

## Effort estimate

For a working v1 (Twitch + YouTube parallel voting, read-only YouTube, channel-ID auto-discovery, single shared window):

- `YouTubeChatService` scraper + IChatService impl: ~3–4 days.
- `MultiChatService` aggregator + tests: ~1 day.
- Live-broadcast auto-discovery: ~1 day.
- Settings + ModEntry wiring: ~1 day.
- Integration tests + operator-validation playthrough: ~1 day.

Total: **~1–2 weeks** of focused work. Most risk is in the scraper because YouTube's endpoints are undocumented and change unannounced — budget for a "fix this when it breaks" cadence post-ship.

## Open questions for FrostPrime

If Surfinite and FrostPrime sync up (per ArmadilloTea's suggestion, when the tournament finishes):

1. Is YouTube parallel a hard requirement, or nice-to-have? Affects whether we ship without it.
2. What's the expected YouTube chat volume? Pytchat-class scrapers reportedly lag on >1000 msg/min; we'd need to benchmark if his audience is that big.
3. Members-only or public chat?
4. Cross-platform vote-counting policy preference (D1)?
5. Does the existing chat overlay he uses for YouTube (the "displays both chats on screen" tool Jessie mentioned) constrain our integration shape?
6. Is FrostPrime open to having Surfinite continue this work after the tournament rather than commissioning someone else? (per ArmadilloTea: "he intended on paying someone")

## Cross-references

- Discord conversation that prompted this writeup: pasted in chat transcript 2026-05-12.
- `Ti/Chat/IChatService.cs` — the interface to implement.
- `Ti/Chat/ChatMessage.cs` — the message schema with `VoterKey` dedup.
- `Ti/Chat/TwitchIrcChatService.cs` — the existing reference implementation for one platform.
- Memory: `youtube_chat_scraping_landscape.md` — reusable technical notes on the scraping approach (cross-project applicable).
