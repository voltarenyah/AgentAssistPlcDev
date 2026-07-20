# TIA Portal V17 LAD/FBD Instruction Catalog — FlgNet Part Names, Semantics, SCL Patterns

Reference for extending the FlgNet→SCL translator in
`src/Mcp.Knowledge/Parsing/ProgramBlockLogicYamlWriter.cs`. One row per TIA V17 instruction as it
appears in Openness FlgNet exports (`<Part Name="...">`).

**Purpose.** Every instruction the translator may meet must be identified here with its technical
logic (priority/dominance, edge memory, EN/ENO gating) and the SCL pattern that reproduces it.
The SR/RS bug (2026-07-20) is the model case: the translator emitted reset-then-set, but Siemens
`Sr` is reset-dominant, so the reset line must come last. Rows below exist to prevent that class
of bug for every other instruction.

## Verification status legend

- `export` — part name observed in a real TIA V17 Openness export in this repo
  (`exported/TestPLCExportDemo/`, `tests/.../Fixtures/`).
- `tests` — part name used in `ProgramBlockLogicTests.cs` inline FlgNet (ported from real exports
  of the reference project, but not re-verified against V17 here).
- `unverified` — taken from Siemens documentation knowledge; **must be confirmed by the
  kitchen-sink export (see §3) before implementing against it.**

## Coverage status legend

- ✅ Supported — translator emits correct statements today (code ref given).
- 🟡 Partial — something is emitted, but a known gap/wrong-semantics risk exists (described).
- ❌ Missing — part falls through to `EvaluateOutput`'s catch-all: note
  `Unsupported LAD/FBD part '<name>'.` is added, consuming statements are skipped, and a network
  with zero statements is marked `untranslated`. **Missing = silently dropped logic.**

## 0. How the translator works (reading key for the SCL patterns)

Per network (`TranslateFlgNet`, `ProgramBlockLogicYamlWriter.cs:147`):

1. Instance calls → `"InstanceName(IN := expr, ...);"` (optionally `IF <en> THEN ... END_IF;`
   wrapped), then procedure-function calls (`RD_LOC_T` style: `"NAME(pin := x, OUT => target);"`).
2. Plain coils (`Coil`) → `"target := <input-expr>;"`.
3. Latch coils (`SCoil`/`RCoil`) → `"IF <input> THEN target := TRUE|FALSE; END_IF;"`.
4. Set/reset flip-flops (`Sr`/`Rs`) → ordered `IF` pairs — **dominant input last** (§A.4).
5. Pulse coils (`PCoil`) → `"target := PULSE(<input>, <membit>);"`.
6. `Call` elements (user FB/FC) → `"Name(param := value, out => target);"`.
7. Control flow (`Jump`/`Return*`/`Inc`), then direct assignments from box outputs
   (`Move`, arithmetic, ...) → `"target := <expr>;"`, EN-gated as
   `"IF <en> THEN target := <expr>; END_IF;"`.

Expressions fold inline: contacts → operand symbols (`NOT x` when negated), `And`/`Or` parts →
`(a AND b)` / `(a OR b)`, compares → `(l <op> r)`, arithmetic → `(a + b)` etc. Unresolvable pins
produce a `Skipped ...` note and an empty/omitted statement — never an exception.

---

## A. Bit logic operations

### A.1 Contacts and coils

| TIA instruction | Part name | Status | Pins | Semantics | SCL pattern |
|---|---|---|---|---|---|
| NO contact `-\| \|-"` | `Contact` (`export`) | ✅ `:1007` | in `in`, operand, out `out` | Series = AND with upstream | folded into input expr |
| NC contact `-\|/\|-` | `Contact` w/ negated operand pin | ✅ `:1017` | same | Negation on the `operand` pin, not the part | `NOT <operand>` |
| NO contact (FBD dialect) | `ContactF` (`tests`) | ✅ `:1008` | same | Same as `Contact` | folded |
| Coil `-( )-` | `Coil` (`export`) | ✅ `:161` | in `in`, operand | Assignment | `target := <input>;` |
| Negated coil `-(/)‌-` | `Coil` w/ negated `in` pin | 🟡 `:1017` pattern | same | Negation on input pin — confirm negated-`in` handling in kitchen-sink | `target := NOT <input>;` |
| Set coil `-(S)-` | `SCoil` (`export`) | ✅ `:689` | in `in`, operand | Sets bit when RLO=1, holds otherwise | `IF <in> THEN t := TRUE; END_IF;` |
| Reset coil `-(R)-` | `RCoil` (`export`) | ✅ `:689` | in `in`, operand | Clears bit when RLO=1, holds otherwise | `IF <in> THEN t := FALSE; END_IF;` |
| Set bit field `SET_BF` | `unverified` (likely `Set_BF`/`SET_BF`) | ❌ | in, operand, `n` (count) | Sets n consecutive bits starting at operand when RLO=1 | `IF <in> THEN FOR i := 0 TO n-1 DO t[i] := TRUE; END_FOR; END_IF;` |
| Reset bit field `RESET_BF` | `unverified` | ❌ | in, operand, `n` | Same, clearing | analogous with `FALSE` |
| Midline output `-(#)-` | `unverified` (likely `Coil` variant) | ❌ | in, operand, out | Stores RLO mid-branch; downstream uses stored bit | `t := <input>;` (order matters: before rest of branch) |

