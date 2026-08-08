using System.Diagnostics;
using Contracts;
using Contracts.Engineering;
using Mcp.Engineering.Export;
using Mcp.Engineering.Openness;
using Mcp.Engineering.Sessions;
using Microsoft.Extensions.Logging;
using Siemens.Engineering;
using Siemens.Engineering.Cax;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.HW;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Types;

namespace Mcp.Engineering.Adapter;

/// <summary>
/// TIA Portal V17 implementation of <see cref="IEngineeringPlatform"/>
/// (buildnote/plan/mcp-engineering.md §4). All public members serialize through a single
/// gate — Openness objects are not thread-safe. Must only be used after
/// <see cref="OpennessAssemblyResolver.Register"/> has run.
/// </summary>
public sealed class TiaV17Adapter : IEngineeringPlatform
{
    private readonly object _gate = new();
    private readonly ILogger<TiaV17Adapter> _logger;
    private TiaPortal? _portal;
    private Project? _project;

    /// <summary>True when we started the portal process (open mode); false when attached to a user's session.</summary>
    private bool _ownsPortal;

    /// <summary>OS process id of the connected portal, used to detect a portal that exited on its own.</summary>
    private int? _portalProcessId;

    public TiaV17Adapter(ILogger<TiaV17Adapter> logger)
    {
        _logger = logger;
    }

    public EnvCheckResult CheckEnvironment() => EnvironmentChecker.Check();

    public SessionInfo[] ListSessions() => TiaSessionEnumerator.ListSessions().ToArray();

    public ConnectionInfo Connect(ConnectOptions options)
    {
        lock (_gate)
        {
            if (_portal is not null)
            {
                if (IsPortalProcessAlive())
                    throw new AdapterException("ALREADY_CONNECTED", "Already connected. Call disconnect first.");
                // The portal process exited on its own (crash or killed externally) while we
                // still hold stale handles. Clean up silently and fall through to reconnect.
                _logger.LogWarning(
                    "Previous TIA Portal connection is stale (process {ProcessId} exited); cleaning up before reconnecting.",
                    _portalProcessId);
                CleanupStaleConnection();
            }
            if (options.SessionId is not null && options.ProjectPath is not null)
                throw new AdapterException("AMBIGUOUS_CONNECT", "Provide sessionId or projectPath, not both.");
            if (options.SessionId is null && options.ProjectPath is null)
                throw new AdapterException("MISSING_CONNECT_TARGET", "Provide sessionId or projectPath.");

            return options.SessionId is not null
                ? Attach(options.SessionId.Value, options.TimeoutSeconds)
                : Open(options.ProjectPath!, options.WithUI);
        }
    }

    /// <summary>
    /// True while the OS process behind the current connection is still running. When no
    /// process id was recorded we cannot prove the portal died, so we report alive and keep
    /// the safe ALREADY_CONNECTED behavior.
    /// </summary>
    private bool IsPortalProcessAlive()
    {
        if (_portalProcessId is not int pid)
            return true;
        try
        {
            using var process = Process.GetProcessById(pid);
            // Guard against pid reuse by an unrelated process.
            return !process.HasExited
                && process.ProcessName.StartsWith("Siemens.Automation.Portal", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // ArgumentException: no process with that id — the portal is gone.
            return false;
        }
    }

    /// <summary>
    /// Releases stale handles after the portal process died on its own. Does not touch
    /// _project — any access would round-trip to the dead process.
    /// </summary>
    private void CleanupStaleConnection()
    {
        try { _portal?.Dispose(); } catch { }
        _portal = null;
        _project = null;
        _ownsPortal = false;
        _portalProcessId = null;
    }

    private ConnectionInfo Attach(int processId, int timeoutSeconds)
    {
        Exception? lastError = null;
        TiaPortalMode? attachedMode = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            // GetProcess returns null (without throwing) when the process cannot be
            // acquired — including the poisoned state after enumerated TiaPortalProcess
            // objects have been disposed (see TiaSessionEnumerator). Null-check is mandatory.
            TiaPortalProcess? process;
            try
            {
                process = TiaPortal.GetProcess(processId, timeoutSeconds * 1000);
            }
            catch (Exception ex)
            {
                throw new AdapterException("SESSION_NOT_FOUND",
                    $"No running TIA Portal process with id {processId}: {ex.Message}",
                    "Call list_sessions to see attachable processes.");
            }
            if (process is null)
            {
                lastError = new NullReferenceException("TiaPortal.GetProcess returned null (process not acquirable).");
            }
            else
            {
                try
                {
                    _portal = process.Attach();
                    attachedMode = process.Mode;
                    break;
                }
                catch (NullReferenceException ex)
                {
                    // Transient acquisition failure (e.g. portal still initializing).
                    // Bounded retry; only NRE is considered transient.
                    lastError = ex;
                }
                catch (Exception ex)
                {
                    throw new AdapterException("ATTACH_FAILED",
                        $"Failed to attach to TIA process {processId}: {ex.Message}");
                }
            }
            if (attempt < 3)
                Thread.Sleep(TimeSpan.FromSeconds(5));
        }
        if (_portal is null)
        {
            throw new AdapterException("ATTACH_FAILED",
                $"Failed to attach to TIA process {processId} after 3 attempts: {lastError?.Message}",
                "Check the TIA session is fully loaded and has no modal dialogs open.");
        }

        _ownsPortal = false;
        _portalProcessId = processId;
        // A project that is still loading shows as an empty Projects collection for a while —
        // same bounded retry as the attach acquisition above before declaring failure.
        for (var attempt = 1; attempt <= 3 && _portal!.Projects.Count == 0; attempt++)
        {
            if (attempt < 3)
            {
                Thread.Sleep(TimeSpan.FromSeconds(5));
            }
        }

        if (_portal!.Projects.Count == 0)
        {
            _portal.Dispose();
            _portal = null;
            _portalProcessId = null;
            throw new AdapterException("PROJECT_NOT_FOUND", "TIA is running but no project is open.",
                "Open a project in that TIA session (or connect with projectPath instead). If the project is still loading, retry once TIA shows it fully loaded; if it is open in a different TIA version, attach to that session instead.");
        }

        _project = _portal.Projects[0];
        return new ConnectionInfo
        {
            Attached = true,
            HasUI = attachedMode == TiaPortalMode.WithUserInterface,
            ProjectName = _project.Name,
            ProjectPath = _project.Path?.FullName,
        };
    }

    private ConnectionInfo Open(string projectPath, bool withUI)
    {
        if (!File.Exists(projectPath))
            throw new AdapterException("PROJECT_NOT_FOUND", $"Project file not found: {projectPath}");

        var mode = withUI ? TiaPortalMode.WithUserInterface : TiaPortalMode.WithoutUserInterface;
        try
        {
            // Snapshot running TIA pids so we can identify the process we are about to start.
            var knownPids = new HashSet<int>();
            try
            {
                foreach (var p in TiaPortal.GetProcesses())
                    knownPids.Add(p.Id);
            }
            catch { /* pid tracking is best-effort; liveness falls back to safe behavior */ }

            _portal = new TiaPortal(mode);
            _ownsPortal = true;
            _project = _portal.Projects.Open(new FileInfo(projectPath));

            try
            {
                _portalProcessId = TiaPortal.GetProcesses()
                    .Where(p => !knownPids.Contains(p.Id))
                    .Select(p => (int?)p.Id)
                    .FirstOrDefault();
            }
            catch { /* best-effort, as above */ }
        }
        catch (Exception ex)
        {
            // A failed open must not leak the portal process.
            try { _portal?.Dispose(); } catch { }
            _portal = null;
            _project = null;
            _portalProcessId = null;
            throw new AdapterException("PROJECT_NOT_FOUND",
                $"Could not open project '{projectPath}': {ex.Message}",
                "Check the project exists, is a V17 project, and is not open in another TIA instance.");
        }

        return new ConnectionInfo
        {
            Attached = false,
            HasUI = withUI,
            ProjectName = _project!.Name,
            ProjectPath = _project.Path?.FullName,
        };
    }

    public DisconnectResult Disconnect()
    {
        lock (_gate)
        {
            var result = new DisconnectResult { WasConnected = _portal is not null };
            if (result.WasConnected)
            {
                try { result.HadUnsavedChanges = _project?.IsModified ?? false; } catch { }
                if (_ownsPortal)
                {
                    // We own this portal: close the project we opened. Never saves (§1.1).
                    try { _project?.Close(); } catch { }
                }
                // Attached mode: release our handles only — never close the user's project.
                try { _portal?.Dispose(); } catch { }
            }
            _project = null;
            _portal = null;
            _ownsPortal = false;
            _portalProcessId = null;
            return result;
        }
    }

    public void CloseSession(int sessionId)
    {
        // Use System.Diagnostics.Process to send a close signal (WM_CLOSE) to the TIA window.
        // This is the same as clicking the X button — the user can save or discard changes.
        // For headless sessions (our own), disposal during disconnect already cleans up.
        lock (_gate)
        {
            try
            {
                var process = Process.GetProcessById(sessionId);
                if (!process.CloseMainWindow())
                {
                    // CloseMainWindow returned false (no window or already closing).
                    // If still running after a short wait, the session may be headless.
                    if (!process.WaitForExit(3000))
                    {
                        // Force-close only headless sessions (no window) that linger
                        if (process.MainWindowHandle == IntPtr.Zero)
                        {
                            process.Kill();
                            process.WaitForExit(3000);
                        }
                    }
                }
            }
            catch (ArgumentException ex)
            {
                throw new AdapterException("SESSION_NOT_FOUND",
                    $"No running TIA Portal process with id {sessionId}: {ex.Message}");
            }
            catch (Exception ex)
            {
                throw new AdapterException("CLOSE_FAILED",
                    $"Failed to close TIA session {sessionId}: {ex.Message}");
            }
        }
    }

    public void SaveProject()
    {
        lock (_gate)
        {
            RequireProject().Save();
        }
    }

    public ProjectInfo GetProjectInfo()
    {
        lock (_gate)
        {
            var project = RequireProject();
            var plcs = PlcSoftwareResolver.FindAll(project);
            return new ProjectInfo
            {
                Name = project.Name,
                Path = project.Path?.FullName,
                PlcDevices = plcs.Select(p => p.Name).ToArray(),
                BlockCount = plcs.Sum(p => BlockEnumerator.Enumerate(p.BlockGroup).Count()),
                LastModified = project.LastModified,
            };
        }
    }

