# TIA Portal V17 Openness API surface (reflection-verified)

Ground truth from `C:\Program Files\Siemens\Automation\Portal V17\PublicAPI\V17\Siemens.Engineering.dll`
(assembly Version=17.0.0.0), dumped with `scripts/Dump-OpennessApi.ps1` (public, declared-only members;
enum values via `[Enum]::GetNames`). XML-doc quotes from the companion `Siemens.Engineering.xml`.
Nothing below is guessed — "NOT FOUND" results and interface-implementation checks are reported as such.

## 0. Entry point (verified earlier, kept for completeness)

- `TiaPortal` (static): `GetProcesses()` → `IList<TiaPortalProcess>`; `GetProcess(Int32, ...)`; instance: `Projects` → `ProjectComposition`, `ExclusiveAccess()`, `ExclusiveAccess(String)`, `GetCurrentProcess()`, `Dispose()`.
- `TiaPortalProcess`: `Id:Int32`, `Mode:TiaPortalMode`, `Path:FileInfo`, `ProjectPath:FileInfo`, `AcquisitionTime:DateTime`, `AttachedSessions`, `InstalledSoftware`; `Attach()` → `TiaPortal`.
- `TiaPortalMode` enum: `WithoutUserInterface`, `WithUserInterface`.

## 1. Project lifecycle

`Siemens.Engineering.ProjectComposition` — indexer `Item` → `Project`, `Count`, `Contains(Project)`, `GetEnumerator()`. Open/create:

- `Project Open(FileInfo path)` / `Open(FileInfo, UmacDelegate)` / `Open(FileInfo, UmacDelegate, ProjectOpenMode)`
- `Project OpenWithUpgrade(...)` — same three overloads
- `Project Retrieve(FileInfo sourcePath, DirectoryInfo targetDirectory[, UmacDelegate[, ProjectOpenMode]])` and `RetrieveWithUpgrade(...)` — same pattern (open archived projects)
- `Project Create(DirectoryInfo targetDirectory, String name)`
- `UmacDelegate` = `void Invoke(UmacCredentials)` (UMAC credential callback)

`Siemens.Engineering.Project : ProjectBase` (also implements `ITransactionSupport`):

- `void Save()` — XML: "Saves all changes of the project"
- `void SaveAs(DirectoryInfo targetFolderPath)` — saves to a new location
- `void Close()` — the **only** Close overload (reflection: Close overload count = 1); no close-with-save variant exists
- `void Archive(DirectoryInfo targetDirectory, String targetName, ProjectArchivationMode archivationMode)`

`Siemens.Engineering.ProjectBase` holds the data properties (so `Project` inherits them):
`Name:String`, `Path:FileInfo`, `Devices:DeviceComposition`, `DeviceGroups:DeviceUserGroupComposition`,
`IsModified:Boolean` (XML: "True if there are unsaved changes to the project"), plus `Author`, `Copyright`,
`CreationTime`, `LastModified`, `LastModifiedBy`, `Version`, `Size`, `Comment`, `ProjectLibrary`,
`LanguageSettings`, `Subnets`, `UngroupedDevicesGroup`, `HwUtilities`, `Graphics`, `HistoryEntries`,
`UsedProducts`, `IsPrimary`; also `ShowHwEditor(Siemens.Engineering.HW.View)` and `ExportProjectTexts`/`ImportProjectTexts`.

Enums: `ProjectOpenMode` = `Primary`, `Secondary`; `ProjectArchivationMode` = `None`, `Compressed`,
`DiscardRestorableData`, `DiscardRestorableDataAndCompressed`.

## 2. Device tree traversal

Namespace is `Siemens.Engineering.HW` — **not** the root namespace (`Siemens.Engineering.Device` is NOT FOUND).

- `Siemens.Engineering.HW.HardwareObject` (base of both): `Name:String`, `TypeIdentifier:String`,
  `DeviceItems:DeviceItemComposition`, `Items:DeviceItemAssociation`, `Parent`.
- `Siemens.Engineering.HW.Device : HardwareObject`: `IsGsd`, `UnpluggedItems`, `GetService<T>()`,
  `ShowInEditor(Siemens.Engineering.HW.View)`, `Delete()`.