### A.2 Edge detection (all require an edge memory bit — the previous state)

| TIA instruction | Part name | Status | Pins | Semantics | SCL pattern |
|---|---|---|---|---|---|
| Pos. signal edge contact `-\|P\|-` | `PContact` (`tests`) | ✅ `:1022` | operand, `bit` (mem), in `pre`, out | TRUE for one scan when operand rises | `PULSE(<op>, <mem>)` ANDed with upstream |
| Neg. signal edge contact `-\|N\|-` | `NContact` (`tests`) | ✅ `:1022` | same | one scan on falling edge | `NPULSE(<op>, <mem>)` |
| RLO pos. edge `-(P)-` coil | `PCoil` (`tests`) | ✅ `:833` | in, operand (target), `bit` (mem) | Sets target one scan when RLO rises | `t := PULSE(<in>, <mem>);` |
| RLO neg. edge `-(N)-` coil | `NCoil` (`unverified`) | ❌ | in, operand, `bit` | falling-edge twin of `PCoil` | `t := NPULSE(<in>, <mem>);` |
| RLO edge box `P` / `N` (FBD) | `PBox`/`NBox` (`tests`) | ✅ `:1105` | in, `bit`, out | Edge of input expression | `PULSE(<in>, <mem>)` / `NPULSE(...)` |
| R_TRIG instruction (FB) | `R_TRIG` (`tests`) | ✅ instance call `:1461` | `CLK`, out `Q` | FB: Q one scan on rising CLK; state in instance DB | `"Inst(CLK := x);"`, refs → `Inst.Q` |
| F_TRIG instruction (FB) | `F_TRIG` (`unverified`) | ❌ (add to `IsInstanceCallPart`) | `CLK`, out `Q` | falling-edge twin | same pattern as `R_TRIG` |
| Legacy `P_TRIG`/`N_TRIG` | `unverified` | ❌ | in, `bit`, out | S7-300/400 style RLO edge | treat like `PBox`/`NBox` |

### A.3 Set/reset flip-flops — **dominance is the trap**

| TIA instruction | Part name | Status | Pins | Semantics | SCL pattern |
|---|---|---|---|---|---|
| SR flip-flop | `Sr` (`export`) | ✅ `:770` | `s`, `r1`, operand, out `q` | **Reset dominant** (the `1` marks the dominant input): both TRUE ⇒ Q=FALSE | `IF <s> THEN t := TRUE; END_IF;` **then** `IF <r1> THEN t := FALSE; END_IF;` — last line wins |
| RS flip-flop | `Rs` (`tests`) | ✅ `:770` | `s1`, `r`, operand, out `q` | **Set dominant**: both TRUE ⇒ Q=TRUE | `IF <r> THEN t := FALSE; END_IF;` **then** `IF <s1> THEN t := TRUE; END_IF;` |
| Q output of either | — | ✅ `:1183` | `q`/`out` | Q reads the operand memory bit | `<operand symbol>` |

Rule of thumb for any future flip-flop-like part: identify the dominant input (Siemens marks it
with `1` in the pin name), emit its statement **last**.

## B. Timer operations

