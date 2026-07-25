# MCP Source Editor Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a standalone MCP server that precisely edits approved TIA V17 XML titles, comments, and allowlisted scalar properties while proving that logic and protected structure remain unchanged.

**Architecture:** A thin MCP tool layer calls a typed XML editing pipeline composed of secure loading, inspection/target resolution, allowlisted mutation, protected projection, validation, diffing, and atomic output. Preview and apply use the same pipeline; sibling output is the default, while in-place replacement requires two explicit flags.

**Tech Stack:** C# 12, .NET 8, `System.Xml.Linq`, `System.Xml`, `ModelContextProtocol` 1.4.1, `Microsoft.Extensions.Hosting` 10.0.7, xUnit 2.9.2.

**Approved design:** `buildnote/plan/mcp-sourceeditor.md`

---

## File map

### New production project

- `src/Mcp.SourceEditor/Mcp.SourceEditor.csproj` — net8 executable, MCP/Hosting packages,
  Contracts reference, test internals exposure.
- `src/Mcp.SourceEditor/Program.cs` — stdio MCP host; stderr-only logging.
- `src/Mcp.SourceEditor/Models/EditModels.cs` — edit request, target, enum, normalized edit, and
  batch result records.
- `src/Mcp.SourceEditor/Models/InspectionModels.cs` — block/network/culture/property inspection DTOs.
- `src/Mcp.SourceEditor/Models/ValidationModels.cs` — validation finding/result DTOs.
- `src/Mcp.SourceEditor/Models/DiffModels.cs` — editable-field and protected-difference DTOs.
- `src/Mcp.SourceEditor/Xml/SourceEditorException.cs` — stable error code, remediation, batch index.
- `src/Mcp.SourceEditor/Xml/TiaXmlDocument.cs` — secure load, encoding/declaration retention,
  canonical save, hashes, atomic output.
- `src/Mcp.SourceEditor/Xml/TiaBlockInspector.cs` — identify supported block and enumerate editable
  fields/networks.
- `src/Mcp.SourceEditor/Xml/TargetResolver.cs` — XML-ID/network-number resolution and mismatch checks.
- `src/Mcp.SourceEditor/Xml/MultilingualTextEditor.cs` — culture selection, structure creation, and
  deterministic unused-ID allocation.
- `src/Mcp.SourceEditor/Xml/SafePropertyRegistry.cs` — closed allowlist of safe scalar properties.
- `src/Mcp.SourceEditor/Xml/StructuredEditEngine.cs` — validate and apply an all-or-nothing batch.
- `src/Mcp.SourceEditor/Xml/ProtectedProjection.cs` — remove only editable slots and canonicalize
  everything else.
- `src/Mcp.SourceEditor/Xml/SourceValidator.cs` — standalone validation plus baseline integrity.
- `src/Mcp.SourceEditor/Xml/SourceDiff.cs` — field-level diff and protected-projection comparison.
- `src/Mcp.SourceEditor/Xml/SourceEditorService.cs` — parse/preview/apply/diff/validate orchestration.
- `src/Mcp.SourceEditor/Tools/ToolJson.cs` — structured MCP success/error envelopes.
- `src/Mcp.SourceEditor/Tools/SourceEditorTools.cs` — five MCP tools and path-jail boundary.

### New test project

- `tests/Mcp.SourceEditor.Tests/Mcp.SourceEditor.Tests.csproj` — xUnit project plus linked real XML
  fixtures.
- `tests/Mcp.SourceEditor.Tests/SourceFixture.cs` — isolated temp root, fixture copy helpers, cleanup.
- `tests/Mcp.SourceEditor.Tests/ToolResults.cs` — MCP envelope deserialization helpers.
- `tests/Mcp.SourceEditor.Tests/TiaXmlDocumentTests.cs` — secure loading and serialization.
- `tests/Mcp.SourceEditor.Tests/TiaBlockInspectorTests.cs` — metadata/network/culture inspection.
- `tests/Mcp.SourceEditor.Tests/TargetResolverTests.cs` — ID/number targeting.
- `tests/Mcp.SourceEditor.Tests/MultilingualTextEditorTests.cs` — culture and ID creation.
- `tests/Mcp.SourceEditor.Tests/StructuredEditEngineTests.cs` — typed edit behavior and batch atomicity.
- `tests/Mcp.SourceEditor.Tests/ProtectedProjectionTests.cs` — protected-region invariants.
- `tests/Mcp.SourceEditor.Tests/SourceEditorServiceTests.cs` — sibling/in-place/diff/validate pipeline.
- `tests/Mcp.SourceEditor.Tests/SourceEditorToolsTests.cs` — MCP contract and error envelopes.

### Existing files to modify

- `AgentAssistPlcDev.sln` — add production/test projects beneath existing solution folders.
- `src/Contracts/Sandbox/SandboxPolicy.cs` — classify all five tools.
- `tests/Contracts.Tests/SandboxPolicyTests.cs` — expected tier cases and complete inventory.
- `src/Agent/Mcp/McpHost.cs` — add SourceEditor server connection.
- `src/Agent/Chat/McpToolCatalog.cs` — discover and route SourceEditor tools.
- `src/ApiHost/Program.cs` — resolve/start SourceEditor executable and allow raw API routing.
- `agent.md` — status, solution layout, inventory, safety notes.
- `buildnote/plan/initialLaunch_20260717.md` — record Phase 2.4 completion only after acceptance.
- `buildnote/log/source-editor-20260725.md` — automated and real-TIA evidence.

## Task 1: Scaffold projects and lock sandbox classification