- `Siemens.Engineering.HW.DeviceItem : HardwareObject`: `GetService<T>()`, `Delete()`, plus `Addresses`,
  `Channels`, `Classification`, `Container`, `IsBuiltIn`, `IsPlugged`, `PositionNumber`.
  (`Name` and `DeviceItems` come from `HardwareObject`.)
- `Siemens.Engineering.HW.Features.SoftwareContainer : DeviceItemFeature` (implements `IEngineeringService`):
  `Software` → `Siemens.Engineering.HW.Software`. Obtained via `deviceItem.GetService<SoftwareContainer>()`.
- `Siemens.Engineering.SW.PlcSoftware : Siemens.Engineering.HW.Software` (base carries `Name`):
  `BlockGroup:PlcBlockSystemGroup`, `TagTableGroup:PlcTagTableSystemGroup`, `TypeGroup:PlcTypeSystemGroup`,
  `ExternalSourceGroup:PlcExternalSourceSystemGroup`, `TechnologicalObjectGroup:TechnologicalInstanceDBGroup`,
  `WatchAndForceTableGroup:PlcWatchAndForceTableSystemGroup`; `GetService<T>()`;
  `CompareToOnline()` / `CompareTo(ISoftwareCompareTarget)`.

Note: `Siemens.Engineering.SW.Software` is NOT FOUND — the software base class lives in
`Siemens.Engineering.HW.Software`.

## 3. Blocks (`Siemens.Engineering.SW.Blocks`)

- `PlcBlockGroup`: `Blocks:PlcBlockComposition`, `Groups:PlcBlockUserGroupComposition`, `Name`,
  `GetService<T>()`. `PlcBlockSystemGroup : PlcBlockGroup` (adds `SystemBlockGroups`);
  `PlcBlockUserGroup : PlcBlockGroup` (adds `Delete()`). Nesting: recurse through `group.Groups`.
  No type named `PlcBlockGroupComposition` exists — group composition is `PlcBlockUserGroupComposition`
  (`Create(String name)`, `Find(String name)`, indexer/Contains/GetEnumerator).
- `PlcBlock` (base class of `FB`, `FC`, `OB`, `GlobalDB`, `InstanceDB`, `ArrayDB`, `CodeBlock`, `DataBlock`):
  - Properties: `Name:String`, `Number:Int32`, `AutoNumber:Boolean`, `ProgrammingLanguage:ProgrammingLanguage`,
    `IsConsistent:Boolean` (XML: "True if block and used data is consistent"), `IsKnowHowProtected:Boolean`,
    `MemoryLayout`, `CompileDate:DateTime`, `ModifiedDate`, `InterfaceModifiedDate`, `CodeModifiedDate`,
    `HeaderAuthor/HeaderFamily/HeaderName/HeaderVersion`.
  - **Correction 2026-07-18** (re-dumped with `scripts/Dump-OpennessApi.ps1`): the property list above is
    incomplete — `PlcBlock` also declares `CreationDate:DateTime`, `ParameterModified:DateTime`,
    `StructureModified:DateTime`.
  - **No `Type`/`BlockType` property** — block kind is the concrete .NET subclass (`FB`, `FC`, `OB`, ...),
    not a property. (A `BlockType` enum exists — `Undef, FB, SFB, UDT, FBT, SDT` — but no member on `PlcBlock` exposes it.)
  - Methods: `void Export(FileInfo path, ExportOptions exportOptions)` (XML: "Simatic ML export of a Plc block"),
    `void Delete()`, `void ShowInEditor()`, `GetService<T>()`. **No `Compile` method.**
- `PlcBlockComposition`: indexer `Item` → `PlcBlock`, `Count`, `Contains(PlcBlock)`, `GetEnumerator()`,
  `PlcBlock Find(String name)`,
  `IList<PlcBlock> Import(FileInfo path, ImportOptions importOptions)`,
  `IList<PlcBlock> Import(FileInfo path, ImportOptions importOptions, SWImportOptions swImportOptions)`
  (generic argument verified: `IList<Siemens.Engineering.SW.Blocks.PlcBlock>`),
  `CreateFB(String, Boolean isAutoNumbered, Int32 number, ProgrammingLanguage)`,
  `CreateInstanceDB(String, Boolean, Int32, String instanceOfName)`, `CreateFrom(MasterCopy)`,
  `CreateFrom(CodeBlockLibraryTypeVersion)`.