    public BlockInfo[] ListBlocks(string? plcName)
    {
        lock (_gate)
        {
            var plc = PlcSoftwareResolver.Resolve(RequireProject(), plcName);
            return BlockEnumerator.Enumerate(plc.BlockGroup)
                .Select(x => new BlockInfo
                {
                    Name = x.Block.Name,
                    Number = x.Block.Number,
                    BlockType = x.Block.GetType().Name,
                    ProgrammingLanguage = x.Block.ProgrammingLanguage.ToString(),
                    GroupPath = x.GroupPath,
                })
                .OrderBy(b => b.BlockType)
                .ThenBy(b => b.Number)
                .ToArray();
        }
    }

    public PlcChecksumInfo[] GetPlcChecksums(string? plcName = null)
    {
        lock (_gate)
        {
            var project = RequireProject();
            var plcs = plcName is null
                ? PlcSoftwareResolver.FindAll(project)
                : new[] { PlcSoftwareResolver.Resolve(project, plcName) };
            var projectIdentity = project.Path?.FullName ?? project.Name;

            return plcs
                .Select(plc => new PlcChecksumInfo
                {
                    PlcName = plc.Name,
                    ProjectIdentity = projectIdentity,
                    SoftwareChecksum = TryReadSoftwareChecksum(plc),
                })
                .OrderBy(info => info.PlcName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    public ExportResult ExportBlock(string blockName, string outputDir)
    {
        lock (_gate)
        {
            var plc = PlcSoftwareResolver.Resolve(RequireProject(), null);
            var (block, groupPath) = BlockEnumerator.FindWithPath(plc.BlockGroup, blockName);
            var device = CaptureDeviceMetadata(RequireProject(), plc);
            Directory.CreateDirectory(outputDir);
            try
            {
                if (FailSafeBlocks.IsFailSafe(block))
                {
                    throw new AdapterException("BLOCK_EXPORT_NOT_PERMITTED",
                        $"Block '{blockName}' is a fail-safe block ({block.ProgrammingLanguage}); TIA Openness does not permit exporting F-blocks.");
                }
                if (!block.IsConsistent)
                {
                    throw new AdapterException("BLOCK_INCONSISTENT",
                        $"Block '{blockName}' is inconsistent. Compile it first before export.");
                }
                var result = ExportCore(block, outputDir, groupPath);
                ExportManifest.Upsert(outputDir, ExportManifest.CreateRecord(block, groupPath, outputDir, result), device);
                return result;
            }
            catch (Exception ex)
            {
                // Even a failed single export leaves a Failed record in the manifest (§5.2 evidence).
                // Never mask the original error with a manifest-write failure.
                var failed = new ExportResult
                {
                    BlockName = block.Name,
                    Success = false,
                    Error = ex.Message,
                    ExportedAt = DateTime.Now,
                };
                try { ExportManifest.Upsert(outputDir, ExportManifest.CreateRecord(block, groupPath, outputDir, failed), device); }
                catch { }
                throw;
            }
        }
    }

    public ExportResult[] ExportAllBlocks(string outputDir, IProgress<EngineeringProgress>? progress = null)
    {
        lock (_gate)
        {
            var plcs = PlcSoftwareResolver.FindAll(RequireProject());
            var results = new List<ExportResult>();
            foreach (var plc in plcs)
            {
                // Per-device subfolder, each its own export root with its own metadata.json.
                var dir = Path.Combine(outputDir, Sanitize(plc.Name));
                results.AddRange(ExportAllBlocksForPlc(plc, dir, progress: progress));
            }
            return results.ToArray();
        }
    }

    /// <summary>Per-PLC body of <see cref="ExportAllBlocks"/> (also used by sync_export when no
    /// baseline manifest exists): export every block, rewrite the block categories of the manifest,
    /// and stamp the current software checksum into project metadata as the sync gate value.</summary>
    private List<ExportResult> ExportAllBlocksForPlc(
        PlcSoftware plc,
        string dir,
        bool writeProjectMetadata = true,
        IProgress<EngineeringProgress>? progress = null,
        List<UnsupportedSourceObject>? unsupported = null)
    {
        Directory.CreateDirectory(dir);
        var exportStartedUtc = DateTimeOffset.UtcNow;
        var results = new List<ExportResult>();
        var records = new List<ExportMetadataRecord>();
        var blocks = new List<(PlcBlock Block, string? GroupPath)>();
        foreach (var item in BlockEnumerator.Enumerate(plc.BlockGroup))
        {
            if (FailSafeBlocks.IsFailSafe(item.Block))
            {
                // F-blocks cannot be exported via Openness; skip them instead of failing the export.
                _logger.LogWarning(
                    "export_all_blocks: SKIPPED fail-safe block {Block} (ProgrammingLanguage: {Language}) — TIA Openness does not permit exporting F-blocks",
                    item.Block.Name, item.Block.ProgrammingLanguage);
                Report(progress, $"Skipping fail-safe block {item.Block.Name} (TIA Openness cannot export F-blocks)...");
                unsupported?.Add(new UnsupportedSourceObject
                {
                    Name = item.Block.Name,
                    Category = "FailSafeBlock",
                    Reason = "TIA_EXPORT_UNSUPPORTED",
                });
                continue;
            }

            blocks.Add(item);
        }

        _logger.LogInformation("export_all_blocks: {Count} blocks to export ({Plc})", blocks.Count, plc.Name);
        var index = 0;
        foreach (var (block, groupPath) in blocks)
        {
            index++;
            ExportResult result;
            try
            {
                Report(progress, $"Exporting block {block.Name}...");
                result = ExportCore(block, dir, groupPath);
            }
            catch (Exception ex) when (FailSafeBlocks.IsExportNotPermitted(ex))
            {
                // Openness refused the export outright (fail-safe block the language prefix did
                // not identify) — skip it instead of failing the whole device export.
                _logger.LogWarning(
                    "export_all_blocks: SKIPPED {Block} — Openness: export not permitted (ProgrammingLanguage: {Language})",
                    block.Name, block.ProgrammingLanguage);
                Report(progress, $"Skipping block {block.Name} (TIA Openness: export not permitted)...");
                unsupported?.Add(new UnsupportedSourceObject
                {
                    Name = block.Name,
                    Category = block.GetType().Name,
                    Reason = "TIA_EXPORT_UNSUPPORTED",
                });
                continue;
            }
            catch (Exception ex)
            {
                result = new ExportResult
                {
                    BlockName = block.Name,
                    Success = false,
                    Error = ex.Message,
                    ExportedAt = DateTime.Now,
                };
            }

            if (!result.Success)
            {
                _logger.LogWarning("export_all_blocks: FAILED {Block} — {Error}", block.Name, result.Error);
            }
            else if (index % 25 == 0 || index == blocks.Count)
            {
                _logger.LogInformation("export_all_blocks: {Index}/{Total} ({Plc})", index, blocks.Count, plc.Name);
            }

            results.Add(result);
            records.Add(ExportManifest.CreateRecord(block, groupPath, dir, result));
        }

        ExportManifest.WriteAll(
            dir, exportStartedUtc, records, ExportManifest.BlockCategories, CaptureDeviceMetadata(RequireProject(), plc));
        if (writeProjectMetadata)
        {
            ProjectMetadata.SetPlcSoftwareChecksum(
                Path.GetDirectoryName(dir)!, plc.Name, TryReadSoftwareChecksum(plc));
        }
        return results;
    }

    public SyncResult[] SyncExport(string outputDir, string? plcName, IProgress<EngineeringProgress>? progress = null)
    {
        lock (_gate)
        {
            var project = RequireProject();
            var plcs = plcName is null
                ? PlcSoftwareResolver.FindAll(project)
                : new[] { PlcSoftwareResolver.Resolve(project, plcName) };
            var results = new List<SyncResult>();
            foreach (var plc in plcs)
            {
                // Per-device export-root subfolder (same rule as the export tools).
                var dir = Path.Combine(outputDir, Sanitize(plc.Name));
                results.Add(SyncExportForPlc(plc, dir, progress));
            }
            // Update project metadata with the complete device list discovered.
            if (plcName is null)
            {
                var deviceNames = plcs.Select(p => p.Name).ToList();
                var checksums = new Dictionary<string, string>();
                foreach (var plc in plcs)
                {
                    var cs = TryReadSoftwareChecksum(plc);
                    if (cs is not null) checksums[plc.Name] = cs;
                }
                var meta = new ProjectMetadataDocument
                {
                    PlcDevices = deviceNames,
                    PlcSoftwareChecksums = checksums,
                };
                ProjectMetadata.Write(outputDir, meta);
            }
            return results.ToArray();
        }
    }

    /// <summary>Per-PLC sync (buildnote/plan/export-sync.md): project-level checksum gate first;
    /// otherwise a timestamp-nominated, hash-confirmed diff that re-exports only real changes
    /// and rewrites the manifest (all categories) with the fresh gate value in project metadata.</summary>
    private SyncResult SyncExportForPlc(
        PlcSoftware plc,
        string dir,
        IProgress<EngineeringProgress>? progress = null)
    {
        var checksum = TryReadSoftwareChecksum(plc);
        var projectRoot = Path.GetDirectoryName(dir)!;
        var result = new SyncResult
        {
            PlcName = plc.Name,
            ExportRoot = dir,
            ChecksumAfter = checksum,
        };

        var storedChecksum = ProjectMetadata.GetPlcSoftwareChecksum(projectRoot, plc.Name);

        if (!ExportManifest.TryRead(dir, out var manifest) || manifest is null)
        {
            // No baseline manifest → full export for this PLC (blocks rewrite the manifest and
            // stamp the checksum in project metadata; tags/UDTs upsert into it afterwards).
            var full = new List<ExportResult>();
            full.AddRange(ExportAllBlocksForPlc(plc, dir, progress: progress));
            full.AddRange(ExportObjectsForPlc(plc, dir, "export_tag_tables", p => TagTableEnumerator.Enumerate(p.TagTableGroup), ExportTagTableCore, CreateTagTableRecord, progress));
            full.AddRange(ExportObjectsForPlc(plc, dir, "export_udts", p => PlcTypeEnumerator.Enumerate(p.TypeGroup), ExportUdtCore, CreateUdtRecord, progress));
            result.Status = "updated";
            result.Added = full.Where(r => r.Success).Select(r => new SyncChange { Name = r.BlockName, Reason = "no-baseline" }).ToArray();
            result.Failed = full.Where(r => !r.Success).Select(r => new SyncChange { Name = r.BlockName, Reason = r.Error }).ToArray();
            return result;
        }

        result.ChecksumBefore = storedChecksum;
        result.BaselineExisted = true;

        // Tier 0 gate: a matching project-level checksum proves nothing changed — no exports.
        if (checksum is not null && checksum == storedChecksum)
        {
            result.Status = "unchanged";
            return result;
        }

        // Tier 1: flatten live objects to plain values (id formula shared with the manifest) and diff.
        var snapshot = CaptureLiveSnapshot(plc);
        var live = snapshot.Live;
        var blocksById = snapshot.BlocksById;
        var tablesById = snapshot.TablesById;
        var typesById = snapshot.TypesById;

        // Tier 2: execute the plan — re-export candidates, hash each result, classify, delete removals.
        var plan = SyncPlanner.Plan(manifest.Components, live, VerifiedLocalFileIds(dir, manifest.Components));
        if (live.Count > 0 && live.All(item => item.Fingerprints is null))
        {
            _logger.LogWarning(
                "sync_export: 0/{Total} live items have fingerprints (last read error: {Error}) — falling back to the timestamp path",
                live.Count, FingerprintReader.LastError ?? "(none)");
        }
        var keptRecords = new List<ExportMetadataRecord>();
        var added = new List<SyncChange>();
        var changed = new List<SyncChange>();
        var touched = new List<SyncChange>();
        var removed = new List<SyncChange>();
        var failed = new List<SyncChange>();
        foreach (var item in plan)
        {
            switch (item.Action)
            {
                case SyncAction.Skip:
                    keptRecords.Add(item.Record!);
                    break;

                case SyncAction.Remove:
                    DeleteComponentFile(dir, item.Record!);
                    removed.Add(ToChange(item.Record!, item.Reason));
                    break;

                case SyncAction.UpdateRecord:
                    // Legacy fingerprint backfill: timestamps proved the content stood still —
                    // stamp the live fingerprints onto the kept record, no export.
                    item.Record!.Fingerprints = item.Live!.Fingerprints;
                    keptRecords.Add(item.Record!);
                    touched.Add(ToChange(item.Record!, item.Reason));
                    break;

                case SyncAction.ReExport:
                    ExportMetadataRecord record;
                    ExportResult exportResult;
                    try
                    {
                        record = ReExportComponent(dir, item.Live!, blocksById, tablesById, typesById, progress, out exportResult);
                    }
                    catch (Exception ex) when (FailSafeBlocks.IsExportNotPermitted(ex))
                    {
                        // Openness refused the export outright (fail-safe component the language
                        // prefix did not identify) — skip it and keep any previous record.
                        _logger.LogWarning(
                            "sync_export: SKIPPED {Component} — Openness: export not permitted",
                            item.Live!.Name);
                        Report(progress, $"Skipping {item.Live!.Name} (TIA Openness: export not permitted)...");
                        if (item.Record is not null)
                        {
                            keptRecords.Add(item.Record);
                        }
                        break;
                    }

                    var change = ToChange(record, item.Reason);
                    if (!exportResult.Success)
                    {
                        // Keep the last-known-good record when there is one: a Failed stub would
                        // lose exportedFile/hash/fingerprints — and the later recovery would then
                        // misreport as "added" instead of "changed" (bug found 2026-07-21).
                        keptRecords.Add(item.Record ?? record);
                        change.Reason = exportResult.Error;
                        failed.Add(change);
                        break;
                    }

                    keptRecords.Add(record);
                    if (item.Record is null || !string.Equals(item.Record.Status, "Exported", StringComparison.OrdinalIgnoreCase))
                    {
                        // New component, or first successful export after a Failed record.
                        added.Add(change);
                    }
                    else if (item.Reason == SyncPlanner.ReasonFingerprint)
                    {
                        // Detection was the fingerprint mismatch itself — the change is certain.
                        changed.Add(change);
                    }
                    else if (item.Record.ContentHash is not null
                        ? !string.Equals(record.ContentHash, item.Record.ContentHash, StringComparison.Ordinal)
                        : item.Reason != SyncPlanner.ReasonLegacyNoHash)
                    {
                        // Old hash present → it decides. Old hash absent (legacy) → timestamps
                        // disagreed (legacy-no-hash with agreeing timestamps lands in touched).
                        changed.Add(change);
                    }
                    else
                    {
                        touched.Add(change);
                    }
                    break;
            }
        }

        ExportManifest.WriteAll(
            dir, manifest.ExportStartedUtc, keptRecords, ExportManifest.AllCategories,
            CaptureDeviceMetadata(RequireProject(), plc));
        ProjectMetadata.SetPlcSoftwareChecksum(projectRoot, plc.Name, checksum);
        result.Status = "updated";
        result.Added = added.ToArray();
        result.Changed = changed.ToArray();
        result.Touched = touched.ToArray();
        result.Removed = removed.ToArray();
        result.Failed = failed.ToArray();
        _logger.LogInformation(
            "sync_export: {Plc} — {Added} added, {Changed} changed, {Touched} touched, {Removed} removed, {Failed} failed",
            plc.Name, added.Count, changed.Count, touched.Count, removed.Count, failed.Count);
        return result;
    }

    /// <summary>Full rebuild export (§full-rebuild). A selected PLC is exported directly to
    /// <paramref name="outputDir"/>; otherwise every PLC is exported to a per-device subfolder
    /// and project-level metadata is written. No incremental diff.</summary>
    public SyncResult[] RebuildExport(
        string outputDir,
        string? plcName = null,
        IProgress<EngineeringProgress>? progress = null)
    {
        lock (_gate)
        {
            var project = RequireProject();
            // Hardware configuration is exported only by the explicit export_hardware_configuration
            // flow (workbench "Reload hardware configuration"); device-level compare/refresh must
            // not pay the CAx export cost or touch the saved hardware baseline.
            var plcs = plcName is null
                ? PlcSoftwareResolver.FindAll(project)
                : new[] { PlcSoftwareResolver.Resolve(project, plcName) };
            var results = new List<SyncResult>();
            var allDeviceNames = new List<string>();
            var allChecksums = new Dictionary<string, string>();

            foreach (var plc in plcs)
            {
                var name = plc.Name;
                allDeviceNames.Add(name);
                var dir = plcName is null
                    ? Path.Combine(outputDir, Sanitize(name))
                    : outputDir;
                Directory.CreateDirectory(dir);

                var checksum = TryReadSoftwareChecksum(plc);
                if (checksum is not null) allChecksums[name] = checksum;

                // Full export: blocks rewrite the manifest; tags/UDTs upsert into it.
                var full = new List<ExportResult>();
                var unsupported = new List<UnsupportedSourceObject>();
                full.AddRange(ExportAllBlocksForPlc(
                    plc,
                    dir,
                    writeProjectMetadata: plcName is null,
                    progress: progress,
                    unsupported: unsupported));
                full.AddRange(ExportObjectsForPlc(plc, dir, "export_tag_tables",
                    p => TagTableEnumerator.Enumerate(p.TagTableGroup), ExportTagTableCore, CreateTagTableRecord, progress));
                full.AddRange(ExportObjectsForPlc(plc, dir, "export_udts",
                    p => PlcTypeEnumerator.Enumerate(p.TypeGroup), ExportUdtCore, CreateUdtRecord, progress));

                results.Add(new SyncResult
                {
                    PlcName = name,
                    ExportRoot = dir,
                    ChecksumAfter = checksum,
                    Status = "updated",
                    BaselineExisted = false,
                    Added = full.Where(r => r.Success).Select(r => new SyncChange { Name = r.BlockName, Reason = "full-rebuild" }).ToArray(),
                    Failed = full.Where(r => !r.Success).Select(r => new SyncChange { Name = r.BlockName, Reason = r.Error }).ToArray(),
                    Unsupported = unsupported.ToArray(),
                });

                _logger.LogInformation(
                    "rebuild_export: {Plc} — {Added} exported, {Failed} failed",
                    name, full.Count(r => r.Success), full.Count(r => !r.Success));
            }

            // Write project-level metadata with the complete device list and checksums.
            var projectMeta = new ProjectMetadataDocument
            {
                PlcDevices = allDeviceNames,
                PlcSoftwareChecksums = allChecksums,
            };
            if (plcName is null)
            {
                ProjectMetadata.Write(outputDir, projectMeta);
            }

            return results.ToArray();
        }
    }

    /// <summary>Exports the canonical project AML and optional device-level AML artifacts.
    /// CAx export is read-only with respect to TIA and writes only under the caller's output root.</summary>
    public HardwareExportResult[] ExportHardwareConfiguration(
        string outputDir,
        bool includeDeviceExports = true,
        IProgress<EngineeringProgress>? progress = null)
    {
        lock (_gate)
        {
            RequireProject();
            return ExportHardwareConfigurationCore(outputDir, includeDeviceExports, progress);
        }
    }

    private HardwareExportResult[] ExportHardwareConfigurationCore(
        string outputDir,
        bool includeDeviceExports,
        IProgress<EngineeringProgress>? progress)
    {
        if (string.IsNullOrWhiteSpace(outputDir))
            throw new AdapterException("EXPORT_DIRECTORY_REQUIRED", "An export output directory is required.");

        var project = RequireProject();
        // The caller already supplies the canonical worktree hardware root.
        // Keep the AML manifest directly under it so the layout is
        // <worktree>\\hardware\\manifest.json, not hardware\\Hardware.
        var hardwareRoot = outputDir;
        var deviceRoot = Path.Combine(hardwareRoot, "Devices");
        Directory.CreateDirectory(hardwareRoot);
        if (includeDeviceExports)
            Directory.CreateDirectory(deviceRoot);

        var results = new List<HardwareExportResult>();
        var projectAml = Path.Combine(hardwareRoot, "project.aml");
        var projectLog = Path.Combine(hardwareRoot, "project-export.log");
        progress?.Report(new EngineeringProgress("Exporting hardware configuration (project)..."));
        var projectResult = ExportCax(
            "project",
            null,
            null,
            projectAml,
            projectLog,
            export: cax => cax.Export(project, new FileInfo(projectAml), new FileInfo(projectLog)));
        results.Add(projectResult);

        var manifest = new HardwareExportManifest
        {
            ExportedAt = DateTimeOffset.UtcNow,
            ProjectAmlFile = ToManifestPath(hardwareRoot, projectAml),
            ProjectLogFile = ToManifestPath(hardwareRoot, projectLog),
            ProjectSuccess = projectResult.Success,
            ProjectError = projectResult.Error,
            ProjectContentHash = projectResult.ContentHash,
        };

        if (includeDeviceExports)
        {
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var device in EnumerateDevices(project))
            {
                var deviceName = device.Name;
                var folderName = UniqueSanitizedName(deviceName, usedNames);
                var folder = Path.Combine(deviceRoot, folderName);
                Directory.CreateDirectory(folder);
                var amlPath = Path.Combine(folder, "device.aml");
                var logPath = Path.Combine(folder, "export.log");
                var typeIdentifier = ReadDeviceTypeIdentifier(device);
                progress?.Report(new EngineeringProgress($"Exporting hardware configuration (device {deviceName})..."));

                var result = ExportCax(
                    "device",
                    deviceName,
                    typeIdentifier,
                    amlPath,
                    logPath,
                    export: cax => cax.Export(device, new FileInfo(amlPath), new FileInfo(logPath)));
                results.Add(result);
                manifest.Devices.Add(new HardwareExportManifestDevice
                {
                    DeviceName = deviceName,
                    TypeIdentifier = typeIdentifier,
                    AmlFile = ToManifestPath(hardwareRoot, amlPath),
                    LogFile = ToManifestPath(hardwareRoot, logPath),
                    Success = result.Success,
                    Error = result.Error,
                    ContentHash = result.ContentHash,
                    ExportedAt = new DateTimeOffset(result.ExportedAt),
                });
            }
        }

        var manifestPath = Path.Combine(hardwareRoot, "manifest.json");
        File.WriteAllText(manifestPath, HardwareExportManifestJsonSerializer.Serialize(manifest));
        return results.ToArray();
    }

    private HardwareExportResult ExportCax(
        string scope,
        string? deviceName,
        string? typeIdentifier,
        string amlPath,
        string logPath,
        Func<CaxProvider, bool> export)
    {
        var result = new HardwareExportResult
        {
            Scope = scope,
            DeviceName = deviceName,
            TypeIdentifier = typeIdentifier,
            AmlFilePath = amlPath,
            LogFilePath = logPath,
            ExportedAt = DateTime.UtcNow,
        };

        try
        {
            var logDirectory = Path.GetDirectoryName(logPath);
            if (!string.IsNullOrWhiteSpace(logDirectory))
                Directory.CreateDirectory(logDirectory);
            var cax = RequireProject().GetService<CaxProvider>();
            if (cax is null)
                throw new AdapterException(
                    "HARDWARE_EXPORT_UNAVAILABLE",
                    "TIA Openness did not expose the CaxProvider service for the connected project.",
                    "Use a TIA Portal V17 project with the CAx export service available.");

            result.Success = export(cax);
            if (!result.Success)
            {
                result.Error = "TIA Openness reported that the CAx export failed. See the export log.";
            }
            else
            {
                result.ContentHash = ContentHasher.TryCompute(amlPath);
            }
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
        }

        return result;
    }

    private static IEnumerable<Device> EnumerateDevices(Project project)
    {
        foreach (Device device in project.Devices)
            yield return device;
        foreach (DeviceUserGroup group in project.DeviceGroups)
        {
            foreach (var device in EnumerateDevices(group))
                yield return device;
        }
    }

    private static IEnumerable<Device> EnumerateDevices(DeviceUserGroup group)
    {
        foreach (Device device in group.Devices)
            yield return device;
        foreach (DeviceUserGroup child in group.Groups)
        {
            foreach (var device in EnumerateDevices(child))
                yield return device;
        }
    }

    private static string? ReadDeviceTypeIdentifier(Device device)
    {
        try
        {
            foreach (DeviceItem item in device.DeviceItems)
            {
                var identifier = ReadFirstTypeIdentifier(item);
                if (identifier is not null)
                    return identifier;
            }
        }
        catch
        {
            // Device identity is metadata only; it must not prevent the AML export.
        }

        return null;
    }

    private static string? ReadFirstTypeIdentifier(DeviceItem item)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(item.TypeIdentifier))
                return item.TypeIdentifier;
            foreach (DeviceItem child in item.DeviceItems)
            {
                var identifier = ReadFirstTypeIdentifier(child);
                if (identifier is not null)
                    return identifier;
            }
        }
        catch
        {
            // Some device items do not expose an order number; continue with the next item.
        }

