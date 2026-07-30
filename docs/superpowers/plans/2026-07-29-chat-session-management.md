# Chat Session Management Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add device-scoped saved-chat browsing, rename, resume, removal, center chat tabs, and a collapsible right session dock.

**Architecture:** Extend the existing per-device JSON session header with a backward-compatible title and keep all persistence rules in `SessionManager`. Expose rename through the existing compatibility API, then add focused Studio session-state utilities, a center chat workspace, and a right dock integrated with `MainStudio`.

**Tech Stack:** .NET 8, ASP.NET Core minimal APIs, xUnit, React 19, TypeScript, Vitest, Tailwind CSS.

---

### Task 1: Persist and derive session titles

**Files:**
- Modify: `src/Agent/Chat/SessionFileFormat.cs`
- Modify: `src/Agent/Chat/SessionManager.cs`
- Test: `tests/Agent.Tests/SessionManagerTests.cs`

- [ ] **Step 1: Write failing title tests**

Add tests proving that a new session has `New chat`, legacy metadata derives a
title from the first user message, rename trims and persists the title, an empty
rename throws `ArgumentException`, and `ListSessions` orders by `UpdatedAt`
descending.

```csharp
[Fact]
public void RenameSession_trims_and_persists_title()
{
    var device = CreateDeviceContext();
    var created = SessionManager.CreateNewSession(device, new ChatRequestSettings(), null);

    var renamed = SessionManager.RenameSession(device, created.Header.SessionId, "  Valve diagnosis  ");

    Assert.Equal("Valve diagnosis", renamed!.Header.Title);
    Assert.Equal("Valve diagnosis",
        SessionManager.LoadSession(device, created.Header.SessionId)!.Header.Title);
}
```

- [ ] **Step 2: Run tests and verify RED**

Run:
`dotnet test tests/Agent.Tests/Agent.Tests.csproj --filter "FullyQualifiedName~SessionManagerTests"`

Expected: compilation failures because `Title` and `RenameSession` do not exist.

- [ ] **Step 3: Add the title model and persistence behavior**

Add `string? Title` as the final positional member of `ChatSessionHeader` with a
default value of `null`, and add `string Title` to `ChatSessionInfo`.
Create sessions with `Title: "New chat"`. Implement:

```csharp
public static ChatSessionData? RenameSession(
    DeviceContext device, string sessionId, string title)
{
    ArgumentNullException.ThrowIfNull(device);
    var normalized = title?.Trim();
    if (string.IsNullOrWhiteSpace(normalized))
        throw new ArgumentException("Session title cannot be blank.", nameof(title));
    var data = LoadSession(device, sessionId);
    if (data is null) return null;
    var updated = data with
    {
        Header = data.Header with
        {
            Title = normalized,
            UpdatedAt = DateTimeOffset.UtcNow.ToString("O"),
        },
    };
    SaveSession(device, updated);
    return updated;
}
```

When reading list metadata, use a trimmed stored title, otherwise derive a
single-line title from the first user message, limited to 60 characters, and
otherwise return `New chat`. Sort by `UpdatedAt`, then `CreatedAt`, descending.

- [ ] **Step 4: Run tests and verify GREEN**

Run:
`dotnet test tests/Agent.Tests/Agent.Tests.csproj --filter "FullyQualifiedName~SessionManagerTests"`

Expected: all `SessionManagerTests` pass.

- [ ] **Step 5: Commit**

```powershell
git add src/Agent/Chat/SessionFileFormat.cs src/Agent/Chat/SessionManager.cs tests/Agent.Tests/SessionManagerTests.cs
git commit -m "feat(agent): persist chat session titles"
```

### Task 2: Add rename and automatic first-message titles to the API

**Files:**
- Modify: `src/ApiHost/CompatibilityEndpoints.cs`
- Test: `tests/ApiHost.Tests/WorkbenchEndpointsTests.cs`

- [ ] **Step 1: Write failing API tests**

