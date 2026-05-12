# YouTube fixture refresh — monthly maintenance task

**Cadence**: monthly OR when scraper health-check telemetry (`[YouTubeLiveChatScraper] N consecutive parse failures at ...`) starts firing.

**Why**: YouTube's `youtubei` endpoint is undocumented and changes silently. The `tests/Fixtures/youtube_live_chat_*.json` fixtures rot when YouTube ships a redesign; refreshing them monthly catches regressions before they bite during a live operator-validation session.

## Manual refresh process

1. Pick a public live broadcast (YouTube channel currently streaming). Note the channel ID.
2. Capture the channel/live redirect:
   ```powershell
   curl -i -L "https://www.youtube.com/channel/$CHANNEL_ID/live" `
       -A "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36" `
       -b "CONSENT=YES+cb"
   ```
   Note the final `Location:` header — confirm it matches `/watch?v={VIDEO_ID}`.

3. Capture the `live_chat?v=...` page:
   ```powershell
   curl -s "https://www.youtube.com/live_chat?v=$VIDEO_ID" `
       -A "Mozilla/5.0 ..." -b "CONSENT=YES+cb" `
       -o tests/Fixtures/youtube_live_chat_page.html
   ```
   Inspect the file:
   - `INNERTUBE_API_KEY` value (regex `"INNERTUBE_API_KEY":"([A-Za-z0-9_-]+)"`).
   - `INNERTUBE_CONTEXT.client.clientVersion`.
   - Initial continuation token nested under `liveChatContinuation.continuations[0]`.

4. Capture a `get_live_chat` POST response:
   ```powershell
   $body = '{"context":{"client":{"clientName":"WEB","clientVersion":"$CV"}},"continuation":"$CONT"}'
   curl -X POST "https://www.youtube.com/youtubei/v1/live_chat/get_live_chat?key=$API_KEY" `
       -H "Content-Type: application/json" -A "Mozilla/5.0 ..." -b "CONSENT=YES+cb" `
       -d $body -o tests/Fixtures/youtube_live_chat_2026-MM-DD.raw.json
   ```

5. Anonymize:
   - `authorExternalChannelId` → `UCfixture001`, `UCfixture002`, ...
   - `authorName.simpleText` → `Fixture Author 1`, ...
   - `message.runs[*].text` → benign text (e.g., `Test message #0`).
   - `videoId` → `FIXTUREvid001`.

6. Save anonymized to `tests/Fixtures/youtube_live_chat_2026-MM-DD.json`. Archive the prior fixture.

7. Run scraper tests:
   ```powershell
   dotnet test --filter "FullyQualifiedName~YouTubeLiveChatScraperTests"
   ```
   If anything fails, the parser needs updating — see `src/Ti/Chat/YouTubeChat/YouTubeLiveChatScraper.cs`.

8. Bump `ScraperRevision` constant in `YouTubeLiveChatScraper.cs` to the current date.

9. Commit: `chore(yt-chat): refresh fixtures YYYY-MM-DD`.

## Redesign-response checklist

If a YouTube redesign breaks the scraper, the failures are scoped to:
- `ApiKeyRegex` — update regex pattern.
- `ClientVersionRegex` — update regex pattern.
- `ContinuationRegex` — update regex pattern.
- JSON traversal in `PollAsync` — check renderer type names and field paths.

The health-check telemetry log line (`[YouTubeLiveChatScraper] N consecutive parse failures at <location>; structural sample: ...`) tells you exactly which location is failing.