        return null;
    }

    private static string UniqueSanitizedName(string name, ISet<string> usedNames)
    {
        var baseName = Sanitize(name);
        if (string.IsNullOrWhiteSpace(baseName))
            baseName = "device";
        var candidate = baseName;
        var suffix = 2;
        while (!usedNames.Add(candidate))
            candidate = $"{baseName}-{suffix++}";
        return candidate;
    }

    private static string ToManifestPath(string root, string path)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullPath = Path.GetFullPath(path);
        var relative = fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? fullPath.Substring(fullRoot.Length + 1)
            : fullPath;
        return relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    /// <summary>Live enumeration shared by sync and compare: blocks + tag tables + UDTs flattened
    /// to <see cref="SyncLiveComponent"/>, plus the object lookups the export cores need.</summary>
    private sealed class LiveSnapshot
    {
        public List<SyncLiveComponent> Live { get; } = new();
        public Dictionary<string, (PlcBlock Block, string? GroupPath)> BlocksById { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, (PlcTagTable Table, string? GroupPath)> TablesById { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, (PlcType Type, string? GroupPath)> TypesById { get; } = new(StringComparer.Ordinal);
    }

    private static LiveSnapshot CaptureLiveSnapshot(PlcSoftware plc)
    {
        var snapshot = new LiveSnapshot();
        foreach (var (block, groupPath) in BlockEnumerator.Enumerate(plc.BlockGroup))
        {
            // F-blocks cannot be exported via Openness; exclude them from sync planning.
            if (FailSafeBlocks.IsFailSafe(block))
            {
                continue;
            }

            var category = ExportManifest.CategoryOf(block);
            var sourcePath = ExportManifest.SourcePathOf(block.Name, groupPath);
            var id = StableId.Create(category, sourcePath);
            var (modified, codeModified, interfaceModified) = ReadBlockTimestamps(block);
            snapshot.Live.Add(new SyncLiveComponent
            {
                Id = id,
                Name = block.Name,
                Category = category,
                SourcePath = sourcePath,
                SiemensTypeName = block.GetType().Name,
                Fingerprints = FingerprintReader.TryRead(block),
                ModifiedDate = modified,
                CodeModifiedDate = codeModified,
                InterfaceModifiedDate = interfaceModified,
            });
            snapshot.BlocksById[id] = (block, groupPath);
        }

        foreach (var (table, groupPath) in TagTableEnumerator.Enumerate(plc.TagTableGroup))
        {
            var sourcePath = ExportManifest.SourcePathOf(table.Name, groupPath);
            var id = StableId.Create("Tags", sourcePath);
            snapshot.Live.Add(new SyncLiveComponent
            {
                Id = id,
                Name = table.Name,
                Category = "Tags",
                SourcePath = sourcePath,
                ModifiedDate = ReadTagTableModified(table),
            });
            snapshot.TablesById[id] = (table, groupPath);
        }

        foreach (var (type, groupPath) in PlcTypeEnumerator.Enumerate(plc.TypeGroup))
        {
            var sourcePath = ExportManifest.SourcePathOf(type.Name, groupPath);
            var id = StableId.Create("UDT", sourcePath);
            var (_, _, modified, interfaceModified) = ReadTypeMetadata(type);
            snapshot.Live.Add(new SyncLiveComponent
            {
                Id = id,
                Name = type.Name,
                Category = "UDT",
                SourcePath = sourcePath,
                Fingerprints = FingerprintReader.TryRead(type),
                ModifiedDate = modified,
                InterfaceModifiedDate = interfaceModified,
            });
            snapshot.TypesById[id] = (type, groupPath);
        }

        return snapshot;
    }

    /// <summary>Read-only per-component diff (buildnote/plan/export-sync.md §Compare): runs the same
    /// capture + planner as sync_export but executes nothing — entries report live vs stored
    /// fingerprints/timestamps and the planner's verdict per component. No exports, no writes.</summary>
    public ContextCompareResult[] CompareContext(string outputDir, string? plcName)
    {
        lock (_gate)
        {
            var project = RequireProject();
            var plcs = plcName is null
                ? PlcSoftwareResolver.FindAll(project)
                : new[] { PlcSoftwareResolver.Resolve(project, plcName) };
            var results = new List<ContextCompareResult>();
            foreach (var plc in plcs)
            {
                // Per-device export-root subfolder (same rule as the export/sync tools).
                var dir = Path.Combine(outputDir, Sanitize(plc.Name));
                var liveChecksum = TryReadSoftwareChecksum(plc);
                var manifestExists = ExportManifest.TryRead(dir, out var manifest) && manifest is not null;
                var records = manifestExists ? manifest!.Components : new List<ExportMetadataRecord>();
                var plan = SyncPlanner.Plan(records, CaptureLiveSnapshot(plc).Live, VerifiedLocalFileIds(dir, records));
                var entries = plan
                    .Select(item => new ContextCompareEntry
                    {
                        Name = item.Live?.Name ?? item.Record!.Name,
                        Category = item.Live?.Category ?? item.Record!.Category,
                        SourcePath = item.Live?.SourcePath ?? item.Record!.SourcePath,
                        LiveFingerprints = item.Live?.Fingerprints,
                        StoredFingerprints = item.Record?.Fingerprints,
                        FingerprintsMatch = item.Live?.Fingerprints is null || item.Record?.Fingerprints is null
                            ? null
                            : string.Equals(item.Live.Fingerprints, item.Record.Fingerprints, StringComparison.Ordinal),
                        LiveModifiedDate = item.Live?.ModifiedDate,
                        StoredModifiedDate = item.Record?.ModifiedDate,
                        State = CompareStateFor(item),
                    })
                    .OrderBy(entry => entry.Category, StringComparer.Ordinal)
                    .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                results.Add(new ContextCompareResult
                {
                    PlcName = plc.Name,
                    ExportRoot = dir,
                    ManifestExists = manifestExists,
                    StoredChecksum = ProjectMetadata.GetPlcSoftwareChecksum(outputDir, plc.Name),
                    LiveChecksum = liveChecksum,
                    Components = entries,
                });
            }
            return results.ToArray();
        }
    }

    private static string CompareStateFor(SyncPlanItem item) => item.Action switch
    {
        SyncAction.Skip => "same",
        // Timestamps agree; only the fingerprint backfill is pending → content is the same.
        SyncAction.UpdateRecord => "same",
        SyncAction.Remove => "missing",
        SyncAction.ReExport => item.Reason switch
        {
            SyncPlanner.ReasonNew => "new",
            SyncPlanner.ReasonFingerprint or SyncPlanner.ReasonTimestamp => "different",
            // Instance DBs can never be proven same without an export (system-side regeneration
            // moves neither fingerprints nor timestamps) — sync verifies them via hash.
            SyncPlanner.ReasonInstanceDbVerify => "unverifiable",
            // legacy-no-hash / unreadable-metadata / previous-export-failed: no reliable verdict.
            _ => "unknown",
        },
        _ => "unknown",
    };

    /// <summary>Read-only status per PLC export root (buildnote/plan/export-sync.md §UI): manifest
    /// presence + stored project-level checksum vs the live software checksum. No enumeration, no
    /// exports — cheap enough to run on every attach. The tier-1 diff stays exclusive to sync_export.</summary>
    public ContextStatusResult[] GetContextStatus(string outputDir, string? plcName)    {
        lock (_gate)
        {
            var project = RequireProject();
            var plcs = plcName is null
                ? PlcSoftwareResolver.FindAll(project)
                : new[] { PlcSoftwareResolver.Resolve(project, plcName) };
            var results = new List<ContextStatusResult>();
            foreach (var plc in plcs)
            {
                // Per-device export-root subfolder (same rule as the export/sync tools).
                var dir = Path.Combine(outputDir, Sanitize(plc.Name));
                var liveChecksum = TryReadSoftwareChecksum(plc);
                var manifestExists = ExportManifest.TryRead(dir, out var manifest) && manifest is not null;
                var storedChecksum = ProjectMetadata.GetPlcSoftwareChecksum(outputDir, plc.Name);
                results.Add(new ContextStatusResult
                {
                    PlcName = plc.Name,
                    ExportRoot = dir,
                    ManifestExists = manifestExists,
                    ComponentCount = manifestExists ? manifest!.Components.Count : 0,
                    StoredChecksum = storedChecksum,
                    LiveChecksum = liveChecksum,
                    State = !manifestExists
                        ? "no-baseline"
                        : liveChecksum is null || storedChecksum is null
                            ? "unknown"
                            : liveChecksum == storedChecksum
                                ? "in-sync"
                                : "changed",
                });
            }
            return results.ToArray();
        }
    }

    /// <summary>Re-exports one component the planner nominated and rebuilds its manifest record
    /// (with fresh timestamps and content hash, via the normal CreateRecord paths).</summary>
    private static ExportMetadataRecord ReExportComponent(
        string dir,
        SyncLiveComponent live,
        IReadOnlyDictionary<string, (PlcBlock Block, string? GroupPath)> blocksById,
        IReadOnlyDictionary<string, (PlcTagTable Table, string? GroupPath)> tablesById,
        IReadOnlyDictionary<string, (PlcType Type, string? GroupPath)> typesById,
        IProgress<EngineeringProgress>? progress,
        out ExportResult exportResult)
    {
        switch (live.Category)
        {
            case "Tags":
                var (table, tablePath) = tablesById[live.Id];
                Report(progress, $"Exporting tag table {table.Name}...");
                exportResult = ExportTagTableCore(table, dir, tablePath);
                return CreateTagTableRecord(table, tablePath, dir, exportResult);
            case "UDT":
                var (type, typePath) = typesById[live.Id];
                Report(progress, $"Exporting UDT {type.Name}...");
                exportResult = ExportUdtCore(type, dir, typePath);
                return CreateUdtRecord(type, typePath, dir, exportResult);
            default:
                var (block, blockPath) = blocksById[live.Id];
                Report(progress, $"Exporting block {block.Name}...");
                exportResult = ExportCore(block, dir, blockPath);
                return ExportManifest.CreateRecord(block, blockPath, dir, exportResult);
        }
    }

    /// <summary>Deletes a removed component's XML. The manifest is user-writable, so the recorded
    /// relative path is re-validated to resolve inside the export root before anything is deleted;
    /// failures are logged, never thrown (the record is dropped either way).</summary>
    private void DeleteComponentFile(string exportRoot, ExportMetadataRecord record)
    {
        if (record.ExportedFile is null)
        {
            return;
        }

        var root = Path.GetFullPath(exportRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(root, record.ExportedFile));
        if (!fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("sync_export: refusing to delete outside the export root: {Path}", record.ExportedFile);
            return;
        }

        try
        {
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("sync_export: failed to delete {Path}: {Error}", fullPath, ex.Message);
        }
    }

    private static SyncChange ToChange(ExportMetadataRecord record, string? reason) => new()
    {
        Name = record.Name,
        Category = record.Category,
        SourcePath = record.SourcePath,
        ExportedFile = record.ExportedFile,
        Reason = reason,
    };

    /// <summary>Ids of manifest records whose exported file on disk still hashes to the recorded
    /// content hash — proof the local export was not modified in place since the last export
    /// (local edits belong in the modified-source overlay, never in the export folder itself).
    /// The manifest is user-writable, so the recorded relative path is re-validated to resolve
    /// inside the export root before it is read (same jail as <see cref="DeleteComponentFile"/>).</summary>
    private static HashSet<string> VerifiedLocalFileIds(string exportRoot, IReadOnlyList<ExportMetadataRecord> records)
    {
        var root = Path.GetFullPath(exportRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var verified = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            if (record.ContentHash is null || record.ExportedFile is null)
            {
                continue;
            }

            var fullPath = Path.GetFullPath(Path.Combine(root, record.ExportedFile));
            if (!fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(ContentHasher.TryCompute(fullPath), record.ContentHash, StringComparison.Ordinal))
            {
                verified.Add(record.Id);
            }
        }

        return verified;
    }

    /// <summary>Station-level software checksum (PlcChecksumProvider.Software): null when the PLC
    /// does not support checksums (GetService → null) or the program is not compiled (Software →
    /// null). Verified byte-identical to the TIA UI value on PLC_1/TestPLCExportDemo, 2026-07-20
    /// (scripts/Probe-PlcChecksum.ps1).</summary>
    private static string? TryReadSoftwareChecksum(PlcSoftware plc)
    {
        try
        {
            return plc.GetService<PlcChecksumProvider>()?.Software;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Project- and device-level metadata for the manifest's device section (lets a UI
    /// page display PLC type / project author/comment/version without a live TIA session).
    /// Guarded throughout — a failed Openness read degrades the capture to null rather than
    /// failing the export it rides along with.</summary>
    private static DeviceMetadata? CaptureDeviceMetadata(Project project, PlcSoftware plc)
    {
        try
        {
            var (deviceName, typeIdentifier) = ReadDeviceIdentity(plc);
            return new DeviceMetadata
            {
                PlcName = plc.Name,
                DeviceName = deviceName,
                TypeIdentifier = typeIdentifier,
                ProjectName = TryRead(() => project.Name),
                ProjectAuthor = TryRead(() => project.Author),
                ProjectComment = TryRead(() => ReadMultilingual(project.Comment)),
                ProjectVersion = TryRead(() => project.Version),
                ProjectCopyright = TryRead(() => project.Copyright),
                ProjectCreationTime = TryRead(() => (DateTimeOffset?)project.CreationTime),
                ProjectLastModified = TryRead(() => (DateTimeOffset?)project.LastModified),
                ProjectLastModifiedBy = TryRead(() => project.LastModifiedBy),
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Walks from the PLC software up the device tree: the first (deepest) device-item
    /// TypeIdentifier is the CPU module's order number + firmware version; the Device name is the
    /// station name. Each read is guarded — items without an identifier degrade to null.</summary>
    private static (string? DeviceName, string? TypeIdentifier) ReadDeviceIdentity(PlcSoftware plc)
    {
        string? deviceName = null;
        string? typeIdentifier = null;
        try
        {
            for (var node = plc.Parent; node is not null; node = node.Parent)
            {
                if (typeIdentifier is null && node is DeviceItem item)
                {
                    typeIdentifier = TryRead(() => item.TypeIdentifier);
                }

                if (node is Device device)
                {
                    deviceName = TryRead(() => device.Name);
                    break; // Station level reached — the PLC module sits below it.
                }
            }
        }
        catch
        {
            // Parent chain unreadable — return whatever was collected so far.
        }

        return (deviceName, typeIdentifier);
    }

    private static T? TryRead<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch
        {
            return default;
        }
    }

    /// <summary>First entry of a multilingual text — project comments are single-language in
    /// practice; null when the text has no entries.</summary>
    private static string? ReadMultilingual(MultilingualText? text)
    {
        if (text is null)
        {
            return null;
        }

        foreach (MultilingualTextItem item in text.Items)
        {
            return item.Text;
        }

        return null;
    }

    /// <summary>Block timestamps for the sync diff — same guarded read as the manifest metadata
    /// (know-how-protected blocks can throw; nulls make the planner re-export conservatively).</summary>
    private static (DateTimeOffset? Modified, DateTimeOffset? CodeModified, DateTimeOffset? InterfaceModified)
        ReadBlockTimestamps(PlcBlock block)
    {
        try
        {
            return (block.ModifiedDate, block.CodeModifiedDate, block.InterfaceModifiedDate);
        }
        catch
        {
            return (null, null, null);
        }
    }

    /// <summary>Exports into &lt;exportRoot&gt;/Blocks/ or &lt;exportRoot&gt;/DB/ (created as needed), depending on the block category.</summary>
    private static ExportResult ExportCore(PlcBlock block, string exportRoot, string? groupPath = null)
    {
        if (!block.IsConsistent)
        {
            return new ExportResult
            {
                BlockName = block.Name,
                Success = false,
                Error = $"Block '{block.Name}' is inconsistent. Compile it first before export.",
                ExportedAt = DateTime.Now,
            };
        }

        var category = ExportManifest.CategoryOf(block);
        var relativePath = SourceExportPath.Build(
            ExportManifest.FolderFor(category),
            groupPath,
            $"{Sanitize(block.Name)} [{TypeCode(block)}{block.Number}].xml");
        var path = Path.Combine(exportRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // V17 PlcBlock.Export refuses to overwrite an existing file (verified 2026-07-18) —
        // replace our own previous export.
        if (File.Exists(path))
            File.Delete(path);
        block.Export(new FileInfo(path), ExportOptions.WithDefaults);
        return new ExportResult
        {
            BlockName = block.Name,
            BlockNumber = block.Number,
            BlockType = block.GetType().Name,
            Path = path,
            Success = true,
            ExportedAt = DateTime.Now,
        };
    }

    public ExportResult[] ExportTagTables(
        string outputDir,
        string? plcName,
        IProgress<EngineeringProgress>? progress = null)
    {
        lock (_gate)
        {
            return ExportObjects(
                outputDir,
                plcName,
                "export_tag_tables",
                plc => TagTableEnumerator.Enumerate(plc.TagTableGroup),
                ExportTagTableCore,
                CreateTagTableRecord,
                progress);
        }
    }

    public ExportResult[] ExportUdts(
        string outputDir,
        string? plcName,
        IProgress<EngineeringProgress>? progress = null)
    {
        lock (_gate)
        {
            return ExportObjects(
                outputDir,
                plcName,
                "export_udts",
                plc => PlcTypeEnumerator.Enumerate(plc.TypeGroup),
                ExportUdtCore,
                CreateUdtRecord,
                progress);
        }
    }

    private static ExportMetadataRecord CreateTagTableRecord(PlcTagTable table, string? groupPath, string root, ExportResult result) =>
        ExportManifest.CreateRecord(
            table.Name, "Tags", table.GetType().Name, groupPath, root, result,
            modifiedDate: ReadTagTableModified(table));

    private static ExportMetadataRecord CreateUdtRecord(PlcType type, string? groupPath, string root, ExportResult result)
    {
        var (khp, created, modified, interfaceModified) = ReadTypeMetadata(type);
        return ExportManifest.CreateRecord(
            type.Name, "UDT", type.GetType().Name, groupPath, root, result,
            isKnowHowProtected: khp,
            creationDate: created,
            modifiedDate: modified,
            interfaceModifiedDate: interfaceModified,
            fingerprints: FingerprintReader.TryRead(type));
    }

    /// <summary>Shared export loop for tag tables and UDTs: per-PLC export root (subfolder when
    /// multiple PLCs), one manifest upsert per object. exportCore converts per-object failures
    /// into Failed results — it never throws for them.</summary>
    private ExportResult[] ExportObjects<TObject>(
        string outputDir,
        string? plcName,
        string label,
        Func<PlcSoftware, IEnumerable<(TObject Item, string? GroupPath)>> enumerate,
        Func<TObject, string, string?, ExportResult> exportCore,
        Func<TObject, string?, string, ExportResult, ExportMetadataRecord> createRecord,
        IProgress<EngineeringProgress>? progress = null)
    {
        var project = RequireProject();
        var plcs = plcName is null
            ? PlcSoftwareResolver.FindAll(project)
            : new[] { PlcSoftwareResolver.Resolve(project, plcName) };
        var results = new List<ExportResult>();
        foreach (var plc in plcs)
        {
            // Per-device subfolder, each its own export root with its own metadata.json.
            var dir = Path.Combine(outputDir, Sanitize(plc.Name));
            results.AddRange(ExportObjectsForPlc(plc, dir, label, enumerate, exportCore, createRecord, progress));
        }
        return results.ToArray();
    }

    /// <summary>Per-PLC body of <see cref="ExportObjects{TObject}"/> (also used by sync_export
    /// when no baseline manifest exists).</summary>
    private ExportResult[] ExportObjectsForPlc<TObject>(
        PlcSoftware plc,
        string dir,
        string label,
        Func<PlcSoftware, IEnumerable<(TObject Item, string? GroupPath)>> enumerate,
        Func<TObject, string, string?, ExportResult> exportCore,
        Func<TObject, string?, string, ExportResult, ExportMetadataRecord> createRecord,
        IProgress<EngineeringProgress>? progress = null)
    {
        Directory.CreateDirectory(dir);
        var device = CaptureDeviceMetadata(RequireProject(), plc);
        var results = new List<ExportResult>();
        var items = enumerate(plc).ToArray();
        _logger.LogInformation("{Label}: {Count} objects to export ({Plc})", label, items.Length, plc.Name);
        var index = 0;
        foreach (var (item, groupPath) in items)
        {
            index++;
            Report(progress, $"Exporting {ExportKind(label)} {ExportName(item)}...");
            var result = exportCore(item, dir, groupPath);
            if (!result.Success)
            {
                _logger.LogWarning("{Label}: FAILED {Name} — {Error}", label, result.BlockName, result.Error);
            }
            else if (index % 25 == 0 || index == items.Length)
            {
                _logger.LogInformation("{Label}: {Index}/{Total} ({Plc})", label, index, items.Length, plc.Name);
            }

            results.Add(result);
            ExportManifest.Upsert(dir, createRecord(item, groupPath, dir, result), device);
        }
        return results.ToArray();
    }

    private static ExportResult ExportTagTableCore(PlcTagTable table, string exportRoot, string? groupPath = null)
    {
        var relativePath = SourceExportPath.Build("Tags", groupPath, $"{Sanitize(table.Name)}.xml");
        var path = Path.Combine(exportRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            // V17 Export refuses to overwrite an existing file (verified for blocks 2026-07-18) —
            // replace our own previous export.
            if (File.Exists(path))
                File.Delete(path);
            table.Export(new FileInfo(path), ExportOptions.WithDefaults);
        }
        catch (Exception ex)
        {
            return Failure(table.Name, "Tags", ex.Message);
        }
        return new ExportResult
        {
            BlockName = table.Name,
            BlockType = "Tags",
            Path = path,
            Success = true,
            ExportedAt = DateTime.Now,
        };
    }

    private static ExportResult ExportUdtCore(PlcType type, string exportRoot, string? groupPath = null)
    {
        if (!type.IsConsistent)
        {
            return Failure(type.Name, "UDT",
                $"UDT '{type.Name}' is inconsistent. Compile it first before export.");
        }
        var relativePath = SourceExportPath.Build("UDT", groupPath, $"{Sanitize(type.Name)}.xml");
        var path = Path.Combine(exportRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            if (File.Exists(path))
                File.Delete(path);
            type.Export(new FileInfo(path), ExportOptions.WithDefaults);
        }
        catch (Exception ex)
        {
            return Failure(type.Name, "UDT", ex.Message);
        }
        return new ExportResult
        {
            BlockName = type.Name,
            BlockType = "UDT",
            Path = path,
            Success = true,
            ExportedAt = DateTime.Now,
        };
    }

    private static ExportResult Failure(string name, string kind, string error) => new()
    {
        BlockName = name,
        BlockType = kind,
        Success = false,
        Error = error,
        ExportedAt = DateTime.Now,
    };

    /// <summary>PlcTagTable exposes only ModifiedTimeStamp (openness-v17-api-surface.md §9); guarded like block metadata.</summary>
    private static void Report(IProgress<EngineeringProgress>? progress, string message)
    {
        try
        {
            progress?.Report(new EngineeringProgress(message));
        }
        catch
        {
            // Progress is informational only; it must never change export behavior.
        }
    }

    private static string ExportKind(string label) =>
        label == "export_tag_tables" ? "tag table" : "UDT";

    private static string ExportName<TObject>(TObject item) => item switch
    {
        PlcTagTable table => table.Name,
        PlcType type => type.Name,
        _ => "component",
    };

    private static DateTimeOffset? ReadTagTableModified(PlcTagTable table)
    {
        try { return table.ModifiedTimeStamp; } catch { return null; }
    }

    /// <summary>PlcType metadata (openness-v17-api-surface.md §9); guarded like block metadata.</summary>
    private static (bool? Khp, DateTimeOffset? Created, DateTimeOffset? Modified, DateTimeOffset? InterfaceModified)
        ReadTypeMetadata(PlcType type)
    {
        try
        {
            return (type.IsKnowHowProtected, type.CreationDate, type.ModifiedDate, type.InterfaceModifiedDate);
        }
        catch
        {
            return (null, null, null, null);
        }
    }

    private static string TypeCode(PlcBlock block) => block.GetType().Name switch
    {
        "GlobalDB" or "InstanceDB" => "DB",
        var name => name,
    };

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    public SourceObjectImportResult ImportSourceObject(
        string relativePath,
        string xmlFilePath,
        string? plcName = null)
    {
        lock (_gate)
        {
            if (!File.Exists(xmlFilePath))
                throw new AdapterException("XML_NOT_FOUND", $"XML file not found: {xmlFilePath}");

            var location = ParseSourceObjectPath(relativePath);
            var plc = PlcSoftwareResolver.Resolve(RequireProject(), plcName);
            var importedCount = 0;

            try
            {
                // Exclusive access + transaction is mandatory for every TIA-side write.
                using var exclusiveAccess = _portal!.ExclusiveAccess("Import source object " + location.ObjectName);
                using var transaction = exclusiveAccess.Transaction(RequireProject(), "Import source object " + location.ObjectName);

                switch (location.Kind)
                {
                    case SourceObjectKind.Block:
                    {
                        var group = ResolveBlockGroup(plc, location.GroupSegments);
                        EnsureExistingBlock(group, location.ObjectName, relativePath);
                        importedCount = group.Blocks.Import(new FileInfo(xmlFilePath), ImportOptions.Override).Count;
                        break;
                    }
                    case SourceObjectKind.TagTable:
                    {
                        var group = ResolveTagTableGroup(plc, location.GroupSegments);
                        EnsureExistingTagTable(group, location.ObjectName, relativePath);
                        importedCount = group.TagTables.Import(new FileInfo(xmlFilePath), ImportOptions.Override).Count;
                        break;
                    }
                    case SourceObjectKind.Udt:
                    {
                        var group = ResolveTypeGroup(plc, location.GroupSegments);
                        EnsureExistingType(group, location.ObjectName, relativePath);
                        importedCount = group.Types.Import(new FileInfo(xmlFilePath), ImportOptions.Override).Count;
                        break;
                    }
                    default:
                        throw new AdapterException("SOURCE_PATH_INVALID", $"Unsupported source object kind in '{relativePath}'.");
                }

                transaction.CommitOnDispose();
            }
            catch (Exception ex) when (IsEditorConflict(ex))
            {
                throw new AdapterException(
                    "SOURCE_OBJECT_OPEN_IN_EDITOR",
                    $"Import of '{relativePath}' was rejected — the object appears to be open in a TIA editor: {ex.Message}",
                    "Close the object editor in TIA Portal and retry.");
            }

            return new SourceObjectImportResult
            {
                RelativePath = relativePath.Replace('\\', '/'),
                ObjectName = location.ObjectName,
                ObjectKind = location.Kind.ToString(),
                Success = importedCount > 0,
            };
        }
    }

    private static SourceObjectLocation ParseSourceObjectPath(string relativePath)
    {
        SourceObjectKind kind;
        try
        {
            kind = SourceObjectImport.Classify(relativePath);
        }
        catch (ArgumentException ex)
        {
            throw new AdapterException("SOURCE_PATH_INVALID", ex.Message, "Use a Blocks, DB, Tags, or UDT XML source path.");
        }

        var normalized = relativePath.Replace('\\', '/');
        var segments = normalized.Split('/');
        if (segments.Length < 2 || !segments[^1].EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            throw new AdapterException("SOURCE_PATH_INVALID", $"'{relativePath}' must identify an XML source object.");

        var fileName = segments[^1];
        var objectName = Path.GetFileNameWithoutExtension(fileName);
        if (kind == SourceObjectKind.Block)
        {
            var suffixStart = objectName.LastIndexOf(" [", StringComparison.Ordinal);
            if (suffixStart > 0 && objectName.EndsWith("]", StringComparison.Ordinal))
                objectName = objectName.Substring(0, suffixStart);
        }

        if (string.IsNullOrWhiteSpace(objectName))
            throw new AdapterException("SOURCE_PATH_INVALID", $"'{relativePath}' does not contain an object name.");

        return new SourceObjectLocation(
            kind,
            segments.Skip(1).Take(segments.Length - 2).ToArray(),
            objectName);
    }

    private static PlcBlockGroup ResolveBlockGroup(PlcSoftware plc, IReadOnlyList<string> groupSegments)
    {
        PlcBlockGroup group = plc.BlockGroup;
        foreach (var segment in groupSegments)
        {
            group = group.Groups.FirstOrDefault(candidate => candidate.Name == segment)
                ?? throw new AdapterException("SOURCE_GROUP_NOT_FOUND", $"Block group '{string.Join("/", groupSegments)}' was not found.");
        }

        return group;
    }

    private static PlcTagTableGroup ResolveTagTableGroup(PlcSoftware plc, IReadOnlyList<string> groupSegments)
    {
        PlcTagTableGroup group = plc.TagTableGroup;
        foreach (var segment in groupSegments)
        {
            group = group.Groups.FirstOrDefault(candidate => candidate.Name == segment)
                ?? throw new AdapterException("SOURCE_GROUP_NOT_FOUND", $"Tag-table group '{string.Join("/", groupSegments)}' was not found.");
        }

        return group;
    }

    private static PlcTypeGroup ResolveTypeGroup(PlcSoftware plc, IReadOnlyList<string> groupSegments)
    {
        PlcTypeGroup group = plc.TypeGroup;
        foreach (var segment in groupSegments)
        {
            group = group.Groups.FirstOrDefault(candidate => candidate.Name == segment)
                ?? throw new AdapterException("SOURCE_GROUP_NOT_FOUND", $"UDT group '{string.Join("/", groupSegments)}' was not found.");
        }

        return group;
    }

    private static void EnsureExistingBlock(PlcBlockGroup group, string objectName, string relativePath)
    {
        if (!group.Blocks.Cast<PlcBlock>().Any(block => block.Name == objectName))
            throw NewSourceAddUnsupported(relativePath, objectName);
    }

    private static void EnsureExistingTagTable(PlcTagTableGroup group, string objectName, string relativePath)
    {
        if (!group.TagTables.Cast<PlcTagTable>().Any(table => table.Name == objectName))
            throw NewSourceAddUnsupported(relativePath, objectName);
    }

    private static void EnsureExistingType(PlcTypeGroup group, string objectName, string relativePath)
    {
        if (!group.Types.Cast<PlcType>().Any(type => type.Name == objectName))
            throw NewSourceAddUnsupported(relativePath, objectName);
    }

    private static AdapterException NewSourceAddUnsupported(string relativePath, string objectName) =>
        new(
            "SOURCE_ADD_UNSUPPORTED",
            $"Source object '{objectName}' from '{relativePath}' does not exist in the target TIA group.",
            "Only overwrites of existing blocks, tag tables, and UDTs are supported.");

    private sealed class SourceObjectLocation
    {
        public SourceObjectLocation(SourceObjectKind kind, string[] groupSegments, string objectName)
        {
            Kind = kind;
            GroupSegments = groupSegments;
            ObjectName = objectName;
        }

        public SourceObjectKind Kind { get; }
        public string[] GroupSegments { get; }
        public string ObjectName { get; }
    }

    public ImportResult ImportBlock(string blockName, string xmlFilePath, string? plcName = null)
    {
        lock (_gate)
        {
            if (!File.Exists(xmlFilePath))
                throw new AdapterException("XML_NOT_FOUND", $"XML file not found: {xmlFilePath}");

            var plc = PlcSoftwareResolver.Resolve(RequireProject(), plcName);
            var targetGroup = ResolveImportGroup(plc, blockName);

            IList<PlcBlock> imported;
            try
            {
                // Exclusive access + transaction is mandatory for all writes (§13.3).
                using var exclusiveAccess = _portal!.ExclusiveAccess("Import block " + blockName);
                using var transaction = exclusiveAccess.Transaction(RequireProject(), "Import block " + blockName);
                imported = targetGroup.Blocks.Import(new FileInfo(xmlFilePath), ImportOptions.Override);
                transaction.CommitOnDispose();
            }
            catch (Exception ex) when (IsEditorConflict(ex))
            {
                // Exception-driven guard (§6.1 item 2): Openness has no editor-enumeration API.
                throw new AdapterException("BLOCK_OPEN_IN_EDITOR",
                    $"Import of '{blockName}' was rejected — the block appears to be open in a TIA editor: {ex.Message}",
                    "Close the block editor in TIA Portal and retry.");
            }

            var warnings = new List<string>();

            // Re-export verify (§6.1 item 5): export the block again and compare against the
            // imported file, ignoring export-volatile metadata. Spike B proved comment-only
            // edits round-trip byte-stable except <Created>.
            var interfaceDrift = false;
            var interfaceVerified = false;
            try
            {
                var verifyPath = Path.Combine(Path.GetTempPath(), $"mcp-eng-verify-{Guid.NewGuid():N}.xml");
                BlockEnumerator.Find(plc.BlockGroup, blockName)
                    .Export(new FileInfo(verifyPath), ExportOptions.WithDefaults);
                var expected = NormalizeForCompare(File.ReadAllText(xmlFilePath));
                var actual = NormalizeForCompare(File.ReadAllText(verifyPath));
                try { File.Delete(verifyPath); } catch { }
                interfaceVerified = true;
                interfaceDrift = expected != actual;
                if (interfaceDrift)
                    warnings.Add("Interface drift detected: re-export differs from the imported XML.");
            }
            catch (Exception ex) when (ex.Message.IndexOf("Inconsistent", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                // Expected right after import (proven in Spike B): code blocks are
                // inconsistent until compiled, and inconsistent blocks cannot be exported.
                warnings.Add("Interface verify deferred: block is inconsistent after import — compile, then re-export to verify (§6.1 item 5).");
            }
            catch (Exception ex)
            {
                warnings.Add($"Re-export verify could not run: {ex.Message}");
            }

            return new ImportResult
            {
                BlockName = blockName,
                Success = imported.Count > 0,
                Warnings = warnings.ToArray(),
                InterfaceVerified = interfaceVerified,
                InterfaceDrift = interfaceDrift,
                ImportedAt = DateTime.Now,
            };
        }
    }

    public HardwareImportResult ImportHardwareConfiguration(
        string amlFilePath,
        string? logFilePath = null,
        HardwareImportConflictPolicy conflictPolicy = HardwareImportConflictPolicy.MoveToParkingLot)
    {
        lock (_gate)
        {
            RequireProject();
            if (!File.Exists(amlFilePath))
                throw new AdapterException("AML_NOT_FOUND", $"AML file not found: {amlFilePath}");
            if (!string.Equals(Path.GetExtension(amlFilePath), ".aml", StringComparison.OrdinalIgnoreCase))
                throw new AdapterException(
                    "INVALID_HARDWARE_IMPORT_FILE",
                    $"Hardware configuration import requires an AML file: {amlFilePath}",
                    "Export the hardware configuration as CAx/AML and retry.");

            var actualLogPath = logFilePath ?? Path.Combine(
                Path.GetTempPath(), $"mcp-eng-cax-{Guid.NewGuid():N}.log");
            var logDirectory = Path.GetDirectoryName(actualLogPath);
            if (!string.IsNullOrWhiteSpace(logDirectory))
                Directory.CreateDirectory(logDirectory);
            var cax = RequireProject().GetService<CaxProvider>();
            if (cax is null)
                throw new AdapterException(
                    "HARDWARE_IMPORT_UNAVAILABLE",
                    "TIA Openness did not expose the CaxProvider service for the connected project.",
                    "Use a TIA Portal V17 project with the CAx import service available.");

            // CAx import is intentionally not wrapped in ExclusiveAccess: Siemens documents
            // that the CaxProvider rejects calls made from inside exclusive access.
            var imported = cax.Import(
                new FileInfo(amlFilePath),
                new FileInfo(actualLogPath),
                ToCaxImportOptions(conflictPolicy));

            return new HardwareImportResult
            {
                Success = imported,
                AmlFilePath = amlFilePath,
                LogFilePath = actualLogPath,
                ConflictPolicy = conflictPolicy,
                Error = imported ? null : "TIA Openness reported that the CAx import failed. See the import log.",
                ImportedAt = DateTime.Now,
            };
        }
    }

    public BlockInfo CreateBlock(
        string blockName,
        string blockType,
        int number = 0,
        string? programmingLanguage = null,
        string? instanceOfName = null,
        string? plcName = null)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(blockName))
                throw new AdapterException("BLOCK_NAME_REQUIRED", "Block name is required.");
            if (string.IsNullOrWhiteSpace(blockType))
                throw new AdapterException("BLOCK_TYPE_REQUIRED", "Block type is required.");

            var plc = PlcSoftwareResolver.Resolve(RequireProject(), plcName);
            if (BlockEnumerator.Enumerate(plc.BlockGroup).Any(x =>
                    string.Equals(x.Block.Name, blockName, StringComparison.OrdinalIgnoreCase)))
            {
                throw new AdapterException("BLOCK_ALREADY_EXISTS",
                    $"A block named '{blockName}' already exists.",
                    "Choose a new name or delete the existing block first.");
            }

            PlcBlock created;
            try
            {
                using var exclusiveAccess = _portal!.ExclusiveAccess("Create block " + blockName);
                using var transaction = exclusiveAccess.Transaction(RequireProject(), "Create block " + blockName);
                created = blockType.Trim().ToLowerInvariant() switch
                {
                    "fb" => plc.BlockGroup.Blocks.CreateFB(
                        blockName,
                        number <= 0,
                        Math.Max(number, 0),
                        ParseProgrammingLanguage(programmingLanguage)),
                    "instancedb" or "instance_db" or "instance db" =>
                        CreateInstanceDb(plc, blockName, number, instanceOfName),
                    _ => throw new AdapterException(
                        "BLOCK_TYPE_UNSUPPORTED",
                        $"Native block creation does not support '{blockType}' in TIA Openness V17.",
                        "Use blockType FB or InstanceDB, or import an XML block with import_block."),
                };
                transaction.CommitOnDispose();
            }
            catch (AdapterException) { throw; }
            catch (Exception ex)
            {
                throw new AdapterException("BLOCK_CREATE_FAILED",
                    $"Failed to create block '{blockName}': {ex.Message}");
            }

            return ToBlockInfo(created);
        }
    }

    public BlockMutationResult DeleteBlock(string blockName, string? plcName = null)
    {
        lock (_gate)
        {
            var plc = PlcSoftwareResolver.Resolve(RequireProject(), plcName);
            var block = BlockEnumerator.Find(plc.BlockGroup, blockName);
            var result = new BlockMutationResult
            {
                BlockName = block.Name,
                BlockType = block.GetType().Name,
                BlockNumber = block.Number,
                ChangedAt = DateTime.Now,
            };

            try
            {
                using var exclusiveAccess = _portal!.ExclusiveAccess("Delete block " + blockName);
                using var transaction = exclusiveAccess.Transaction(RequireProject(), "Delete block " + blockName);
                block.Delete();
                transaction.CommitOnDispose();
                result.Success = true;
                return result;
            }
            catch (Exception ex) when (IsEditorConflict(ex))
            {
                throw new AdapterException(
                    "BLOCK_OPEN_IN_EDITOR",
                    $"Delete of '{blockName}' was rejected — the block appears to be open in a TIA editor: {ex.Message}",
                    "Close the block editor in TIA Portal and retry.");
            }
            catch (Exception ex)
            {
                throw new AdapterException("BLOCK_DELETE_FAILED",
                    $"Failed to delete block '{blockName}': {ex.Message}");
            }
        }
    }

    private static InstanceDB CreateInstanceDb(PlcSoftware plc, string blockName, int number, string? instanceOfName)
    {
        if (string.IsNullOrWhiteSpace(instanceOfName))
            throw new AdapterException(
                "INSTANCE_BLOCK_REQUIRED",
                "instanceOfName is required when creating an InstanceDB.",
                "Set instanceOfName to the existing FB name.");
        return plc.BlockGroup.Blocks.CreateInstanceDB(
            blockName,
            number <= 0,
            Math.Max(number, 0),
            instanceOfName);
    }

    private static ProgrammingLanguage ParseProgrammingLanguage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return ProgrammingLanguage.LAD;
        if (Enum.TryParse<ProgrammingLanguage>(value, ignoreCase: true, out var language)
            && Enum.IsDefined(typeof(ProgrammingLanguage), language))
            return language;
        throw new AdapterException(
            "INVALID_PROGRAMMING_LANGUAGE",
            $"Unknown programming language '{value}'.",
            "Use a TIA Openness V17 language such as LAD, FBD, or SCL.");
    }

    private static CaxImportOptions ToCaxImportOptions(HardwareImportConflictPolicy policy) => policy switch
    {
        HardwareImportConflictPolicy.MoveToParkingLot => CaxImportOptions.MoveToParkingLot,
        HardwareImportConflictPolicy.RetainTiaDevice => CaxImportOptions.RetainTiaDevice,
        HardwareImportConflictPolicy.OverwriteTiaDevice => CaxImportOptions.OverwriteTiaDevice,
        _ => throw new AdapterException("INVALID_HARDWARE_IMPORT_POLICY", $"Unknown hardware import policy: {policy}"),
    };

    private static BlockInfo ToBlockInfo(PlcBlock block) => new()
    {
        Name = block.Name,
        Number = block.Number,
        BlockType = block.GetType().Name,
        ProgrammingLanguage = block.ProgrammingLanguage.ToString(),
    };

    /// <summary>Import beside the existing block (its group), or at the root group for new blocks.</summary>
    private static PlcBlockGroup ResolveImportGroup(PlcSoftware plc, string blockName)
    {
        var match = BlockEnumerator.Enumerate(plc.BlockGroup)
            .FirstOrDefault(x => string.Equals(x.Block.Name, blockName, StringComparison.OrdinalIgnoreCase));
        if (match.Block is null || match.GroupPath is null)
            return plc.BlockGroup;

        PlcBlockGroup group = plc.BlockGroup;
        foreach (var part in match.GroupPath.Split('/'))
            group = group.Groups.First(g => g.Name == part);
        return group;
    }

    private static bool IsEditorConflict(Exception ex)
    {
        var message = ex.Message;
        return message.IndexOf("checked out", StringComparison.OrdinalIgnoreCase) >= 0
            || (message.IndexOf("open", StringComparison.OrdinalIgnoreCase) >= 0
                && message.IndexOf("editor", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    /// <summary>Strips export-volatile metadata before comparison (§6.1 item 5).</summary>
    private static string NormalizeForCompare(string xml) => XmlCompare.Normalize(xml);

    public CompileResult CompileBlock(string blockName, string? plcName = null)
    {
        lock (_gate)
        {
            var plc = PlcSoftwareResolver.Resolve(RequireProject(), plcName);
            BlockEnumerator.Find(plc.BlockGroup, blockName); // throws BLOCK_NOT_FOUND if absent

            // V17 has no per-block compile (verified): compile the software, filter messages (§7.1).
            var full = CompileCore(plc, blockFilter: null);
            var mine = full.Messages
                .Where(m => string.Equals(m.BlockName, blockName, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            return new CompileResult
            {
                BlockName = blockName,
                State = mine.Length == 0 ? "success" : WorstState(mine),
                Messages = mine,
                DurationMs = full.DurationMs,
            };
        }
    }

    public CompileResult CompilePlc(string? plcName = null)
    {
        lock (_gate)
        {
            var plc = PlcSoftwareResolver.Resolve(RequireProject(), plcName);
            return CompileCore(plc, blockFilter: null);
        }
    }

    private static CompileResult CompileCore(PlcSoftware plc, string? blockFilter)
    {
        var compiler = plc.GetService<ICompilable>();
        var stopwatch = Stopwatch.StartNew();
        var result = compiler.Compile(); // synchronous (verified)
        stopwatch.Stop();

        var messages = new List<CompileMessage>();
        CollectMessages(result.Messages, messages);

        var filtered = blockFilter is null
            ? messages.ToArray()
            : messages.Where(m => string.Equals(m.BlockName, blockFilter, StringComparison.OrdinalIgnoreCase)).ToArray();

        return new CompileResult
        {
            State = MapState(result.State),
            Messages = filtered,
            DurationMs = stopwatch.ElapsedMilliseconds,
        };
    }

    private static void CollectMessages(CompilerResultMessageComposition composition, List<CompileMessage> output)
    {
        foreach (CompilerResultMessage message in composition)
        {
            output.Add(new CompileMessage
            {
                Type = MapState(message.State),
                Text = message.Description,
                BlockName = ExtractBlockName(message.Path),
                NetworkNumber = ExtractNetworkNumber(message.Path),
            });
            CollectMessages(message.Messages, output); // nested messages
        }
    }

    /// <summary>Message paths look like "PLC_1\Main (OB1)\Network 1" — segment 2 is the block,
    /// with a " (OB1)"-style suffix to strip.</summary>
    private static string? ExtractBlockName(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return null;
        var segments = path!.Split('\\', '/');
        if (segments.Length < 2)
            return segments[0];
        var segment = segments[1];
        var paren = segment.LastIndexOf(" (", StringComparison.Ordinal);
        return paren > 0 ? segment.Substring(0, paren) : segment;
    }

    /// <summary>Third path segment, when shaped like "Network 3".</summary>
    private static int? ExtractNetworkNumber(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return null;
        var segments = path!.Split('\\', '/');
        if (segments.Length < 3)
            return null;
        var segment = segments[2];
        return segment.StartsWith("Network ")
            && int.TryParse(segment.Substring(8), out var number)
            ? number
            : null;
    }

    private static string MapState(CompilerResultState state) => state switch
    {
        CompilerResultState.Success => "success",
        CompilerResultState.Information => "info",
        CompilerResultState.Warning => "warnings",
        CompilerResultState.Error => "error",
        _ => state.ToString().ToLowerInvariant(),
    };

    private static string WorstState(CompileMessage[] messages)
    {
        if (messages.Any(m => m.Type == "error")) return "error";
        if (messages.Any(m => m.Type == "warnings")) return "warnings";
        return "info";
    }

    public void Dispose() => Disconnect();

    public void OpenBlockInEditor(string blockName)
    {
        lock (_gate)
        {
            var plc = PlcSoftwareResolver.Resolve(RequireProject(), null);
            var block = BlockEnumerator.Find(plc.BlockGroup, blockName);
            try
            {
                // PlcBlock implements IShowable which exposes ShowInEditor().
                // This opens the block in the TIA Portal editor window.
                var showable = block as Siemens.Engineering.IShowable;
                if (showable == null)
                    throw new AdapterException("EDITOR_NOT_AVAILABLE",
                        $"Cannot open block '{blockName}' in editor — block does not support the editor interface.",
                        "This Siemens Openness API feature may not be available in this version.");
                showable.ShowInEditor();
            }
            catch (AdapterException) { throw; }
            catch (Exception ex)
            {
                throw new AdapterException("EDITOR_NOT_AVAILABLE",
                    $"Cannot open block '{blockName}' in editor — {ex.Message}",
                    "TIA Portal must be in UI mode (withUI=true on connect).");
            }
        }
    }

    private Project RequireProject() =>
        _project ?? throw new AdapterException("NOT_CONNECTED", "No project connected. Call connect first.");
}