**Files:**

- Create: `src/Mcp.SourceEditor/Mcp.SourceEditor.csproj`
- Create: `src/Mcp.SourceEditor/Program.cs`
- Create: `tests/Mcp.SourceEditor.Tests/Mcp.SourceEditor.Tests.csproj`
- Modify: `AgentAssistPlcDev.sln`
- Modify: `src/Contracts/Sandbox/SandboxPolicy.cs`
- Modify: `tests/Contracts.Tests/SandboxPolicyTests.cs`

- [ ] **Step 1: Add failing sandbox tier cases**

Add these cases to the existing tier theory in `SandboxPolicyTests.cs`:

```csharp
[InlineData("src_parse_block", SandboxTier.Read)]
[InlineData("src_diff", SandboxTier.Read)]
[InlineData("src_validate", SandboxTier.Read)]
[InlineData("src_preview_edits", SandboxTier.Write)]
[InlineData("src_apply_edits", SandboxTier.Write)]
```

Add the same five names to the inventory asserted by
`EveryCurrentMcpToolIsClassified`.

- [ ] **Step 2: Verify the sandbox tests fail**

Run:

```powershell
dotnet test tests\Contracts.Tests\Contracts.Tests.csproj --filter SandboxPolicyTests
```

Expected: failures showing the five tool names are absent from `SandboxPolicy.Defaults`.

- [ ] **Step 3: Classify the tools**

Insert into `SandboxPolicy.Defaults`:

```csharp
// Source editor — inspection and comparison.
["src_parse_block"] = SandboxTier.Read,
["src_diff"] = SandboxTier.Read,
["src_validate"] = SandboxTier.Read,
// Source editor — creates or replaces local XML under jailed roots.
["src_preview_edits"] = SandboxTier.Write,
["src_apply_edits"] = SandboxTier.Write,
```

Update the summary count in the class XML comment from 34 to 39 and adjust its read/write totals
from the dictionary rather than guessing.

- [ ] **Step 4: Create the production project**

Create `Mcp.SourceEditor.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="ModelContextProtocol" Version="1.4.1" />
    <PackageReference Include="Microsoft.Extensions.Hosting" Version="10.0.7" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\Contracts\Contracts.csproj" />
  </ItemGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="Mcp.SourceEditor.Tests" />
  </ItemGroup>
</Project>
```

Create `Program.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services.AddMcpServer().WithStdioServerTransport().WithToolsFromAssembly();
await builder.Build().RunAsync();
```

- [ ] **Step 5: Create the test project**

Create `Mcp.SourceEditor.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Mcp.SourceEditor\Mcp.SourceEditor.csproj" />
  </ItemGroup>
  <ItemGroup>
    <None Include="..\Mcp.Knowledge.Tests\Fixtures\*.xml"
          Link="Fixtures\%(Filename)%(Extension)"
          CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
```

- [ ] **Step 6: Add both projects to the solution**

Run:

```powershell
dotnet sln AgentAssistPlcDev.sln add src\Mcp.SourceEditor\Mcp.SourceEditor.csproj --solution-folder src
dotnet sln AgentAssistPlcDev.sln add tests\Mcp.SourceEditor.Tests\Mcp.SourceEditor.Tests.csproj --solution-folder tests
```

Expected: both commands report that one project was added.

- [ ] **Step 7: Verify scaffold and sandbox**

Run:

```powershell
dotnet test tests\Contracts.Tests\Contracts.Tests.csproj --filter SandboxPolicyTests
dotnet build src\Mcp.SourceEditor\Mcp.SourceEditor.csproj
```

Expected: both commands exit 0.

- [ ] **Step 8: Commit**

```powershell
git add AgentAssistPlcDev.sln src/Contracts/Sandbox/SandboxPolicy.cs tests/Contracts.Tests/SandboxPolicyTests.cs src/Mcp.SourceEditor tests/Mcp.SourceEditor.Tests/Mcp.SourceEditor.Tests.csproj
git commit -m "feat(source-editor): scaffold MCP server"
```

## Task 2: Define stable models and structured errors

**Files:**

- Create: `src/Mcp.SourceEditor/Models/EditModels.cs`
- Create: `src/Mcp.SourceEditor/Models/InspectionModels.cs`
- Create: `src/Mcp.SourceEditor/Models/ValidationModels.cs`
- Create: `src/Mcp.SourceEditor/Models/DiffModels.cs`
- Create: `src/Mcp.SourceEditor/Xml/SourceEditorException.cs`
- Create: `tests/Mcp.SourceEditor.Tests/EditModelTests.cs`

- [ ] **Step 1: Write failing JSON contract tests**

Test that camel-case JSON containing:

```json
{
  "operation": "setNetworkComment",
  "target": { "xmlId": "42", "networkNumber": 3 },
  "culture": "zh-CN",
  "value": "Motor permissive"
}
```

deserializes to an edit whose enum is `SetNetworkComment`, and that an unknown operation fails
deserialization. Configure the test serializer with:

```csharp
new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
};
```

- [ ] **Step 2: Verify the tests fail**

Run:

```powershell
dotnet test tests\Mcp.SourceEditor.Tests\Mcp.SourceEditor.Tests.csproj --filter EditModelTests
```

Expected: compile failure because the model types do not exist.

- [ ] **Step 3: Add the edit contract**

Define:

```csharp
public enum SourceEditOperation
{
    SetNetworkTitle,
    SetNetworkComment,
    SetBlockTitle,
    SetBlockComment,
    SetSafeProperty,
}

public sealed record EditTarget(string? XmlId = null, int? NetworkNumber = null);

public sealed record SourceEdit(
    SourceEditOperation Operation,
    EditTarget? Target,
    string? Culture,
    string Value,
    string? PropertyName = null);

public sealed record NormalizedEdit(
    int BatchIndex,
    SourceEditOperation Operation,
    string OwnerKind,
    string OwnerXmlId,
    int? NetworkNumber,
    string? Culture,
    string? PropertyName,
    string OldValue,
    string NewValue);
```

Add `EditBatchResult`, `SourceInspection`, `BlockInspection`, `NetworkInspection`,
`MultilingualValue`, `ValidationFinding`, `SourceValidationResult`, `EditableFieldChange`,
`ProtectedDifference`, and `SourceDiffResult`. All result collections are
`IReadOnlyList<T>` and all optional values are nullable.

- [ ] **Step 4: Add stable domain errors**

Implement:

```csharp
internal sealed class SourceEditorException : Exception
{
    public SourceEditorException(
        string code,
        string message,
        string? remediation = null,
        int? batchIndex = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        Remediation = remediation;
        BatchIndex = batchIndex;
    }

    public string Code { get; }
    public string? Remediation { get; }
    public int? BatchIndex { get; }
}
```

Use constants in `SourceErrorCodes` for every error named in the approved design.

- [ ] **Step 5: Run the model tests**

Run:

```powershell
dotnet test tests\Mcp.SourceEditor.Tests\Mcp.SourceEditor.Tests.csproj --filter EditModelTests
```

Expected: pass.

- [ ] **Step 6: Commit**

```powershell
git add src/Mcp.SourceEditor/Models src/Mcp.SourceEditor/Xml/SourceEditorException.cs tests/Mcp.SourceEditor.Tests/EditModelTests.cs
git commit -m "feat(source-editor): define typed edit contracts"
```

## Task 3: Implement secure XML loading and atomic serialization

**Files:**

- Create: `src/Mcp.SourceEditor/Xml/TiaXmlDocument.cs`
- Create: `tests/Mcp.SourceEditor.Tests/SourceFixture.cs`
- Create: `tests/Mcp.SourceEditor.Tests/TiaXmlDocumentTests.cs`

- [ ] **Step 1: Write secure-load tests**

Cover:

- Load `Main [OB1].xml` and retain the root document.
- Reject malformed XML with `SOURCE_XML_INVALID`.
- Reject `<!DOCTYPE ...>` even when it contains no external entity.
- Reject a file outside the supplied `PathJail`.
- Reject a non-`.xml` input.

Use a per-test jail:

```csharp
var jail = new PathJail(new[] { fixture.RootPath });
```

- [ ] **Step 2: Verify secure-load tests fail**

Run:

```powershell
dotnet test tests\Mcp.SourceEditor.Tests\Mcp.SourceEditor.Tests.csproj --filter TiaXmlDocumentTests
```

Expected: compile failure because `TiaXmlDocument` does not exist.

- [ ] **Step 3: Implement secure loading**

The load method signature is:

```csharp
internal static TiaXmlDocument Load(string path, PathJail jail, string parameterName)
```

Use:

```csharp
var settings = new XmlReaderSettings
{
    DtdProcessing = DtdProcessing.Prohibit,
    XmlResolver = null,
    IgnoreWhitespace = false,
    IgnoreComments = false,
};
using var reader = XmlReader.Create(canonicalPath, settings);
var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
```

Detect encoding from the XML declaration and BOM, defaulting to UTF-8 without BOM. Expose
`Path`, `Document`, `Encoding`, `Declaration`, `Clone()`, and `Sha256()`.

- [ ] **Step 4: Write serialization tests**

Cover:

- `SaveNew` creates a sibling XML that reopens successfully.
- Existing output fails with `SOURCE_OUTPUT_EXISTS` unless overwrite is true.
- XML declaration and UTF-8/UTF-16 encoding round-trip.
- `ReplaceAtomically` requires source and destination to be the same canonical path.
- A validation callback failure leaves the original bytes unchanged.

- [ ] **Step 5: Implement safe output**

Add:

```csharp
internal string SaveNew(
    XDocument edited,
    string outputPath,
    PathJail jail,
    bool overwrite,
    Action<string> reopenValidator);

internal void ReplaceAtomically(
    XDocument edited,
    PathJail jail,
    Action<string> reopenValidator);
```

Write to a GUID-suffixed temporary file in the destination directory. Serialize with an
`XmlWriter` configured for the detected encoding and `Indent = false`. Reopen through the supplied
validator. For new output, move the temp file to the final path. For in-place output, use
`File.Replace` when supported and a same-directory `File.Move(temp, source, true)` fallback only
after retaining a backup until validation completes. Delete only temp/backup files created by this
operation.

- [ ] **Step 6: Run document tests**

Run:

```powershell
dotnet test tests\Mcp.SourceEditor.Tests\Mcp.SourceEditor.Tests.csproj --filter TiaXmlDocumentTests
```

Expected: pass.

- [ ] **Step 7: Commit**

```powershell
git add src/Mcp.SourceEditor/Xml/TiaXmlDocument.cs tests/Mcp.SourceEditor.Tests/SourceFixture.cs tests/Mcp.SourceEditor.Tests/TiaXmlDocumentTests.cs
git commit -m "feat(source-editor): load and save XML safely"
```

## Task 4: Inspect blocks and resolve precise targets

**Files:**