Extend the chat-session endpoint test to rename a created session, assert the
trimmed title appears in `/api/chat/sessions`, reject a blank title with 400,
and return 404 for a missing session.

```csharp
var rename = await client.PostAsJsonAsync(
    "/api/chat/session/rename",
    new { sessionId, title = "  Startup checks  " });
rename.EnsureSuccessStatusCode();
Assert.Equal("Startup checks",
    (await rename.Content.ReadFromJsonAsync<JsonElement>())
        .GetProperty("header").GetProperty("title").GetString());
```

Add an `ApiChatService` unit/integration assertion that after the first
successful message, a session still titled `New chat` receives a derived title,
while a manually renamed session does not.

- [ ] **Step 2: Run tests and verify RED**

Run:
`dotnet test tests/ApiHost.Tests/ApiHost.Tests.csproj --filter "FullyQualifiedName~WorkbenchEndpointsTests"`

Expected: rename endpoint returns 404 and auto-title assertions fail.

- [ ] **Step 3: Implement rename and auto-title**

Map `POST /api/chat/session/rename`, requiring `sessionId` and `title`. Delegate
to a new `ApiChatService.RenameSession` method so cached and pending session
copies are updated alongside the file. Return 404 when the session is absent.

After a successful `RunAsync`, derive the title only when the current title is
blank or exactly `New chat`:

```csharp
var title = SessionManager.IsDefaultTitle(active.Session.Header.Title)
    ? SessionManager.DeriveTitle(message)
    : active.Session.Header.Title;
```

Persist that title in the same save that persists messages and `UpdatedAt`.

- [ ] **Step 4: Run tests and verify GREEN**

Run:
`dotnet test tests/ApiHost.Tests/ApiHost.Tests.csproj --filter "FullyQualifiedName~WorkbenchEndpointsTests"`

Expected: all endpoint tests pass.

- [ ] **Step 5: Commit**

```powershell
git add src/ApiHost/CompatibilityEndpoints.cs tests/ApiHost.Tests/WorkbenchEndpointsTests.cs
git commit -m "feat(api): rename and title chat sessions"
```

### Task 3: Add typed Studio session API operations

**Files:**
- Modify: `studio/src/api/client.ts`
- Create: `studio/src/api/chat-sessions.test.ts`

- [ ] **Step 1: Write failing client tests**

Mock `fetch` and assert that list, load, rename, and delete use the selected
device-bound endpoints and bodies. Include `title` in `ChatSessionInfo` and full
session messages in `ChatSessionResponse`.

```typescript
it('renames a session with the normalized API request', async () => {
  vi.stubGlobal('fetch', vi.fn().mockResolvedValue(
    new Response(JSON.stringify({ header: { sessionId: 's1', title: 'Valves' }, messages: [] }),
      { status: 200, headers: { 'Content-Type': 'application/json' } }),
  ))
  await renameChatSession('s1', 'Valves')
  expect(fetch).toHaveBeenCalledWith(
    '/api/chat/session/rename',
    expect.objectContaining({ method: 'POST', body: JSON.stringify({ sessionId: 's1', title: 'Valves' }) }),
  )
})
```

- [ ] **Step 2: Run tests and verify RED**

Run: `npm test -- src/api/chat-sessions.test.ts --run`

Expected: compilation failure because `renameChatSession` is missing.

- [ ] **Step 3: Implement the typed client**

Update session types to include `title`, `updatedAt`, `messages`, and header
metadata. Remove obsolete `projectName` parameters from chat-session methods
because context is selected server-side. Add:

```typescript
export async function renameChatSession(
  sessionId: string,
  title: string,
): Promise<ChatSessionData> {
  return requestJson('/chat/session/rename', {
    method: 'POST',
    body: JSON.stringify({ sessionId, title }),
  })
}
```

Keep load/delete/new/list behavior on their existing compatibility endpoints.

- [ ] **Step 4: Run tests and verify GREEN**

Run: `npm test -- src/api/chat-sessions.test.ts --run`

