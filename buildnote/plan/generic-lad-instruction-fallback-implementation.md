# Generic LAD/FBD Instruction Fallback Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the FlgNet → SCL-like translator render any unknown LAD/FBD instruction as a best-effort, topology-derived statement instead of failing with "Unsupported LAD/FBD part".

**Architecture:** Add a generic renderer as the final branch of `EvaluatePartOutput` in `ProgramBlockLogicYamlWriter.cs`, infer pin direction from wire topology (three-state pin roles) instead of only hardcoded pin-name lists, and generalize instance-FB handling from a hardcoded name list to "part has an `<Instance>` element". Generically rendered networks get a leading `//` comment in their statements so the trust signal reaches knowledge.db (notes/confidence are dropped by the public API today).

**Tech Stack:** C# / .NET 8, xUnit, `dotnet test`.

**Spec:** `buildnote/plan/generic-lad-instruction-fallback-design.md`

**Amendments to the spec discovered during planning (apply to the design doc in Task 4):**

1. The hardcoded lists `IsFunctionExpressionPart` / `IsInstanceCallPart` are **kept**, not deleted: they are the exact/no-note path. The generic fallback handles everything else *with* a note. Deleting them would silently reclassify known-exact instructions as generic.
2. The "rendered generically" note must also surface as a leading `//` comment statement, because `GetNetworkStatementTextByCompileUnitId` returns only statements — notes and confidence never reach knowledge.db.
3. FlgNet `<Part>` XML carries no pin-direction info (verified against `exported/TestPLCExportDemo/`), so direction inference is topology + conventions, never XML attributes.

---

## File Structure

- Modify: `src/Mcp.Knowledge/Parsing/ProgramBlockLogicYamlWriter.cs`
  - `FlgNetContext`: add `_sourcePins` set, `PinRole` enum + `GetPinRole`/`IsExplicitInputPin`, unknown-source inference in wire parsing, role-based `GetInputPinNames`, new `BuildGenericPartExpression`, generalized instance-call gates, generic-comment insertion in `TranslateFlgNet`.
- Modify: `tests/Mcp.Knowledge.Tests/ProgramBlockLogicTests.cs` — 4 new tests (helpers `TranslateLadBlock` / `StatementsOf` already exist at lines 1358/1392).
- Modify: `buildnote/plan/generic-lad-instruction-fallback-design.md` — record the 3 amendments above.

Test command for this suite: `dotnet test tests/Mcp.Knowledge.Tests/Mcp.Knowledge.Tests.csproj --filter "FullyQualifiedName~ProgramBlockLogicTests"`

---

### Task 1: Generic expression renderer + generic marker comment

Unknown non-instance parts (e.g. `SHR`, `NORM_X`) render as `PART_NAME(pin := value, ...)`, flowing into the existing direct-assignment path. Networks containing generic renderings get a leading comment statement. EN wiring wraps the assignment in `IF ... THEN ... END_IF;` (existing behavior of `BuildDirectAssignmentStatements`, no change needed).

**Files:**
- Modify: `src/Mcp.Knowledge/Parsing/ProgramBlockLogicYamlWriter.cs` (terminal branch of `EvaluatePartOutput` ~line 1250; `TranslateFlgNet` ~line 201; new method `BuildGenericPartExpression` inside `FlgNetContext`)
- Test: `tests/Mcp.Knowledge.Tests/ProgramBlockLogicTests.cs`

- [ ] **Step 1: Write the failing tests**

Append to `ProgramBlockLogicTests.cs`, before the `private static IReadOnlyDictionary<string, string> Translate(` helper:

```csharp
    [Fact]
    public void RendersUnknownExpressionInstructionFromTopology()
    {
        var result = TranslateLadBlock(
            "ShiftLogic",
            "cu-shr",
            """
            <FlgNet xmlns="http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v5">
              <Parts>
                <Access Scope="LocalVariable" UId="a-value"><Symbol><Component Name="Value" /></Symbol></Access>
                <Access Scope="LiteralConstant" UId="a-two"><Constant><ConstantValue>2</ConstantValue></Constant></Access>
                <Access Scope="LocalVariable" UId="a-shifted"><Symbol><Component Name="Shifted" /></Symbol></Access>
                <Part Name="SHR" UId="p-shr" />
              </Parts>
              <Wires>
                <Wire UId="w-in"><IdentCon UId="a-value" /><NameCon UId="p-shr" Name="in" /></Wire>
                <Wire UId="w-n"><IdentCon UId="a-two" /><NameCon UId="p-shr" Name="n" /></Wire>
                <Wire UId="w-out"><NameCon UId="p-shr" Name="out" /><IdentCon UId="a-shifted" /></Wire>
              </Wires>
            </FlgNet>
            """);

        var statements = StatementsOf(result);
        Assert.Contains("Shifted := SHR(in := Value, n := 2);", statements);
        Assert.Contains("// Translated generically: pin semantics of one or more instructions were not verified.", statements);
    }

    [Fact]
    public void WrapsGenericallyRenderedInstructionInEnableGuard()
    {
        var result = TranslateLadBlock(
            "GuardedShiftLogic",
            "cu-shr-en",
            """
            <FlgNet xmlns="http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v5">
              <Parts>
                <Access Scope="LocalVariable" UId="a-enable"><Symbol><Component Name="Enable" /></Symbol></Access>
                <Access Scope="LocalVariable" UId="a-value"><Symbol><Component Name="Value" /></Symbol></Access>
                <Access Scope="LiteralConstant" UId="a-two"><Constant><ConstantValue>2</ConstantValue></Constant></Access>
                <Access Scope="LocalVariable" UId="a-shifted"><Symbol><Component Name="Shifted" /></Symbol></Access>
                <Part Name="Contact" UId="p-enable" />
                <Part Name="SHR" UId="p-shr" />
              </Parts>
              <Wires>
                <Wire UId="w-enable-in"><Powerrail /><NameCon UId="p-enable" Name="in" /></Wire>
                <Wire UId="w-enable-op"><IdentCon UId="a-enable" /><NameCon UId="p-enable" Name="operand" /></Wire>
                <Wire UId="w-en"><NameCon UId="p-enable" Name="out" /><NameCon UId="p-shr" Name="en" /></Wire>
                <Wire UId="w-in"><IdentCon UId="a-value" /><NameCon UId="p-shr" Name="in" /></Wire>
                <Wire UId="w-n"><IdentCon UId="a-two" /><NameCon UId="p-shr" Name="n" /></Wire>
                <Wire UId="w-out"><NameCon UId="p-shr" Name="out" /><IdentCon UId="a-shifted" /></Wire>
              </Wires>
            </FlgNet>
            """);

        var statements = StatementsOf(result);
        Assert.Contains("IF Enable THEN Shifted := SHR(in := Value, n := 2); END_IF;", statements);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Mcp.Knowledge.Tests/Mcp.Knowledge.Tests.csproj --filter "FullyQualifiedName~RendersUnknownExpressionInstructionFromTopology|FullyQualifiedName~WrapsGenericallyRenderedInstructionInEnableGuard"`
Expected: FAIL — no statements produced for `SHR` (today it hits `Unsupported LAD/FBD part 'SHR'.` and the network yields no dictionary entry, so `Assert.Contains` fails).

- [ ] **Step 3: Implement the generic renderer**

In `src/Mcp.Knowledge/Parsing/ProgramBlockLogicYamlWriter.cs`:

3a. In `EvaluatePartOutput`, replace the terminal dead end (currently lines 1250–1251):

```csharp
            notes.Add($"Unsupported LAD/FBD part '{part.Name}'.");
            return string.Empty;
```

with:

```csharp
            return BuildGenericPartExpression(part, pinName, notes);
```

3b. Add the new method immediately after `EvaluatePartOutput` (still inside `FlgNetContext`):

```csharp
        private string BuildGenericPartExpression(PartNode part, string pinName, List<string> notes)
        {
            // eno behaves like every other LAD enable-out: it mirrors the en input.
            if (MatchesAny(pinName, "eno"))
            {
                var enable = EvaluateInput(part.Uid, "en", notes);
                notes.Add($"Rendered '{part.Name}' generically; pin semantics not verified.");
                return string.IsNullOrWhiteSpace(enable) ? "TRUE" : enable;
            }

            var bindings = GetInputBindings(part.Uid, notes);
            if (bindings.Count == 0 && !HasEnableInput(part.Uid))
            {
                notes.Add($"Unsupported LAD/FBD part '{part.Name}'.");
                return string.Empty;
            }

            notes.Add($"Rendered '{part.Name}' generically; pin semantics not verified.");
            return $"{part.Name}({string.Join(", ", bindings)})";
        }
```

