# Plan B.1 Vertical Slice Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a working end-to-end Slay the Spire 2 mod that lets Twitch chat vote on a player's Neow blessing choice — production `TwitchIrcChatService`, JSON-file credentials, in-game tally label, single Harmony patch on `NEventRoom.OptionButtonClicked`, all degrading gracefully to vanilla on any failure.

**Architecture:** Suspend-and-resume Harmony pattern (prefix returns `false`, fires `_ = HandleVoteAsync(...)`, async handler runs vote, `dispatcher.Post(...)` re-invokes original with chat-chosen option). Plan A's TI core is reused unchanged except for one 1-line addition (`VoteCoordinator.Dispatcher` property). All new BCL-only code is unit-tested via xUnit + Plan A's existing fakes; Godot UI and Harmony patch are operator-validated in-game.

**Tech Stack:** C# 12 / .NET 9, Godot 4.5.1 Mono SDK, HarmonyLib (`0Harmony.dll` shipped with game), xUnit 2.9, `System.Text.Json`. Tests run via `dotnet test`; build assembled via `pwsh -File build.ps1`.

**Source spec:** [`docs/superpowers/specs/2026-05-09-plan-b-1-vertical-slice-design-v3.md`](../specs/2026-05-09-plan-b-1-vertical-slice-design-v3.md). When the plan and spec disagree, the spec wins; flag the disagreement and stop for clarification.

**Per-task commits**: each task ends in a `git commit` with a `plan-b-1/N.M:` prefix per the established convention. The user has pre-authorised commits to `main` for this work.

---

## File Structure

**New files:**
- `src/Ti/Chat/Internal/IIrcTransport.cs` — internal abstraction so unit tests can inject a fake stream.
- `src/Ti/Chat/Internal/SslIrcTransport.cs` — production TLS implementation of `IIrcTransport`.
- `src/Ti/Chat/TwitchIrcChatService.cs` — `IChatService` impl wiring parser + queue + transport + state machine.
- `src/Ti/Ui/VoteTallyLabel.cs` — Godot `RichTextLabel` showing vote tally + countdown.
- `src/Game/Bootstrap/ModSettings.cs` — JSON config reader (path injected).
- `src/Game/DecisionVotes/NeowBlessingVotePatch.cs` — Harmony Prefix on `NEventRoom.OptionButtonClicked`.
- `tests/Chat/Internal/FakeIrcTransport.cs` — test transport that records writes + injects reads.
- `tests/Chat/TwitchIrcChatServiceTests.cs` — ~18 lifecycle tests.
- `tests/Bootstrap/ModSettingsTests.cs` — ~14 JSON-parse tests.

**Modified files:**
- `src/Ti/Voting/VoteCoordinator.cs` — add `public IMainThreadDispatcher Dispatcher => _dispatcher;`.
- `src/Ti/Chat/Internal/OutgoingMessageQueue.cs` — add `minInterval` param for 1-msg/sec spacing.
- `tests/Chat/Internal/OutgoingMessageQueueTests.cs` — add spacing test (file already exists).
- `src/ModEntry.cs` — add sections 6–9: settings load, build chat, wire `Voter.Default`, connect.
- `tests/slay_the_streamer_2.tests.csproj` — add `Game/Bootstrap/**/*.cs` to source includes.

**One-file responsibilities (each file does one job):**
- `IIrcTransport`: socket abstraction (read line / write line / dispose).
- `SslIrcTransport`: real TLS over TCP.
- `TwitchIrcChatService`: state machine + protocol matrix + dispatcher gateway. Delegates parsing to `TwitchIrcParser` and queueing to `OutgoingMessageQueue`.
- `VoteTallyLabel`: in-game rendering of one vote session.
- `ModSettings`: JSON parse + validation. Pure function on a path string.
- `NeowBlessingVotePatch`: filter + suspend + resume + fallback for one decision type.

---

## Phase 1: Plumbing

### Task 1: Add `VoteCoordinator.Dispatcher` get-only property

**Files:**
- Modify: `src/Ti/Voting/VoteCoordinator.cs:21`
- Test: `tests/Voting/VoteCoordinatorTests.cs` (add one assertion)

- [ ] **Step 1.1: Find the property declaration block in VoteCoordinator.cs**

The existing block is around line 21:
```csharp
public IChatService Chat => _chat;
public VoteSession? CurrentSession { get; private set; }
```

- [ ] **Step 1.2: Add the new property right after `Chat`**

```csharp
public IChatService Chat => _chat;
public IMainThreadDispatcher Dispatcher => _dispatcher;
public VoteSession? CurrentSession { get; private set; }
```

- [ ] **Step 1.3: Add a test verifying the property returns the constructor-injected dispatcher**

In `tests/Voting/VoteCoordinatorTests.cs`, add:
```csharp
[Fact]
public void Dispatcher_ReturnsConstructorInjected() {
    var chat = new FakeChatService();
    var clock = new FakeClock(DateTimeOffset.UtcNow);
    var scheduler = new FakeTimerScheduler(clock);
    var dispatcher = new ImmediateDispatcher();
    var coord = new VoteCoordinator(chat, clock, scheduler, dispatcher);
    Assert.Same(dispatcher, coord.Dispatcher);
}
```

- [ ] **Step 1.4: Run tests**

```bash
dotnet test tests/slay_the_streamer_2.tests.csproj --nologo
```
Expected: all 143 tests pass (142 from Plan A + 1 new).

- [ ] **Step 1.5: Commit**

```powershell
git add src/Ti/Voting/VoteCoordinator.cs tests/Voting/VoteCoordinatorTests.cs
git commit -m "plan-b-1/1.1: VoteCoordinator.Dispatcher get-only property"
```

---

### Task 2: Wire test project to compile `Game/Bootstrap`

**Files:**
- Modify: `tests/slay_the_streamer_2.tests.csproj:11-15`

- [ ] **Step 2.1: Add Game/Bootstrap to the source-reference list**

Edit the `<ItemGroup>` containing the `<Compile Include>` entries to add a new include:

```xml
<ItemGroup>
  <Compile Include="..\src\Ti\Internal\**\*.cs" />
  <Compile Include="..\src\Ti\Chat\**\*.cs" />
  <Compile Include="..\src\Ti\Voting\**\*.cs" />
  <Compile Include="..\src\Game\Bootstrap\**\*.cs" />
</ItemGroup>
```

- [ ] **Step 2.2: Verify build still succeeds (no Game/Bootstrap files exist yet, so the glob matches zero files — should be fine)**

```bash
dotnet build tests/slay_the_streamer_2.tests.csproj --nologo
```
Expected: build succeeds with 0 warnings.

- [ ] **Step 2.3: Commit**

```powershell
git add tests/slay_the_streamer_2.tests.csproj
git commit -m "plan-b-1/1.2: tests csproj — include Game/Bootstrap source"
```

---

## Phase 2: ModSettings (TDD)

### Task 3: Define `SettingsResult` discriminated union + ChatSettings record

**Files:**
- Create: `src/Game/Bootstrap/ModSettings.cs`
- Test: `tests/Bootstrap/ModSettingsTests.cs` (new file + folder)

- [ ] **Step 3.1: Create the test file with one trivial type-existence test**

Create `tests/Bootstrap/ModSettingsTests.cs`:
```csharp
using SlayTheStreamer2.Game.Bootstrap;
using SlayTheStreamer2.Ti.Chat;
using Xunit;

namespace SlayTheStreamer2.Tests.Bootstrap;

public class ModSettingsTests {
    [Fact]
    public void SettingsResult_TypesExist() {
        SettingsResult missing = new SettingsResult.Missing("x");
        SettingsResult malformed = new SettingsResult.Malformed("x", "y");
        SettingsResult success = new SettingsResult.Success(
            new ChatSettings("foo", new ChatCredentials("bar", "abc123")),
            new[] { "warn" });
        Assert.NotNull(missing);
        Assert.NotNull(malformed);
        Assert.NotNull(success);
    }
}
```

- [ ] **Step 3.2: Run test — should fail with "type not found"**

```bash
dotnet test tests/slay_the_streamer_2.tests.csproj --filter "FullyQualifiedName~ModSettingsTests" --nologo
```
Expected: compile error mentioning `SettingsResult` and `ChatSettings`.

- [ ] **Step 3.3: Create `src/Game/Bootstrap/ModSettings.cs` with the type definitions**

```csharp
using System.Collections.Generic;
using SlayTheStreamer2.Ti.Chat;

namespace SlayTheStreamer2.Game.Bootstrap;

public sealed record ChatSettings(string Channel, ChatCredentials Credentials);

public abstract record SettingsResult {
    public sealed record Success(ChatSettings Settings, IReadOnlyList<string> Warnings) : SettingsResult;
    public sealed record Missing(string Path) : SettingsResult;
    public sealed record Malformed(string Path, string Reason) : SettingsResult;

    private SettingsResult() { }   // restrict subclassing to nested records
}

public static class ModSettings {
    public const int CurrentSchemaVersion = 1;

    public static SettingsResult Load(string path) {
        // Implementation in subsequent tasks.
        return new SettingsResult.Missing(path);
    }
}
```

- [ ] **Step 3.4: Run test — should pass**

```bash
dotnet test tests/slay_the_streamer_2.tests.csproj --filter "FullyQualifiedName~ModSettingsTests" --nologo
```
Expected: 1 test passes.

- [ ] **Step 3.5: Commit**

```powershell
git add src/Game/Bootstrap/ModSettings.cs tests/Bootstrap/ModSettingsTests.cs
git commit -m "plan-b-1/2.1: ModSettings — SettingsResult union + ChatSettings record"
```

---

### Task 4: `Load` returns `Missing` when file doesn't exist

**Files:**
- Modify: `src/Game/Bootstrap/ModSettings.cs`
- Test: `tests/Bootstrap/ModSettingsTests.cs`

- [ ] **Step 4.1: Add the failing test**

```csharp
[Fact]
public void Load_NonexistentPath_ReturnsMissing() {
    var nonexistent = Path.Combine(Path.GetTempPath(), "nonexistent_" + Guid.NewGuid() + ".json");
    var result = ModSettings.Load(nonexistent);
    var missing = Assert.IsType<SettingsResult.Missing>(result);
    Assert.Equal(nonexistent, missing.Path);
}
```
Add `using System.IO;` and `using System;` at the top of the file if not present.

- [ ] **Step 4.2: Run — should pass already because of stub returning Missing**

```bash
dotnet test --filter "FullyQualifiedName~Load_NonexistentPath" --nologo
```
Expected: passes (the stub from Task 3 already does this).

- [ ] **Step 4.3: Replace the stub with a real check using `File.Exists`**

```csharp
public static SettingsResult Load(string path) {
    if (!File.Exists(path)) return new SettingsResult.Missing(path);

    // Subsequent tasks add JSON parsing here.
    return new SettingsResult.Malformed(path, "not implemented yet");
}
```
Add `using System.IO;` to the file.

- [ ] **Step 4.4: Run — should still pass**

Expected: passes.

- [ ] **Step 4.5: Commit**

```powershell
git add src/Game/Bootstrap/ModSettings.cs tests/Bootstrap/ModSettingsTests.cs
git commit -m "plan-b-1/2.2: ModSettings.Load — Missing for nonexistent paths"
```

---

### Task 5: Parse a valid JSON to `Success`

**Files:**
- Modify: `src/Game/Bootstrap/ModSettings.cs`
- Test: `tests/Bootstrap/ModSettingsTests.cs`

- [ ] **Step 5.1: Add the test (use a temp file)**

```csharp
[Fact]
public void Load_ValidJson_ReturnsSuccess() {
    var path = WriteTempJson("""
    {
        "schemaVersion": 1,
        "channel": "surfinite",
        "username": "surfinitebot",
        "oauthToken": "abc123def456ghi789jkl012mno345"
    }
    """);
    try {
        var result = ModSettings.Load(path);
        var success = Assert.IsType<SettingsResult.Success>(result);
        Assert.Equal("surfinite", success.Settings.Channel);
        Assert.Equal("surfinitebot", success.Settings.Credentials.Username);
        Assert.Equal("abc123def456ghi789jkl012mno345", success.Settings.Credentials.OauthToken);
    } finally {
        File.Delete(path);
    }
}

private static string WriteTempJson(string contents) {
    var path = Path.Combine(Path.GetTempPath(), "modsettings_test_" + Guid.NewGuid() + ".json");
    File.WriteAllText(path, contents);
    return path;
}
```

- [ ] **Step 5.2: Run — should fail (returns Malformed)**

Expected: assertion failure on `Assert.IsType<SettingsResult.Success>`.

- [ ] **Step 5.3: Implement minimal JSON parsing**

Replace the body of `Load`:
```csharp
public static SettingsResult Load(string path) {
    if (!File.Exists(path)) return new SettingsResult.Missing(path);

    string raw;
    try { raw = File.ReadAllText(path); }
    catch (Exception ex) { return new SettingsResult.Malformed(path, $"failed to read file: {ex.Message}"); }

    if (string.IsNullOrWhiteSpace(raw)) return new SettingsResult.Malformed(path, "file is empty");

    JsonDocument doc;
    try { doc = JsonDocument.Parse(raw); }
    catch (JsonException ex) { return new SettingsResult.Malformed(path, $"JSON parse error: {ex.Message}"); }

    using (doc) {
        var root = doc.RootElement;
        var warnings = new List<string>();

        var channel = ReadStringOrNull(root, "channel");
        var username = ReadStringOrNull(root, "username");
        var oauthToken = ReadStringOrNull(root, "oauthToken");

        if (string.IsNullOrWhiteSpace(channel)) return new SettingsResult.Malformed(path, "channel is missing or empty");
        if (string.IsNullOrWhiteSpace(username)) return new SettingsResult.Malformed(path, "username is missing or empty");
        if (string.IsNullOrWhiteSpace(oauthToken)) return new SettingsResult.Malformed(path, "oauthToken is missing or empty");

        var creds = new ChatCredentials(username, oauthToken);
        return new SettingsResult.Success(new ChatSettings(channel, creds), warnings);
    }
}

private static string? ReadStringOrNull(JsonElement root, string name) {
    return root.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
        ? prop.GetString()
        : null;
}
```

Add `using System.Collections.Generic;`, `using System.Text.Json;`.

- [ ] **Step 5.4: Run — test should now pass**

Expected: pass.

