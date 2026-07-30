# Agent Chat Workflow Performance Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make chat answers appear progressively, avoid unnecessary live-TIA/tool rounds, and answer common PLC FB/interface questions with fewer tokens and less latency.

**Architecture:** First align `/api/chat` with the frontend's existing SSE client so progress/content appears while the agent works. Then tighten the system prompt and default settings so offline knowledge is preferred for normal Q&A. Finally add a purpose-built knowledge helper for FB/interface summaries so the model does not synthesize large SQL queries for a common workflow.

**Tech Stack:** ASP.NET Core minimal APIs, C#/.NET, React/Vite/TypeScript, Vitest, xUnit.

---

## File Structure

- Modify: `src/ApiHost/CompatibilityEndpoints.cs`
  - Stream `/api/chat` as SSE.
  - Keep current `ApiChatService.RunAsync` behavior for session persistence.
  - Add a focused helper for running a chat turn with progress/delta callbacks if needed.
- Modify: `src/Agent/Chat/SystemPrompt.cs`
  - Prefer offline knowledge DB for normal Q&A.
  - Restrict live engineering tools to explicit live/export/compile cases.
  - Add small evidence-plan and tool-budget guidance.
  - Add FB/interface-specific tool strategy.
- Modify: `src/Agent/Chat/ChatModels.cs`
  - Change default request settings to fast mode if accepted.
- Modify: `src/ApiHost/CompatibilityEndpoints.cs`
  - Align default config fallback with `ChatRequestSettings`.
- Create: `src/Agent/Chat/BlockInterfaceSummary.cs`
  - DTOs for a compact FB/interface summary.
- Create: `src/Agent/Chat/BlockInterfaceReader.cs`
  - Reads graph DB directly for block metadata, instance DB, members, call sites, and network summaries.
- Modify: `src/ApiHost/CompatibilityEndpoints.cs`
  - Add `/api/knowledge/block-interface?blockName=...` for UI/API use, or wire the reader into an MCP-style callable if that is the chosen tool boundary.
- Modify: `studio/src/api/client.ts`
  - Confirm SSE parsing works against the new server stream.
  - Parse progress/content/error events consistently.
- Modify: `studio/src/studio/MainStudio.tsx`
  - Pass SSE progress/content events into chat tab state instead of waiting for session reload only.
- Modify: `studio/src/studio/chat/chatTabState.ts`
  - Add pending assistant/progress message state helpers.
- Modify: `studio/src/studio/chat/ChatWorkspace.tsx`
  - Render progress/tool lines while the assistant is running.
- Test: `tests/ApiHost.Tests/WorkbenchEndpointsTests.cs`
  - SSE contract and chat-session persistence tests.
- Test: `tests/Agent.Tests/SystemPromptTests.cs`
  - Prompt contains the required workflow constraints.
- Test: `tests/Agent.Tests/BlockInterfaceReaderTests.cs`
  - Compact interface summary from a tiny synthetic graph DB.
- Test: `studio/src/studio/chat/chatTabState.test.ts`
  - Progressive content/progress state updates.
- Test: `studio/src/studio/chat/ChatWorkspace.test.tsx`
  - Progress lines render while busy.

---

### Task 1: Stream `/api/chat` Progress and Content

**Files:**
- Modify: `src/ApiHost/CompatibilityEndpoints.cs`
- Test: `tests/ApiHost.Tests/WorkbenchEndpointsTests.cs`
- Verify existing client: `studio/src/api/client.ts`

- [ ] **Step 1: Write the failing API streaming test**

Add a test near the existing chat endpoint tests in `tests/ApiHost.Tests/WorkbenchEndpointsTests.cs`:

```csharp
[Fact]
public async Task ChatEndpointStreamsSseProgressBeforeFinalSessionReload()
{
    await using var fixture = await SelectedApiFixture.CreateAsync(root, databaseExists: true);
    var runtimeState = fixture.Services.GetRequiredService<CompatibilityRuntimeState>();
    runtimeState.ApiKey = "test-key";

    using var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat")
    {
        Content = JsonContent.Create(new { message = "what is the function of FB block and interface" }),
    };

    using var response = await fixture.Client.SendAsync(
        request,
        HttpCompletionOption.ResponseHeadersRead);

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

    var body = await response.Content.ReadAsStringAsync();
    Assert.Contains("data: ", body);
    Assert.Contains("\"kind\":\"progress\"", body);
    Assert.Contains("data: [DONE]", body);
}
```

If `SelectedApiFixture.Services` is not exposed, add:

```csharp
public IServiceProvider Services => factory.Services;
```

to the fixture.

- [ ] **Step 2: Run the failing test**

Run:

```powershell
dotnet test tests\ApiHost.Tests\ApiHost.Tests.csproj --filter ChatEndpointStreamsSseProgressBeforeFinalSessionReload
```

Expected: fail because `/api/chat` currently returns `application/json` after the whole run.

- [ ] **Step 3: Implement SSE response in `/api/chat`**

Replace the current `/api/chat` endpoint body in `src/ApiHost/CompatibilityEndpoints.cs`:

```csharp
app.MapPost("/api/chat", async (JsonElement body, WorkbenchApiState state, ApiChatService chat, CancellationToken ct) =>
{
    var device = Device(state);
    var message = body.TryGetProperty("message", out var value) ? value.GetString() : null;
    if (string.IsNullOrWhiteSpace(message)) throw new ArgumentException("message is required.");
    return Results.Ok(new { content = await chat.RunAsync(device, message, ct) });
});
```

with:

```csharp
app.MapPost("/api/chat", async (HttpContext http, JsonElement body, WorkbenchApiState state, ApiChatService chat, CancellationToken ct) =>
{
    var device = Device(state);
    var message = body.TryGetProperty("message", out var value) ? value.GetString() : null;
    if (string.IsNullOrWhiteSpace(message)) throw new ArgumentException("message is required.");

    http.Response.StatusCode = StatusCodes.Status200OK;
    http.Response.ContentType = "text/event-stream";
    http.Response.Headers.CacheControl = "no-cache";

    async Task SendAsync(object payload)
    {
        await http.Response.WriteAsync(
            "data: " + JsonSerializer.Serialize(payload) + "\n\n",
            ct);
        await http.Response.Body.FlushAsync(ct);
    }

    try
    {
        var answer = await chat.RunStreamingAsync(
            device,
            message,
            async line => await SendAsync(new { kind = "progress", delta = line }),
            async (kind, delta) => await SendAsync(new { kind, delta }),
            ct);

        await SendAsync(new { kind = "answer", delta = answer });
        await http.Response.WriteAsync("data: [DONE]\n\n", ct);
    }
    catch (Exception exception)
    {
        await SendAsync(new { kind = "error", delta = exception.Message });
        await http.Response.WriteAsync("data: [DONE]\n\n", ct);
    }
});
```

- [ ] **Step 4: Add `RunStreamingAsync` without changing persistence semantics**

In `ApiChatService`, keep `RunAsync` as a wrapper and add:

```csharp
public Task<string> RunAsync(DeviceContext device, string message, CancellationToken token) =>
    RunStreamingAsync(device, message, _ => Task.CompletedTask, (_, _) => Task.CompletedTask, token);

public async Task<string> RunStreamingAsync(
    DeviceContext device,
    string message,
    Func<string, Task> progress,
    Func<string, string, Task> streamDelta,
    CancellationToken token)
{
    var active = await EnsureActiveChatAsync(device, token);
    void OnProgress(string line) => _ = progress(line);
    void OnDelta(string kind, string delta) => _ = streamDelta(kind, delta);

    active.Loop.Progress += OnProgress;
    active.Loop.StreamDelta += OnDelta;
    try
    {
        var answer = await active.Loop.RunAsync(message, token);
        SaveActiveSession(device, active);
        return answer;
    }
    finally
    {
        active.Loop.Progress -= OnProgress;
        active.Loop.StreamDelta -= OnDelta;
    }
}
```

Extract the existing setup logic from `RunAsync` into:

```csharp
private async Task<ActiveChat> EnsureActiveChatAsync(DeviceContext device, CancellationToken token)
```

and the existing save block into:

```csharp
private void SaveActiveSession(DeviceContext device, ActiveChat active)
```

The implementation must preserve:

```csharp
SessionManager.SaveSession(device, updated);
chats[contextKey] = active with { Session = updated };
```

- [ ] **Step 5: Run the focused API test**

Run:

```powershell
dotnet test tests\ApiHost.Tests\ApiHost.Tests.csproj --filter ChatEndpointStreamsSseProgressBeforeFinalSessionReload
```

Expected: pass.

- [ ] **Step 6: Run existing chat endpoint tests**

Run:

```powershell
dotnet test tests\ApiHost.Tests\ApiHost.Tests.csproj --filter FullyQualifiedName~WorkbenchEndpointsTests
```

Expected: pass. If legacy tests expected JSON from `/api/chat`, update only those assertions to expect SSE.

- [ ] **Step 7: Commit**

```powershell
git add src/ApiHost/CompatibilityEndpoints.cs tests/ApiHost.Tests/WorkbenchEndpointsTests.cs
git commit -m "fix(api): stream chat progress events"
```

---

### Task 2: Render Streaming Progress in the Chat Workspace