3c. In `TranslateFlgNet`, insert the generic marker comment right before the `if (statements.Count == 0)` check (currently line 205):

```csharp
            if (statements.Count > 0 && notes.Any(note => note.Contains("generically", StringComparison.Ordinal)))
            {
                statements.Insert(0, "// Translated generically: pin semantics of one or more instructions were not verified.");
            }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Mcp.Knowledge.Tests/Mcp.Knowledge.Tests.csproj --filter "FullyQualifiedName~RendersUnknownExpressionInstructionFromTopology|FullyQualifiedName~WrapsGenericallyRenderedInstructionInEnableGuard"`
Expected: PASS (2 passed).

- [ ] **Step 5: Run the full writer suite for regressions**

Run: `dotnet test tests/Mcp.Knowledge.Tests/Mcp.Knowledge.Tests.csproj --filter "FullyQualifiedName~ProgramBlockLogicTests"`
Expected: PASS — all pre-existing tests unchanged (the fallback only fires for parts no existing branch handles).

- [ ] **Step 6: Commit**

```bash
git add src/Mcp.Knowledge/Parsing/ProgramBlockLogicYamlWriter.cs tests/Mcp.Knowledge.Tests/ProgramBlockLogicTests.cs
git commit -m "feat(mcp-knowledge): render unknown LAD/FBD parts as generic expressions"
```

---

### Task 2: Three-state pin roles + unknown output-pin inference

Today every pin that isn't a known output is treated as an input (`IsInputPin` defaults to `true`), so an unknown instruction's output pin with an unusual name (e.g. `RESULT`) can never act as a wire source — chained logic after it is lost. Introduce a three-state role; when a wire has no known-output pin and exactly one unknown-role pin plus at least one known-input pin, the unknown pin is the source. Also exclude pins known to be sources from input bindings.

**Files:**
- Modify: `src/Mcp.Knowledge/Parsing/ProgramBlockLogicYamlWriter.cs` (`FlgNetContext` fields/constructor ~lines 490–513, wire classification ~lines 607–624, `GetInputPinNames` ~line 1346, new `PinRole`/`GetPinRole`/`IsExplicitInputPin` near `IsInputPin` ~line 1254)
- Test: `tests/Mcp.Knowledge.Tests/ProgramBlockLogicTests.cs`

- [ ] **Step 1: Write the failing test**

Append to `ProgramBlockLogicTests.cs`, before the `private static IReadOnlyDictionary<string, string> Translate(` helper:

```csharp
    [Fact]
    public void ChainsUnknownInstructionOutputPinIntoDownstreamLogic()
    {
        var result = TranslateLadBlock(
            "ChainLogic",
            "cu-foo-chain",
            """
            <FlgNet xmlns="http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v5">
              <Parts>
                <Access Scope="LocalVariable" UId="a-start"><Symbol><Component Name="Start" /></Symbol></Access>
                <Access Scope="LocalVariable" UId="a-run"><Symbol><Component Name="Run" /></Symbol></Access>
                <Part Name="FOO" UId="p-foo" />
                <Part Name="Coil" UId="p-coil" />
              </Parts>
              <Wires>
                <Wire UId="w-foo-in"><IdentCon UId="a-start" /><NameCon UId="p-foo" Name="in" /></Wire>
                <Wire UId="w-foo-out"><NameCon UId="p-foo" Name="RESULT" /><NameCon UId="p-coil" Name="in" /></Wire>
                <Wire UId="w-coil-op"><IdentCon UId="a-run" /><NameCon UId="p-coil" Name="operand" /></Wire>
              </Wires>
            </FlgNet>
            """);

        var statements = StatementsOf(result);
        Assert.Contains("Run := FOO(in := Start);", statements);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Mcp.Knowledge.Tests/Mcp.Knowledge.Tests.csproj --filter "FullyQualifiedName~ChainsUnknownInstructionOutputPinIntoDownstreamLogic"`
Expected: FAIL — `RESULT` is classified as an input by default, so the wire `p-foo.RESULT → p-coil.in` produces no `_inputSources` edge and the coil is skipped ("coil input could not be resolved").