- [ ] **Step 5.5: Commit**

```powershell
git add src/Game/Bootstrap/ModSettings.cs tests/Bootstrap/ModSettingsTests.cs
git commit -m "plan-b-1/2.3: ModSettings.Load — happy path JSON parse"
```

---

### Task 6: Validate `schemaVersion`

**Files:**
- Modify: `src/Game/Bootstrap/ModSettings.cs`
- Test: `tests/Bootstrap/ModSettingsTests.cs`

- [ ] **Step 6.1: Add three tests covering missing, wrong, and current schemaVersion**

```csharp
[Fact]
public void Load_MissingSchemaVersion_ReturnsMalformed() {
    var path = WriteTempJson("""
    { "channel": "x", "username": "y", "oauthToken": "abc123def456ghi789jkl012mno345" }
    """);
    try {
        var result = ModSettings.Load(path);
        var malformed = Assert.IsType<SettingsResult.Malformed>(result);
        Assert.Contains("schemaVersion", malformed.Reason);
    } finally { File.Delete(path); }
}

[Fact]
public void Load_UnknownSchemaVersion_ReturnsMalformed() {
    var path = WriteTempJson("""
    { "schemaVersion": 999, "channel": "x", "username": "y", "oauthToken": "abc123def456ghi789jkl012mno345" }
    """);
    try {
        var result = ModSettings.Load(path);
        var malformed = Assert.IsType<SettingsResult.Malformed>(result);
        Assert.Contains("999", malformed.Reason);
    } finally { File.Delete(path); }
}

[Fact]
public void Load_CurrentSchemaVersion_ReturnsSuccess() {
    var path = WriteTempJson($$"""
    { "schemaVersion": {{ModSettings.CurrentSchemaVersion}}, "channel": "x", "username": "y", "oauthToken": "abc123def456ghi789jkl012mno345" }
    """);
    try {
        Assert.IsType<SettingsResult.Success>(ModSettings.Load(path));
    } finally { File.Delete(path); }
}
```

- [ ] **Step 6.2: Run — first two fail, third passes**

Expected: first two fail (no version check yet); third passes by accident (no check rejects it).

- [ ] **Step 6.3: Add schemaVersion check before reading other fields**

Insert after `var root = doc.RootElement;`:
```csharp
if (!root.TryGetProperty("schemaVersion", out var versionProp) || versionProp.ValueKind != JsonValueKind.Number) {
    return new SettingsResult.Malformed(path, "schemaVersion is missing or not a number");
}
var version = versionProp.GetInt32();
if (version != CurrentSchemaVersion) {
    return new SettingsResult.Malformed(path,
        $"unknown schemaVersion {version}; this mod build supports schemaVersion {CurrentSchemaVersion}");
}
```

- [ ] **Step 6.4: Run — all three pass**

- [ ] **Step 6.5: Commit**

```powershell
git add src/Game/Bootstrap/ModSettings.cs tests/Bootstrap/ModSettingsTests.cs
git commit -m "plan-b-1/2.4: ModSettings — schemaVersion validation"
```

---

### Task 7: Channel normalisation + warnings

**Files:**
- Modify: `src/Game/Bootstrap/ModSettings.cs`
- Test: `tests/Bootstrap/ModSettingsTests.cs`

- [ ] **Step 7.1: Add tests for channel normalisation forms**

```csharp
[Theory]
[InlineData("foo")]
[InlineData("#foo")]
[InlineData("https://twitch.tv/foo")]
[InlineData("https://www.twitch.tv/foo")]
[InlineData("http://twitch.tv/foo/")]
public void Load_ChannelForms_NormaliseToBareLowercase(string channelInput) {
    var path = WriteTempJson($$"""
    { "schemaVersion": 1, "channel": "{{channelInput}}", "username": "y", "oauthToken": "abc123def456ghi789jkl012mno345" }
    """);
    try {
        var result = ModSettings.Load(path);
        var success = Assert.IsType<SettingsResult.Success>(result);
        Assert.Equal("foo", success.Settings.Channel);
    } finally { File.Delete(path); }
}

[Fact]
public void Load_ChannelWithUrlForm_AddsWarning() {
    var path = WriteTempJson("""
    { "schemaVersion": 1, "channel": "https://twitch.tv/Surfinite", "username": "y", "oauthToken": "abc123def456ghi789jkl012mno345" }
    """);
    try {
        var result = ModSettings.Load(path);
        var success = Assert.IsType<SettingsResult.Success>(result);
        Assert.Contains(success.Warnings, w => w.Contains("normalised") || w.Contains("normalized"));
    } finally { File.Delete(path); }
}
```

- [ ] **Step 7.2: Run — should fail (no normalisation logic yet)**

- [ ] **Step 7.3: Add a NormaliseChannel helper and use it in Load**

Add to the class:
```csharp
private static (string Normalised, string? Warning) NormaliseChannel(string raw) {
    var trimmed = raw.Trim();
    var lower = trimmed.ToLowerInvariant();

    string normalised;
    if (lower.StartsWith("https://www.twitch.tv/", StringComparison.Ordinal)) {
        normalised = lower.Substring("https://www.twitch.tv/".Length);
    } else if (lower.StartsWith("http://www.twitch.tv/", StringComparison.Ordinal)) {
        normalised = lower.Substring("http://www.twitch.tv/".Length);
    } else if (lower.StartsWith("https://twitch.tv/", StringComparison.Ordinal)) {
        normalised = lower.Substring("https://twitch.tv/".Length);
    } else if (lower.StartsWith("http://twitch.tv/", StringComparison.Ordinal)) {
        normalised = lower.Substring("http://twitch.tv/".Length);
    } else if (lower.StartsWith("#", StringComparison.Ordinal)) {
        normalised = lower.Substring(1);
    } else {
        normalised = lower;
    }
    // Strip trailing slash and any trailing path segment.
    var slashIdx = normalised.IndexOf('/');
    if (slashIdx >= 0) normalised = normalised.Substring(0, slashIdx);

    string? warning = (normalised != trimmed)
        ? $"channel '{trimmed}' normalised to '{normalised}'"
        : null;
    return (normalised, warning);
}
```
Use in `Load`:
```csharp
var (normalisedChannel, channelWarning) = NormaliseChannel(channel);
if (channelWarning is not null) warnings.Add(channelWarning);
// ... use normalisedChannel instead of channel when constructing ChatSettings
```

- [ ] **Step 7.4: Run all ModSettings tests**

Expected: all pass.

- [ ] **Step 7.5: Commit**

```powershell
git add src/Game/Bootstrap/ModSettings.cs tests/Bootstrap/ModSettingsTests.cs
git commit -m "plan-b-1/2.5: ModSettings — channel normalisation + warning"
```

---

### Task 8: Username lowercasing + warning

**Files:**
- Modify: `src/Game/Bootstrap/ModSettings.cs`
- Test: `tests/Bootstrap/ModSettingsTests.cs`

- [ ] **Step 8.1: Add the test**

```csharp
[Fact]
public void Load_UsernameWithUppercase_LowercasesAndWarns() {
    var path = WriteTempJson("""
    { "schemaVersion": 1, "channel": "x", "username": "SurfiniteBot", "oauthToken": "abc123def456ghi789jkl012mno345" }
    """);
    try {
        var result = ModSettings.Load(path);
        var success = Assert.IsType<SettingsResult.Success>(result);
        Assert.Equal("surfinitebot", success.Settings.Credentials.Username);
        Assert.Contains(success.Warnings, w => w.Contains("SurfiniteBot") || w.Contains("lowercased"));
    } finally { File.Delete(path); }
}
```