| TIA instruction | Part name | Status | Pins | Semantics | SCL pattern |
|---|---|---|---|---|---|
| On delay `TON` | `TON` (`export`) | ✅ instance call | `IN`, `PT`; outs `Q`, `ET` | Q rises after PT of continuous IN; instance DB holds state | `"Inst(IN := x, PT := t);"`, refs → `Inst.Q` / `Inst.ET` |
| Off delay `TOF` | `TOF` (`tests`) | ✅ instance call | same | Q falls PT after IN falls | same |
| Pulse `TP` | `TP` (`unverified`) | ✅ instance call (in list `:1468`) | same | Q high for PT regardless of IN | same |
| Retentive on delay `TONR` | `TONR` (`unverified`) | ✅ instance call | `IN`, `PT`, `R`; outs `Q`, `ET` | Accumulates; `R` resets ET | same, `R` bound |
| Reset timer `-(RT)-` | `unverified` (likely `RtTimer`/`ResetTimer`) | ❌ | in, operand (timer instance) | Coil form: resets a TONR-style instance | `Inst.R := TRUE;`-style or `RESET_TIMER(...)` — decide after export |
| Preset timer `-(PT)-` | `unverified` | ❌ | in, operand, duration | Coil form: loads PT into instance | decide after export |
| Legacy S5 timers (`S_PULSE` etc.) | `unverified` | ❌ | many | S7-300/400 only; rare in V17 projects | defer (P2) |

## C. Counter operations

| TIA instruction | Part name | Status | Pins | Semantics | SCL pattern |
|---|---|---|---|---|---|
| Count up `CTU` | `CTU` (`tests`) | ✅ instance call | `CU`, `R`, `PV`; outs `Q`, `CV` | CV+1 on CU rise; Q when CV≥PV; R clears | `"Inst(CU := x, R := r, PV := n);"` → `Inst.Q`, `Inst.CV` |
| Count down `CTD` | `CTD` (`unverified`) | ❌ (add to `IsInstanceCallPart`) | `CD`, `LD`, `PV`; outs `Q`, `CV` | CV−1 on CD rise; **LD loads PV** (load, not reset); Q when CV≤0 | same pattern; note LD≠R |
| Up/down `CTUD` | `CTUD` (`unverified`) | ❌ | `CU`, `CD`, `R`, `LD`, `PV`; outs `QU`, `QD`, `CV` | Both edges; QU/QD by direction thresholds | same pattern |

## D. Comparator operations

| TIA instruction | Part name | Status | Pins | Semantics | SCL pattern |
|---|---|---|---|---|---|
| `== <> < <= > >=` | `Eq Ne Lt Le Gt Ge` (`Eq..Ge`: `tests` for `Ne`,`Le`) | ✅ `:1614` | `in1`, `in2`, in `pre`, out | Series compare = AND; types from operands | `(l <op> r)`; `<> ≤ ≥` → `<>`, `<=`, `>=` |
| Value in range `IN_RANGE` | `InRange` (`tests`) | ✅ `:1235` | `min`, `in`, `max`, `pre` | Inclusive both ends | `(<min> <= v AND v <= <max>)` ANDed upstream |
| Value out of range `OUT_RANGE` | `OutRange` (`unverified`, in fn list `:1435`) | 🟡 | same | `v < min OR v > max` | currently emitted as call-form `OutRange(...)` — verify |
| Check OK / NOT_OK | `OK`/`Not_OK` (`unverified`) | ❌ | operand | Validity (REAL NaN) check | `t := OK(x);`-style — decide after export |

## E. Math operations

