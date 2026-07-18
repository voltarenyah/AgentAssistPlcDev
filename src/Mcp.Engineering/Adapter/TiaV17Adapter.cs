using System.Diagnostics;
using Contracts;
using Contracts.Engineering;
using Mcp.Engineering.Export;
using Mcp.Engineering.Openness;
using Mcp.Engineering.Sessions;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
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
    private TiaPortal? _portal;
    private Project? _project;

    /// <summary>True when we started the portal process (open mode); false when attached to a user's session.</summary>
    private bool _ownsPortal;

    public EnvCheckResult CheckEnvironment() => EnvironmentChecker.Check();

    public SessionInfo[] ListSessions() => TiaSessionEnumerator.ListSessions().ToArray();

    public ConnectionInfo Connect(ConnectOptions options)
    {
        lock (_gate)
        {
            if (_portal is not null)
                throw new AdapterException("ALREADY_CONNECTED", "Already connected. Call disconnect first.");
            if (options.SessionId is not null && options.ProjectPath is not null)
                throw new AdapterException("AMBIGUOUS_CONNECT", "Provide sessionId or projectPath, not both.");
            if (options.SessionId is null && options.ProjectPath is null)
                throw new AdapterException("MISSING_CONNECT_TARGET", "Provide sessionId or projectPath.");

            return options.SessionId is not null
                ? Attach(options.SessionId.Value, options.TimeoutSeconds)
                : Open(options.ProjectPath!, options.WithUI);
        }
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
        if (_portal!.Projects.Count == 0)
        {
            _portal.Dispose();
            _portal = null;
            throw new AdapterException("PROJECT_NOT_FOUND", "TIA is running but no project is open.",
                "Open a project in that TIA session, or connect with projectPath instead.");
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
            _portal = new TiaPortal(mode);
            _ownsPortal = true;
            _project = _portal.Projects.Open(new FileInfo(projectPath));
        }
        catch (Exception ex)
        {
            // A failed open must not leak the portal process.
            try { _portal?.Dispose(); } catch { }
            _portal = null;
            _project = null;
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
            return result;
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

    public ExportResult ExportBlock(string blockName, string outputDir)
    {
        lock (_gate)
        {
            var plc = PlcSoftwareResolver.Resolve(RequireProject(), null);
            var (block, groupPath) = BlockEnumerator.FindWithPath(plc.BlockGroup, blockName);
            Directory.CreateDirectory(outputDir);
            try
            {
                if (!block.IsConsistent)
                {
                    throw new AdapterException("BLOCK_INCONSISTENT",
                        $"Block '{blockName}' is inconsistent. Compile it first before export.");
                }
                var result = ExportCore(block, outputDir);
                ExportManifest.Upsert(outputDir, ExportManifest.CreateRecord(block, groupPath, outputDir, result));
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
                try { ExportManifest.Upsert(outputDir, ExportManifest.CreateRecord(block, groupPath, outputDir, failed)); }
                catch { }
                throw;
            }
        }
    }

    public ExportResult[] ExportAllBlocks(string outputDir)
    {
        lock (_gate)
        {
            var plcs = PlcSoftwareResolver.FindAll(RequireProject());
            var results = new List<ExportResult>();
            foreach (var plc in plcs)
            {
                // Multi-PLC: per-PLC subfolder, each its own export root with its own metadata.json.
                var dir = plcs.Count > 1 ? Path.Combine(outputDir, Sanitize(plc.Name)) : outputDir;
                Directory.CreateDirectory(dir);
                var exportStartedUtc = DateTimeOffset.UtcNow;
                var records = new List<ExportMetadataRecord>();
                foreach (var (block, groupPath) in BlockEnumerator.Enumerate(plc.BlockGroup))
                {
                    ExportResult result;
                    try
                    {
                        result = ExportCore(block, dir);
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
                    results.Add(result);
                    records.Add(ExportManifest.CreateRecord(block, groupPath, dir, result));
                }
                ExportManifest.WriteAll(dir, exportStartedUtc, records, ExportManifest.BlockCategories);
            }
            return results.ToArray();
        }
    }

    /// <summary>Exports into &lt;exportRoot&gt;/Blocks/ or &lt;exportRoot&gt;/DB/ (created as needed), depending on the block category.</summary>
    private static ExportResult ExportCore(PlcBlock block, string exportRoot)
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

        var dir = Path.Combine(exportRoot, ExportManifest.FolderFor(ExportManifest.CategoryOf(block)));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{Sanitize(block.Name)} [{TypeCode(block)}{block.Number}].xml");
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

    public ExportResult[] ExportTagTables(string outputDir, string? plcName)
    {
        lock (_gate)
        {
            return ExportObjects(
                outputDir,
                plcName,
                plc => TagTableEnumerator.Enumerate(plc.TagTableGroup),
                ExportTagTableCore,
                (table, groupPath, root, result) => ExportManifest.CreateRecord(
                    table.Name, "Tags", table.GetType().Name, groupPath, root, result,
                    modifiedDate: ReadTagTableModified(table)));
        }
    }

    public ExportResult[] ExportUdts(string outputDir, string? plcName)
    {
        lock (_gate)
        {
            return ExportObjects(
                outputDir,
                plcName,
                plc => PlcTypeEnumerator.Enumerate(plc.TypeGroup),
                ExportUdtCore,
                (type, groupPath, root, result) =>
                {
                    var (khp, created, modified, interfaceModified) = ReadTypeMetadata(type);
                    return ExportManifest.CreateRecord(
                        type.Name, "UDT", type.GetType().Name, groupPath, root, result,
                        isKnowHowProtected: khp,
                        creationDate: created,
                        modifiedDate: modified,
                        interfaceModifiedDate: interfaceModified);
                });
        }
    }

    /// <summary>Shared export loop for tag tables and UDTs: per-PLC export root (subfolder when
    /// multiple PLCs), one manifest upsert per object. exportCore converts per-object failures
    /// into Failed results — it never throws for them.</summary>
    private ExportResult[] ExportObjects<TObject>(
        string outputDir,
        string? plcName,
        Func<PlcSoftware, IEnumerable<(TObject Item, string? GroupPath)>> enumerate,
        Func<TObject, string, ExportResult> exportCore,
        Func<TObject, string?, string, ExportResult, ExportMetadataRecord> createRecord)
    {
        var project = RequireProject();
        var plcs = plcName is null
            ? PlcSoftwareResolver.FindAll(project)
            : new[] { PlcSoftwareResolver.Resolve(project, plcName) };
        var results = new List<ExportResult>();
        foreach (var plc in plcs)
        {
            // Multi-PLC: per-PLC subfolder, each its own export root with its own metadata.json.
            var dir = plcs.Count > 1 ? Path.Combine(outputDir, Sanitize(plc.Name)) : outputDir;
            Directory.CreateDirectory(dir);
            foreach (var (item, groupPath) in enumerate(plc))
            {
                var result = exportCore(item, dir);
                results.Add(result);
                ExportManifest.Upsert(dir, createRecord(item, groupPath, dir, result));
            }
        }
        return results.ToArray();
    }

    private static ExportResult ExportTagTableCore(PlcTagTable table, string exportRoot)
    {
        var dir = Path.Combine(exportRoot, ExportManifest.FolderFor("Tags"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{Sanitize(table.Name)}.xml");
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

    private static ExportResult ExportUdtCore(PlcType type, string exportRoot)
    {
        if (!type.IsConsistent)
        {
            return Failure(type.Name, "UDT",
                $"UDT '{type.Name}' is inconsistent. Compile it first before export.");
        }
        var dir = Path.Combine(exportRoot, ExportManifest.FolderFor("UDT"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{Sanitize(type.Name)}.xml");
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

    public ImportResult ImportBlock(string blockName, string xmlFilePath)
    {
        lock (_gate)
        {
            if (!File.Exists(xmlFilePath))
                throw new AdapterException("XML_NOT_FOUND", $"XML file not found: {xmlFilePath}");

            var plc = PlcSoftwareResolver.Resolve(RequireProject(), null);
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

    public CompileResult CompileBlock(string blockName)
    {
        lock (_gate)
        {
            var plc = PlcSoftwareResolver.Resolve(RequireProject(), null);
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

    public CompileResult CompilePlc()
    {
        lock (_gate)
        {
            var plc = PlcSoftwareResolver.Resolve(RequireProject(), null);
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

    private Project RequireProject() =>
        _project ?? throw new AdapterException("NOT_CONNECTED", "No project connected. Call connect first.");
}