- Create: `src/Mcp.SourceEditor/Xml/TiaBlockInspector.cs`
- Create: `src/Mcp.SourceEditor/Xml/TargetResolver.cs`
- Create: `tests/Mcp.SourceEditor.Tests/TiaBlockInspectorTests.cs`
- Create: `tests/Mcp.SourceEditor.Tests/TargetResolverTests.cs`

- [ ] **Step 1: Write inspection tests against real fixtures**

For `Main [OB1].xml`, assert:

- Block name/type/number/language are detected.
- Networks are returned in document order with one-based numbers.
- Each network includes its compile-unit XML `ID`.
- Existing Title and Comment cultures/text are returned.

For `FC_LAD_SimulateCylinder_Call [FC1].xml`, assert the same contract without assuming the same
network count. For `GlobalData [DB1].xml`, assert that block metadata is returned and networks are
empty.

- [ ] **Step 2: Verify inspection tests fail**

Run:

```powershell
dotnet test tests\Mcp.SourceEditor.Tests\Mcp.SourceEditor.Tests.csproj --filter TiaBlockInspectorTests
```

Expected: compile failure because the inspector does not exist.

- [ ] **Step 3: Implement namespace-independent inspection**

Use `Name.LocalName` only at Siemens wrapper boundaries and match `CompositionName` ordinally.
Identify program blocks by `SW.Blocks.OB`, `.FB`, `.FC`, or `.DB` element names. Enumerate
`SW.Blocks.CompileUnit` descendants in document order. Extract language from each compile unit and
block metadata from its `AttributeList`.

Provide:

```csharp
internal sealed class TiaBlockInspector
{
    public SourceInspection Inspect(XDocument document);
    public IReadOnlyList<CompileUnitRef> CompileUnits(XDocument document);
    public XElement BlockElement(XDocument document);
}
```

- [ ] **Step 4: Write target-resolution tests**

Cover:

- XML ID alone resolves.
- Network number alone resolves.
- Both resolve to the same unit.
- Mismatch returns `SOURCE_TARGET_MISMATCH`.
- Missing ID/number returns `SOURCE_TARGET_NOT_FOUND`.
- Zero/negative number returns `SOURCE_TARGET_NOT_FOUND`.
- A synthetic duplicate-ID fixture returns `SOURCE_TARGET_AMBIGUOUS`.
- Network operations without a target fail.

- [ ] **Step 5: Implement resolution**

Provide:

```csharp
internal CompileUnitRef ResolveNetwork(
    IReadOnlyList<CompileUnitRef> units,
    EditTarget? target,
    int batchIndex);
```

Resolve each selector independently. If both are present, compare the same `XElement` reference.
Never silently fall back from a supplied but invalid XML ID to the number.

- [ ] **Step 6: Run inspection/target tests**

Run:

```powershell
dotnet test tests\Mcp.SourceEditor.Tests\Mcp.SourceEditor.Tests.csproj --filter "TiaBlockInspectorTests|TargetResolverTests"
```

Expected: pass.

- [ ] **Step 7: Commit**

```powershell
git add src/Mcp.SourceEditor/Xml/TiaBlockInspector.cs src/Mcp.SourceEditor/Xml/TargetResolver.cs tests/Mcp.SourceEditor.Tests/TiaBlockInspectorTests.cs tests/Mcp.SourceEditor.Tests/TargetResolverTests.cs
git commit -m "feat(source-editor): inspect blocks and resolve networks"
```

## Task 5: Implement multilingual editing and the safe-property registry

**Files:**

- Create: `src/Mcp.SourceEditor/Xml/MultilingualTextEditor.cs`
- Create: `src/Mcp.SourceEditor/Xml/SafePropertyRegistry.cs`
- Create: `tests/Mcp.SourceEditor.Tests/MultilingualTextEditorTests.cs`
- Create: `tests/Mcp.SourceEditor.Tests/SafePropertyRegistryTests.cs`

- [ ] **Step 1: Write multilingual behavior tests**

Cover:

- Explicit existing `en-US` updates only that item.
- Explicit missing `zh-CN` creates an item without changing existing cultures.
- Omitted culture updates the first existing item.
- Omitted culture on an empty supported composition creates `en-US`.
- `<`, `>`, `&`, quotes, Chinese text, multiline text, and empty string serialize correctly.
- New IDs do not collide with any existing `ID` attribute.
- Missing usable composition template returns `SOURCE_TEMPLATE_MISSING`.
- Invalid culture strings return `SOURCE_CULTURE_INVALID`.

- [ ] **Step 2: Verify multilingual tests fail**

Run:

```powershell
dotnet test tests\Mcp.SourceEditor.Tests\Mcp.SourceEditor.Tests.csproj --filter MultilingualTextEditorTests
```

Expected: compile failure because the editor does not exist.

- [ ] **Step 3: Implement culture resolution and structure creation**

Provide:

```csharp
internal NormalizedEdit SetText(
    XDocument document,
    XElement owner,
    string ownerKind,
    string ownerXmlId,
    int? networkNumber,
    string compositionName,
    string? requestedCulture,
    string value,
    int batchIndex);
```

Validate cultures with `CultureInfo.GetCultureInfo`. Find
`MultilingualText[CompositionName=...]`, then `MultilingualTextItem`, then `Culture` and `Text`.
When creation is needed, clone only the wrapper shape from the nearest same-composition example,
clear its culture/text values, allocate new unused numeric IDs from `max(existing numeric IDs)+1`,
and insert it next to the owner's existing multilingual compositions.

- [ ] **Step 4: Define the initial safe-property registry**

The first implementation intentionally exposes no speculative property. Define the registry API:

```csharp
internal sealed record SafePropertyDefinition(
    string Name,
    Func<XElement, XElement?> LocateValue,
    Func<string, bool> IsValid,
    string ValidationMessage);

internal static class SafePropertyRegistry
{
    public static IReadOnlyCollection<string> Names { get; }
    public static SafePropertyDefinition Get(string name);
}
```

Populate only `blockHeaderAuthor` if and only if a repository fixture contains the corresponding
`AttributeList/Author` field. If no fixture demonstrates it, leave `Names` empty and make
`setSafeProperty` return `SOURCE_PROPERTY_UNSUPPORTED`. This is a deliberate approved safety
boundary, not unfinished work.

- [ ] **Step 5: Test the registry boundary**

Assert the exposed names match the fixture-demonstrated list exactly and that arbitrary values such
as `ProgrammingLanguage`, `Number`, `ID`, `Interface`, and `FlgNet` are rejected.

- [ ] **Step 6: Run multilingual/property tests**

Run:

```powershell
dotnet test tests\Mcp.SourceEditor.Tests\Mcp.SourceEditor.Tests.csproj --filter "MultilingualTextEditorTests|SafePropertyRegistryTests"
```

Expected: pass.

- [ ] **Step 7: Commit**

```powershell
git add src/Mcp.SourceEditor/Xml/MultilingualTextEditor.cs src/Mcp.SourceEditor/Xml/SafePropertyRegistry.cs tests/Mcp.SourceEditor.Tests/MultilingualTextEditorTests.cs tests/Mcp.SourceEditor.Tests/SafePropertyRegistryTests.cs
git commit -m "feat(source-editor): edit multilingual safe fields"
```

## Task 6: Enforce protected projection and baseline validation

**Files:**

- Create: `src/Mcp.SourceEditor/Xml/ProtectedProjection.cs`
- Create: `src/Mcp.SourceEditor/Xml/SourceValidator.cs`
- Create: `tests/Mcp.SourceEditor.Tests/ProtectedProjectionTests.cs`
- Create: `tests/Mcp.SourceEditor.Tests/SourceValidatorTests.cs`

- [ ] **Step 1: Write projection invariance tests**

Start from a real fixture and make one mutation at a time. Assert projection equality for:

- Network title text change.
- Network comment text change.
- Block title/comment text change.
- Permitted multilingual wrapper creation.

Assert inequality and a protected finding for:

- A `FlgNet` part/access/wire change.
- Compile-unit reorder/removal/insertion.
- Compile-unit or block `ID` change.
- Programming language change.
- Interface member/type/address/start-value change.
- Block number/name/type change.
- An unregistered attribute insertion.

- [ ] **Step 2: Verify projection tests fail**

Run:

```powershell
dotnet test tests\Mcp.SourceEditor.Tests\Mcp.SourceEditor.Tests.csproj --filter ProtectedProjectionTests
```

Expected: compile failure because projection is absent.

- [ ] **Step 3: Implement exact editable-slot removal**

Provide:

```csharp
internal static XDocument Create(XDocument source);
internal static string CanonicalString(XDocument projection);
```

Clone the document. For Title/Comment compositions owned by supported block/compile-unit elements,
replace only each `Text` value with the constant `__EDITABLE_TEXT__`. Normalize permitted newly
created culture scaffolding into sorted `(culture, __EDITABLE_TEXT__)` records so adding one culture
does not mask unrelated wrapper changes. Apply the same constant replacement only to registered
safe-property value nodes. Do not strip whole `MultilingualText`, `AttributeList`, or compile-unit
elements.

Canonicalize using `XmlWriter` with no indentation, normalized line endings, sorted attributes by
expanded name, and preserved element order.

- [ ] **Step 4: Write standalone/baseline validation tests**

Cover:

- Supported fixture validates.
- Malformed XML maps to `SOURCE_XML_INVALID`.
- Duplicate Siemens IDs are findings with error severity.
- Missing compile-unit source shape is a warning when parsing but an error when editing that unit.
- Baseline-safe title/comment edit validates.
- Protected mutation fails with `SOURCE_INTEGRITY_CHANGED`.

- [ ] **Step 5: Implement validation**

Provide:

```csharp
internal SourceValidationResult ValidateStandalone(XDocument document);
internal SourceValidationResult ValidateAgainstBaseline(
    XDocument baseline,
    XDocument candidate,
    IReadOnlyList<NormalizedEdit>? expectedEdits = null);
internal void ThrowIfInvalid(SourceValidationResult result);
```

Baseline validation compares protected canonical strings and independently diffs editable fields.
When `expectedEdits` is supplied, reject any editable-field change not represented by the normalized
batch.

- [ ] **Step 6: Run protection/validation tests**

Run:

```powershell
dotnet test tests\Mcp.SourceEditor.Tests\Mcp.SourceEditor.Tests.csproj --filter "ProtectedProjectionTests|SourceValidatorTests"
```

Expected: pass.

- [ ] **Step 7: Commit**

```powershell
git add src/Mcp.SourceEditor/Xml/ProtectedProjection.cs src/Mcp.SourceEditor/Xml/SourceValidator.cs tests/Mcp.SourceEditor.Tests/ProtectedProjectionTests.cs tests/Mcp.SourceEditor.Tests/SourceValidatorTests.cs
git commit -m "feat(source-editor): protect PLC logic and structure"
```

## Task 7: Apply typed batches and produce focused diffs

**Files:**