| TIA instruction | Part name | Status | Pins | Semantics | SCL pattern |
|---|---|---|---|---|---|
| `ADD/SUB/MUL/DIV` | `Add Sub Mul Div` (`tests`) | ✅ `:1390` | `in1..inN`, out | n-ary fold | `(a + b + …)`, `(a - b - …)`, … |
| `MOD` | `Mod` (`tests`) | ✅ `:1410` | `in1`, `in2` | remainder | `(a MOD b)` |
| `NEG` | `Neg` (`tests`) | ✅ `:1379` | in, out | sign flip | `(-x)` |
| `ABS` | `Abs` (`unverified`, in fn list `:1427`) | ✅ (call-form) | in, out | magnitude | `Abs(x)` |
| `EXPT` | `Expt` (`unverified`, in arith list `:1420`) | ✅ `:1415` | `in1`, `in2` | power | `EXPT(a, b)` |
| `CALC` | `Calc` (`tests`) | ✅ `:1211` | free equation | Expression with named inputs; identifiers substituted | equation text |
| `INC` | `Inc` (`tests`) | ✅ `:900` | in `en`, operand | +1 in place when EN | `t := t + 1;` (EN-gated) |
| `DEC` | `Dec` (`unverified`) | ❌ | in `en`, operand | −1 twin | `t := t - 1;` |
| `MIN` / `MAX` | `Min` / `Max` (`unverified`) | ❌ | `in1..inN`, out | n-ary pick | `MIN(a,b)`/`MAX(a,b)` — SCL has no builtin; emit nested `IF`/ternary or keep call-form |
| `LIMIT` | `LIMIT` (`tests`) | ✅ (call-form `:1431`) | `MN`, `IN`, `MX`, out | clamp | `LIMIT(MN, IN, MX)` |
| `SQR`/`SQRT`/`LN`/`EXP`/`SIN`/`COS`/`TAN`/`ASIN`/`ACOS`/`ATAN` | `Sqr Sqrt Ln Exp Sin Cos Tan Asin Acos Atan` (`unverified`) | ❌ | in, out | real math | SCL builtins exist: `SQRT`, `LN`, `EXP`, `SIN`, … — map 1:1 |

## F. Move operations

| TIA instruction | Part name | Status | Pins | Semantics | SCL pattern |
|---|---|---|---|---|---|
| `MOVE` | `Move` (`tests`) | ✅ `:1149` + direct-assign | in `in`, `en`, out `out1` | Copy value; EN-gated | `t := x;` / `IF <en> THEN t := x; END_IF;` |
| String move `S_MOVE` | `S_Move` (`tests`) | ✅ `:1149` | same | String-aware copy | same |
| `MOVE_BLK` | `MoveBlockI` (`tests`, in fn list `:1433`) | 🟡 call-form | `IN`, `COUNT`; out `OUT` | Copy COUNT elements; **interruptible** | `MOVE_BLK(IN, COUNT, OUT)` — keep call-form |
| `UMOVE_BLK` | `UMoveBlockI`? (`unverified`) | ❌ | same | Uninterruptible variant | same shape |
| `FILL_BLK` | `FillBlockI` (`tests`, in fn list `:1429`) | 🟡 call-form | `IN`, `COUNT`; out `OUT` | Replicate value COUNT times | call-form |
| `UFILL_BLK` | `unverified` | ❌ | same | uninterruptible | call-form |
| `SWAP` | `Swap` (`unverified`) | ❌ | in, out | Byte-order swap | `SWAP(x)` |
| `SCATTER`/`GATHER` | `unverified` | ❌ | bit-field pins | Word ↔ bit array explode/implode | decide after export |
| `Serialize`/`Deserialize` | `unverified` | ❌ | many | struct ↔ byte array | call-form passthrough |

## G. Conversion operations

| TIA instruction | Part name | Status | Pins | Semantics | SCL pattern |
|---|---|---|---|---|---|
| `CONVERT` | `Convert` (`tests`) | ✅ (call-form `:1428`) | in, out | Explicit type cast (target type from XML `Card`? — operand type) | `Convert(x)` — **widening/narrowing type not in statement; note as known limitation** |
| `ROUND` | `Round` (`unverified`) | ❌ | in, out | nearest int, ties→even | `ROUND(x)` |
| `CEIL` / `FLOOR` | `Ceil` / `Floor` (`unverified`) | ❌ | in, out | up / down | `CEIL(x)` / `FLOOR(x)` |
| `TRUNC` | `Trunc` (`unverified`, in fn list `:1444`) | ✅ (call-form) | in, out | toward zero | `Trunc(x)` |
| `SCALE_X` | `Scale_X` (`unverified`, in fn list `:1439`) | ✅ (call-form) | `MIN`,`VALUE`,`MAX` | normalized→engineering | `Scale_X(...)` |
| `NORM_X` | `Normalize` (`unverified`, in fn list `:1434`) | ✅ (call-form) | `MIN`,`VALUE`,`MAX` | engineering→normalized | `Normalize(...)` |

## H. Program control