- [ ] **Step 3: Implement pin roles and source inference**

In `src/Mcp.Knowledge/Parsing/ProgramBlockLogicYamlWriter.cs`, inside `FlgNetContext`:

3a. Add the `_sourcePins` field next to the other private fields (~line 490):

```csharp
        private readonly HashSet<PartPin> _sourcePins;
```

3b. Extend the constructor signature and body to accept and store it:

```csharp
        private FlgNetContext(
            IReadOnlyDictionary<string, PartNode> parts,
            IReadOnlyDictionary<string, AccessNode> accesses,
            IReadOnlyList<CallNode> calls,
            Dictionary<PartPin, PartPin> inputSources,
            Dictionary<PartPin, string> pinAccesses,
            Dictionary<PartPin, string> powerInputs,
            IReadOnlyList<DirectAssignment> directAssignments,
            HashSet<PartPin> sourcePins)
        {
            Parts = parts;
            Accesses = accesses;
            Calls = calls;
            _inputSources = inputSources;
            _pinAccesses = pinAccesses;
            _powerInputs = powerInputs;
            _directAssignments = directAssignments;
            _sourcePins = sourcePins;
        }
```

3c. In `Create`, declare the set next to the other local collections (~line 566):

```csharp
            var sourcePins = new HashSet<PartPin>();
```

and pass it as the final argument to `new FlgNetContext(...)` at the end of `Create`:

```csharp
            return new FlgNetContext(parts, accesses, calls, inputSources, pinAccesses, powerInputs, directAssignments, sourcePins);
```

3d. In the wire loop of `Create`, replace the source/target classification (currently):

```csharp
                var sourcePins = nameCons.Where(pin => IsOutputPin(pin.PinName)).ToArray();
                var targetPins = nameCons.Where(pin => IsInputPin(pin.PinName)).ToArray();
```

with (rename the locals to avoid colliding with the new `sourcePins` set):

```csharp
                var wireSourcePins = nameCons.Where(pin => GetPinRole(pin.PinName) == PinRole.Output).ToArray();
                if (wireSourcePins.Length == 0)
                {
                    // No known output pin: when exactly one pin has an unknown role and the wire
                    // also has a known input, the unknown pin is the source. This is how outputs
                    // of instructions with unlisted pin names (e.g. RESULT) chain downstream.
                    var unknownPins = nameCons.Where(pin => GetPinRole(pin.PinName) == PinRole.Unknown).ToArray();
                    if (unknownPins.Length == 1 && nameCons.Any(pin => GetPinRole(pin.PinName) == PinRole.Input))
                    {
                        wireSourcePins = unknownPins;
                    }
                }

                var targetPins = nameCons
                    .Where(pin => GetPinRole(pin.PinName) != PinRole.Output && !wireSourcePins.Contains(pin))
                    .ToArray();
```

Then update the two loops that follow to use the renamed locals and record sources. Replace:

```csharp
                foreach (var target in targetPins)
                {
                    foreach (var source in sourcePins)
                    {
                        inputSources[target] = source;
                    }
                }

                if (accessValues.Length == 1)
                {
                    foreach (var source in sourcePins)
                    {
                        directAssignments.Add(new DirectAssignment(source, accessValues[0], item.Order));
                    }
                }
```

with:

```csharp
                foreach (var target in targetPins)
                {
                    foreach (var source in wireSourcePins)
                    {
                        inputSources[target] = source;
                    }
                }

                foreach (var source in wireSourcePins)
                {
                    sourcePins.Add(source);
                }

                if (accessValues.Length == 1)
                {
                    foreach (var source in wireSourcePins)
                    {
                        directAssignments.Add(new DirectAssignment(source, accessValues[0], item.Order));
                    }
                }
```

3e. Add the `PinRole` enum and helpers immediately before the existing `IsInputPin` method (~line 1254). `IsInputPin`/`IsOutputPin` themselves stay untouched — other callers (powerrail rule, Or/And input collection) rely on their current default-true behavior:

```csharp
        private enum PinRole
        {
            Input,
            Output,
            Unknown
        }

        private static PinRole GetPinRole(string pinName)
        {
            if (IsOutputPin(pinName))
            {
                return PinRole.Output;
            }

            return IsExplicitInputPin(pinName) ? PinRole.Input : PinRole.Unknown;
        }

        // Same names as IsInputPin but without its default-true fallthrough, so pins of
        // unlisted instructions land on PinRole.Unknown instead of being forced to input.
        private static bool IsExplicitInputPin(string pinName)
        {
            if (pinName.StartsWith("in", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (pinName.StartsWith("SD_", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(pinName.Substring(3), out _))
            {
                return true;
            }

            return MatchesAny(
                pinName,
                "pre",
                "en",
                "SIG",
                "TIMESTAMP",
                "s",
                "s1",
                "r",
                "r1",
                "PT",
                "min",
                "max",
                "mn",
                "mx",
                "operand",
                "bit",
                "IN",
                "CLK",
                "CU",
                "CD",
                "R",
                "LD",
                "PV",
                "RESET",
                "REQ",
                "MODE",
                "LOCALE",
                "MEM",
                "OB",
                "VALUE",
                "L",
                "M",
                "H",
                "IN_OUT",
                "N",
                "P",
                "K");
        }
```

3f. Make `GetInputPinNames` role-based so pins known to be sources are never emitted as input bindings. Replace its `Where` predicate (currently):

```csharp
                .Where(pin => string.Equals(pin.PartUid, partUid, StringComparison.OrdinalIgnoreCase) && IsInputPin(pin.PinName))
```

with:

```csharp
                .Where(pin => string.Equals(pin.PartUid, partUid, StringComparison.OrdinalIgnoreCase) &&
                    (GetPinRole(pin.PinName) == PinRole.Input ||
                        (GetPinRole(pin.PinName) == PinRole.Unknown && !_sourcePins.Contains(pin))))
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Mcp.Knowledge.Tests/Mcp.Knowledge.Tests.csproj --filter "FullyQualifiedName~ChainsUnknownInstructionOutputPinIntoDownstreamLogic"`
Expected: PASS.

- [ ] **Step 5: Run the full writer suite for regressions**

Run: `dotnet test tests/Mcp.Knowledge.Tests/Mcp.Knowledge.Tests.csproj --filter "FullyQualifiedName~ProgramBlockLogicTests"`
Expected: PASS — known-instruction output unchanged (classification is identical when all pin roles are known).

- [ ] **Step 6: Commit**

```bash
git add src/Mcp.Knowledge/Parsing/ProgramBlockLogicYamlWriter.cs tests/Mcp.Knowledge.Tests/ProgramBlockLogicTests.cs
git commit -m "feat(mcp-knowledge): infer unknown LAD/FBD pin direction from wire topology"
```

---

### Task 3: Generalize instance-FB handling beyond the hardcoded list

Any part with an `<Instance>` element is an FB call. Today only 13 listed names (`TON`, `TOF`, `CTU`, ...) get instance-call statements and `Instance.PIN` output refs; an unlisted FB like `CTD` produces nothing. Listed parts keep their exact, note-free path; unlisted ones take the same shape plus a generic note.

**Files:**
- Modify: `src/Mcp.Knowledge/Parsing/ProgramBlockLogicYamlWriter.cs` (`BuildPartCallStatements` ~line 742, instance branch of `EvaluatePartOutput` ~line 1125)
- Test: `tests/Mcp.Knowledge.Tests/ProgramBlockLogicTests.cs`

- [ ] **Step 1: Write the failing test**

Append to `ProgramBlockLogicTests.cs`, before the `private static IReadOnlyDictionary<string, string> Translate(` helper:

```csharp
    [Fact]
    public void RendersUnknownInstanceFunctionBlockAsInstanceCall()
    {
        var result = TranslateLadBlock(
            "CounterLogic",
            "cu-ctd",
            """
            <FlgNet xmlns="http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v5">
              <Parts>
                <Access Scope="LocalVariable" UId="a-start"><Symbol><Component Name="Start" /></Symbol></Access>
                <Access Scope="LiteralConstant" UId="a-five"><Constant><ConstantValue>5</ConstantValue></Constant></Access>
                <Access Scope="LocalVariable" UId="a-motor"><Symbol><Component Name="Motor" /></Symbol></Access>
                <Part Name="Contact" UId="p-start" />
                <Part Name="CTD" Version="1.0" UId="p-ctd">
                  <Instance Scope="GlobalVariable"><Component Name="IEC_CTD" /></Instance>
                </Part>
                <Part Name="Coil" UId="p-coil" />
              </Parts>
              <Wires>
                <Wire UId="w-start-in"><Powerrail /><NameCon UId="p-start" Name="in" /></Wire>
                <Wire UId="w-start-op"><IdentCon UId="a-start" /><NameCon UId="p-start" Name="operand" /></Wire>
                <Wire UId="w-cd"><NameCon UId="p-start" Name="out" /><NameCon UId="p-ctd" Name="CD" /></Wire>
                <Wire UId="w-pv"><IdentCon UId="a-five" /><NameCon UId="p-ctd" Name="PV" /></Wire>
                <Wire UId="w-q"><NameCon UId="p-ctd" Name="Q" /><NameCon UId="p-coil" Name="in" /></Wire>
                <Wire UId="w-motor"><IdentCon UId="a-motor" /><NameCon UId="p-coil" Name="operand" /></Wire>
              </Wires>
            </FlgNet>
            """);

        var statements = StatementsOf(result);
        Assert.Contains("IEC_CTD(PV := 5, CD := Start);", statements);
        Assert.Contains("Motor := IEC_CTD.Q;", statements);
        Assert.Contains("// Translated generically: pin semantics of one or more instructions were not verified.", statements);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Mcp.Knowledge.Tests/Mcp.Knowledge.Tests.csproj --filter "FullyQualifiedName~RendersUnknownInstanceFunctionBlockAsInstanceCall"`
Expected: FAIL — `CTD` is not in `IsInstanceCallPart`, so no call statement is emitted and the coil input (`p-ctd.Q`) cannot be resolved.

- [ ] **Step 3: Generalize the instance-call gates**

In `src/Mcp.Knowledge/Parsing/ProgramBlockLogicYamlWriter.cs`:

3a. In `BuildPartCallStatements`, replace the `foreach` header and the statement emission. Replace:

```csharp
            var statements = new List<string>();
            foreach (var part in Parts.Values.Where(part => IsInstanceCallPart(part.Name)).OrderBy(part => part.Order))
            {
```

with:

```csharp
            var statements = new List<string>();
            foreach (var part in Parts.Values
                .Where(part => IsInstanceCallPart(part.Name) || !string.IsNullOrWhiteSpace(part.InstanceName))
                .OrderBy(part => part.Order))
            {
```

and replace the final `statements.Add(...)` of that loop:

```csharp
                statements.Add($"{part.InstanceName}({string.Join(", ", bindings)});");
```

with:

```csharp
                if (!IsInstanceCallPart(part.Name))
                {
                    notes.Add($"Rendered '{part.Name}' generically; pin semantics not verified.");
                }

                statements.Add($"{part.InstanceName}({string.Join(", ", bindings)});");
```

3b. In `EvaluatePartOutput`, generalize the instance branch (currently gated on `IsInstanceCallPart(part.Name)` at ~line 1125). Replace:

```csharp
            if (IsInstanceCallPart(part.Name))
            {
                if (string.IsNullOrWhiteSpace(part.InstanceName))
                {
                    notes.Add($"Skipped {part.Name} output because its instance name could not be resolved.");
                    return string.Empty;
                }

                if (IsOutputPin(pinName))
                {
                    return $"{part.InstanceName}.{NormalizeOutputPinName(pinName)}";
                }

                notes.Add($"Skipped {part.Name} output pin '{pinName}' because it is not supported.");
                return string.Empty;
            }
```

with:

```csharp
            if (IsInstanceCallPart(part.Name) || !string.IsNullOrWhiteSpace(part.InstanceName))
            {
                if (string.IsNullOrWhiteSpace(part.InstanceName))
                {
                    notes.Add($"Skipped {part.Name} output because its instance name could not be resolved.");
                    return string.Empty;
                }

                if (IsOutputPin(pinName) || _sourcePins.Contains(new PartPin(part.Uid, pinName)))
                {
                    if (!IsInstanceCallPart(part.Name))
                    {
                        notes.Add($"Rendered '{part.Name}' generically; pin semantics not verified.");
                    }

                    return $"{part.InstanceName}.{NormalizeOutputPinName(pinName)}";
                }

                notes.Add($"Skipped {part.Name} output pin '{pinName}' because it is not supported.");
                return string.Empty;
            }
```