- Create: `src/Mcp.SourceEditor/Xml/StructuredEditEngine.cs`
- Create: `src/Mcp.SourceEditor/Xml/SourceDiff.cs`
- Create: `tests/Mcp.SourceEditor.Tests/StructuredEditEngineTests.cs`
- Create: `tests/Mcp.SourceEditor.Tests/SourceDiffTests.cs`

- [ ] **Step 1: Write batch engine tests**

Cover all five operation enum values. Assert:

- Network/block title and comment operations reach the intended owner.
- `setSafeProperty` accepts registry names only.
- Edits run in request order.
- Two edits normalizing to the same owner/field/culture/property fail with
  `SOURCE_OPERATION_UNSUPPORTED` and no returned candidate.
- Any failed edit rejects the complete batch and does not mutate the caller's `XDocument`.
- Protected projection matches after a valid batch.
- Each normalized edit includes its batch index, target identity, old value, and new value.

- [ ] **Step 2: Verify engine tests fail**

Run:

```powershell
dotnet test tests\Mcp.SourceEditor.Tests\Mcp.SourceEditor.Tests.csproj --filter StructuredEditEngineTests
```

Expected: compile failure because the engine does not exist.

- [ ] **Step 3: Implement clone-first batch editing**

Provide:

```csharp
internal sealed record EditEngineResult(
    XDocument Candidate,
    IReadOnlyList<NormalizedEdit> Edits,
    SourceValidationResult Validation);

internal EditEngineResult Apply(XDocument baseline, IReadOnlyList<SourceEdit> edits);
```

Reject an empty edit list. Clone the baseline before any resolution or mutation. Resolve block
operations against the inspected block and network operations through `TargetResolver`. After all
mutations, run baseline validation with the normalized edits. Return the clone only on success.

- [ ] **Step 4: Write diff tests**

Assert field-level diff reports owner kind, owner ID, network number, culture, old/new values, and
property name. Assert a protected mutation sets `ProtectedContentMatches=false` and returns at
least one path-oriented protected finding.

- [ ] **Step 5: Implement diff**

Provide:

```csharp
internal SourceDiffResult Compare(XDocument original, XDocument modified);
```

Build editable-field dictionaries keyed by
`(ownerKind, ownerId, networkNumber, composition/property, culture)`. Compare unioned keys
ordinally. Reuse `ProtectedProjection` and `SourceValidator`; do not implement a second integrity
algorithm.

- [ ] **Step 6: Run engine/diff tests**

Run:

```powershell
dotnet test tests\Mcp.SourceEditor.Tests\Mcp.SourceEditor.Tests.csproj --filter "StructuredEditEngineTests|SourceDiffTests"
```

Expected: pass.

- [ ] **Step 7: Commit**

```powershell
git add src/Mcp.SourceEditor/Xml/StructuredEditEngine.cs src/Mcp.SourceEditor/Xml/SourceDiff.cs tests/Mcp.SourceEditor.Tests/StructuredEditEngineTests.cs tests/Mcp.SourceEditor.Tests/SourceDiffTests.cs
git commit -m "feat(source-editor): apply typed edit batches"
```

## Task 8: Orchestrate preview, apply, validation, and file integrity

**Files:**

- Create: `src/Mcp.SourceEditor/Xml/SourceEditorService.cs`
- Create: `tests/Mcp.SourceEditor.Tests/SourceEditorServiceTests.cs`

- [ ] **Step 1: Write service pipeline tests**

Use a jail rooted at `SourceFixture.RootPath`. Cover:

- Parse returns inspection and source hash.
- Preview default path is `.preview.xml`.
- Apply default path is `.edited.xml`.
- Explicit sibling output is honored.
- Existing sibling output fails unless overwrite is true.
- Preview/apply sibling output are byte-identical for identical inputs.
- In-place apply fails unless both `inPlace` and `confirmInPlace` are true.
- In-place apply rejects a distinct output path.
- Valid in-place apply changes only requested fields.
- A forced reopen-validation failure leaves the original hash unchanged.
- Validate without baseline performs standalone checks.
- Validate with baseline performs protected checks.
- Diff includes hashes and field changes.

- [ ] **Step 2: Verify service tests fail**

Run:

```powershell
dotnet test tests\Mcp.SourceEditor.Tests\Mcp.SourceEditor.Tests.csproj --filter SourceEditorServiceTests
```

Expected: compile failure because the service does not exist.

- [ ] **Step 3: Implement the service**

Constructor:

```csharp
internal sealed class SourceEditorService
{
    public SourceEditorService(PathJail pathJail);
    public SourceInspection Parse(string xmlFilePath);
    public EditBatchResult Preview(
        string xmlFilePath,
        IReadOnlyList<SourceEdit> edits,
        string? outputFilePath,
        bool overwriteOutput);
    public EditBatchResult Apply(
        string xmlFilePath,
        IReadOnlyList<SourceEdit> edits,
        string? outputFilePath,
        bool overwriteOutput,
        bool inPlace,
        bool confirmInPlace);
    public SourceDiffResult Diff(string originalFilePath, string modifiedFilePath);
    public SourceValidationResult Validate(string xmlFilePath, string? baselineFilePath);
}
```

For every written candidate: edit in memory, validate against baseline, write temp, reopen using
secure loader, validate reopened document against baseline and expected edits, then publish.

- [ ] **Step 4: Run service tests**

Run:

```powershell
dotnet test tests\Mcp.SourceEditor.Tests\Mcp.SourceEditor.Tests.csproj --filter SourceEditorServiceTests
```

Expected: pass.

- [ ] **Step 5: Commit**