| TIA instruction | Part name | Status | Pins | Semantics | SCL pattern |
|---|---|---|---|---|---|
| Jump if RLO=1 `JMP` | `Jump` (`tests`) | ✅ `:862` | in, `label` | Conditional goto | `IF <in> THEN GOTO <label>; END_IF;` |
| Jump if RLO=0 `JMPN` | `JumpN` (`unverified`) | ❌ | in, `label` | Inverted condition | `IF NOT (<in>) THEN GOTO <label>; END_IF;` |
| Jump label | `Label` (`unverified`) | ❌ | name | Target marker — **without it GOTO targets dangle** | `<label>:` line or comment marker |
| Jump list `JMP_LIST` / branch `SWITCH` | `unverified` | ❌ | selector + k labels | multi-way | `CASE`/`ELSIF` chain |
| Return `RET` | `Return` (`unverified`, in control list `:1453`) | ✅ `:894` | in | conditional block exit | `IF <in> THEN RETURN; END_IF;` |
| Return value variants | `ReturnValue` (`tests`), `ReturnTrue` | ✅ `:875/:888` | in, operand | exit w/ value | `IF <in> THEN RETURN <v>; END_IF;` |
| `GET_ERROR`/`GET_ERR_ID` | `unverified` | ❌ | out only | block-local error info | call-form |

## I. Word logic operations — **name collision warning**

The boolean FBD gates `And`/`Or` (✅ `:1064/:1085`, fold to `(a AND b)`) share TIA display names
with the **word logic** instructions AND/OR/XOR, which operate bitwise on WORD/DWORD operands.
In FlgNet they are distinct part names (`unverified`: likely `And`/`Or`/`Xor` with word-typed
pins, or `AndWord`…). The kitchen-sink export must disambiguate before implementing `Xor`/`Inv`.

| TIA instruction | Part name | Status | Pins | Semantics | SCL pattern |
|---|---|---|---|---|---|
| `AND` (word) | see warning | 🟡 (bool `And` handled) | `in1..inN`, out | bitwise AND | `(a AND b)` is also valid SCL for words |
| `OR` (word) | see warning | 🟡 | same | bitwise OR | `(a OR b)` |
| `XOR` | `Xor` (`unverified`) | ❌ | `in1..inN`, out | bitwise XOR | `(a XOR b)` |
| `INV` | `Inv` (`unverified`) | ❌ | in, out | one's complement | `NOT x` |
| `DECO`/`ENCO` | `Deco`/`Enco` (`unverified`) | ❌ | in, out | set bit n / lowest set bit pos | call-form |
| `SEL` | `Sel` (`unverified`) | ❌ | `G`, `IN0`, `IN1`, out | G?IN1:IN0 | `SEL(G, IN0, IN1)` or `IF G THEN o := IN1; ELSE o := IN0; END_IF;` |
| `MUX`/`DEMUX` | `Mux`/`Demux` (`unverified`) | ❌ | `K` + ins/outs | index select | call-form |

## J. Shift/rotate

| TIA instruction | Part name | Status | Pins | Semantics | SCL pattern |
|---|---|---|---|---|---|
| `SHL` | `Shl` (`unverified`) | ❌ | `IN`, `N`, out | left, zero-fill | `SHL(IN, N)` |
| `SHR` | `Shr` (`unverified`) | ❌ | `IN`, `N`, out | right; **sign-extends for signed types** | `SHR(IN, N)` |
| `ROL`/`ROR` | `Rol`/`Ror` (`unverified`) | ❌ | `IN`, `N`, out | circular | `ROL`/`ROR` |

## K. Date, time & runtime

| TIA instruction | Part name | Status | Pins | Semantics | SCL pattern |
|---|---|---|---|---|---|
| `T_ADD`/`T_SUB`/`T_CONV` | same (`tests`) | ✅ call-form `:1441` | ins per op | time arithmetic/cast | call-form |
| `T_DIFF`/`T_COMP` | `unverified` | ❌ | IN1/IN2 | subtract / compare times | call-form |
| `RD_SYS_T` | `RD_SYS_T` (`tests`) | ✅ procedure `:1456` | out `RET_VAL`? (outputs via `=>`) | read module clock | `RD_SYS_T(OUT => t);` |
| `WR_SYS_T` | `WR_SYS_T` (`tests`) | ✅ call-form `:1448` | in | write clock | call-form |
| `RD_LOC_T` | `RD_LOC_T` (`tests`) | ✅ procedure | outs | local time | `RD_LOC_T(OUT => t);` |
| `WR_LOC_T` | `unverified` | ❌ | in | write local time | call-form |
| `SET_TIMEZONE` | `SET_TIMEZONE` (`unverified`) | ✅ instance call `:1471` | in | set TZ | call-form |
| `RT_INFO` | `RT_INFO` (`tests`) | ✅ call-form `:1436` | in | OB runtime info | call-form |
| `RUNTIME` | `Runtime` (`tests`) | ✅ call-form `:1437` | in | runtime measurement | call-form |