## 4. Enums

- `Siemens.Engineering.ExportOptions`: `None`, `WithDefaults`, `WithReadOnly`
- `Siemens.Engineering.ImportOptions`: `None`, `Override`
- `Siemens.Engineering.SW.SWImportOptions`: `None`, `IgnoreStructuralChanges`, `IgnoreMissingReferencedObjects`, `IgnoreUnitAttributes`
- `Siemens.Engineering.SW.Blocks.ProgrammingLanguage`: `Undef, STL, LAD, FBD, SCL, DB, GRAPH, CPU_DB, CFC, SFC, FBD_IEC, LAD_IEC, SDB, S7_PDIAG, RSE, F_STL, F_LAD, F_FBD, F_DB, F_LAD_LIB, F_FBD_LIB, FCP, FLD, ProDiag, ProDiag_OB, Motion_DB, F_CALL, CEM`
- `Siemens.Engineering.Compiler.CompilerResultState`: `Success`, `Information`, `Warning`, `Error`
- `Siemens.Engineering.HW.View`: `Device`, `Network`, `Topology` (for the `ShowInEditor`/`ShowHwEditor` calls)

## 5. Compile (`Siemens.Engineering.Compiler`)

Reflection over the whole `Compiler` namespace yields exactly: `ICompilable`, `CompileProvider`,
`CompilerResult`, `CompilerResultMessage`, `CompilerResultMessageComposition`, `CompilerResultState`
(+ internal factory facades). **No other enum or options type exists** — no compile-configuration enum in V17.

- `ICompilable` (XML: "An interface indication that the item supports compilation"):
  **`CompilerResult Compile()`** — parameterless, synchronous, returns the result object. No `CompileAsync`,
  no `Task`/waitable return, no options parameter.
- `CompileProvider` (XML: "The class that provides compilation of an item") implements `ICompilable` and
  `IEngineeringService` — it is the service object returned by `GetService<ICompilable>()`.
  `PlcSoftware` implements `IEngineeringServiceProvider` (`GetService<T>()`), so the usage is
  `plcSoftware.GetService<ICompilable>().Compile()`. `PlcSoftware` itself does **not** implement `ICompilable`.
- `CompilerResult`: `State:CompilerResultState` (XML: "Final state of a given compile scenario"),
  `ErrorCount:Int32`, `WarningCount:Int32`, `Messages:CompilerResultMessageComposition`
  (composition: `Count`, indexer, `Contains`, `GetEnumerator`).
- `CompilerResultMessage`: `Description:String` (**the text property is `Description`, not `Text`**),
  `Path:String`, `DateTime:DateTime`, `State:CompilerResultState`, `ErrorCount:Int32`, `WarningCount:Int32`,
  `Messages:CompilerResultMessageComposition` (recursive nesting for sub-messages).
  Severity is expressed through `State` — there is no separate `Severity` property.
- **No block-level compile**: `PlcBlock` has no `Compile` method and does not implement `ICompilable`
  (its interface list verified: no `Compiler` interfaces). Compilation granularity is the whole PLC software.

## 6. Transactions

- `Siemens.Engineering.ExclusiveAccess` (from `TiaPortal.ExclusiveAccess([String])`):
  `Transaction Transaction(ITransactionSupport peristence, String undoDescription)` → `Transaction`
  (parameter name `peristence` is the actual metadata name in the assembly — Siemens typo);
  `IsCancellationRequested:Boolean`, `Text:String`, `Dispose()`.
- `Siemens.Engineering.Transaction`: `void CommitOnDispose()`, `void Dispose()`;
  `CanCommit:Boolean`, `CommitRequested:Boolean`.
- `Siemens.Engineering.ITransactionSupport`: marker interface, zero public members. `Project` implements it,
  so `exclusiveAccess.Transaction(project, "description")` is a valid call shape.

## 7. Exceptions

- `Siemens.Engineering.EngineeringException : System.Exception` (directly; verified via BaseType).
  Declared members: `Message`, `StackTrace`, `MessageData:ExceptionMessageData`,
  `DetailMessageData:IList<ExceptionMessageData>` (generic arg verified).