Expected: all chat-session client tests pass.

- [ ] **Step 5: Commit in the Studio repository**

```powershell
git add src/api/client.ts src/api/chat-sessions.test.ts
git commit -m "feat(ui): add typed chat session API"
```

### Task 4: Implement deterministic open-tab state

**Files:**
- Create: `studio/src/studio/chat/chatTabState.ts`
- Create: `studio/src/studio/chat/chatTabState.test.ts`

- [ ] **Step 1: Write failing pure-state tests**

Test opening a new session, focusing an existing one without duplication,
tracking most-recent use, removing the active tab with MRU fallback, and clearing
all tabs on device change.

```typescript
it('focuses an existing tab without duplicating it', () => {
  const first = openTab(emptyChatTabs(), session('s1'))
  const next = openTab(first, session('s1'))
  expect(next.tabs).toHaveLength(1)
  expect(next.activeId).toBe('s1')
})
```

- [ ] **Step 2: Run tests and verify RED**

Run: `npm test -- src/studio/chat/chatTabState.test.ts --run`

Expected: module-not-found failure.

- [ ] **Step 3: Implement the reducer helpers**

Define:

```typescript
export type ChatTab = { sessionId: string; title: string; messages: ChatMessage[] }
export type ChatTabsState = { tabs: ChatTab[]; activeId: string | null; mru: string[] }
export const emptyChatTabs = (): ChatTabsState => ({ tabs: [], activeId: null, mru: [] })
export function openTab(state: ChatTabsState, session: ChatSessionData): ChatTabsState
export function renameTab(state: ChatTabsState, sessionId: string, title: string): ChatTabsState
export function closeTab(state: ChatTabsState, sessionId: string): ChatTabsState
```

`openTab` replaces stored session history with the loaded response and moves the
ID to the front of `mru`. `closeTab` chooses the first remaining MRU ID.

- [ ] **Step 4: Run tests and verify GREEN**

Run: `npm test -- src/studio/chat/chatTabState.test.ts --run`

Expected: all tab-state tests pass.

- [ ] **Step 5: Commit**

```powershell
git add src/studio/chat/chatTabState.ts src/studio/chat/chatTabState.test.ts
git commit -m "feat(ui): model resumable chat tabs"
```

### Task 5: Build the right session dock and center chat workspace

**Files:**
- Create: `studio/src/studio/chat/SessionDock.tsx`
- Create: `studio/src/studio/chat/SessionDock.test.tsx`
- Create: `studio/src/studio/chat/ChatWorkspace.tsx`
- Create: `studio/src/studio/chat/ChatWorkspace.test.tsx`

- [ ] **Step 1: Write failing component tests**

Use `happy-dom` with React DOM to verify:

- session click calls `onActivate(sessionId)`;
- inline rename submits a trimmed non-empty title;
- delete requires confirmation before `onRemove`;
- every open pane stays mounted while only the active pane is visible;
- send is disabled while activation or message submission is busy.

```tsx
expect(container.querySelector('[data-session-pane="s1"]')).not.toBeNull()
expect(container.querySelector('[data-session-pane="s2"]')).not.toBeNull()
expect(container.querySelector('[data-session-pane="s1"]')).toHaveProperty('hidden', true)
```

- [ ] **Step 2: Run tests and verify RED**

Run:
`npm test -- src/studio/chat/SessionDock.test.tsx src/studio/chat/ChatWorkspace.test.tsx --run`

Expected: module-not-found failures.

- [ ] **Step 3: Implement focused components**

`SessionDock` receives session metadata and callbacks for create, activate,
rename, and remove. Use an inline input for rename and the existing `Dialog`
component for destructive confirmation.

`ChatWorkspace` receives the tab state, activation callback, and send callback.
Render a tab strip, then map every tab to:

```tsx
<section
  data-session-pane={tab.sessionId}
  hidden={tab.sessionId !== activeId}
  className="h-full"
>
  <ChatMessages messages={tab.messages} />
  <ChatComposer disabled={busy || tab.sessionId !== activeId} onSend={onSend} />
</section>
```