## L. String operations

| TIA instruction | Part name | Status | Pins | Semantics | SCL pattern |
|---|---|---|---|---|---|
| `LEN` | `LEN` (`tests`) | ✅ call-form `:1430` | in | length | `LEN(s)` |
| `S_CONV` | `S_CONV` (`unverified`, in fn list `:1440`) | ✅ call-form | in/out typed | string↔number | call-form |
| `STRG_VAL` / `VAL_STRG` | `Strg_VAL`/`VAL_STRG` (`VAL_STRG`: `tests` `:1445`) | 🟡 (VAL_STRG ✅; STRG_VAL ❌) | format pins | number↔string with format | call-form |
| `MID` | `MID` (`unverified`, in fn list `:1432`) | ✅ call-form | `IN`,`L`,`P` | substring | call-form |
| `CONCAT`/`LEFT`/`RIGHT`/`DELETE`/`INSERT`/`REPLACE`/`FIND` | `unverified` (`Concat Left Right Delete Insert Replace Find`) | ❌ | per op | standard string ops | call-form (SCL builtins exist) |
| `JOIN`/`SPLIT` | `unverified` | ❌ | array pins | V17 additions | call-form |

## M. Diagnostics, technology, comms (passthrough policy)

Instance-call and call-form passthrough is the intended end state for these — the SCL pattern is
always `"Inst(...);"` / `NAME(bindings);`; deep semantics are out of scope for logic translation.

| Part name | Status | Notes |
|---|---|---|
| `Program_Alarm` (`tests`) | ✅ instance call `:1472` | alarm with up to 10 SD_i inputs (pin rule `:1232`) |
| `ESTOP1`, `FDBACK`, `ACK_GL` (`unverified`) | ✅ instance call list | safety program parts — translation of safety logic needs review anyway |
| `RDREC` (`unverified`) | ✅ instance call `:1476` | `WRREC` ❌ — add to list |
| `SinaPos` (`tests`) | ✅ instance call `:1477` | technology object wrapper |
| `LED`, `GET_DIAG`, `DeviceStates`, `ModuleStates`, `GET_IM_DATA`, `GEN_DIAG` | ❌ `unverified` | add as call-form/instance on demand |
| `TCON/TSEND/TRCV/MB_CLIENT/...` | ❌ `unverified` | comms instructions; instance-call passthrough when met |

---

## 1. Pin-level conventions

- Input pins recognized (`IsInputPin`, `:1254`): `pre`, `en`, `in*`, `SD_<n>`, `s`, `s1`, `r`,
  `r1`, `PT`, `min/max/mn/mx`, `operand`, `bit`, `IN`, `CLK`, `CU`, `CD`, `R`, `LD`, `PV`,
  `RESET`, `REQ`, `MODE`, `LOCALE`, `MEM`, `OB`, `VALUE`, `L`, `M`, `H`, `IN_OUT`, `N`, `P`, `K`.
  Any instruction introducing a **new input pin name must extend this list** or the pin is treated
  as an output.
- Output pins (`IsOutputPin`, `:1312`): `eno`, `q`, `Q`, `out*`, `ET`, `CV`, `QU`, `QD`,
  `RET_VAL`, `STATUS`, `ERROR`, `BUSY`.
- Negation is a pin property (`PartNode.IsNegated`), not a part — check negation handling for
  every new part (only `Contact.operand` consults it today — **audit risk** for negated inputs on
  other parts).
- EN/ENO: `en`/`pre` inputs gate statements (`IF <en> THEN ... END_IF;`); `TRUE` (power rail) is
  dropped. ENO chaining is not modeled — the output side is read directly.

## 2. Subtle-rules checklist (the "SR dominance" class)

1. **Flip-flop dominance** — dominant input last (§A.3). ✅ enforced + tested 2026-07-20.
2. **Edge memory** — every edge part needs its `bit` operand; skip with note if absent. ✅.
3. **CTD `LD` loads PV** — it is not a reset; CTUD has both `R` and `LD`. Pending implementation.
4. **Timer instance state** — timers/counters/edge FBs are stateful: always instance calls, never
   fold to expressions. ✅ via `IsInstanceCallPart`.