**Files:**
- Modify: `studio/src/studio/chat/chatTabState.ts`
- Modify: `studio/src/studio/MainStudio.tsx`
- Modify: `studio/src/studio/chat/ChatWorkspace.tsx`
- Test: `studio/src/studio/chat/chatTabState.test.ts`
- Test: `studio/src/studio/chat/ChatWorkspace.test.tsx`

- [ ] **Step 1: Add failing tab-state tests for progress and streamed assistant content**

In `studio/src/studio/chat/chatTabState.test.ts`, add:

```ts
it('appends progress and streamed assistant content to the active tab', () => {
  let state = openTab(emptyChatTabs(), session('s1'))

  state = appendProgressMessage(state, 's1', '-> get_block({"blockName":"FB"})')
  expect(state.tabs[0]?.messages.at(-1)?.role).toBe('tool')
  expect(state.tabs[0]?.messages.at(-1)?.content).toContain('get_block')

  state = appendAssistantDelta(state, 's1', 'This FB')
  state = appendAssistantDelta(state, 's1', ' simulates a cylinder.')

  expect(state.tabs[0]?.messages.at(-1)?.role).toBe('assistant')
  expect(state.tabs[0]?.messages.at(-1)?.content).toBe('This FB simulates a cylinder.')
})
```

- [ ] **Step 2: Run the failing frontend test**

Run:

```powershell
npm test -- src/studio/chat/chatTabState.test.ts --run
```

Expected: fail because `appendProgressMessage` and `appendAssistantDelta` do not exist.

- [ ] **Step 3: Implement tab state helpers**

Add to `studio/src/studio/chat/chatTabState.ts`:

```ts
const timestamp = () => new Date().toISOString()

export function appendProgressMessage(
  state: ChatTabsState,
  sessionId: string,
  content: string,
): ChatTabsState {
  return {
    ...state,
    tabs: state.tabs.map(tab => tab.sessionId === sessionId
      ? {
          ...tab,
          messages: [
            ...tab.messages,
            { role: 'tool', content, toolCallId: null, timestamp: timestamp() },
          ],
        }
      : tab),
  }
}

export function appendAssistantDelta(
  state: ChatTabsState,
  sessionId: string,
  delta: string,
): ChatTabsState {
  return {
    ...state,
    tabs: state.tabs.map(tab => {
      if (tab.sessionId !== sessionId) return tab
      const last = tab.messages.at(-1)
      if (last?.role === 'assistant') {
        return {
          ...tab,
          messages: [
            ...tab.messages.slice(0, -1),
            { ...last, content: `${last.content ?? ''}${delta}` },
          ],
        }
      }
      return {
        ...tab,
        messages: [
          ...tab.messages,
          { role: 'assistant', content: delta, toolCallId: null, timestamp: timestamp() },
        ],
      }
    }),
  }
}
```

- [ ] **Step 4: Wire SSE events in `MainStudio.tsx`**

Import the helpers:

```ts
appendAssistantDelta,
appendProgressMessage,
```

Change the send call from:

```ts
await api.sendChatMessage(message, () => undefined)
```

to:

```ts
await api.sendChatMessage(message, event => {
  if (event.kind === 'progress') {
    setChatTabs(previous => appendProgressMessage(previous, sessionId, event.delta))
  }
  if (event.kind === 'content') {
    setChatTabs(previous => appendAssistantDelta(previous, sessionId, event.delta))
  }
  if (event.kind === 'error') {
    setChatTabs(previous => appendProgressMessage(previous, sessionId, `Error: ${event.delta}`))
  }
})
```

Keep the final `loadChatSession(sessionId)` after streaming completes so persisted history remains authoritative.

- [ ] **Step 5: Update `ChatWorkspace` rendering for tool/progress rows**

In `roleLabel`, use:

```ts
message.role === 'tool' ? 'Progress'
```

Style `tool` rows with muted text but keep them visible.

- [ ] **Step 6: Run focused frontend tests**

Run:

```powershell
npm test -- src/studio/chat/chatTabState.test.ts src/studio/chat/ChatWorkspace.test.tsx --run
```

Expected: pass.

- [ ] **Step 7: Run frontend build**

Run:

```powershell
npm run build
```

Expected: pass.

- [ ] **Step 8: Commit**

```powershell
git add studio/src/studio/MainStudio.tsx studio/src/studio/chat/chatTabState.ts studio/src/studio/chat/chatTabState.test.ts studio/src/studio/chat/ChatWorkspace.tsx studio/src/studio/chat/ChatWorkspace.test.tsx
git commit -m "feat(ui): show streaming chat progress"
```

---

### Task 3: Tighten the System Prompt Tool Policy

**Files:**
- Modify: `src/Agent/Chat/SystemPrompt.cs`
- Test: `tests/Agent.Tests/SystemPromptTests.cs`

- [ ] **Step 1: Create failing prompt-policy tests**