Keep message rendering and composer behavior local to this file; do not add a
global state framework.

- [ ] **Step 4: Run tests and verify GREEN**

Run:
`npm test -- src/studio/chat/SessionDock.test.tsx src/studio/chat/ChatWorkspace.test.tsx --run`

Expected: all dock and workspace tests pass.

- [ ] **Step 5: Commit**

```powershell
git add src/studio/chat
git commit -m "feat(ui): add chat workspace and session dock"
```

### Task 6: Integrate session tabs and dock with Main Studio

**Files:**
- Modify: `studio/src/studio/MainStudio.tsx`
- Create: `studio/src/studio/MainStudio.sessions.test.tsx`

- [ ] **Step 1: Write failing integration tests**

Mock the API and verify:

- selecting a device loads only that device's session metadata;
- clicking a saved session loads it before enabling send;
- clicking an already-open session focuses rather than duplicates it;
- switching device resets open tabs;
- rename updates both dock and tab;
- successful delete closes the tab and uses MRU fallback;
- the upper-right title-bar button toggles the dock without destroying it.

- [ ] **Step 2: Run tests and verify RED**

Run: `npm test -- src/studio/MainStudio.sessions.test.tsx --run`

Expected: the title-bar dock toggle and chat workspace are absent.

- [ ] **Step 3: Integrate the components**

Add state:

```typescript
const [chatTabs, setChatTabs] = useState(emptyChatTabs)
const [sessionDockVisible, setSessionDockVisible] = useState(true)
const [chatBusy, setChatBusy] = useState(false)
```

On device selection, clear tabs and fetch session metadata. `activateSession`
must await `loadChatSession` before calling `openTab`. `sendMessage` must first
ensure the visible session is active, then use the existing streaming client and
refresh that tab's history plus session metadata. Rename and delete update UI
state only after successful API responses.

Place the dock-toggle button at the far right of the existing title bar. Render
the main content and `ChatWorkspace` in the center and conditionally allocate
the fixed-width right dock. Keep the dock mounted with `hidden` when collapsed.

- [ ] **Step 4: Run integration and full Studio verification**

Run:

```powershell
npm test -- src/studio/MainStudio.sessions.test.tsx --run
npm test -- --run
npm run lint
npm run build
```

Expected: all tests pass, lint reports no errors, production build succeeds.

- [ ] **Step 5: Commit**

```powershell
git add src/studio/MainStudio.tsx src/studio/MainStudio.sessions.test.tsx
git commit -m "feat(ui): integrate device chat session dock"
```

### Task 7: Full verification and parent gitlink update

**Files:**
- Modify: `studio` gitlink in the parent repository
- Modify: `docs/superpowers/plans/2026-07-29-chat-session-management.md`

- [ ] **Step 1: Run full backend verification**

Run:

```powershell
dotnet test AgentAssistPlcDev.sln
dotnet build AgentAssistPlcDev.sln --no-restore
```

Expected: all tests and projects pass.

- [ ] **Step 2: Run full Studio verification**

Run:

```powershell
npm test -- --run
npm run lint
npm run build
```

Expected: all tests pass, lint reports no errors, build succeeds.

- [ ] **Step 3: Verify diffs and repository cleanliness**

Run in both repositories:

```powershell
git diff --check
git status --short
git log -8 --oneline
```

Expected: no whitespace errors; only the parent `studio` gitlink and checked-off
plan remain uncommitted.

- [ ] **Step 4: Update the parent gitlink and finish the plan**

Mark completed plan steps, then:

```powershell
git add studio docs/superpowers/plans/2026-07-29-chat-session-management.md
git commit -m "feat: add device chat session management"
```

- [ ] **Step 5: Report handoff**

Report the two worktree paths, both branch heads, verification commands/results,
and any intentionally deferred behavior. Do not merge or delete either worktree
without explicit user direction.