- `Siemens.Engineering.NonRecoverableException : System.Exception` — a **sibling**, NOT a subclass of
  `EngineeringException`. Catching `EngineeringException` will not catch it. Same `MessageData`/`DetailMessageData` shape.
- `Siemens.Engineering.ExceptionMessageData`: `Text:String`, `DetailText:String`, `ToString()`.
- Related types seen in the assembly: `EngineeringObjectDisposedException : EngineeringException`,
  `EngineeringNotSupportedException`, `EngineeringRuntimeException`, `EngineeringSecurityException`,
  `EngineeringTargetInvocationException`, `EngineeringDelegateInvocationException`,
  `EngineeringOutOfMemoryException`, `EngineeringUserAbortException`, `LicenseNotFoundException`,
  `FunctionRightNotFoundException`.

## 8. Editor state (open editors)

A full-assembly type-name scan for `Editor`, `OpenDocument`, `Window` finds **no** editor-enumeration API:
the only `Window` type is `Siemens.Engineering.HW.MonitoringWindow` (a hardware monitoring-window
configuration object, not an open-editor handle), and there are no `Editor`/`OpenDocument` types at all.
What exists is open-only, one-directional:

- `PlcBlock.ShowInEditor()`, `Device.ShowInEditor(Siemens.Engineering.HW.View)`,
  `ProjectBase.ShowHwEditor(Siemens.Engineering.HW.View)`.

Confirmed: the V17 Openness API cannot enumerate, focus-check, or close open editors.

## 9. Tag tables & PLC data types / UDTs (dumped 2026-07-18)

Namespaces: `Siemens.Engineering.SW.Tags` / `Siemens.Engineering.SW.Types`. Both object kinds export
with the same signature as blocks: `void Export(FileInfo path, ExportOptions exportOptions)`
(declared directly on `PlcTagTable` / `PlcType`).

`Siemens.Engineering.SW.Tags`:

- `PlcTagTableGroup`: `TagTables:PlcTagTableComposition`, `Groups:PlcTagTableUserGroupComposition`,
  `Name:String`. `PlcTagTableSystemGroup : PlcTagTableGroup` (declares nothing relevant);
  `PlcTagTableUserGroup : PlcTagTableGroup` (adds `Delete()`, re-declares `Name`).
  Nesting: recurse through `group.Groups` (same pattern as block groups, §3).