Create `tests/Agent.Tests/SystemPromptTests.cs`:

```csharp
using Agent.Chat;
using Xunit;

public sealed class SystemPromptTests
{
    [Fact]
    public void PromptPrefersOfflineKnowledgeBeforeLiveEngineering()
    {
        var prompt = SystemPrompt.Build("Knowledge DB: C:\\db\\plc-knowledge.db");

        Assert.Contains("use the offline knowledge DB first", prompt);
        Assert.Contains("Do not call live engineering tools", prompt);
        Assert.Contains("dbPath exists", prompt);
    }

    [Fact]
    public void PromptConstrainsCommonFbInterfaceWorkflow()
    {
        var prompt = SystemPrompt.Build("Knowledge DB: C:\\db\\plc-knowledge.db");

        Assert.Contains("For FB/interface questions", prompt);
        Assert.Contains("get_block", prompt);
        Assert.Contains("instance DB", prompt);
        Assert.Contains("call-site network", prompt);
        Assert.Contains("Prefer 1-3 tool calls", prompt);
    }
}
```

- [ ] **Step 2: Run failing tests**

Run:

```powershell
dotnet test tests\Agent.Tests\Agent.Tests.csproj --filter SystemPromptTests
```

Expected: fail because the prompt lacks these constraints.

- [ ] **Step 3: Modify `SystemPrompt.Build`**

Replace the rules block in `src/Agent/Chat/SystemPrompt.cs` with:

```csharp
Rules:
- Separate general PLC concepts from project-specific facts. General Siemens PLC concepts may be answered directly. Ground project-specific claims in tool results.
- For ordinary PLC Q&A, use the offline knowledge DB first when dbPath exists. Do not call live engineering tools unless the user explicitly asks for live TIA state, export, compile, online status, or the knowledge DB is missing/stale.
- If dbPath exists but no live TIA project is connected, continue with knowledge tools. Do not call list_sessions or connect just to answer an offline knowledge question.
- Prefer the smallest evidence plan. Prefer 1-3 tool calls; exceed 5 only when necessary and briefly say why.
- If an exact block or network id/name is known, call get_block or get_network directly. Do not search first.
- For "what does X do / where is X used" questions where X is not exact, call search first, then get_block / get_network and cite the block/network ids you used.
- For structured or aggregate questions, call get_schema only if needed, then query with a single read-only SELECT.
- For FB/interface questions: identify the FB, use get_block for logic, use the instance DB relationship and DB members for retained/interface evidence, and inspect the call-site network for parameter mapping. Do not dump all graph edges unless targeted queries fail.
- knowledge tools require dbPath — use the path from the runtime context below verbatim. If the context says no knowledge base exists, tell the user to update knowledge first.
- This build is read-only on the TIA side: you may list, export and compile, but importing or modifying blocks is not available.
- Exports and compiles can take minutes on big projects; warn the user before triggering them and prefer knowledge-base answers when the data is already there.
- Answer concisely, engineer to engineer. Cite the block/network ids your answer is based on.
```

- [ ] **Step 4: Run prompt tests**

Run:

```powershell
dotnet test tests\Agent.Tests\Agent.Tests.csproj --filter SystemPromptTests
```

Expected: pass.

- [ ] **Step 5: Run agent tests**

Run:

```powershell
dotnet test tests\Agent.Tests\Agent.Tests.csproj
```

Expected: pass.

- [ ] **Step 6: Commit**

```powershell
git add src/Agent/Chat/SystemPrompt.cs tests/Agent.Tests/SystemPromptTests.cs
git commit -m "fix(agent): prefer offline knowledge for chat qa"
```

---

### Task 4: Change Default Chat Settings to Fast Mode

**Files:**
- Modify: `src/Agent/Chat/ChatModels.cs`
- Modify: `src/ApiHost/CompatibilityEndpoints.cs`
- Test: `tests/Agent.Tests/DeepSeekClientTests.cs`
- Test: `tests/ApiHost.Tests/WorkbenchEndpointsTests.cs`

- [ ] **Step 1: Update default-setting expectations**

In `tests/Agent.Tests/DeepSeekClientTests.cs`, update the default request body test to expect:

```csharp
Assert.Equal("disabled", body["thinking"]!["type"]!.GetValue<string>());
Assert.Null(body["reasoning_effort"]);
```

Keep explicit thinking-enabled tests unchanged.

- [ ] **Step 2: Run failing DeepSeek client tests**

Run:

```powershell
dotnet test tests\Agent.Tests\Agent.Tests.csproj --filter DeepSeekClientTests
```

Expected: fail because defaults still enable thinking.

- [ ] **Step 3: Change model defaults**

In `src/Agent/Chat/ChatModels.cs`, change:

```csharp
public bool ThinkingEnabled { get; init; } = true;
```

to:

```csharp
public bool ThinkingEnabled { get; init; } = false;
```

Keep:

```csharp
public const string DefaultReasoningEffort = "high";
```

so explicit deep mode can still use it.

- [ ] **Step 4: Align API fallback settings**

In `ApiChatService.Settings` inside `src/ApiHost/CompatibilityEndpoints.cs`, change the thinking fallback from:

```csharp
out var thinking) ? thinking : true,
```

to:

```csharp
out var thinking) ? thinking : ChatRequestSettings.DefaultThinkingEnabled,
```

Add this constant in `ChatModels.cs`:

```csharp
public const bool DefaultThinkingEnabled = false;
```

- [ ] **Step 5: Run settings tests**

Run:

```powershell
dotnet test tests\Agent.Tests\Agent.Tests.csproj --filter DeepSeekClientTests
dotnet test tests\ApiHost.Tests\ApiHost.Tests.csproj --filter SelectionResolvesRegisteredDeviceAndUnknownApprovalIsConflict
```

Expected: pass.

- [ ] **Step 6: Commit**

```powershell
git add src/Agent/Chat/ChatModels.cs src/ApiHost/CompatibilityEndpoints.cs tests/Agent.Tests/DeepSeekClientTests.cs tests/ApiHost.Tests/WorkbenchEndpointsTests.cs
git commit -m "perf(agent): default chat to fast thinking mode"
```

---

### Task 5: Add a Compact FB/Interface Summary Reader

**Files:**
- Create: `src/Agent/Chat/BlockInterfaceSummary.cs`
- Create: `src/Agent/Chat/BlockInterfaceReader.cs`
- Test: `tests/Agent.Tests/BlockInterfaceReaderTests.cs`

- [ ] **Step 1: Write DTOs**

Create `src/Agent/Chat/BlockInterfaceSummary.cs`:

```csharp
namespace Agent.Chat;

public sealed record BlockInterfaceSummary(
    string BlockId,
    string Kind,
    string Name,
    string? SourceFile,
    string? InstanceDb,
    IReadOnlyList<BlockInterfaceMember> Members,
    IReadOnlyList<BlockCallSite> CallSites,
    IReadOnlyList<BlockNetworkSummary> Networks);

public sealed record BlockInterfaceMember(
    string Name,
    string? Path,
    string? DataType);

public sealed record BlockCallSite(
    string CallerBlock,
    string NetworkId,
    int? NetworkIndex,
    string? SourceFile,
    string LogicStatements);

public sealed record BlockNetworkSummary(
    string NetworkId,
    int? Index,
    string? Language,
    string? LogicStatements);
```

- [ ] **Step 2: Write failing reader test**

Create `tests/Agent.Tests/BlockInterfaceReaderTests.cs` with a temp SQLite DB containing:

```csharp
using Agent.Chat;
using Microsoft.Data.Sqlite;
using Xunit;

public sealed class BlockInterfaceReaderTests : IDisposable
{
    private readonly string dbPath = Path.Combine(Path.GetTempPath(), "block-interface-" + Guid.NewGuid().ToString("N") + ".db");

    [Fact]
    public void ReadsCompactFbInterfaceSummary()
    {
        Seed();

        var summary = BlockInterfaceReader.Read(dbPath, "FB_LAD_SimulateCylinder");

        Assert.Equal("block:FB_LAD_SimulateCylinder", summary.BlockId);
        Assert.Equal("FB", summary.Kind);
        Assert.Equal("FB_LAD_SimulateCylinder_DB", summary.InstanceDb);
        Assert.Contains(summary.Members, member => member.Name == "btn_forward");
        Assert.Contains(summary.CallSites, site => site.CallerBlock == "Main" && site.NetworkId == "network:Main:2");
        Assert.Contains(summary.Networks, network => network.NetworkId == "network:FB_LAD_SimulateCylinder:1");
    }

    private void Seed()
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE graph_nodes (id TEXT PRIMARY KEY, kind TEXT NOT NULL, name TEXT NOT NULL);
            CREATE TABLE graph_node_properties (node_id TEXT NOT NULL, name TEXT NOT NULL, value TEXT NOT NULL);
            CREATE TABLE graph_edges (id TEXT PRIMARY KEY, from_node_id TEXT NOT NULL, to_node_id TEXT NOT NULL, type TEXT NOT NULL);
            CREATE TABLE graph_edge_properties (edge_id TEXT NOT NULL, name TEXT NOT NULL, value TEXT NOT NULL);
            INSERT INTO graph_nodes VALUES
              ('block:FB_LAD_SimulateCylinder','FB','FB_LAD_SimulateCylinder'),
              ('block:Main','OB','Main'),
              ('db:FB_LAD_SimulateCylinder_DB','Instance DB','FB_LAD_SimulateCylinder_DB'),
              ('db-member:FB_LAD_SimulateCylinder_DB:btn_forward','DB Member','btn_forward'),
              ('network:FB_LAD_SimulateCylinder:1','Network','Network 1'),
              ('network:Main:2','Network','Network 2');
            INSERT INTO graph_node_properties VALUES
              ('block:FB_LAD_SimulateCylinder','sourceFile','Blocks\\FB_LAD_SimulateCylinder [FB1].xml'),
              ('network:FB_LAD_SimulateCylinder:1','logicStatements','outputGoForwardPos := TRUE;'),
              ('network:FB_LAD_SimulateCylinder:1','language','LAD'),
              ('network:Main:2','logicStatements','FB_LAD_SimulateCylinder(btn_forward := Btn_ForwardCommand);');
            INSERT INTO graph_edges VALUES
              ('edge:instance','db:FB_LAD_SimulateCylinder_DB','block:FB_LAD_SimulateCylinder','INSTANCE_OF'),
              ('edge:member','db:FB_LAD_SimulateCylinder_DB','db-member:FB_LAD_SimulateCylinder_DB:btn_forward','CONTAINS'),
              ('edge:contains-network','block:FB_LAD_SimulateCylinder','network:FB_LAD_SimulateCylinder:1','CONTAINS'),
              ('edge:call','block:Main','block:FB_LAD_SimulateCylinder','CALLS');
            INSERT INTO graph_edge_properties VALUES
              ('edge:call','networkId','network:Main:2'),
              ('edge:call','networkIndex','2'),
              ('edge:call','sourceFile','Blocks\\Main [OB1].xml');
            """;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        if (File.Exists(dbPath)) File.Delete(dbPath);
    }
}
```