- [ ] **Step 8.2: Run — should fail (warning not surfaced; ChatCredentials does lowercase but ModSettings doesn't tell)**

- [ ] **Step 8.3: Add detection in Load**

Before constructing `ChatCredentials`:
```csharp
if (!string.Equals(username, username.ToLowerInvariant(), StringComparison.Ordinal)) {
    warnings.Add($"username '{username}' lowercased to '{username.ToLowerInvariant()}'");
}
```
(`ChatCredentials` already lowercases internally, so we just emit the warning.)

- [ ] **Step 8.4: Run — passes**

- [ ] **Step 8.5: Commit**

```powershell
git add src/Game/Bootstrap/ModSettings.cs tests/Bootstrap/ModSettingsTests.cs
git commit -m "plan-b-1/2.6: ModSettings — username lowercasing warning"
```

---

### Task 9: Oauth normalisation + soft regex warning

**Files:**
- Modify: `src/Game/Bootstrap/ModSettings.cs`
- Test: `tests/Bootstrap/ModSettingsTests.cs`

- [ ] **Step 9.1: Add tests for oauth handling**

```csharp
[Theory]
[InlineData("oauth:abc123def456ghi789jkl012mno345")]
[InlineData("abc123def456ghi789jkl012mno345")]
public void Load_OauthBothForms_NormaliseToBare(string token) {
    var path = WriteTempJson($$"""
    { "schemaVersion": 1, "channel": "x", "username": "y", "oauthToken": "{{token}}" }
    """);
    try {
        var result = ModSettings.Load(path);
        var success = Assert.IsType<SettingsResult.Success>(result);
        Assert.Equal("abc123def456ghi789jkl012mno345", success.Settings.Credentials.OauthToken);
    } finally { File.Delete(path); }
}

[Fact]
public void Load_OauthWithUnusualShape_ReturnsSuccessWithWarning() {
    var path = WriteTempJson("""
    { "schemaVersion": 1, "channel": "x", "username": "y", "oauthToken": "ABC123DEF456GHI789JKL012MNO345" }
    """);
    try {
        var result = ModSettings.Load(path);
        var success = Assert.IsType<SettingsResult.Success>(result);
        Assert.Contains(success.Warnings, w => w.Contains("oauth") || w.Contains("token"));
    } finally { File.Delete(path); }
}

[Fact]
public void Load_OauthWithWhitespace_ReturnsMalformed() {
    var path = WriteTempJson("""
    { "schemaVersion": 1, "channel": "x", "username": "y", "oauthToken": "abc 123" }
    """);
    try {
        Assert.IsType<SettingsResult.Malformed>(ModSettings.Load(path));
    } finally { File.Delete(path); }
}
```

- [ ] **Step 9.2: Run — first two pass via existing logic; third may fail (whitespace not yet checked)**

- [ ] **Step 9.3: Add validation logic before constructing ChatCredentials**

```csharp
// Strip optional oauth: prefix for shape inspection.
var bareForCheck = oauthToken.StartsWith("oauth:", StringComparison.OrdinalIgnoreCase)
    ? oauthToken.Substring(6) : oauthToken;
if (bareForCheck.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))) {
    return new SettingsResult.Malformed(path, "oauthToken contains whitespace or control characters");
}
if (!System.Text.RegularExpressions.Regex.IsMatch(bareForCheck, "^[a-z0-9]{30}$")) {
    warnings.Add(
        "oauth token doesn't match the common Twitch user-access-token shape " +
        "(30 lowercase alphanumeric chars); will let Twitch authentication be the source of truth");
}
```
Add `using System.Linq;`.

- [ ] **Step 9.4: Run all ModSettings tests**

Expected: all pass.

- [ ] **Step 9.5: Commit**

```powershell
git add src/Game/Bootstrap/ModSettings.cs tests/Bootstrap/ModSettingsTests.cs
git commit -m "plan-b-1/2.7: ModSettings — oauth normalisation + soft regex warning"
```

---

### Task 10: Malformed JSON + empty file edge cases

**Files:**
- Modify: `tests/Bootstrap/ModSettingsTests.cs` (test additions only — Load already handles these)

- [ ] **Step 10.1: Add edge-case tests**

```csharp
[Fact]
public void Load_EmptyFile_ReturnsMalformed() {
    var path = WriteTempJson("");
    try { Assert.IsType<SettingsResult.Malformed>(ModSettings.Load(path)); }
    finally { File.Delete(path); }
}

[Fact]
public void Load_MalformedJson_ReturnsMalformed() {
    var path = WriteTempJson("{ this is not json");
    try { Assert.IsType<SettingsResult.Malformed>(ModSettings.Load(path)); }
    finally { File.Delete(path); }
}

[Fact]
public void Load_WhitespaceOnlyChannel_ReturnsMalformed() {
    var path = WriteTempJson("""
    { "schemaVersion": 1, "channel": "   ", "username": "y", "oauthToken": "abc123def456ghi789jkl012mno345" }
    """);
    try { Assert.IsType<SettingsResult.Malformed>(ModSettings.Load(path)); }
    finally { File.Delete(path); }
}
```

- [ ] **Step 10.2: Run — should pass via existing logic**

Expected: all pass.

- [ ] **Step 10.3: Commit**

```powershell
git add tests/Bootstrap/ModSettingsTests.cs
git commit -m "plan-b-1/2.8: ModSettings — edge-case tests (empty/malformed/whitespace)"
```

---

## Phase 3: OutgoingMessageQueue 1-msg/sec spacing

### Task 11: Add `minInterval` to `OutgoingMessageQueue`

**Files:**
- Modify: `src/Ti/Chat/Internal/OutgoingMessageQueue.cs`
- Test: `tests/Chat/Internal/OutgoingMessageQueueTests.cs`

- [ ] **Step 11.1: Read the existing OutgoingMessageQueue to understand its drain logic**

```bash
# Use Read tool on src/Ti/Chat/Internal/OutgoingMessageQueue.cs to understand the Drain method.
```
Note the `_clock` field, the existing `_windowStart` / `_tokens` mechanism, and how `Drain` decides what to send next.

- [ ] **Step 11.2: Add a failing test**

In `tests/Chat/Internal/OutgoingMessageQueueTests.cs`:
```csharp
[Fact]
public async Task Enqueue_WithMinInterval_SpacesSendsAtLeastMinIntervalApart() {
    var clock = new FakeClock(DateTimeOffset.UtcNow);
    var scheduler = new FakeTimerScheduler(clock);
    var sent = new List<(DateTimeOffset At, string Msg)>();
    var minInterval = TimeSpan.FromSeconds(1);

    var queue = new OutgoingMessageQueue(
        capacity: 20, window: TimeSpan.FromSeconds(30),
        minInterval: minInterval,
        clock: clock, scheduler: scheduler,
        send: msg => { sent.Add((clock.UtcNow, msg)); return Task.CompletedTask; });

    queue.Enqueue("first", OutgoingMessagePriority.High);
    queue.Enqueue("second", OutgoingMessagePriority.High);

    // First send fires at t=0 (or scheduler-zero-tick); second must wait minInterval.
    scheduler.Advance(TimeSpan.Zero);
    await Task.Yield();
    scheduler.Advance(minInterval);
    await Task.Yield();
    scheduler.Advance(minInterval);
    await Task.Yield();

    Assert.Equal(2, sent.Count);
    var gap = sent[1].At - sent[0].At;
    Assert.True(gap >= minInterval,
        $"expected gap >= {minInterval}, got {gap}");
}
```

- [ ] **Step 11.3: Run — should fail because minInterval parameter doesn't exist**

```bash
dotnet test --filter "FullyQualifiedName~Enqueue_WithMinInterval" --nologo
```
Expected: compile error: no overload takes minInterval.

- [ ] **Step 11.4: Add `minInterval` parameter to OutgoingMessageQueue constructor**

In `src/Ti/Chat/Internal/OutgoingMessageQueue.cs`, change the constructor signature to:
```csharp
private readonly TimeSpan _minInterval;
private DateTimeOffset? _lastSentAt;

public OutgoingMessageQueue(
    int capacity, TimeSpan window,
    IClock clock, ITimerScheduler scheduler,
    Func<string, Task> send,
    TimeSpan? minInterval = null) {
    if (capacity < 0) throw new ArgumentOutOfRangeException(nameof(capacity));
    if (window <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(window));
    _capacity = capacity; _window = window;
    _clock = clock; _scheduler = scheduler; _send = send;
    _minInterval = minInterval ?? TimeSpan.Zero;
    _windowStart = clock.UtcNow;
    _tokens = capacity;
    _periodicTimer = scheduler.SchedulePeriodic(window, RefillAndDrain);
}
```

Add an alternate constructor matching the test's parameter order:
```csharp
public OutgoingMessageQueue(
    int capacity, TimeSpan window, TimeSpan minInterval,
    IClock clock, ITimerScheduler scheduler,
    Func<string, Task> send)
    : this(capacity, window, clock, scheduler, send, minInterval) { }
```

In the `Drain` method, before invoking `_send`, check:
```csharp
if (_minInterval > TimeSpan.Zero && _lastSentAt is DateTimeOffset last) {
    var earliestNext = last + _minInterval;
    if (_clock.UtcNow < earliestNext) {
        // Reschedule a deferred drain at the earliest allowed time.
        var delay = earliestNext - _clock.UtcNow;
        if (!_drainPending) {
            _drainPending = true;
            _scheduler.Schedule(delay, RunDeferredDrain);
        }
        return;
    }
}
```

After the `await _send(...)` call (or `_send(...)` call), set:
```csharp
_lastSentAt = _clock.UtcNow;
```

- [ ] **Step 11.5: Run — should pass**

```bash
dotnet test --filter "FullyQualifiedName~OutgoingMessageQueueTests" --nologo
```
Expected: all queue tests pass (existing + new).

- [ ] **Step 11.6: Commit**

```powershell
git add src/Ti/Chat/Internal/OutgoingMessageQueue.cs tests/Chat/Internal/OutgoingMessageQueueTests.cs
git commit -m "plan-b-1/3.1: OutgoingMessageQueue — minInterval param for 1-msg/sec spacing"
```

---

## Phase 4: TwitchIrcChatService — start with the test seam

### Task 12: `IIrcTransport` abstraction + `FakeIrcTransport` for tests

**Files:**
- Create: `src/Ti/Chat/Internal/IIrcTransport.cs`
- Create: `tests/Chat/Internal/FakeIrcTransport.cs`

- [ ] **Step 12.1: Create the interface**

`src/Ti/Chat/Internal/IIrcTransport.cs`:
```csharp
using System;
using System.Threading;
using System.Threading.Tasks;

namespace SlayTheStreamer2.Ti.Chat.Internal;

/// <summary>
/// IRC connection abstraction so TwitchIrcChatService can be unit-tested
/// without a real socket. Production impl is SslIrcTransport.
/// </summary>
internal interface IIrcTransport : IDisposable {
    Task ConnectAsync(string host, int port, CancellationToken ct);
    Task<string?> ReadLineAsync(CancellationToken ct);   // null = remote closed
    Task WriteLineAsync(string line, CancellationToken ct);
}
```

- [ ] **Step 12.2: Create the fake transport**

`tests/Chat/Internal/FakeIrcTransport.cs`:
```csharp
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SlayTheStreamer2.Ti.Chat.Internal;

namespace SlayTheStreamer2.Tests.Chat.Internal;

/// <summary>Records writes; releases reads when InjectIncoming is called.</summary>
internal sealed class FakeIrcTransport : IIrcTransport {
    private readonly BlockingCollection<string> _incoming = new();
    public List<string> Writes { get; } = new();
    public bool ConnectCalled { get; private set; }
    public bool Disposed { get; private set; }
    public string? ConnectHost { get; private set; }
    public int ConnectPort { get; private set; }

    public Task ConnectAsync(string host, int port, CancellationToken ct) {
        ConnectCalled = true;
        ConnectHost = host;
        ConnectPort = port;
        return Task.CompletedTask;
    }

    public Task<string?> ReadLineAsync(CancellationToken ct) {
        return Task.Run<string?>(() => {
            try { return _incoming.Take(ct); }
            catch (OperationCanceledException) { return null; }
            catch (InvalidOperationException) { return null; }   // CompleteAdding called
        }, ct);
    }

    public Task WriteLineAsync(string line, CancellationToken ct) {
        lock (Writes) Writes.Add(line);
        return Task.CompletedTask;
    }

    /// <summary>Test API: deliver a line as if read from the remote.</summary>
    public void InjectIncoming(string line) => _incoming.Add(line);

    /// <summary>Test API: simulate remote closing the connection.</summary>
    public void Close() => _incoming.CompleteAdding();

    public void Dispose() {
        Disposed = true;
        _incoming.CompleteAdding();
    }
}
```

- [ ] **Step 12.3: Verify build (no test runs needed yet)**

```bash
dotnet build tests/slay_the_streamer_2.tests.csproj --nologo
```
Expected: build succeeds.

- [ ] **Step 12.4: Commit**

```powershell
git add src/Ti/Chat/Internal/IIrcTransport.cs tests/Chat/Internal/FakeIrcTransport.cs
git commit -m "plan-b-1/4.1: IIrcTransport seam + FakeIrcTransport"
```

---

### Task 13: `TwitchIrcChatService` skeleton (constructor, fields, no-op state)

**Files:**
- Create: `src/Ti/Chat/TwitchIrcChatService.cs`
- Create: `tests/Chat/TwitchIrcChatServiceTests.cs`

- [ ] **Step 13.1: Create the test file with one constructor smoke test**

`tests/Chat/TwitchIrcChatServiceTests.cs`:
```csharp
using System;
using System.Threading.Tasks;
using SlayTheStreamer2.Tests.Chat.Internal;
using SlayTheStreamer2.Ti.Chat;
using SlayTheStreamer2.Ti.Chat.Internal;
using SlayTheStreamer2.Ti.Internal;
using Xunit;

namespace SlayTheStreamer2.Tests.Chat;

public class TwitchIrcChatServiceTests {
    private static (TwitchIrcChatService svc, FakeIrcTransport transport, FakeClock clock, FakeTimerScheduler sched) Build() {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var sched = new FakeTimerScheduler(clock);
        var dispatcher = new ImmediateDispatcher();
        var transport = new FakeIrcTransport();
        var svc = new TwitchIrcChatService(
            dispatcher: dispatcher, clock: clock, scheduler: sched,
            transportFactory: () => transport,
            sendCapacity: 20, sendWindow: TimeSpan.FromSeconds(30),
            sendMinInterval: TimeSpan.FromSeconds(1));
        return (svc, transport, clock, sched);
    }

    [Fact]
    public void NewService_StartsDisconnected() {
        var (svc, _, _, _) = Build();
        Assert.Equal(ChatConnectionState.Disconnected, svc.State);
        Assert.False(svc.IsConnected);
        Assert.False(svc.CanSend);
        svc.Dispose();
    }
}
```

- [ ] **Step 13.2: Run — fails (TwitchIrcChatService doesn't exist)**

```bash
dotnet test --filter "FullyQualifiedName~TwitchIrcChatServiceTests" --nologo
```
Expected: compile error.

- [ ] **Step 13.3: Create skeleton TwitchIrcChatService**

`src/Ti/Chat/TwitchIrcChatService.cs`:
```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using SlayTheStreamer2.Ti.Chat.Internal;
using SlayTheStreamer2.Ti.Internal;

namespace SlayTheStreamer2.Ti.Chat;

public sealed class TwitchIrcChatService : IChatService {
    private readonly IMainThreadDispatcher _dispatcher;
    private readonly IClock _clock;
    private readonly ITimerScheduler _scheduler;
    private readonly Func<IIrcTransport> _transportFactory;
    private readonly int _sendCapacity;
    private readonly TimeSpan _sendWindow;
    private readonly TimeSpan _sendMinInterval;
    private ChatConnectionState _state = ChatConnectionState.Disconnected;
    private bool _disposed;

    public ChatConnectionState State => _state;
    public bool IsConnected => _state is
        ChatConnectionState.ConnectedReadOnly or
        ChatConnectionState.ConnectedReadWrite or
        ChatConnectionState.Reconnecting;
    public bool CanSend => _state is ChatConnectionState.ConnectedReadWrite;
    public DateTimeOffset? LastMessageReceivedAt { get; private set; }
    public Exception? LastError { get; private set; }

    public event EventHandler<ChatMessage>? MessageReceived;
    public event EventHandler<ChatConnectionChangedEventArgs>? ConnectionStateChanged;

    /// <summary>Production constructor.</summary>
    public TwitchIrcChatService(
        IMainThreadDispatcher dispatcher, IClock clock, ITimerScheduler scheduler,
        int sendCapacity, TimeSpan sendWindow)
        : this(dispatcher, clock, scheduler,
               transportFactory: () => new SslIrcTransport(),
               sendCapacity, sendWindow, sendMinInterval: TimeSpan.FromSeconds(1)) {
    }

    /// <summary>Internal constructor for tests — accepts a transport factory + custom timing.</summary>
    internal TwitchIrcChatService(
        IMainThreadDispatcher dispatcher, IClock clock, ITimerScheduler scheduler,
        Func<IIrcTransport> transportFactory,
        int sendCapacity, TimeSpan sendWindow, TimeSpan sendMinInterval) {
        _dispatcher = dispatcher;
        _clock = clock;
        _scheduler = scheduler;
        _transportFactory = transportFactory;
        _sendCapacity = sendCapacity;
        _sendWindow = sendWindow;
        _sendMinInterval = sendMinInterval;
    }

    public Task ConnectAsync(string channel, ChatCredentials? creds = null, CancellationToken ct = default) {
        // Stub — real impl in subsequent tasks.
        return Task.CompletedTask;
    }

    public void Disconnect() {
        // Stub.
    }

    public Task SendMessageAsync(string text, OutgoingMessagePriority priority = OutgoingMessagePriority.Normal, CancellationToken ct = default) {
        // Stub.
        return Task.CompletedTask;
    }

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
    }
}

// Placeholder so the production constructor compiles. Real impl in Task 33.
internal sealed class SslIrcTransport : IIrcTransport {
    public Task ConnectAsync(string host, int port, CancellationToken ct) =>
        throw new NotImplementedException("SslIrcTransport implemented in Task 33");
    public Task<string?> ReadLineAsync(CancellationToken ct) =>
        throw new NotImplementedException();
    public Task WriteLineAsync(string line, CancellationToken ct) =>
        throw new NotImplementedException();
    public void Dispose() { }
}
```

InternalsVisibleTo: the test project source-includes the file via the existing csproj `Compile Include="..\src\Ti\Chat\**\*.cs"` glob, so `internal` ctors are accessible. No `InternalsVisibleTo` needed.

- [ ] **Step 13.4: Run — passes**

Expected: 1 test passes (NewService_StartsDisconnected).

- [ ] **Step 13.5: Commit**

```powershell
git add src/Ti/Chat/TwitchIrcChatService.cs tests/Chat/TwitchIrcChatServiceTests.cs
git commit -m "plan-b-1/4.2: TwitchIrcChatService skeleton + first test"
```

---

### Task 14: `ConnectAsync` — happy-path PASS/NICK/JOIN flow

**Files:**
- Modify: `src/Ti/Chat/TwitchIrcChatService.cs`
- Modify: `tests/Chat/TwitchIrcChatServiceTests.cs`

- [ ] **Step 14.1: Add the failing test**

```csharp
[Fact]
public async Task ConnectAsync_HappyPath_TransitionsToConnectedReadWrite() {
    var (svc, transport, clock, sched) = Build();
    var stateChanges = new List<(ChatConnectionState Old, ChatConnectionState New)>();
    svc.ConnectionStateChanged += (_, e) => stateChanges.Add((e.OldState, e.NewState));

    var creds = new ChatCredentials("surfinitebot", "abc123def456ghi789jkl012mno345");
    var connectTask = svc.ConnectAsync("surfinite", creds);

    // Drive the read loop: deliver successful auth + JOIN confirmation.
    transport.InjectIncoming(":tmi.twitch.tv CAP * ACK :twitch.tv/tags twitch.tv/commands");
    transport.InjectIncoming(":tmi.twitch.tv 001 surfinitebot :Welcome, GLHF!");
    transport.InjectIncoming(":tmi.twitch.tv ROOMSTATE #surfinite");

    // Allow read loop iterations.
    for (int i = 0; i < 10 && svc.State != ChatConnectionState.ConnectedReadWrite; i++) {
        await Task.Delay(20);
    }

    Assert.Equal(ChatConnectionState.ConnectedReadWrite, svc.State);
    Assert.Contains(transport.Writes, w => w == "CAP REQ :twitch.tv/tags twitch.tv/commands");
    Assert.Contains(transport.Writes, w => w == "PASS oauth:abc123def456ghi789jkl012mno345");
    Assert.Contains(transport.Writes, w => w == "NICK surfinitebot");
    Assert.Contains(transport.Writes, w => w == "JOIN #surfinite");
    Assert.Contains(stateChanges, c => c.New == ChatConnectionState.ConnectedReadWrite);
    svc.Dispose();
}
```

- [ ] **Step 14.2: Run — fails (ConnectAsync is a stub)**

- [ ] **Step 14.3: Implement ConnectAsync + read loop**

This is the largest single chunk of code in B.1. Add the following to `TwitchIrcChatService.cs`:

```csharp
private const string TwitchIrcHost = "irc.chat.twitch.tv";
private const int TwitchIrcPort = 6697;

private IIrcTransport? _transport;
private CancellationTokenSource? _cts;
private Task? _readLoopTask;
private string? _selfLogin;
private string? _channel;
private ChatCredentials? _creds;

public Task ConnectAsync(string channel, ChatCredentials? creds = null, CancellationToken ct = default) {
    if (_disposed) return Task.FromException(new ObjectDisposedException(nameof(TwitchIrcChatService)));
    if (_state != ChatConnectionState.Disconnected) return Task.CompletedTask;

    _channel = NormaliseChannel(channel);
    _creds = creds;
    _selfLogin = creds?.Username ?? "justinfan" + Random.Shared.Next(1000, 9999);
    _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    _transport = _transportFactory();

    TransitionTo(ChatConnectionState.Connecting, "ConnectAsync called");
    _readLoopTask = Task.Run(() => RunConnectionAsync(_cts.Token));
    return Task.CompletedTask;
}

private async Task RunConnectionAsync(CancellationToken ct) {
    try {
        await _transport!.ConnectAsync(TwitchIrcHost, TwitchIrcPort, ct);
        await _transport.WriteLineAsync("CAP REQ :twitch.tv/tags twitch.tv/commands", ct);
        if (_creds is not null) {
            await _transport.WriteLineAsync($"PASS oauth:{_creds.OauthToken}", ct);
        }
        await _transport.WriteLineAsync($"NICK {_selfLogin}", ct);
        await _transport.WriteLineAsync($"JOIN #{_channel}", ct);

        while (!ct.IsCancellationRequested) {
            var line = await _transport.ReadLineAsync(ct);
            if (line is null) break;   // remote closed
            ProcessIncomingLine(line);
        }
    } catch (Exception ex) {
        LastError = ex;
        TiLog.Error("[TwitchIrcChatService] read loop error", ex);
        if (_state != ChatConnectionState.Disposed) TransitionTo(ChatConnectionState.Disconnected, "transport error");
    }
}

private void ProcessIncomingLine(string line) {
    var ev = TwitchIrcParser.Parse(line);
    if (ev is null) return;

    switch (ev) {
        case CapAckEvent: /* tags + commands acknowledged */ break;
        case CapNakEvent: /* TODO Task 16: fall back to no-tags mode */ break;
        case PingEvent ping:
            _ = _transport!.WriteLineAsync($"PONG :{ping.Token}", _cts!.Token);
            break;
        case PrivmsgEvent privmsg:
            HandlePrivmsg(privmsg);
            break;
        case NoticeEvent notice:
            HandleNotice(notice);
            break;
        case ReconnectEvent:
            // TODO Task 25: graceful disconnect + reconnect
            break;
        case RoomStateEvent _:
        case UserStateEvent _:
            // ROOMSTATE/USERSTATE means JOIN succeeded.
            if (_state is ChatConnectionState.Connecting) {
                TransitionTo(ChatConnectionState.ConnectedReadWrite, "JOIN confirmed");
            }
            break;
        case UnknownIrcEvent:
            // Numeric replies like 001/353/366 fall here; treat 353/366 as JOIN confirmation too.
            if (_state is ChatConnectionState.Connecting && Is366Or353(line)) {
                TransitionTo(ChatConnectionState.ConnectedReadWrite, "JOIN confirmed via numeric");
            }
            break;
    }
}

private static bool Is366Or353(string line) =>
    line.Contains(" 353 ", StringComparison.Ordinal) || line.Contains(" 366 ", StringComparison.Ordinal);

private void HandlePrivmsg(PrivmsgEvent privmsg) {
    LastMessageReceivedAt = _clock.UtcNow;
    if (IsSelfEcho(privmsg.Message)) return;
    var msg = privmsg.Message;
    _dispatcher.Post(() => MessageReceived?.Invoke(this, msg));
}

private bool IsSelfEcho(ChatMessage msg) {
    if (_selfLogin is null) return false;
    return string.Equals(msg.Login, _selfLogin, StringComparison.OrdinalIgnoreCase);
}

private void HandleNotice(NoticeEvent notice) {
    // TODO Task 17/18/26: handle terminal notices + rate-limit notices
}

private void TransitionTo(ChatConnectionState next, string? reason = null) {
    if (_state == next) return;
    var old = _state;
    _state = next;
    var args = new ChatConnectionChangedEventArgs(old, next, reason);
    _dispatcher.Post(() => ConnectionStateChanged?.Invoke(this, args));
}

private static string NormaliseChannel(string raw) {
    var lower = raw.Trim().ToLowerInvariant();
    return lower.StartsWith("#") ? lower.Substring(1) : lower;
}
```

Add `using System;`, `using System.Linq;` if needed.

- [ ] **Step 14.4: Run — should pass**

Expected: ConnectAsync_HappyPath passes, plus the previous test still passes.

- [ ] **Step 14.5: Commit**

```powershell
git add src/Ti/Chat/TwitchIrcChatService.cs tests/Chat/TwitchIrcChatServiceTests.cs
git commit -m "plan-b-1/4.3: ConnectAsync — happy-path PASS/NICK/JOIN with read loop"
```

---

### Task 15: AuthenticationFailed terminal NOTICE

**Files:**
- Modify: `src/Ti/Chat/TwitchIrcChatService.cs`
- Modify: `tests/Chat/TwitchIrcChatServiceTests.cs`

- [ ] **Step 15.1: Add the failing test**

```csharp
[Fact]
public async Task ConnectAsync_AuthFailureNotice_TransitionsToAuthenticationFailed() {
    var (svc, transport, _, _) = Build();
    var creds = new ChatCredentials("surfinitebot", "abc123def456ghi789jkl012mno345");
    var connectTask = svc.ConnectAsync("surfinite", creds);

    transport.InjectIncoming(":tmi.twitch.tv NOTICE * :Login authentication failed");

    for (int i = 0; i < 10 && svc.State != ChatConnectionState.AuthenticationFailed; i++) {
        await Task.Delay(20);
    }
    Assert.Equal(ChatConnectionState.AuthenticationFailed, svc.State);
    svc.Dispose();
}
```

- [ ] **Step 15.2: Run — fails (HandleNotice is empty)**

- [ ] **Step 15.3: Fill in HandleNotice for auth-failure cases**

Replace the `HandleNotice` body:
```csharp
private void HandleNotice(NoticeEvent notice) {
    var msgId = notice.MsgId?.ToLowerInvariant();
    var text = notice.Text;

    // Terminal: auth failure (matches by msg-id when tags enabled, by text otherwise).
    bool isAuthFailure =
        msgId is "msg_login_unsuccessful" or "msg_authentication_failed" or "improperly_formatted_auth" ||
        text.Contains("Login authentication failed", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("Error logging in", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("Improperly formatted auth", StringComparison.OrdinalIgnoreCase);
    if (isAuthFailure) {
        TransitionTo(ChatConnectionState.AuthenticationFailed, $"NOTICE: {text}");
        _cts?.Cancel();   // stop the read loop; no retry
        return;
    }

    // Terminal: channel banned/suspended.
    bool isJoinFailure =
        msgId is "msg_banned" or "msg_channel_suspended" or "tos_ban";
    if (isJoinFailure) {
        TransitionTo(ChatConnectionState.JoinFailed, $"NOTICE: {text}");
        _cts?.Cancel();
        return;
    }

    // Rate-limit / slow-mode NOTICEs handled in Task 26.
}
```

- [ ] **Step 15.4: Run — passes**

- [ ] **Step 15.5: Commit**

```powershell
git add src/Ti/Chat/TwitchIrcChatService.cs tests/Chat/TwitchIrcChatServiceTests.cs
git commit -m "plan-b-1/4.4: AuthenticationFailed + JoinFailed terminal NOTICE handling"
```

---

### Task 16: CAP NAK fallback (no-tags mode)

**Files:**
- Modify: `src/Ti/Chat/TwitchIrcChatService.cs`
- Modify: `tests/Chat/TwitchIrcChatServiceTests.cs`

- [ ] **Step 16.1: Add the test**

```csharp
[Fact]
public async Task CapNak_FallsBackToNoTagsMode_AndStillReachesConnected() {
    var (svc, transport, _, _) = Build();
    var creds = new ChatCredentials("surfinitebot", "abc123def456ghi789jkl012mno345");
    var connectTask = svc.ConnectAsync("surfinite", creds);

    transport.InjectIncoming(":tmi.twitch.tv CAP * NAK :twitch.tv/tags twitch.tv/commands");
    transport.InjectIncoming(":tmi.twitch.tv ROOMSTATE #surfinite");

    for (int i = 0; i < 10 && svc.State != ChatConnectionState.ConnectedReadWrite; i++) {
        await Task.Delay(20);
    }
    Assert.Equal(ChatConnectionState.ConnectedReadWrite, svc.State);
    Assert.False(svc.HasTags);   // expose a property indicating tag-mode
    svc.Dispose();
}
```

- [ ] **Step 16.2: Run — fails (no HasTags property; CAP NAK currently no-op)**

- [ ] **Step 16.3: Add `HasTags` (internal) and CAP NAK handling**

In TwitchIrcChatService:
```csharp
internal bool HasTags { get; private set; } = true;   // optimistic; falsified by CAP NAK
```

In `ProcessIncomingLine`'s CAP cases:
```csharp
case CapAckEvent: HasTags = true; break;
case CapNakEvent:
    HasTags = false;
    TiLog.Warn("[TwitchIrcChatService] CAP NAK — falling back to no-tags mode");
    break;
```

- [ ] **Step 16.4: Run — passes**

- [ ] **Step 16.5: Commit**

```powershell
git add src/Ti/Chat/TwitchIrcChatService.cs tests/Chat/TwitchIrcChatServiceTests.cs
git commit -m "plan-b-1/4.5: CAP NAK fallback — no-tags mode flag"
```

---

### Task 17: PRIVMSG receive + dispatcher.Post + MessageReceived

**Files:**
- Modify: `tests/Chat/TwitchIrcChatServiceTests.cs` (impl already done in Task 14; add test for completeness)

- [ ] **Step 17.1: Add the test**

```csharp
[Fact]
public async Task Privmsg_RaisesMessageReceived_OnDispatcherThread() {
    var (svc, transport, _, _) = Build();
    var creds = new ChatCredentials("surfinitebot", "abc123def456ghi789jkl012mno345");
    var received = new List<ChatMessage>();
    svc.MessageReceived += (_, m) => received.Add(m);

    var connectTask = svc.ConnectAsync("surfinite", creds);
    transport.InjectIncoming(":tmi.twitch.tv ROOMSTATE #surfinite");
    transport.InjectIncoming(
        "@user-id=1234;display-name=Carol :carol!carol@carol.tmi.twitch.tv PRIVMSG #surfinite :#0");

    for (int i = 0; i < 10 && received.Count == 0; i++) await Task.Delay(20);

    Assert.Single(received);
    Assert.Equal("#0", received[0].Text);
    Assert.Equal("carol", received[0].Login);
    svc.Dispose();
}
```

- [ ] **Step 17.2: Run — should pass (Task 14's HandlePrivmsg already does this)**

- [ ] **Step 17.3: Commit (test-only change)**

```powershell
git add tests/Chat/TwitchIrcChatServiceTests.cs
git commit -m "plan-b-1/4.6: test — PRIVMSG raises MessageReceived"
```

---

### Task 18: Self-echo guard with CAP NAK fallback

**Files:**
- Modify: `tests/Chat/TwitchIrcChatServiceTests.cs`
- (Impl already in Task 14's `IsSelfEcho` which compares by login; verify it works in no-tags mode)

- [ ] **Step 18.1: Add two tests — one tag-mode, one no-tags-mode**

```csharp
[Fact]
public async Task Privmsg_SelfEchoByLogin_IsFiltered() {
    var (svc, transport, _, _) = Build();
    var creds = new ChatCredentials("surfinitebot", "abc123def456ghi789jkl012mno345");
    var received = new List<ChatMessage>();
    svc.MessageReceived += (_, m) => received.Add(m);

    var connectTask = svc.ConnectAsync("surfinite", creds);
    transport.InjectIncoming(":tmi.twitch.tv ROOMSTATE #surfinite");
    // Self message — login matches the bot's NICK.
    transport.InjectIncoming(":surfinitebot!surfinitebot@surfinitebot.tmi.twitch.tv PRIVMSG #surfinite :hello from bot");
    // Another user — should NOT be filtered.
    transport.InjectIncoming(":alice!alice@alice.tmi.twitch.tv PRIVMSG #surfinite :hello from alice");

    for (int i = 0; i < 10 && received.Count == 0; i++) await Task.Delay(20);
    await Task.Delay(50);   // give the second message a chance to arrive

    Assert.Single(received);
    Assert.Equal("alice", received[0].Login);
    svc.Dispose();
}
```

- [ ] **Step 18.2: Run — should pass (login-based check covers both modes)**

- [ ] **Step 18.3: Commit**

```powershell
git add tests/Chat/TwitchIrcChatServiceTests.cs
git commit -m "plan-b-1/4.7: test — self-echo guard via login (works in no-tags mode)"
```

---

### Task 19: SendMessageAsync via OutgoingMessageQueue

**Files:**
- Modify: `src/Ti/Chat/TwitchIrcChatService.cs`
- Modify: `tests/Chat/TwitchIrcChatServiceTests.cs`

- [ ] **Step 19.1: Add the test**

```csharp
[Fact]
public async Task SendMessageAsync_WhenConnected_WritesPrivmsgToTransport() {
    var (svc, transport, clock, sched) = Build();
    var creds = new ChatCredentials("surfinitebot", "abc123def456ghi789jkl012mno345");
    var connectTask = svc.ConnectAsync("surfinite", creds);
    transport.InjectIncoming(":tmi.twitch.tv ROOMSTATE #surfinite");
    for (int i = 0; i < 10 && svc.State != ChatConnectionState.ConnectedReadWrite; i++) await Task.Delay(20);

    await svc.SendMessageAsync("hello chat", OutgoingMessagePriority.High);
    sched.Advance(TimeSpan.Zero);
    await Task.Delay(50);

    Assert.Contains(transport.Writes, w => w == "PRIVMSG #surfinite :hello chat");
    svc.Dispose();
}
```

- [ ] **Step 19.2: Run — fails (SendMessageAsync is a stub)**

- [ ] **Step 19.3: Wire OutgoingMessageQueue into TwitchIrcChatService**

Add fields:
```csharp
private OutgoingMessageQueue? _sendQueue;
```

In `RunConnectionAsync` BEFORE the read loop, after JOIN:
```csharp
_sendQueue = new OutgoingMessageQueue(
    capacity: _sendCapacity, window: _sendWindow,
    minInterval: _sendMinInterval,
    clock: _clock, scheduler: _scheduler,
    send: line => _transport!.WriteLineAsync(line, ct));
```

Replace `SendMessageAsync`:
```csharp
public Task SendMessageAsync(string text, OutgoingMessagePriority priority = OutgoingMessagePriority.Normal, CancellationToken ct = default) {
    if (_disposed) return Task.FromException(new ObjectDisposedException(nameof(TwitchIrcChatService)));
    if (!CanSend || _sendQueue is null || _channel is null) {
        return Task.FromException(new InvalidOperationException($"Cannot send in state {_state}"));
    }
    var line = $"PRIVMSG #{_channel} :{text}";
    _sendQueue.Enqueue(line, priority);
    return Task.CompletedTask;
}
```

- [ ] **Step 19.4: Run — should pass**

- [ ] **Step 19.5: Commit**

```powershell
git add src/Ti/Chat/TwitchIrcChatService.cs tests/Chat/TwitchIrcChatServiceTests.cs
git commit -m "plan-b-1/4.8: SendMessageAsync via OutgoingMessageQueue"
```

---

### Task 20: 1-msg/sec spacing test through TwitchIrcChatService

**Files:**
- Modify: `tests/Chat/TwitchIrcChatServiceTests.cs`

- [ ] **Step 20.1: Add the test**

```csharp
[Fact]
public async Task Send_TwoBackToBack_AreSpacedAtLeast1Second() {
    var (svc, transport, clock, sched) = Build();
    var creds = new ChatCredentials("surfinitebot", "abc123def456ghi789jkl012mno345");
    var connectTask = svc.ConnectAsync("surfinite", creds);
    transport.InjectIncoming(":tmi.twitch.tv ROOMSTATE #surfinite");
    for (int i = 0; i < 10 && svc.State != ChatConnectionState.ConnectedReadWrite; i++) await Task.Delay(20);

    int writesBefore = transport.Writes.Count;
    await svc.SendMessageAsync("first", OutgoingMessagePriority.High);
    await svc.SendMessageAsync("second", OutgoingMessagePriority.High);

    sched.Advance(TimeSpan.Zero);
    await Task.Delay(50);
    var firstSendTime = clock.UtcNow;
    int writesAfterFirst = transport.Writes.Count - writesBefore;
    Assert.True(writesAfterFirst >= 1, "first message should be sent");
    Assert.True(writesAfterFirst < 2, "second message should NOT be sent yet (gated by minInterval)");

    sched.Advance(TimeSpan.FromMilliseconds(1100));
    await Task.Delay(50);
    var totalWrites = transport.Writes.Count - writesBefore;
    Assert.True(totalWrites >= 2, "second message should be sent after >=1s gap");
    svc.Dispose();
}
```

- [ ] **Step 20.2: Run — should pass (OutgoingMessageQueue's minInterval is wired to 1s)**

- [ ] **Step 20.3: Commit**

```powershell
git add tests/Chat/TwitchIrcChatServiceTests.cs
git commit -m "plan-b-1/4.9: test — 1-msg/sec send spacing through TwitchIrcChatService"
```

---

### Task 21: PING/PONG handling

**Files:**
- Modify: `tests/Chat/TwitchIrcChatServiceTests.cs`
- (Impl already in Task 14; add test)

- [ ] **Step 21.1: Add the test**

```csharp
[Fact]
public async Task Ping_TriggersPong_BeforeJoinConfirmation() {
    var (svc, transport, _, _) = Build();
    var creds = new ChatCredentials("surfinitebot", "abc123def456ghi789jkl012mno345");
    var connectTask = svc.ConnectAsync("surfinite", creds);

    transport.InjectIncoming("PING :tmi.twitch.tv");
    for (int i = 0; i < 10; i++) {
        if (transport.Writes.Any(w => w.StartsWith("PONG"))) break;
        await Task.Delay(20);
    }
    Assert.Contains(transport.Writes, w => w == "PONG :tmi.twitch.tv");
    svc.Dispose();
}
```
Add `using System.Linq;` if needed.

- [ ] **Step 21.2: Run — should pass**

- [ ] **Step 21.3: Commit**

```powershell
git add tests/Chat/TwitchIrcChatServiceTests.cs
git commit -m "plan-b-1/4.10: test — PING triggers PONG (works pre-JOIN)"
```

---

### Task 22: JOIN-confirmation timeout

**Files:**
- Modify: `src/Ti/Chat/TwitchIrcChatService.cs`
- Modify: `tests/Chat/TwitchIrcChatServiceTests.cs`

- [ ] **Step 22.1: Add the test**

```csharp
[Fact]
public async Task JoinConfirmationTimeout_TransitionsToJoinFailed() {
    var (svc, transport, clock, sched) = Build();
    var creds = new ChatCredentials("surfinitebot", "abc123def456ghi789jkl012mno345");
    var connectTask = svc.ConnectAsync("surfinite", creds);

    // No ROOMSTATE/USERSTATE/353/366 injected — simulate a quietly-dropped JOIN.
    // Wait for JOIN to be sent first.
    for (int i = 0; i < 20 && !transport.Writes.Any(w => w.StartsWith("JOIN")); i++) await Task.Delay(20);

    sched.Advance(TimeSpan.FromSeconds(11));
    await Task.Delay(50);

    Assert.Equal(ChatConnectionState.JoinFailed, svc.State);
    svc.Dispose();
}
```

- [ ] **Step 22.2: Run — fails (no timeout)**

- [ ] **Step 22.3: Add JOIN-timeout logic**

Add field:
```csharp
private IDisposable? _joinTimeoutTimer;
private static readonly TimeSpan JoinConfirmationTimeout = TimeSpan.FromSeconds(10);
```

In `RunConnectionAsync` after sending JOIN (before the read loop):
```csharp
_joinTimeoutTimer = _scheduler.Schedule(JoinConfirmationTimeout, () => {
    if (_state is ChatConnectionState.Connecting) {
        TransitionTo(ChatConnectionState.JoinFailed,
            $"JOIN confirmation timeout after {JoinConfirmationTimeout.TotalSeconds}s");
        _cts?.Cancel();
    }
});
```

When transitioning to ConnectedReadWrite (in `ProcessIncomingLine`), cancel the timer:
```csharp
if (_state is ChatConnectionState.Connecting) {
    _joinTimeoutTimer?.Dispose();
    _joinTimeoutTimer = null;
    TransitionTo(ChatConnectionState.ConnectedReadWrite, "JOIN confirmed");
}
```

- [ ] **Step 22.4: Run — should pass**

- [ ] **Step 22.5: Commit**

```powershell
git add src/Ti/Chat/TwitchIrcChatService.cs tests/Chat/TwitchIrcChatServiceTests.cs
git commit -m "plan-b-1/4.11: JOIN-confirmation 10s timeout → JoinFailed"
```

---

### Task 23: NOTICE msg_ratelimit / msg_slowmode / msg_duplicate

**Files:**
- Modify: `src/Ti/Chat/TwitchIrcChatService.cs`
- Modify: `tests/Chat/TwitchIrcChatServiceTests.cs`

- [ ] **Step 23.1: Add the test**

```csharp
[Fact]
public async Task RateLimitNotice_LogsAndDoesNotChangeState() {
    var (svc, transport, _, _) = Build();
    var logs = new List<string>();
    using var _ = TiLog.SinkScope((level, msg, ex) => {
        if (level >= LogLevel.Warn) lock (logs) logs.Add(msg);
    });

    var creds = new ChatCredentials("surfinitebot", "abc123def456ghi789jkl012mno345");
    var connectTask = svc.ConnectAsync("surfinite", creds);
    transport.InjectIncoming(":tmi.twitch.tv ROOMSTATE #surfinite");
    for (int i = 0; i < 10 && svc.State != ChatConnectionState.ConnectedReadWrite; i++) await Task.Delay(20);

    transport.InjectIncoming("@msg-id=msg_ratelimit :tmi.twitch.tv NOTICE #surfinite :Your message was not sent because you are sending messages too quickly.");
    for (int i = 0; i < 10 && !logs.Any(l => l.Contains("ratelimit", StringComparison.OrdinalIgnoreCase)); i++) await Task.Delay(20);

    Assert.Equal(ChatConnectionState.ConnectedReadWrite, svc.State);
    Assert.Contains(logs, l => l.Contains("ratelimit", StringComparison.OrdinalIgnoreCase));
    svc.Dispose();
}
```

(Note: `TiLog.SinkScope` may not exist — if not, set `TiLog.Sink = ...; ... TiLog.Sink = oldSink;` directly. Check the TiLog API in `src/Ti/Internal/TiLog.cs` and adapt.)

- [ ] **Step 23.2: Run — fails (HandleNotice doesn't recognise these)**

- [ ] **Step 23.3: Extend HandleNotice**

After the auth/join checks:
```csharp
bool isRateLimit = msgId is "msg_ratelimit" or "msg_slowmode";
if (isRateLimit) {
    TiLog.Warn($"[TwitchIrcChatService] Twitch ratelimit/slowmode NOTICE: {text}");
    return;
}
bool isDuplicate = msgId is "msg_duplicate";
if (isDuplicate) {
    TiLog.Debug($"[TwitchIrcChatService] duplicate-message NOTICE dropped: {text}");
    return;
}
```

- [ ] **Step 23.4: Run — should pass**

- [ ] **Step 23.5: Commit**

```powershell
git add src/Ti/Chat/TwitchIrcChatService.cs tests/Chat/TwitchIrcChatServiceTests.cs
git commit -m "plan-b-1/4.12: NOTICE msg_ratelimit/msg_slowmode/msg_duplicate handling"
```

---

### Task 24: Anonymous (justinfan) mode

**Files:**
- Modify: `src/Ti/Chat/TwitchIrcChatService.cs`
- Modify: `tests/Chat/TwitchIrcChatServiceTests.cs`

- [ ] **Step 24.1: Add the test**

```csharp
[Fact]
public async Task ConnectAsync_NoCredentials_EntersConnectedReadOnly() {
    var (svc, transport, _, _) = Build();
    var connectTask = svc.ConnectAsync("surfinite", creds: null);
    transport.InjectIncoming(":tmi.twitch.tv ROOMSTATE #surfinite");
    for (int i = 0; i < 10 && !svc.IsConnected; i++) await Task.Delay(20);

    Assert.Equal(ChatConnectionState.ConnectedReadOnly, svc.State);
    Assert.False(svc.CanSend);
    Assert.Contains(transport.Writes, w => w.StartsWith("NICK justinfan"));
    Assert.DoesNotContain(transport.Writes, w => w.StartsWith("PASS"));
    svc.Dispose();
}

[Fact]
public async Task SendMessageAsync_InAnonymousMode_ReturnsFailedTask() {
    var (svc, transport, _, _) = Build();
    var connectTask = svc.ConnectAsync("surfinite", creds: null);
    transport.InjectIncoming(":tmi.twitch.tv ROOMSTATE #surfinite");
    for (int i = 0; i < 10 && !svc.IsConnected; i++) await Task.Delay(20);

    await Assert.ThrowsAsync<InvalidOperationException>(
        async () => await svc.SendMessageAsync("nope"));
    svc.Dispose();
}
```

- [ ] **Step 24.2: Run — `ConnectedReadOnly` test fails (current code transitions to ConnectedReadWrite)**

- [ ] **Step 24.3: Adjust the JOIN-confirmation transition to pick the right state**

Replace the `ConnectedReadWrite` transition:
```csharp
var nextState = _creds is null
    ? ChatConnectionState.ConnectedReadOnly
    : ChatConnectionState.ConnectedReadWrite;
TransitionTo(nextState, "JOIN confirmed");
```

- [ ] **Step 24.4: Run — both tests pass**

- [ ] **Step 24.5: Commit**

```powershell
git add src/Ti/Chat/TwitchIrcChatService.cs tests/Chat/TwitchIrcChatServiceTests.cs
git commit -m "plan-b-1/4.13: anonymous justinfan mode → ConnectedReadOnly"
```

---

### Task 25: Reconnect with exponential backoff + jitter on transport failure

**Files:**
- Modify: `src/Ti/Chat/TwitchIrcChatService.cs`
- Modify: `tests/Chat/TwitchIrcChatServiceTests.cs`

- [ ] **Step 25.1: Add the test**

```csharp
[Fact]
public async Task TransportClose_TriggersReconnect_WithBackoff() {
    // Use a transport factory that hands out a fresh fake on each call.
    var clock = new FakeClock(DateTimeOffset.UtcNow);
    var sched = new FakeTimerScheduler(clock);
    var dispatcher = new ImmediateDispatcher();
    var transports = new List<FakeIrcTransport>();
    var svc = new TwitchIrcChatService(
        dispatcher, clock, sched,
        transportFactory: () => { var t = new FakeIrcTransport(); transports.Add(t); return t; },
        sendCapacity: 20, sendWindow: TimeSpan.FromSeconds(30), sendMinInterval: TimeSpan.FromSeconds(1));

    var creds = new ChatCredentials("surfinitebot", "abc123def456ghi789jkl012mno345");
    var connectTask = svc.ConnectAsync("surfinite", creds);
    for (int i = 0; i < 20 && transports.Count < 1; i++) await Task.Delay(20);
    transports[0].InjectIncoming(":tmi.twitch.tv ROOMSTATE #surfinite");
    for (int i = 0; i < 20 && svc.State != ChatConnectionState.ConnectedReadWrite; i++) await Task.Delay(20);

    // Simulate remote close.
    transports[0].Close();
    for (int i = 0; i < 20 && svc.State != ChatConnectionState.Reconnecting; i++) await Task.Delay(20);
    Assert.Equal(ChatConnectionState.Reconnecting, svc.State);

    // Advance past first backoff (5s nominal, ±20% jitter — advance 7s to be safe).
    sched.Advance(TimeSpan.FromSeconds(7));
    for (int i = 0; i < 20 && transports.Count < 2; i++) await Task.Delay(20);
    Assert.True(transports.Count >= 2, "second transport should be created on reconnect");
    svc.Dispose();
}
```

- [ ] **Step 25.2: Run — fails (no reconnect logic)**

- [ ] **Step 25.3: Add reconnect logic**

In `RunConnectionAsync`'s catch block (replace existing):
```csharp
} catch (OperationCanceledException) {
    // Caller-cancelled or terminal-state-cancelled; do nothing.
} catch (Exception ex) {
    LastError = ex;
    TiLog.Error("[TwitchIrcChatService] read loop error", ex);
}

// Determine post-loop action.
if (_disposed || _state is ChatConnectionState.AuthenticationFailed
                       or ChatConnectionState.JoinFailed
                       or ChatConnectionState.Disposed) {
    return;   // terminal — no reconnect
}

// Read returned null OR transport threw → schedule reconnect.
ScheduleReconnect();
```

Add fields and method:
```csharp
private int _reconnectAttempt;
private static readonly TimeSpan[] BackoffSeconds = {
    TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10),
    TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(60)
};

private void ScheduleReconnect() {
    TransitionTo(ChatConnectionState.Reconnecting, "transport closed/error");
    var idx = Math.Min(_reconnectAttempt, BackoffSeconds.Length - 1);
    var nominal = BackoffSeconds[idx];
    var jitterMs = (Random.Shared.NextDouble() - 0.5) * 0.4 * nominal.TotalMilliseconds;
    var delay = nominal + TimeSpan.FromMilliseconds(jitterMs);
    _reconnectAttempt++;

    _scheduler.Schedule(delay, () => {
        if (_disposed) return;
        // Build a fresh transport + restart the read loop.
        try { _transport?.Dispose(); } catch { }
        _transport = _transportFactory();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(default);
        _readLoopTask = Task.Run(() => RunConnectionAsync(_cts.Token));
    });
}
```

When ConnectedReadWrite/ReadOnly is reached, reset `_reconnectAttempt = 0;` so the next reconnect starts at the base backoff.

- [ ] **Step 25.4: Run — should pass**

- [ ] **Step 25.5: Commit**

```powershell
git add src/Ti/Chat/TwitchIrcChatService.cs tests/Chat/TwitchIrcChatServiceTests.cs
git commit -m "plan-b-1/4.14: reconnect with exponential backoff + jitter"
```

---

### Task 26: No stale reconnect after AuthenticationFailed

**Files:**
- Modify: `tests/Chat/TwitchIrcChatServiceTests.cs`

- [ ] **Step 26.1: Add the test**

```csharp
[Fact]
public async Task AuthenticationFailed_DoesNotTriggerReconnect() {
    var clock = new FakeClock(DateTimeOffset.UtcNow);
    var sched = new FakeTimerScheduler(clock);
    var dispatcher = new ImmediateDispatcher();
    var transports = new List<FakeIrcTransport>();
    var svc = new TwitchIrcChatService(
        dispatcher, clock, sched,
        transportFactory: () => { var t = new FakeIrcTransport(); transports.Add(t); return t; },
        sendCapacity: 20, sendWindow: TimeSpan.FromSeconds(30), sendMinInterval: TimeSpan.FromSeconds(1));

    var creds = new ChatCredentials("surfinitebot", "abc123def456ghi789jkl012mno345");
    var connectTask = svc.ConnectAsync("surfinite", creds);
    for (int i = 0; i < 20 && transports.Count < 1; i++) await Task.Delay(20);
    transports[0].InjectIncoming(":tmi.twitch.tv NOTICE * :Login authentication failed");

    for (int i = 0; i < 20 && svc.State != ChatConnectionState.AuthenticationFailed; i++) await Task.Delay(20);
    sched.Advance(TimeSpan.FromSeconds(120));
    await Task.Delay(50);

    Assert.Equal(1, transports.Count);   // no reconnect
    svc.Dispose();
}
```

- [ ] **Step 26.2: Run — should pass already (terminal-state check in ScheduleReconnect)**

- [ ] **Step 26.3: Commit**

```powershell
git add tests/Chat/TwitchIrcChatServiceTests.cs
git commit -m "plan-b-1/4.15: test — no reconnect after AuthenticationFailed"
```

---

### Task 27: Disconnect during Connecting + Dispose during reconnect-delay

**Files:**
- Modify: `src/Ti/Chat/TwitchIrcChatService.cs`
- Modify: `tests/Chat/TwitchIrcChatServiceTests.cs`

- [ ] **Step 27.1: Add the test**

```csharp
[Fact]
public void Disconnect_WhileConnecting_StopsTransport_NoReconnect() {
    var clock = new FakeClock(DateTimeOffset.UtcNow);
    var sched = new FakeTimerScheduler(clock);
    var transports = new List<FakeIrcTransport>();
    var svc = new TwitchIrcChatService(
        new ImmediateDispatcher(), clock, sched,
        () => { var t = new FakeIrcTransport(); transports.Add(t); return t; },
        20, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1));

    var creds = new ChatCredentials("surfinitebot", "abc123def456ghi789jkl012mno345");
    _ = svc.ConnectAsync("surfinite", creds);
    svc.Disconnect();

    Assert.Equal(ChatConnectionState.Disconnected, svc.State);
    sched.Advance(TimeSpan.FromMinutes(2));
    Assert.Equal(1, transports.Count);   // no reconnect after explicit Disconnect
    svc.Dispose();
}
```

- [ ] **Step 27.2: Implement Disconnect properly**

Replace the stub `Disconnect`:
```csharp
public void Disconnect() {
    if (_state is ChatConnectionState.Disposed) return;
    _cts?.Cancel();
    try { _transport?.Dispose(); } catch { }
    _transport = null;
    TransitionTo(ChatConnectionState.Disconnected, "Disconnect called");
}
```

Adjust ScheduleReconnect to bail if `_state` is now `Disconnected` or `Disposed`:
```csharp
private void ScheduleReconnect() {
    if (_disposed || _state is ChatConnectionState.Disconnected
                            or ChatConnectionState.Disposed
                            or ChatConnectionState.AuthenticationFailed
                            or ChatConnectionState.JoinFailed) return;
    // ... rest as before
}
```

- [ ] **Step 27.3: Run — should pass**

- [ ] **Step 27.4: Commit**

```powershell
git add src/Ti/Chat/TwitchIrcChatService.cs tests/Chat/TwitchIrcChatServiceTests.cs
git commit -m "plan-b-1/4.16: Disconnect stops Connecting + skips pending reconnect"
```

---

### Task 28: Dispose semantics + send-while-disconnected

**Files:**
- Modify: `src/Ti/Chat/TwitchIrcChatService.cs`
- Modify: `tests/Chat/TwitchIrcChatServiceTests.cs`

- [ ] **Step 28.1: Add tests**

```csharp
[Fact]
public async Task SendMessageAsync_WhenDisconnected_ReturnsFailedTask() {
    var (svc, _, _, _) = Build();
    await Assert.ThrowsAsync<InvalidOperationException>(
        async () => await svc.SendMessageAsync("nope"));
    svc.Dispose();
}

[Fact]
public void Dispose_TransitionsToDisposed_AndClosesTransport() {
    var (svc, transport, _, _) = Build();
    var creds = new ChatCredentials("surfinitebot", "abc123def456ghi789jkl012mno345");
    _ = svc.ConnectAsync("surfinite", creds);
    svc.Dispose();
    Assert.Equal(ChatConnectionState.Disposed, svc.State);
    Assert.True(transport.Disposed);
}
```

- [ ] **Step 28.2: Update Dispose**

```csharp
public void Dispose() {
    if (_disposed) return;
    _disposed = true;
    _cts?.Cancel();
    try { _transport?.Dispose(); } catch { }
    try { _sendQueue?.Dispose(); } catch { }
    try { _joinTimeoutTimer?.Dispose(); } catch { }
    _transport = null;
    _sendQueue = null;
    _state = ChatConnectionState.Disposed;
    var args = new ChatConnectionChangedEventArgs(_state, ChatConnectionState.Disposed, "Dispose");
    try { _dispatcher.Post(() => ConnectionStateChanged?.Invoke(this, args)); } catch { }
}
```

(Note: `_state` set directly to skip the `_state == next` no-op in TransitionTo. Setting before posting the event preserves the prior-state-in-args is OK because we're saying "transitioning to Disposed".)

- [ ] **Step 28.3: Run — should pass**

- [ ] **Step 28.4: Commit**

```powershell
git add src/Ti/Chat/TwitchIrcChatService.cs tests/Chat/TwitchIrcChatServiceTests.cs
git commit -m "plan-b-1/4.17: Dispose semantics + send-while-disconnected fails"
```

---

### Task 29: RECONNECT command handling

**Files:**
- Modify: `src/Ti/Chat/TwitchIrcChatService.cs`
- Modify: `tests/Chat/TwitchIrcChatServiceTests.cs`

- [ ] **Step 29.1: Add the test**

```csharp
[Fact]
public async Task ReconnectCommand_TriggersGracefulReconnect() {
    var clock = new FakeClock(DateTimeOffset.UtcNow);
    var sched = new FakeTimerScheduler(clock);
    var transports = new List<FakeIrcTransport>();
    var svc = new TwitchIrcChatService(
        new ImmediateDispatcher(), clock, sched,
        () => { var t = new FakeIrcTransport(); transports.Add(t); return t; },
        20, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1));
    var creds = new ChatCredentials("surfinitebot", "abc123def456ghi789jkl012mno345");
    _ = svc.ConnectAsync("surfinite", creds);
    for (int i = 0; i < 20 && transports.Count < 1; i++) await Task.Delay(20);
    transports[0].InjectIncoming(":tmi.twitch.tv ROOMSTATE #surfinite");
    for (int i = 0; i < 20 && svc.State != ChatConnectionState.ConnectedReadWrite; i++) await Task.Delay(20);

    transports[0].InjectIncoming("RECONNECT");
    sched.Advance(TimeSpan.FromSeconds(7));   // past first backoff
    for (int i = 0; i < 20 && transports.Count < 2; i++) await Task.Delay(20);

    Assert.True(transports.Count >= 2);
    svc.Dispose();
}
```

- [ ] **Step 29.2: Implement RECONNECT handling**

In `ProcessIncomingLine`'s `ReconnectEvent` branch:
```csharp
case ReconnectEvent:
    TiLog.Info("[TwitchIrcChatService] received RECONNECT — reconnecting");
    try { _transport?.Dispose(); } catch { }
    // The read loop will exit on null/exception; ScheduleReconnect kicks in.
    _cts?.Cancel();
    break;
```

- [ ] **Step 29.3: Run — should pass**

- [ ] **Step 29.4: Commit**

```powershell
git add src/Ti/Chat/TwitchIrcChatService.cs tests/Chat/TwitchIrcChatServiceTests.cs
git commit -m "plan-b-1/4.18: RECONNECT command → graceful disconnect+reconnect"
```

---

### Task 30: SslIrcTransport (production TLS implementation)

**Files:**
- Modify: `src/Ti/Chat/Internal/SslIrcTransport.cs` (currently a placeholder inside TwitchIrcChatService.cs)

- [ ] **Step 30.1: Move SslIrcTransport to its own file**

Create `src/Ti/Chat/Internal/SslIrcTransport.cs`:
```csharp
using System;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SlayTheStreamer2.Ti.Chat.Internal;

internal sealed class SslIrcTransport : IIrcTransport {
    private TcpClient? _tcp;
    private SslStream? _ssl;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private bool _disposed;

    public async Task ConnectAsync(string host, int port, CancellationToken ct) {
        _tcp = new TcpClient();
        await _tcp.ConnectAsync(host, port, ct);
        _ssl = new SslStream(_tcp.GetStream(), leaveInnerStreamOpen: false);
        await _ssl.AuthenticateAsClientAsync(host);
        _reader = new StreamReader(_ssl, Encoding.UTF8);
        _writer = new StreamWriter(_ssl, new UTF8Encoding(false)) { NewLine = "\r\n", AutoFlush = true };
    }

    public async Task<string?> ReadLineAsync(CancellationToken ct) {
        if (_reader is null) return null;
        return await _reader.ReadLineAsync(ct);
    }

    public Task WriteLineAsync(string line, CancellationToken ct) {
        if (_writer is null) return Task.FromException(new InvalidOperationException("not connected"));
        return _writer.WriteLineAsync(line);   // CT support omitted; close-on-dispose is enough
    }

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        try { _writer?.Dispose(); } catch { }
        try { _reader?.Dispose(); } catch { }
        try { _ssl?.Dispose(); } catch { }
        try { _tcp?.Dispose(); } catch { }
    }
}
```

Remove the placeholder from `TwitchIrcChatService.cs`.

- [ ] **Step 30.2: Build to confirm no duplicate-class errors**

```bash
dotnet build src/slay_the_streamer_2.csproj --nologo
```
Expected: build succeeds.

- [ ] **Step 30.3: Run all tests to confirm nothing regressed**

```bash
dotnet test tests/slay_the_streamer_2.tests.csproj --nologo
```
Expected: all pass.

- [ ] **Step 30.4: Commit**

```powershell
git add src/Ti/Chat/Internal/SslIrcTransport.cs src/Ti/Chat/TwitchIrcChatService.cs
git commit -m "plan-b-1/4.19: SslIrcTransport (production TLS) extracted to its own file"
```

---

## Phase 5: VoteTallyLabel (Godot — operator-validated)

### Task 31: VoteTallyLabel skeleton + AttachTo

**Files:**
- Create: `src/Ti/Ui/VoteTallyLabel.cs`

- [ ] **Step 31.1: Create the file**

```csharp
using System;
using System.Text;
using Godot;
using SlayTheStreamer2.Ti.Internal;
using SlayTheStreamer2.Ti.Voting;

namespace SlayTheStreamer2.Ti.Ui;

public sealed partial class VoteTallyLabel : RichTextLabel {
    private VoteSession? _session;
    private EventHandler<VoteSession>? _closedHandler;
    private EventHandler<VoteSession>? _cancelledHandler;

    public static void AttachTo(VoteSession session) {
        var tree = (Engine.GetMainLoop() as SceneTree);
        if (tree?.Root is null) {
            TiLog.Warn("[vote-tally-label] no SceneTree.Root available; skipping UI attach");
            return;
        }

        var label = new VoteTallyLabel { Name = "VoteTallyLabel" };
        label.BbcodeEnabled = true;
        label.FitContent = true;
        label.AnchorLeft = 0.6f; label.AnchorTop = 0.05f;
        label.AnchorRight = 0.98f; label.AnchorBottom = 0.4f;
        label._session = session;
        label._closedHandler = (_, _) => label.SafeQueueFree();
        label._cancelledHandler = (_, _) => label.SafeQueueFree();
        session.Closed += label._closedHandler;
        session.Cancelled += label._cancelledHandler;

        // Direct attachment under root. If z-order issues surface in operator-validation,
        // switch to creating/finding a CanvasLayer named "SlayTheStreamerOverlayLayer" under root.
        tree.Root.AddChild(label);
    }

    public override void _Process(double delta) {
        if (!GodotObject.IsInstanceValid(this) || _session is null) return;
        if (_session.State is VoteSessionState.Closed
                              or VoteSessionState.Cancelled
                              or VoteSessionState.Disposed) return;

        var sb = new StringBuilder();
        var secondsLeft = Math.Max(0, (int)_session.TimeRemaining.TotalSeconds);
        sb.AppendLine($"Chat voting — {secondsLeft}s left");
        for (int i = 0; i < _session.Options.Count; i++) {
            _session.Tallies.TryGetValue(i, out var count);
            sb.AppendLine($"#{i} {_session.Options[i].Label}: {count}");
        }
        Text = sb.ToString();
    }

    public override void _ExitTree() {
        if (_session is not null) {
            if (_closedHandler is not null) _session.Closed -= _closedHandler;
            if (_cancelledHandler is not null) _session.Cancelled -= _cancelledHandler;
            if (_session.State is VoteSessionState.Open) {
                try { _session.Cancel(); }
                catch (Exception ex) { TiLog.Warn($"[vote-tally-label] session.Cancel threw on _ExitTree: {ex.Message}"); }
            }
        }
        _session = null;
        _closedHandler = null;
        _cancelledHandler = null;
        base._ExitTree();
    }

    private void SafeQueueFree() {
        if (GodotObject.IsInstanceValid(this) && !IsQueuedForDeletion()) {
            QueueFree();
        }
    }
}
```

- [ ] **Step 31.2: Verify the mod project compiles**

```bash
dotnet build src/slay_the_streamer_2.csproj --nologo
```
Expected: build succeeds.

- [ ] **Step 31.3: Commit**

```powershell
git add src/Ti/Ui/VoteTallyLabel.cs
git commit -m "plan-b-1/5.1: VoteTallyLabel — Godot RichTextLabel with _Process polling"
```

---

## Phase 6: NeowBlessingVotePatch

### Task 32: Patch class with Prepare validation

**Files:**
- Create: `src/Game/DecisionVotes/NeowBlessingVotePatch.cs`

- [ ] **Step 32.1: Create the file with skeleton + Prepare**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using SlayTheStreamer2.Ti.Chat;
using SlayTheStreamer2.Ti.Internal;
using SlayTheStreamer2.Ti.Ui;
using SlayTheStreamer2.Ti.Voting;

namespace SlayTheStreamer2.Game.DecisionVotes;

[HarmonyPatch(typeof(NEventRoom), nameof(NEventRoom.OptionButtonClicked))]
internal static class NeowBlessingVotePatch {
    private static int _voteInProgress;
    private static int _resumeInProgress;
    private static int _multiplayerWarnFired;
    private static readonly Lazy<FieldInfo?> _eventField =
        new(() => AccessTools.Field(typeof(NEventRoom), "_event"));

    static bool Prepare(MethodBase? original) {
        if (original is null) {
            if (_eventField.Value is null) {
                TiLog.Error("[neow-vote] NEventRoom._event field not found; patch will not function");
                return false;
            }
            return true;
        }

        var parameters = original.GetParameters();
        if (parameters.Length != 2 ||
            parameters[0].ParameterType != typeof(EventOption) ||
            parameters[1].ParameterType != typeof(int)) {
            TiLog.Error($"[neow-vote] target signature mismatch: {original.DeclaringType?.FullName}.{original.Name}({string.Join(", ", parameters.Select(p => p.ParameterType.Name))})");
            return false;
        }
        TiLog.Info($"[neow-vote] target resolved: {original.DeclaringType?.FullName}.{original.Name}");
        return true;
    }

    static bool Prefix(NEventRoom __instance, EventOption option, int index) {
        // Stub — fills in next tasks.
        return true;
    }

    private static bool IsNeowEvent(NEventRoom room) {
        var eventModel = _eventField.Value?.GetValue(room);
        return eventModel is Neow;
    }

    private static IReadOnlyList<EventOption>? GetCurrentOptions(NEventRoom room) {
        var eventModel = _eventField.Value?.GetValue(room) as EventModel;
        return eventModel?.CurrentOptions;
    }

    private static int? TryGetEventOwnerPlayerCount(NEventRoom room) {
        var eventModel = _eventField.Value?.GetValue(room) as EventModel;
        return eventModel?.Owner?.RunState?.Players?.Count;
    }
}
```

- [ ] **Step 32.2: Verify build**

```bash
dotnet build src/slay_the_streamer_2.csproj --nologo
```
Expected: builds. Some `MegaCrit.*` namespace imports may need adjustment — check `decompiled/sts2/MegaCrit/sts2/Core/...` for the exact paths and adjust the `using` statements to match.

- [ ] **Step 32.3: Commit**

```powershell
git add src/Game/DecisionVotes/NeowBlessingVotePatch.cs
git commit -m "plan-b-1/6.1: NeowBlessingVotePatch — Prepare with field+signature checks"
```

---

### Task 33: Prefix with all guards + Voter.Start try/catch + DisableEventOptions

**Files:**
- Modify: `src/Game/DecisionVotes/NeowBlessingVotePatch.cs`

- [ ] **Step 33.1: Replace the Prefix stub with the full guard chain**

```csharp
static bool Prefix(NEventRoom __instance, EventOption option, int index) {
    if (_resumeInProgress == 1) return true;
    if (!IsNeowEvent(__instance)) return true;
    if (option.IsLocked || option.IsProceed) return true;

    if (TryGetEventOwnerPlayerCount(__instance) is int playerCount && playerCount > 1) {
        if (Interlocked.CompareExchange(ref _multiplayerWarnFired, 1, 0) == 0) {
            TiLog.Warn("[neow-vote] multiplayer detected (Players.Count > 1); bailing to vanilla (further bail-outs at Debug)");
        } else {
            TiLog.Debug("[neow-vote] multiplayer bail-out");
        }
        return true;
    }

    var coordinator = Voter.Default;
    if (coordinator is null) return true;
    if (coordinator.Chat.State is not ChatConnectionState.ConnectedReadWrite) {
        TiLog.Debug($"[neow-vote] chat not in ConnectedReadWrite (state={coordinator.Chat.State}); bailing to vanilla");
        return true;
    }

    if (Interlocked.CompareExchange(ref _voteInProgress, 1, 0) != 0) {
        TiLog.Debug("[neow-vote] repeat click during open vote — suppressed");
        return false;
    }

    var liveOptions = GetCurrentOptions(__instance);
    if (liveOptions is null || liveOptions.Count == 0) {
        Interlocked.Exchange(ref _voteInProgress, 0);
        return true;
    }
    var optionsSnapshot = liveOptions.ToList();
    var labels = optionsSnapshot.Select(o => o.Title.GetFormattedText()).ToList();

    VoteSession session;
    try {
        session = coordinator.Start("Neow's Bonus", labels, TimeSpan.FromSeconds(30));
    } catch (Exception ex) {
        TiLog.Error("[neow-vote] Voter.Default.Start threw; falling back to vanilla", ex);
        Interlocked.Exchange(ref _voteInProgress, 0);
        return true;
    }

    try {
        __instance.Layout?.DisableEventOptions();
    } catch (Exception ex) {
        TiLog.Warn($"[neow-vote] DisableEventOptions threw (continuing): {ex.Message}");
    }

    TiLog.Info($"[neow-vote] opening vote for {optionsSnapshot.Count} options; player clicked #{index}");
    _ = HandleVoteAsync(coordinator, __instance, session, optionsSnapshot, index);
    return false;
}
```

Add stub `HandleVoteAsync` to compile:
```csharp
private static async Task HandleVoteAsync(VoteCoordinator coordinator, NEventRoom room,
                                          VoteSession session, IReadOnlyList<EventOption> snapshot,
                                          int playerClickIndex) {
    await Task.CompletedTask;   // implemented in Task 34
}
```

- [ ] **Step 33.2: Build**

```bash
dotnet build src/slay_the_streamer_2.csproj --nologo
```
Expected: builds.

- [ ] **Step 33.3: Commit**

```powershell
git add src/Game/DecisionVotes/NeowBlessingVotePatch.cs
git commit -m "plan-b-1/6.2: Prefix — all guards + Voter.Start try/catch + DisableEventOptions"
```

---

### Task 34: HandleVoteAsync + ResumeOnMainThread with fallback

**Files:**
- Modify: `src/Game/DecisionVotes/NeowBlessingVotePatch.cs`

- [ ] **Step 34.1: Replace HandleVoteAsync stub with the real impl + add ResumeOnMainThread**

```csharp
private static async Task HandleVoteAsync(VoteCoordinator coordinator, NEventRoom room,
                                          VoteSession session, IReadOnlyList<EventOption> snapshot,
                                          int playerClickIndex) {
    try {
        coordinator.Dispatcher.Post(() => VoteTallyLabel.AttachTo(session));

        int winnerIndex;
        try {
            winnerIndex = await session.AwaitWinnerAsync();
        } catch (Exception ex) {
            TiLog.Error("[neow-vote] AwaitWinnerAsync threw; falling back to player click", ex);
            winnerIndex = playerClickIndex;
        }

        if (winnerIndex < 0 || winnerIndex >= snapshot.Count) {
            TiLog.Warn($"[neow-vote] winnerIndex {winnerIndex} out of snapshot range; using player click");
            winnerIndex = playerClickIndex;
        }

        TiLog.Info($"[neow-vote] resume: applying winner #{winnerIndex} on main thread");
        coordinator.Dispatcher.Post(() => ResumeOnMainThread(room, snapshot, winnerIndex, playerClickIndex));
    } catch (Exception ex) {
        TiLog.Error("[neow-vote] HandleVoteAsync threw; attempting fallback resume with player click", ex);
        try {
            coordinator.Dispatcher.Post(() => ResumeOnMainThread(room, snapshot, playerClickIndex, playerClickIndex));
        } catch (Exception postEx) {
            TiLog.Error("[neow-vote] fallback resume Post itself threw; resetting flags", postEx);
            Interlocked.Exchange(ref _resumeInProgress, 0);
            Interlocked.Exchange(ref _voteInProgress, 0);
        }
    }
}

private static void ResumeOnMainThread(NEventRoom room, IReadOnlyList<EventOption> snapshot,
                                       int preferredIndex, int playerClickIndex) {
    Interlocked.Exchange(ref _resumeInProgress, 1);
    try {
        if (!GodotObject.IsInstanceValid(room)) {
            TiLog.Warn("[neow-vote] resume: room no longer valid; dropping resume");
            return;
        }
        if (!IsNeowEvent(room)) {
            TiLog.Warn("[neow-vote] resume: active event is no longer Neow; dropping resume");
            return;
        }
        var currentOptions = GetCurrentOptions(room)?.ToList();
        if (currentOptions is null || currentOptions.Count == 0) {
            TiLog.Warn("[neow-vote] resume: no current options; dropping");
            return;
        }

        int applyIndex = preferredIndex;
        if (applyIndex < 0 || applyIndex >= currentOptions.Count) {
            TiLog.Warn($"[neow-vote] resume: preferred index {applyIndex} out of range; falling back to player click");
            applyIndex = playerClickIndex;
        }
        if (applyIndex < 0 || applyIndex >= currentOptions.Count) {
            TiLog.Warn($"[neow-vote] resume: neither preferred nor player index valid (options now {currentOptions.Count}); dropping");
            return;
        }

        var winnerOption = currentOptions[applyIndex];
        room.OptionButtonClicked(winnerOption, applyIndex);
    } catch (Exception ex) {
        TiLog.Error("[neow-vote] resume threw", ex);
    } finally {
        Interlocked.Exchange(ref _resumeInProgress, 0);
        Interlocked.Exchange(ref _voteInProgress, 0);
    }
}
```

- [ ] **Step 34.2: Build**

```bash
dotnet build src/slay_the_streamer_2.csproj --nologo
```
Expected: builds.

- [ ] **Step 34.3: Commit**

```powershell
git add src/Game/DecisionVotes/NeowBlessingVotePatch.cs
git commit -m "plan-b-1/6.3: HandleVoteAsync + ResumeOnMainThread with playerClickIndex fallback"
```

---

## Phase 7: ModEntry wiring

### Task 35: ModEntry — settings load + chat + Voter wiring

**Files:**
- Modify: `src/ModEntry.cs`

- [ ] **Step 35.1: Read existing ModEntry.cs to see where Section 6 belongs**

```bash
# Read src/ModEntry.cs sections 1-5; section 6 currently runs Harmony.PatchAll only.
```

- [ ] **Step 35.2: Add the new sections 6-9 after the existing TiLog.Sink wiring (after current section 5) and BEFORE the Harmony.PatchAll block**

Add at the top of ModEntry.cs:
```csharp
using System;
using System.IO;
using System.Reflection;
using System.Threading;
using SlayTheStreamer2.Game.Bootstrap;
using SlayTheStreamer2.Ti.Chat;
using SlayTheStreamer2.Ti.Voting;
```

Add static fields to the class:
```csharp
private static int _connectAnnounced;
private static readonly CancellationTokenSource _modCts = new();
internal static TwitchIrcChatService? Chat { get; private set; }
internal static VoteCoordinator? Coordinator { get; private set; }
```

Insert this block after the `TiLog.Sink = ...` assignment and before `var harmony = new Harmony(...)`:
```csharp
// 6. Resolve settings file path Godot-side, load settings.
var modVersion = Assembly.GetExecutingAssembly()
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
Log.Info($"[slay_the_streamer_2] mod version: {modVersion}");

var settingsPath = Path.Combine(OS.GetUserDataDir(), "slay_the_streamer_2.json");
var settingsResult = ModSettings.Load(settingsPath);
ChatSettings? settings = null;
switch (settingsResult) {
    case SettingsResult.Success s:
        settings = s.Settings;
        Log.Info($"[slay_the_streamer_2] settings loaded; channel=#{settings.Channel}");
        foreach (var w in s.Warnings) Log.Info($"[slay_the_streamer_2]   {w}");
        break;
    case SettingsResult.Missing m:
        Log.Info($"[slay_the_streamer_2] no settings file at {m.Path}; mod loaded but Twitch not connected. " +
                 "Create the file with: {{ \"schemaVersion\": 1, \"channel\": \"...\", \"username\": \"...\", \"oauthToken\": \"oauth:...\" }}");
        break;
    case SettingsResult.Malformed m:
        Log.Error($"[slay_the_streamer_2] settings file at {m.Path} is malformed: {m.Reason}. Mod loaded but not connecting.");
        break;
}

// 7. Build TI services if settings loaded.
var clock = new SlayTheStreamer2.Ti.Internal.SystemClock();
var scheduler = new SlayTheStreamer2.Ti.Internal.SystemTimerScheduler();

if (settings is not null) {
    Chat = new TwitchIrcChatService(dispatcher, clock, scheduler,
        sendCapacity: 20, sendWindow: TimeSpan.FromSeconds(30));
    Coordinator = new VoteCoordinator(Chat, clock, scheduler, dispatcher);
    Voter.Default = Coordinator;

    Chat.ConnectionStateChanged += (_, e) => {
        if (e.NewState is ChatConnectionState.ConnectedReadWrite
            && Interlocked.CompareExchange(ref _connectAnnounced, 1, 0) == 0) {
            _ = Chat.SendMessageAsync(
                $"slay-the-streamer-2 v{modVersion} connected — votes will go to #{settings.Channel}",
                OutgoingMessagePriority.High);
        }
    };

    _ = Chat.ConnectAsync(settings.Channel, settings.Credentials, _modCts.Token);
}
```

- [ ] **Step 35.3: Build**

```bash
dotnet build src/slay_the_streamer_2.csproj --nologo
```
Expected: builds without errors.

- [ ] **Step 35.4: Run tests to confirm Plan A regression still green**

```bash
dotnet test tests/slay_the_streamer_2.tests.csproj --nologo
```
Expected: all pass.

- [ ] **Step 35.5: Commit**

```powershell
git add src/ModEntry.cs
git commit -m "plan-b-1/7.1: ModEntry — settings load + chat + Voter.Default wiring"
```

---

## Phase 8: Operator validation & build artefacts

### Task 36: Build + install dist for in-game testing

**Files:**
- (Operator-validation only — no code changes)

- [ ] **Step 36.1: Build the dist artefact**

```powershell
pwsh -File build.ps1
```
Expected output ends with `Plan B prep build cycle: OK` (or similar success line).

- [ ] **Step 36.2: Install the mod to the game folder**

```powershell
pwsh -File install.ps1
```
Expected: installs to `<game-install>/mods/slay_the_streamer_2/`.

- [ ] **Step 36.3: No commit (build artefacts gitignored)**

---

### Task 37: Operator-validation Step 0 — vanilla baseline

**Pre-condition**: NO `slay_the_streamer_2.json` file exists at `%APPDATA%\Godot\app_userdata\Slay the Spire 2\` (or platform equivalent). If it exists, rename it temporarily.

- [ ] **Step 37.1: Launch StS2 with the mod installed**

- [ ] **Step 37.2: In the mod log file (`%APPDATA%\Godot\app_userdata\Slay the Spire 2\logs\godot.log` or similar), confirm:**
  - `[slay_the_streamer_2] mod loading...`
  - `[slay_the_streamer_2] no settings file at ...; mod loaded but Twitch not connected.`
  - `[slay_the_streamer_2] target resolved: MegaCrit.Sts2.Core.Nodes.Rooms.NEventRoom.OptionButtonClicked`
  - `[slay_the_streamer_2] Init complete`

- [ ] **Step 37.3: Start a new run; reach Neow; click any blessing button**

Expected: blessing applies immediately as in vanilla (no chat vote opens, no `VoteTallyLabel` appears, no chat receipts because chat isn't connected).

- [ ] **Step 37.4: Note any anomalies in `notes/06-followups-and-deferred.md` (open it now, don't wait)**

- [ ] **Step 37.5: Commit notes update if any**

```powershell
git add notes/06-followups-and-deferred.md
git commit -m "plan-b-1/8.1: notes/06 — Step 0 vanilla-baseline operator-validation results"
```

---

### Task 38: Operator-validation Step 1 — IRC alone (throwaway channel)

**Pre-condition**: Surfinite has a Twitch testbot account with a generated user-access token + an empty/throwaway channel to point the bot at. Create the JSON config:

`%APPDATA%\Godot\app_userdata\Slay the Spire 2\slay_the_streamer_2.json`:
```json
{
    "schemaVersion": 1,
    "channel": "<throwaway-channel-name>",
    "username": "<testbot-login>",
    "oauthToken": "oauth:<actual-token>"
}
```

- [ ] **Step 38.1: Launch StS2 with the JSON file in place**

- [ ] **Step 38.2: In the mod log, confirm**:
  - `[slay_the_streamer_2] settings loaded; channel=#<throwaway-channel-name>`
  - `[TwitchIrcChatService] target resolved...` (or similar — verify the chat service connected)

- [ ] **Step 38.3: Open Twitch chat (web UI as a viewer) for the throwaway channel; verify the bot's "connected" message appears**:
  - `slay-the-streamer-2 v<version> connected — votes will go to #<throwaway-channel-name>`

- [ ] **Step 38.4: Send a test message from a second Twitch account or web chat into the throwaway channel; verify nothing crashes** (the mod log won't show PRIVMSGs since no vote is open, but should also not log errors)

- [ ] **Step 38.5: Document the exact oauth-token-generator URL + scope combination used in the spec's "Twitch oauth setup" section**

Edit `docs/superpowers/specs/2026-05-09-plan-b-1-vertical-slice-design-v3.md` to fill in the placeholder `[a specific generator]` and `[the scopes that actually worked]` lines based on what the operator-validation actually used.

- [ ] **Step 38.6: Commit notes + spec update**

```powershell
git add docs/superpowers/specs/2026-05-09-plan-b-1-vertical-slice-design-v3.md notes/06-followups-and-deferred.md
git commit -m "plan-b-1/8.2: Step 1 IRC operator-validation green; spec updated with tested oauth setup"
```

---

### Task 39: Operator-validation Step 2 — full Neow vote

**Pre-condition**: Step 1 green; settings JSON in place pointing to a channel where the operator can send chat messages.

- [ ] **Step 39.1: Launch StS2; start a new run; wait for connection receipt in chat**

- [ ] **Step 39.2: Reach Neow; click ANY blessing button**

Expected:
- `VoteTallyLabel` appears in the top-right of the game window showing all 3 blessing options + counts (initially 0) + 30s remaining countdown.
- Chat receipt posted: `Vote: Neow's Bonus! Type 0, 1, 2 — 30s left.` (or similar — exact format per Plan A's `EnglishReceipts`).
- All 3 blessing buttons in the game UI are visibly disabled (greyed out).
- Mod log: `[neow-vote] opening vote for 3 options; player clicked #N`.

- [ ] **Step 39.3: Verify z-order — the `VoteTallyLabel` should render ABOVE the Neow UI, not behind it. If behind, document and switch `VoteTallyLabel.AttachTo` to use a `CanvasLayer` overlay instead of direct root attachment.**

- [ ] **Step 39.4: From a second client, send `#0`, `#1`, `#2` messages; verify the tally label updates in-game**

- [ ] **Step 39.5: Wait for 30s vote to close**

Expected:
- Close receipt posted to chat (e.g., `Chat chose 1: <blessing label>.`).
- Winning blessing applies; game proceeds to next phase.
- `VoteTallyLabel` disappears.
- Mod log: `[neow-vote] resume: applying winner #N on main thread`.

- [ ] **Step 39.6: Repeat with no chat input (let the 30s elapse with zero `#N` messages)**

Expected:
- Random option selected per Plan A's no-voter behavior.
- Close receipt: `No votes received — chat got N: <label> randomly.`
- Game proceeds normally.

- [ ] **Step 39.7: Repeat with rapid streamer-clicks during the vote**

Expected: clicks no-op visibly (buttons disabled); mod log shows `[neow-vote] repeat click during open vote — suppressed` (Debug-level).

- [ ] **Step 39.8: Document results in notes/06 + commit**

```powershell
git add notes/06-followups-and-deferred.md
git commit -m "plan-b-1/8.3: Step 2 full Neow vote operator-validation green"
```

---

### Task 40: Operator-validation Step 3 — failure modes

For each failure mode, set up the precondition, launch the game, reach Neow, verify the documented behavior, then restore.

- [ ] **Step 40.1: Bad oauth**
  - Edit JSON: change `oauthToken` to `oauth:invalidtoken`.
  - Launch; check log shows `AuthenticationFailed`.
  - Reach Neow; verify game plays vanilla (no vote opens; click applies immediately).
  - Restore the JSON.

- [ ] **Step 40.2: Mid-vote disconnect**
  - JSON valid; launch; reach Neow; click blessing.
  - During the 30s vote, manually disconnect Wi-Fi (or block port 6697 with `netsh advfirewall firewall add rule name=block-irc dir=out action=block protocol=TCP remoteport=6697`).
  - Restore connectivity within ~15s.
  - Verify reconnect happens, post-reconnect votes tally, close receipt notes the disconnect gap (e.g., `... (chat was offline 8s during voting).`).
  - Cleanup: `netsh advfirewall firewall delete rule name=block-irc`.

- [ ] **Step 40.3: Streamer escape mid-vote**
  - JSON valid; launch; reach Neow; click blessing.
  - During the 30s vote, attempt to escape/back out if the game allows.
  - Verify: no crash; mod log says resume dropped (e.g., `[neow-vote] resume: room no longer valid; dropping resume`).
  - Re-entering Neow on next visit (or new run): blessing buttons re-enabled and functional.

- [ ] **Step 40.4: Multiplayer bail-out (if reachable in v0.1)**
  - If multiplayer is hard to reach: skip this step and note "test-only-validated" in notes/06.

- [ ] **Step 40.5: Commit results**

```powershell
git add notes/06-followups-and-deferred.md
git commit -m "plan-b-1/8.4: Step 3 failure-mode operator-validation green"
```

---

### Task 41: Final B.1 outcome capture in notes/06 + tag

**Files:**
- Modify: `notes/06-followups-and-deferred.md`

- [ ] **Step 41.1: Add a "Plan B.1 outcome" section to notes/06 summarising results**

Mirror the format used for "Pre-Plan-B prep (resolved)" in the existing notes/06. Include:
- B.1 acceptance gate items each marked ✓ or ⚠ (with brief reason).
- Any deferred items that surfaced during implementation (going into B.2).
- Operator-validation observations (z-order verdict, oauth scope/generator combo, etc.).
- Key learnings worth preserving for B.2.

- [ ] **Step 41.2: Commit + tag**

```powershell
git add notes/06-followups-and-deferred.md
git commit -m "plan-b-1/8.5: B.1 vertical slice complete — notes/06 outcome captured"
git tag plan-b-1-complete
```

(Tag is optional but useful as a milestone marker.)

---

## Self-Review Notes

Spec coverage check:
- ✓ TwitchIrcChatService — Tasks 12–30
- ✓ VoteCoordinator.Dispatcher — Task 1
- ✓ ModSettings — Tasks 3–10
- ✓ OutgoingMessageQueue 1-msg/sec spacing — Task 11
- ✓ VoteTallyLabel — Task 31
- ✓ NeowBlessingVotePatch — Tasks 32–34
- ✓ ModEntry wiring — Task 35
- ✓ Operator validation steps 0/1/2/3 — Tasks 37–40
- ✓ notes/06 update — Task 41
- ✓ JOIN-confirmation timeout — Task 22
- ✓ CAP NAK fallback — Task 16
- ✓ Self-echo guard with login fallback — Task 18
- ✓ NOTICE msg_ratelimit/slowmode/duplicate — Task 23
- ✓ Anonymous mode — Task 24
- ✓ Reconnect with backoff+jitter — Task 25
- ✓ Disconnect/Dispose semantics — Tasks 27–28
- ✓ RECONNECT command — Task 29
- ✓ SslIrcTransport (production TLS) — Task 30

Spec items deferred (per spec's own deferrals, not gaps):
- BBCode-stripping for chat receipts → B.2 (spec acknowledged limit).
- Polished VoteOverlayControl → B.2.
- ChatStatusControl → B.2.
- Settings UI → B.2.
- 4 other Harmony patches (card reward, boss relic, map path, act-boss) → B.2/B.3.

Type-consistency check:
- `VoteCoordinator.Start(label, options, duration)` signature confirmed (`tests/Voting/VoteCoordinatorTests.cs` exists; existing tests verify the signature).
- `OutgoingMessageQueue` constructor — Task 11 adds an alternate ctor matching the test's parameter ordering, and the original ctor stays compatible.
- `NEventRoom.OptionButtonClicked(EventOption, int)` confirmed via decompile; `Prepare` validates this at install time.
- `EventOption.Title.GetFormattedText()` confirmed via decompile.

Placeholder scan:
- No "TBD" / "TODO" / "implement later" tokens in steps.
- One forward reference: Task 14's `ProcessIncomingLine` has a comment `// TODO Task 16: fall back to no-tags mode` and similar pointers — these resolve in the labelled tasks.
- Operator-validation Task 38 has a `[a specific generator]` placeholder in the spec to be filled in DURING the validation (this is intentional — we don't know what worked until we test it).

---

## Execution Handoff

**Plan complete and saved to `docs/superpowers/plans/2026-05-09-plan-b-1-vertical-slice.md`. Two execution options:**

**1. Subagent-Driven (recommended)** — I dispatch a fresh subagent per task, review between tasks, fast iteration. Best for a 41-task plan because review-per-task catches drift early.

**2. Inline Execution** — Execute tasks in this session using executing-plans, batch execution with checkpoints. Slower but keeps everything in one conversation.

**Which approach?**