(`IsInstanceCallPart` and its 13-name list stay: listed parts keep the exact, note-free path.)

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Mcp.Knowledge.Tests/Mcp.Knowledge.Tests.csproj --filter "FullyQualifiedName~RendersUnknownInstanceFunctionBlockAsInstanceCall"`
Expected: PASS.

- [ ] **Step 5: Run the full writer suite for regressions**

Run: `dotnet test tests/Mcp.Knowledge.Tests/Mcp.Knowledge.Tests.csproj --filter "FullyQualifiedName~ProgramBlockLogicTests"`
Expected: PASS — in particular the existing TON/Program_Alarm tests, which must keep their exact, comment-free output.

- [ ] **Step 6: Commit**

```bash
git add src/Mcp.Knowledge/Parsing/ProgramBlockLogicYamlWriter.cs tests/Mcp.Knowledge.Tests/ProgramBlockLogicTests.cs
git commit -m "feat(mcp-knowledge): treat any instanced LAD/FBD part as an FB call"
```

---

### Task 4: Full regression, header comment, design-doc amendments

**Files:**
- Modify: `src/Mcp.Knowledge/Parsing/ProgramBlockLogicYamlWriter.cs:1-4` (file header comment)
- Modify: `buildnote/plan/generic-lad-instruction-fallback-design.md`

- [ ] **Step 1: Run the entire Mcp.Knowledge test suite**

Run: `dotnet test tests/Mcp.Knowledge.Tests/Mcp.Knowledge.Tests.csproj`
Expected: PASS — all tests, not just `ProgramBlockLogicTests`.

- [ ] **Step 2: Update the stale port-sync header comment**

The file header still says changes must stay minimal to ease re-syncs with PlcSourceExporter; that constraint was dropped in the spec. Replace lines 1–4:

```csharp
// Ported from PlcSourceExporter (src/PlcSourceExporter.Core/ProgramBlockLogicYamlWriter.cs) — adapted for mcp-knowledge; keep changes minimal to ease future re-syncs.
// Only the FlgNet → SCL-like translator ports (GetNetworkStatementTextByCompileUnitId and everything below);
// the YAML file generation (Write / Serialize / Quote, ProgramBlockLogicYamlResult) stays behind — mcp-knowledge
// stores translated statements as the `logicStatements` network property, not a translate\program-blocks.yaml file.
```

with:

```csharp
// Ported from PlcSourceExporter (src/PlcSourceExporter.Core/ProgramBlockLogicYamlWriter.cs) — mcp-knowledge is
// now the home of this translator; the re-sync constraint is dropped and the two copies may diverge.
// Only the FlgNet → SCL-like translator ports (GetNetworkStatementTextByCompileUnitId and everything below);
// the YAML file generation (Write / Serialize / Quote, ProgramBlockLogicYamlResult) stays behind — mcp-knowledge
// stores translated statements as the `logicStatements` network property, not a translate\program-blocks.yaml file.
// Unknown instructions never hard-fail: they render generically from wire topology (see
// BuildGenericPartExpression / GetPinRole) and the network gets a leading "// Translated generically" comment.
```

- [ ] **Step 3: Record the spec amendments**

In `buildnote/plan/generic-lad-instruction-fallback-design.md`, append:

```markdown

## Amendments (from implementation planning, 2026-07-29)

1. `IsFunctionExpressionPart` / `IsInstanceCallPart` lists are **kept** (not deleted as section 3
   proposed): they remain the exact, note-free path. The generic fallback handles only unlisted
   parts. Deleting the lists would reclassify known-exact instructions as generic.
2. The "rendered generically" signal surfaces as a leading `// Translated generically: ...`
   comment statement, because `GetNetworkStatementTextByCompileUnitId` returns only statements —
   notes/confidence never reach knowledge.db.
3. FlgNet `<Part>` XML carries no pin-direction info (verified against
   `exported/TestPLCExportDemo/`), so pin direction is inferred from wire topology plus
   conventions (`PinRole` enum), never from XML attributes.
```

- [ ] **Step 4: Commit**

```bash
git add src/Mcp.Knowledge/Parsing/ProgramBlockLogicYamlWriter.cs buildnote/plan/generic-lad-instruction-fallback-design.md
git commit -m "docs(mcp-knowledge): record generic-fallback design amendments and drop port-sync note"
```