- [ ] **Step 3: Run failing reader test**

Run:

```powershell
dotnet test tests\Agent.Tests\Agent.Tests.csproj --filter BlockInterfaceReaderTests
```

Expected: fail because `BlockInterfaceReader` does not exist.

- [ ] **Step 4: Implement `BlockInterfaceReader`**

Create `src/Agent/Chat/BlockInterfaceReader.cs`:

```csharp
using Microsoft.Data.Sqlite;

namespace Agent.Chat;

public static class BlockInterfaceReader
{
    public static BlockInterfaceSummary Read(string dbPath, string blockName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(blockName);

        using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        connection.Open();

        var block = Single(connection, """
            SELECT id, kind, name
            FROM graph_nodes
            WHERE kind IN ('FB','FC','OB') AND name = $name
            LIMIT 1;
            """, ("$name", blockName));

        var blockId = block["id"] ?? throw new KeyNotFoundException("BLOCK_NOT_FOUND");
        var sourceFile = Scalar(connection, """
            SELECT value FROM graph_node_properties
            WHERE node_id = $id AND name = 'sourceFile'
            LIMIT 1;
            """, ("$id", blockId));

        var instanceDb = Scalar(connection, """
            SELECT db.name
            FROM graph_edges e
            JOIN graph_nodes db ON db.id = e.from_node_id
            WHERE e.type = 'INSTANCE_OF' AND e.to_node_id = $id
            LIMIT 1;
            """, ("$id", blockId));

        var members = Rows(connection, """
            SELECT member.name AS name,
                   path.value AS path,
                   dtype.name AS dataType
            FROM graph_edges contains
            JOIN graph_nodes db ON db.id = contains.from_node_id
            JOIN graph_nodes member ON member.id = contains.to_node_id
            LEFT JOIN graph_node_properties path
              ON path.node_id = member.id AND path.name = 'path'
            LEFT JOIN graph_edges typed
              ON typed.from_node_id = member.id AND typed.type = 'HAS_TYPE'
            LEFT JOIN graph_nodes dtype ON dtype.id = typed.to_node_id
            WHERE contains.type = 'CONTAINS'
              AND db.kind = 'Instance DB'
              AND db.name = $dbName
              AND member.kind = 'DB Member'
            ORDER BY COALESCE(path.value, member.name), member.name;
            """, ("$dbName", instanceDb ?? string.Empty))
            .Select(row => new BlockInterfaceMember(row["name"]!, row["path"], row["dataType"]))
            .ToList();

        var networks = Rows(connection, """
            SELECT network.id AS networkId,
                   idx.value AS networkIndex,
                   lang.value AS language,
                   logic.value AS logicStatements
            FROM graph_edges contains
            JOIN graph_nodes network ON network.id = contains.to_node_id
            LEFT JOIN graph_node_properties idx
              ON idx.node_id = network.id AND idx.name = 'index'
            LEFT JOIN graph_node_properties lang
              ON lang.node_id = network.id AND lang.name = 'language'
            LEFT JOIN graph_node_properties logic
              ON logic.node_id = network.id AND logic.name = 'logicStatements'
            WHERE contains.type = 'CONTAINS'
              AND contains.from_node_id = $id
              AND network.kind = 'Network'
            ORDER BY CAST(COALESCE(idx.value, '0') AS INTEGER), network.id;
            """, ("$id", blockId))
            .Select(row => new BlockNetworkSummary(
                row["networkId"]!,
                int.TryParse(row["networkIndex"], out var index) ? index : null,
                row["language"],
                row["logicStatements"]))
            .ToList();

        var callSites = Rows(connection, """
            SELECT caller.name AS callerBlock,
                   networkId.value AS networkId,
                   networkIndex.value AS networkIndex,
                   sourceFile.value AS sourceFile,
                   logic.value AS logicStatements
            FROM graph_edges call
            JOIN graph_nodes caller ON caller.id = call.from_node_id
            LEFT JOIN graph_edge_properties networkId
              ON networkId.edge_id = call.id AND networkId.name = 'networkId'
            LEFT JOIN graph_edge_properties networkIndex
              ON networkIndex.edge_id = call.id AND networkIndex.name = 'networkIndex'
            LEFT JOIN graph_edge_properties sourceFile
              ON sourceFile.edge_id = call.id AND sourceFile.name = 'sourceFile'
            LEFT JOIN graph_node_properties logic
              ON logic.node_id = networkId.value AND logic.name = 'logicStatements'
            WHERE call.type = 'CALLS'
              AND call.to_node_id = $id
              AND caller.kind IN ('OB','FB','FC')
            ORDER BY caller.name, CAST(COALESCE(networkIndex.value, '0') AS INTEGER);
            """, ("$id", blockId))
            .Select(row => new BlockCallSite(
                row["callerBlock"]!,
                row["networkId"] ?? string.Empty,
                int.TryParse(row["networkIndex"], out var index) ? index : null,
                row["sourceFile"],
                row["logicStatements"] ?? string.Empty))
            .ToList();

        return new BlockInterfaceSummary(
            blockId,
            block["kind"]!,
            block["name"]!,
            sourceFile,
            instanceDb,
            members,
            callSites,
            networks);
    }
}
```