- `PlcTagTable`: `Name:String`, `IsDefault:Boolean`, `ModifiedTimeStamp:DateTime`
  (**note the name — not `ModifiedDate`**; it is the table's modified timestamp),
  `Tags:PlcTagComposition`, `UserConstants`, `SystemConstants`,
  `Export(FileInfo, ExportOptions)`, `Delete()`, `ShowInEditor()`, `GetService<T>()`.
  **No `IsConsistent`, no `IsKnowHowProtected`, no `CreationDate`, no `Number`.**

`Siemens.Engineering.SW.Types`:

- `PlcTypeGroup`: `Types:PlcTypeComposition`, `Groups:PlcTypeUserGroupComposition`, `Name:String`.
  `PlcTypeSystemGroup : PlcTypeGroup` adds `SystemTypeGroups:PlcSystemTypeGroupComposition`
  (system/library types — not user UDTs); `PlcTypeUserGroup : PlcTypeGroup` (adds `Delete()`).
- `PlcType` (a UDT): `Name:String`, `IsConsistent:Boolean`, `IsKnowHowProtected:Boolean`,
  `CreationDate:DateTime`, `ModifiedDate:DateTime`, `InterfaceModifiedDate:DateTime`,
  `Export(FileInfo, ExportOptions)`, `Delete()`, `ShowInEditor()`, `GetService<T>()`.
  **No `CodeModifiedDate`, no `Number`, no `ProgrammingLanguage`.**

## 10. Change detection: software checksum & fingerprints (verified live 2026-07-20)

Both verified against the running TestPLCExportDemo session via `scripts/Probe-PlcChecksum.ps1`
(attach + traverse + read; PowerShell 5.1 cannot bind `GetService<T>()` — invoke it via reflection
on `IEngineeringServiceProvider`).

- **`Siemens.Engineering.SW.PlcChecksumProvider`** — station-level software checksum, one value per
  PLC: `plc.GetService<PlcChecksumProvider>()?.Software` (String, read-only). `GetService` returns
  null on unsupported CPUs; `Software` returns null when the program is not compiled. Verified
  byte-identical to the TIA UI value (`4F 4F D9 C3 06 F9 C0 23` after a tag-table edit), and a
  tag-table-only edit moves it — coverage includes tags. The UI's *text list* checksum is **not**
  exposed (the provider's only attribute is `Software`), so comment-only changes are invisible to it.
- **`Siemens.Engineering.SW.FingerprintProvider`** — per-object fingerprints for blocks and UDTs
  (**not** tag tables — `GetService` returns null there): `GetFingerprints()` → `IList<Fingerprint>`
  with `Id:FingerprintId` (enum: `Code, Comments, Interface, Properties, Events (OB), Texts, Alarms,
  Supervisions, TechnologyObject, LibraryType, TextualInterface`) and `Value:String` (base64 SHA-256;
  `2jmj7l5rSw0yVb/vlWAYkK/YBwk=` is the empty-content value). DBs have no `Code` fingerprint;
  an instance DB shares its FB's `Interface` value. Fingerprints consider **only user input** —
  compiles/saves do not move them. `GetFingerprints()` throws `RecoverableException` on
  inconsistent objects (which also cannot be exported).
  **Quirk (verified): the returned `IList<Fingerprint>` throws
  `NotSupportedException("Collection is read-only")` from `ICollection<T>.CopyTo`** — LINQ
  buffering (`OrderBy`/`ToList`) uses that fast path and blows up; materialize with a plain
  `foreach` (enumerator path) first. PowerShell `foreach` works by accident for the same reason.
- Timestamp side channel (verified): editing a tag table bumps `ModifiedDate`/`CodeModifiedDate`
  of **blocks referencing tags** (dependency ripple), not only the table's `ModifiedTimeStamp` —
  timestamps are false-positive-prone nomination signals, fingerprints are exact.

## Key findings

**(a) Compile: synchronous.** `ICompilable.Compile()` is parameterless, blocks, and returns
`CompilerResult`. There is no async/Task variant and no compile-options parameter — and no compile-options
enum exists anywhere in the V17 assembly (the only enum in `Siemens.Engineering.Compiler` is
`CompilerResultState` = `Success/Information/Warning/Error`).

**(b) Block-level compile: does not exist.** `PlcBlock` has no `Compile` method and does not implement
`ICompilable` (interface list checked). Compile scope is the whole PLC software:
`plcSoftware.GetService<ICompilable>().Compile()` (PlcSoftware is an `IEngineeringServiceProvider`;
`CompileProvider` is the concrete `ICompilable` + `IEngineeringService`).

**(c) Export/Import signatures.**
`PlcBlock.Export(FileInfo path, ExportOptions exportOptions)` → void; `ExportOptions` = `None, WithDefaults, WithReadOnly`.
`PlcBlockComposition.Import(FileInfo path, ImportOptions importOptions)` → `IList<PlcBlock>`, plus overload
`Import(FileInfo, ImportOptions, SWImportOptions)`; `ImportOptions` = `None, Override`;
`SWImportOptions` = `None, IgnoreStructuralChanges, IgnoreMissingReferencedObjects, IgnoreUnitAttributes`.

**(d) IsConsistent** is a `Boolean` on `Siemens.Engineering.SW.Blocks.PlcBlock`. XML doc: "True if block and
used data is consistent". (Same property also exists on `SW.Types.PlcType`, `PlcWatchTable`, `PlcForceTable`.)

**(e) Project save/close semantics.** `Save()` saves all changes; `SaveAs(DirectoryInfo)` saves to a new
location; `Close()` is the sole, parameterless close — no save-on-close option. Unsaved-changes flag:
`ProjectBase.IsModified` ("True if there are unsaved changes to the project"). To avoid losing work:
check `IsModified`, `Save()` if needed, then `Close()`.

**(f) Open-editor enumeration: does not exist.** No `Editor`/`OpenDocument`/window-collection types in the
assembly — only one-directional openers (`ShowInEditor`/`ShowHwEditor`). Any "is the block open in an editor"
logic must come from outside the Openness API (e.g. UI automation of the TIA process), not from this API.