5. **Word-logic vs boolean gate name collision** — resolve via kitchen-sink before `Xor`/`Inv`.
6. **Order of statements within a network** — S/R coils and flip-flops to the same operand must
   keep LAD evaluation order; today grouped by part kind (coils → latches → flip-flops → ...).
   **Audit risk**: mixed SCoil+Sr on one operand in one network may reorder vs. TIA semantics.
7. **`SHR` sign extension** — signed vs unsigned matters for the pattern comment, SCL `SHR`
   matches TIA.
8. **Negated input pins on parts other than contacts** — not consulted today (see §1).

## 3. Kitchen-sink ground-truth procedure (user, on TIA V17 machine)

1. Create a V17 test project (S7-1500 CPU), one LAD network per instruction from the checklist
   below; give every operand a symbolic tag so translations resolve.
2. Export via the existing pipeline (`export_all_blocks`, same flow as `scripts/e2e-full.json`)
   into `exported/TiaV17InstructionSet/`.
3. Harvest: `grep -rhoP '<Part Name="\K[^"]+' exported/TiaV17InstructionSet | sort -u` and, per
   part, the `NameCon ... Name="pin"` pairs.
4. Diff against this catalog: flip `unverified` → `export`, fix name mismatches, add rows for
   anything unexpected (e.g. word-logic part names from §I).

Checklist per network (LAD): NO/NC contact, coil, negated coil, (S), (R), SET_BF, RESET_BF,
midline output, -|P|-, -|N|-, (P), (N), SR, RS, TON, TOF, TP, TONR, (RT), (PT), CTU, CTD, CTUD,
all six compares, IN_RANGE, OUT_RANGE, OK, NOT_OK, ADD(3-in), SUB, MUL, DIV, MOD, NEG, ABS, INC,
DEC, MIN, MAX, LIMIT, SQR, SQRT, LN, EXP, SIN, EXPT, CALC, MOVE, MOVE_BLK, UMOVE_BLK, FILL_BLK,
UFILL_BLK, SWAP, CONVERT, ROUND, CEIL, FLOOR, TRUNC, SCALE_X, NORM_X, JMP, JMPN, Label, RET,
AND-word, OR-word, XOR, INV, DECO, ENCO, SEL, MUX, DEMUX, SHL, SHR, ROL, ROR, T_DIFF, T_COMP,
RD_SYS_T, WR_SYS_T, RD_LOC_T, WR_LOC_T, SET_TIMEZONE, RT_INFO, RUNTIME, S_CONV, STRG_VAL,
VAL_STRG, LEN, CONCAT, LEFT, RIGHT, MID, DELETE, INSERT, REPLACE, FIND, JOIN, SPLIT.

## 4. Coverage summary & roadmap

**Today:** ~55 part names recognized (35 with real semantics + instance-call/call-form lists).
**Known missing and silently dropped:** everything ❌ above.

- **P0** (common in real projects): `CTD`, `CTUD`, `F_TRIG`, `Dec`, `Min`, `Max`, `Round`,
  `Ceil`, `Floor`, `Swap`, `Sel`, `JumpN`, `Label`, word-logic `Xor`/`Inv`, `NCoil`,
  `WRREC`, `STRG_VAL`.
- **P1**: `Shl/Shr/Rol/Ror`, string ops, `Deco/Enco/Mux/Demux`, `T_DIFF/T_COMP`, `Set_BF/Reset_BF`,
  `UmoveBlk/UfillBlk`, `OK/Not_OK`, `WR_LOC_T`.
- **P2**: diagnostics (`GET_DIAG`, `DeviceStates`, ...), `SCATTER/GATHER`, Serialize/Deserialize,
  legacy S5 timers, `JMP_LIST/SWITCH`, midline output edge cases.

**Standing rule.** Every translator change for a part name must: (1) flip/annotate its row here
(status + semantics learned); (2) add a fixture test in
`tests/Mcp.Knowledge.Tests/ProgramBlockLogicTests.cs` following
`TranslatesBuiltInSrPartAndQOutput` / `TranslatesBuiltInRsPartAsSetDominant` — assert statement
content **and order**, and assert absence of `Skipped`/`Unsupported` notes.