```powershell
git add src/Mcp.SourceEditor/Xml/SourceEditorService.cs tests/Mcp.SourceEditor.Tests/SourceEditorServiceTests.cs
git commit -m "feat(source-editor): add safe edit pipeline"
```

## Task 9: Expose the five MCP tools

**Files:**

- Create: `src/Mcp.SourceEditor/Tools/ToolJson.cs`
- Create: `src/Mcp.SourceEditor/Tools/SourceEditorTools.cs`
- Create: `tests/Mcp.SourceEditor.Tests/ToolResults.cs`
- Create: `tests/Mcp.SourceEditor.Tests/SourceEditorToolsTests.cs`

- [ ] **Step 1: Write MCP contract tests**

Instantiate tools with a `SourceEditorService` using the test jail. Cover:

- Each tool returns `IsError=false` and camel-case JSON on valid input.
- Every `SourceEditorException` returns `IsError=true` with code/message/remediation/batchIndex.
- `src_apply_edits` exposes both `inPlace` and `confirmInPlace`.
- Paths outside the jail return `SOURCE_PATH_DENIED`, mapping the shared
  `SANDBOX_PATH_DENIED` exception without leaking stack traces.
- Tool descriptions say which calls write files and that no tool imports into TIA.

- [ ] **Step 2: Verify tool tests fail**

Run:

```powershell
dotnet test tests\Mcp.SourceEditor.Tests\Mcp.SourceEditor.Tests.csproj --filter SourceEditorToolsTests
```

Expected: compile failure because the tools do not exist.

- [ ] **Step 3: Implement MCP envelopes**

Mirror the existing `ToolJson` convention and include batch index:

```csharp
public static CallToolResult Fail(
    string code,
    string message,
    string? remediation = null,
    int? batchIndex = null);
```

Register `JsonStringEnumConverter(JsonNamingPolicy.CamelCase)` so operation names are agent-friendly.

- [ ] **Step 4: Implement tools and dependency construction**

Define `[McpServerToolType] public sealed class SourceEditorTools`. Because assembly scanning
constructs tool classes through DI, update `Program.cs`:

```csharp
var sandbox = SandboxConfig.LoadDefault();
builder.Services.AddSingleton(sandbox.PathJail);
builder.Services.AddSingleton<SourceEditorService>();
builder.Services.AddSingleton<SourceEditorTools>();
```

Expose exact names:

```csharp
[McpServerTool(Name = "src_parse_block")]
[McpServerTool(Name = "src_preview_edits")]
[McpServerTool(Name = "src_apply_edits")]
[McpServerTool(Name = "src_diff")]
[McpServerTool(Name = "src_validate")]
```

Catch `SourceEditorException`, `SandboxException`, and unexpected exceptions. Map sandbox path
failure to `SOURCE_PATH_DENIED`; unexpected failures use `UNEXPECTED_ERROR`.

- [ ] **Step 5: Run all SourceEditor tests**

Run:

```powershell
dotnet test tests\Mcp.SourceEditor.Tests\Mcp.SourceEditor.Tests.csproj
```

Expected: all tests pass with zero failures.

- [ ] **Step 6: Commit**

```powershell
git add src/Mcp.SourceEditor/Program.cs src/Mcp.SourceEditor/Tools tests/Mcp.SourceEditor.Tests/ToolResults.cs tests/Mcp.SourceEditor.Tests/SourceEditorToolsTests.cs
git commit -m "feat(source-editor): expose structured MCP tools"
```

## Task 10: Integrate SourceEditor into Agent and ApiHost

**Files:**

- Modify: `src/Agent/Mcp/McpHost.cs`
- Modify: `src/Agent/Chat/McpToolCatalog.cs`
- Modify: `tests/Agent.Tests/McpToolCatalogTests.cs`
- Modify: `src/ApiHost/Program.cs`
- Modify: `src/ApiHost/appsettings.json`
- Modify: `src/ApiHost/appsettings.Development.json`

- [ ] **Step 1: Write failing catalog tests**

Extend the fake host/callers so SourceEditor exposes the five names. Assert:

- All five appear in `McpToolCatalog.Tools`.
- Their `ServerName` is `sourceEditor`.
- `CallAsync` routes a SourceEditor tool only to the SourceEditor caller.
- `import_block` remains excluded from the agent tool catalog until the composite
  snapshot/validate/import workflow is implemented.

- [ ] **Step 2: Verify Agent tests fail**

Run:

```powershell
dotnet test tests\Agent.Tests\Agent.Tests.csproj --filter McpToolCatalogTests
```

Expected: failure because `McpHost` and the catalog lack SourceEditor.

- [ ] **Step 3: Extend `McpHost`**

Add a `sourceEditorServerPath` constructor argument, create a `McpServerConnection` named
`sourceEditor`, expose `SourceEditor`, start it with the other servers, and dispose it in reverse
order. Update every `McpHost` construction site and test fake with the new connection.

- [ ] **Step 4: Extend catalog discovery/routing**

Include SourceEditor in live `tools/list` discovery and in the name-to-caller routing dictionary.
Reject cross-server duplicate tool names with an actionable startup exception.

- [ ] **Step 5: Extend ApiHost configuration**

Add `sourceEditorServerPath` config loading with default:

```csharp
Path.Combine(
    slnDir,
    "src",
    "Mcp.SourceEditor",
    "bin",
    BuildConfiguration,
    "net8.0",
    "Mcp.SourceEditor.exe")
```

Include SourceEditor in `/api/status`, `/api/tools`, raw `/api/tool` routing, server logs, and
shutdown. Do not add comment-generation or import orchestration here.