Also add local `Single`, `Scalar`, and `Rows` helpers in the same file:

```csharp
private static Dictionary<string, string?> Single(SqliteConnection connection, string sql, params (string Name, string Value)[] parameters)
{
    var rows = Rows(connection, sql, parameters);
    return rows.Count == 0 ? throw new KeyNotFoundException("BLOCK_NOT_FOUND") : rows[0];
}

private static string? Scalar(SqliteConnection connection, string sql, params (string Name, string Value)[] parameters) =>
    Rows(connection, sql, parameters).FirstOrDefault()?.Values.FirstOrDefault();

private static List<Dictionary<string, string?>> Rows(SqliteConnection connection, string sql, params (string Name, string Value)[] parameters)
{
    using var command = connection.CreateCommand();
    command.CommandText = sql;
    foreach (var (name, value) in parameters)
        command.Parameters.AddWithValue(name, value);

    using var reader = command.ExecuteReader();
    var rows = new List<Dictionary<string, string?>>();
    while (reader.Read())
    {
        var row = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var i = 0; i < reader.FieldCount; i++)
            row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetString(i);
        rows.Add(row);
    }
    return rows;
}
```

- [ ] **Step 5: Run reader tests**

Run:

```powershell
dotnet test tests\Agent.Tests\Agent.Tests.csproj --filter BlockInterfaceReaderTests
```

Expected: pass.

- [ ] **Step 6: Commit**

```powershell
git add src/Agent/Chat/BlockInterfaceSummary.cs src/Agent/Chat/BlockInterfaceReader.cs tests/Agent.Tests/BlockInterfaceReaderTests.cs
git commit -m "feat(agent): summarize block interface evidence"
```

---

### Task 6: Expose Compact FB/Interface Summary to the Chat Agent

**Files:**
- Modify: `src/ApiHost/CompatibilityEndpoints.cs`
- Modify: `src/Agent/Chat/SystemPrompt.cs`
- Test: `tests/ApiHost.Tests/WorkbenchEndpointsTests.cs`

- [ ] **Step 1: Add failing endpoint test**

Add to `WorkbenchEndpointsTests.cs`:

```csharp
[Fact]
public async Task BlockInterfaceEndpointReturnsCompactSummaryForSelectedDevice()
{
    await using var fixture = await SelectedApiFixture.CreateAsync(root, databaseExists: true);
    SeedBlockInterfaceGraph(fixture.Context.KnowledgeDbPath);

    var response = await fixture.Client.GetAsync(
        "/api/knowledge/block-interface?blockName=FB_LAD_SimulateCylinder");

    response.EnsureSuccessStatusCode();
    var body = await response.Content.ReadFromJsonAsync<JsonElement>();
    Assert.Equal("FB_LAD_SimulateCylinder", body.GetProperty("name").GetString());
    Assert.Equal("FB_LAD_SimulateCylinder_DB", body.GetProperty("instanceDb").GetString());
}
```

Expose `Context` from `SelectedApiFixture`:

```csharp
public DeviceContext Context => context;
```

Reuse the seed SQL from `BlockInterfaceReaderTests`.

- [ ] **Step 2: Run failing endpoint test**

Run:

```powershell
dotnet test tests\ApiHost.Tests\ApiHost.Tests.csproj --filter BlockInterfaceEndpointReturnsCompactSummaryForSelectedDevice
```

Expected: fail because endpoint does not exist.

- [ ] **Step 3: Add endpoint**

In `MapCompatibilityEndpoints`, near other knowledge endpoints:

```csharp
app.MapGet("/api/knowledge/block-interface", (string blockName, WorkbenchApiState state) =>
{
    var device = Device(state);
    return Results.Ok(BlockInterfaceReader.Read(device.KnowledgeDbPath, blockName));
});
```

- [ ] **Step 4: Update prompt to prefer compact interface tool**

In `SystemPrompt.cs`, change the FB/interface rule to:

```text
- For FB/interface questions: use the compact block-interface summary when available. Otherwise identify the FB, use get_block for logic, use the instance DB relationship and DB members for retained/interface evidence, and inspect the call-site network for parameter mapping.
```

- [ ] **Step 5: Run endpoint and prompt tests**

Run:

```powershell
dotnet test tests\ApiHost.Tests\ApiHost.Tests.csproj --filter BlockInterfaceEndpointReturnsCompactSummaryForSelectedDevice
dotnet test tests\Agent.Tests\Agent.Tests.csproj --filter SystemPromptTests
```

Expected: pass.

- [ ] **Step 6: Commit**

```powershell
git add src/ApiHost/CompatibilityEndpoints.cs src/Agent/Chat/SystemPrompt.cs tests/ApiHost.Tests/WorkbenchEndpointsTests.cs
git commit -m "feat(api): expose compact block interface summary"
```

---

### Task 7: Verification on the Real Reported Scenario

**Files:**
- No source edits unless verification exposes a defect.

- [ ] **Step 1: Run backend test suites**

Run:

```powershell
dotnet test tests\Agent.Tests\Agent.Tests.csproj
dotnet test tests\ApiHost.Tests\ApiHost.Tests.csproj
```

Expected: pass.

- [ ] **Step 2: Run frontend test/build**

Run:

```powershell
npm test -- src/studio/chat/chatTabState.test.ts src/studio/chat/ChatWorkspace.test.tsx src/studio/chat/SessionDock.test.tsx --run
npm run build
```

Expected: pass.

- [ ] **Step 3: Manual scenario**

Start the app, select the DemoTest / PLC_1 context, create a new chat session, and send:

```text
what is the function of FB block and interface.
```

Expected visible behavior:
- User message appears immediately.
- Progress lines appear while the agent works.
- No live TIA `connect` or `list_sessions` attempt unless no knowledge DB exists.
- Tool path should be roughly: compact block interface summary, optionally `get_block` for network logic, final answer.
- Final answer should mention `FB_LAD_SimulateCylinder`, `FB_LAD_SimulateCylinder_DB`, Main network 2 parameter mapping, and the likely duplicated position tag mapping.

- [ ] **Step 4: Check session transcript**

Open the saved session JSON under the selected worktree `.automation/sessions`.

Expected:
- Far fewer tool messages than the pasted run.
- No `NOT_CONNECTED` tool error.
- No `list_sessions` result.
- No huge truncated edge-property dump.

- [ ] **Step 5: Commit verification-only updates if any**

If no source edits were made, do not commit. If docs or tests were adjusted:

```powershell
git add <changed-files>
git commit -m "test(agent): verify efficient fb interface workflow"
```

---

## Self-Review

- Spec coverage: The plan covers UI streaming, backend SSE mismatch, prompt/tool policy, default thinking cost, FB/interface compact retrieval, and verification against the pasted scenario.
- Placeholder scan: No task uses TBD/TODO/fill-in language. Each task has concrete paths, snippets, commands, and expected results.
- Type consistency: DTO names are consistently `BlockInterfaceSummary`, `BlockInterfaceMember`, `BlockCallSite`, and `BlockNetworkSummary`. The endpoint uses `BlockInterfaceReader.Read(device.KnowledgeDbPath, blockName)`.