- [ ] **Step 6: Run focused integration tests and build**

Run:

```powershell
dotnet test tests\Agent.Tests\Agent.Tests.csproj --filter McpToolCatalogTests
dotnet build src\ApiHost\ApiHost.csproj
```

Expected: both exit 0.

- [ ] **Step 7: Commit**

```powershell
git add src/Agent/Mcp/McpHost.cs src/Agent/Chat/McpToolCatalog.cs tests/Agent.Tests/McpToolCatalogTests.cs src/ApiHost
git commit -m "feat(source-editor): integrate MCP with agent host"
```

## Task 11: Automated verification and documentation

**Files:**

- Modify: `agent.md`
- Modify: `buildnote/plan/initialLaunch_20260717.md`
- Create: `buildnote/log/source-editor-20260725.md`

- [ ] **Step 1: Run the SourceEditor suite fresh**

```powershell
dotnet test tests\Mcp.SourceEditor.Tests\Mcp.SourceEditor.Tests.csproj
```

Expected: zero failed.

- [ ] **Step 2: Run the complete .NET suite**

```powershell
dotnet test AgentAssistPlcDev.sln
```

Expected: zero failed. If the pre-existing malformed-manifest regression still fails, record it
separately and do not claim the solution suite is green; fixing that unrelated regression requires
separate authorization.

- [ ] **Step 3: Build the complete solution**

```powershell
dotnet build AgentAssistPlcDev.sln
```

Expected: exit 0.

- [ ] **Step 4: Exercise the MCP server over stdio**

Use the repository's existing MCP E2E harness or MCP Inspector to call, in order:

1. `src_parse_block` on a copied fixture.
2. `src_preview_edits` with one title and one comment edit.
3. `src_diff` between original and preview.
4. `src_validate` with the original as baseline.
5. `src_apply_edits` to a sibling output.

Record exact commands, tool arguments, result hashes, and validation output in
`buildnote/log/source-editor-20260725.md`. Expected: all calls return `isError=false` and protected
content matches.

- [ ] **Step 5: Update project documentation**

In `agent.md`:

- Add SourceEditor to the solution layout as implemented.
- Replace its planned inventory with the five exact tool names.
- State that only titles/comments and fixture-proven safe properties are editable.
- Preserve the `vc_snapshot` → `src_validate` → `import_block` safety rule.

In `initialLaunch_20260717.md`, mark Phase 2.4 automated implementation complete only if Steps 1–4
passed. Keep the full Phase 2 exit criteria open until the real-TIA acceptance step passes.

- [ ] **Step 6: Commit automated completion evidence**

```powershell
git add agent.md buildnote/plan/initialLaunch_20260717.md buildnote/log/source-editor-20260725.md
git commit -m "docs(source-editor): record implementation verification"
```

## Task 12: Real TIA V17 acceptance

**Files:**

- Modify: `buildnote/log/source-editor-20260725.md`
- Modify: `buildnote/plan/initialLaunch_20260717.md`

- [ ] **Step 1: Export a real FB or FC**

Use Engineering to attach to the intended TIA V17 project and export one non-safety FB/FC into its
jailed project export root. Record project, PLC, block name, original XML path, and SHA-256. Do not
use an F-block or a block open with unsaved changes.

- [ ] **Step 2: Create the mandatory snapshot**

Call `vc_status`, then `vc_snapshot` on the export repository. Record the returned commit SHA.

- [ ] **Step 3: Preview and review**

Call `src_preview_edits` with:

- One network title edit.
- One network comment edit.
- Explicit XML IDs from `src_parse_block`.
- At least one explicit culture.

Call `src_diff` and confirm `protectedContentMatches=true`.

- [ ] **Step 4: Validate immediately before import**

Call `src_validate(previewPath, baselineFilePath=originalPath)`.

Expected: `isValid=true`, no protected differences.

- [ ] **Step 5: Import and compile**

After explicit destructive approval, call `import_block` using the validated preview file, then
`compile_block`.

Expected: import succeeds and compile reports no errors.

- [ ] **Step 6: Re-export and compare**

Re-export the imported block to a distinct file. Run `src_validate(reExportedPath,
baselineFilePath=originalPath)` and `src_diff(originalPath, reExportedPath)`.

Expected: title/comment changes survive; protected projection matches. If TIA rewrites known
volatile metadata, add only narrowly documented normalization backed by a failing fixture test
before repeating acceptance.

- [ ] **Step 7: Record acceptance and update status**

Append the exact result evidence to `buildnote/log/source-editor-20260725.md`. Mark Phase 2.4 fully
complete in `initialLaunch_20260717.md`; keep Phase 2 overall open because comment generation,
review/apply orchestration, and `llm_runs` remain separate work.

- [ ] **Step 8: Commit acceptance evidence**

```powershell
git add buildnote/log/source-editor-20260725.md buildnote/plan/initialLaunch_20260717.md
git commit -m "test(source-editor): verify TIA V17 round trip"
```

## Final verification gate

Run every command fresh:

```powershell
dotnet test tests\Mcp.SourceEditor.Tests\Mcp.SourceEditor.Tests.csproj
dotnet test AgentAssistPlcDev.sln
dotnet build AgentAssistPlcDev.sln
git status --short
```

Completion requires:

- SourceEditor tests: zero failed.
- Full solution tests: zero failed, or a clearly separated pre-existing failure with no false green
  claim.
- Solution build: exit 0.
- MCP stdio exercise: five tools verified.
- Real TIA acceptance: imported, re-exported, protected content equal, compile successful.
- No unrelated files included in SourceEditor commits.

