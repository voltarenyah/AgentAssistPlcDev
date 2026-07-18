// Ported from PlcSourceExporter (src/PlcSourceExporter.Core/ProgramSemanticReference.cs) — adapted for mcp-knowledge; keep changes minimal to ease future re-syncs.
// Adaptations vs the reference:
// - Dropped Write() (walks the metadata.json catalog, which is not ported) and the JSONL
//   serialization helpers that only it used (SerializeNetworks/SerializeReferences/Escape/
//   WriteProperty/WriteArrayProperty, ProgramSemanticReferenceResult, file-name constants).
// - Parse() and all parse helpers are verbatim.
using System.Xml;
using System.Xml.Linq;

namespace Mcp.Knowledge.Parsing;

public sealed class ProgramSemanticParseResult
{
    public ProgramSemanticParseResult(IReadOnlyList<ProgramNetworkRecord> networks, IReadOnlyList<ProgramReferenceRecord> references)
    {
        Networks = networks;
        References = references;
    }

    public IReadOnlyList<ProgramNetworkRecord> Networks { get; }

    public IReadOnlyList<ProgramReferenceRecord> References { get; }
}

public sealed class ProgramNetworkRecord
{
    public ProgramNetworkRecord(
        string id,
        string block,
        string blockKind,
        string language,
        string sourceFile,
        int networkIndex,
        string compileUnitId,
        string title,
        int accessCount,
        int callCount,
        int partCount,
        int wireCount,
        IReadOnlyList<string> reads,
        IReadOnlyList<string> writes,
        IReadOnlyList<string> calls)
    {
        Id = id;
        Block = block;
        BlockKind = blockKind;
        Language = language;
        SourceFile = sourceFile;
        NetworkIndex = networkIndex;
        CompileUnitId = compileUnitId;
        Title = title;
        AccessCount = accessCount;
        CallCount = callCount;
        PartCount = partCount;
        WireCount = wireCount;
        Reads = reads;
        Writes = writes;
        Calls = calls;
    }

    public string Id { get; }

    public string Block { get; }

    public string BlockKind { get; }

    public string Language { get; }

    public string SourceFile { get; }

    public int NetworkIndex { get; }

    public string CompileUnitId { get; }

    public string Title { get; }

    public int AccessCount { get; }

    public int CallCount { get; }

    public int PartCount { get; }

    public int WireCount { get; }

    public IReadOnlyList<string> Reads { get; }

    public IReadOnlyList<string> Writes { get; }

    public IReadOnlyList<string> Calls { get; }
}

public sealed class ProgramReferenceRecord
{
    public ProgramReferenceRecord(
        string from,
        string block,
        int networkIndex,
        string title,
        string to,
        string targetKind,
        string access,
        string scope,
        string parameter,
        string callee,
        string calleeBlockType,
        string instanceDb,
        string sourceFile)
    {
        From = from;
        Block = block;
        NetworkIndex = networkIndex;
        Title = title;
        To = to;
        TargetKind = targetKind;
        Access = access;
        Scope = scope;
        Parameter = parameter;
        Callee = callee;
        CalleeBlockType = calleeBlockType;
        InstanceDb = instanceDb;
        SourceFile = sourceFile;
    }

    public string From { get; }

    public string Block { get; }

    public int NetworkIndex { get; }

    public string Title { get; }

    public string To { get; }

    public string TargetKind { get; }

    public string Access { get; }

    public string Scope { get; }

    public string Parameter { get; }

    public string Callee { get; }

    public string CalleeBlockType { get; }

    public string InstanceDb { get; }

    public string SourceFile { get; }
}

public static class ProgramSemanticReferenceBuilder
{
    public static ProgramSemanticParseResult Parse(string xml, ProgramBlockComponent component)
    {
        if (xml == null)
        {
            throw new ArgumentNullException(nameof(xml));
        }

        if (component == null)
        {
            throw new ArgumentNullException(nameof(component));
        }

        XDocument document;
        try
        {
            document = XDocument.Parse(xml);
        }
        catch (XmlException)
        {
            return new ProgramSemanticParseResult(Array.Empty<ProgramNetworkRecord>(), Array.Empty<ProgramReferenceRecord>());
        }

        var networks = new List<ProgramNetworkRecord>();
        var references = new List<ProgramReferenceRecord>();
        var compileUnits = document
            .Descendants()
            .Where(element => element.Name.LocalName == "SW.Blocks.CompileUnit")
            .Select((element, index) => new { Element = element, Index = index + 1 })
            .ToArray();
        var blockLanguage = GetBlockLanguage(document);

        foreach (var compileUnit in compileUnits)
        {
            var networkId = $"network:{component.Name}:{compileUnit.Index}";
            var title = GetPreferredMultilingualText(compileUnit.Element, "Title");
            if (string.IsNullOrWhiteSpace(title))
            {
                title = $"Network {compileUnit.Index}";
            }

            var language = GetDirectAttributeValue(compileUnit.Element, "ProgrammingLanguage");
            if (string.IsNullOrWhiteSpace(language))
            {
                language = blockLanguage;
            }

            var networkReferences = ParseNetworkReferences(
                compileUnit.Element,
                component,
                networkId,
                compileUnit.Index,
                title);
            references.AddRange(networkReferences);

            networks.Add(new ProgramNetworkRecord(
                networkId,
                component.Name,
                component.Category,
                language,
                component.ExportedFile,
                compileUnit.Index,
                ((string?)compileUnit.Element.Attribute("ID")) ?? string.Empty,
                title,
                CountDescendants(compileUnit.Element, "Access"),
                CountDescendants(compileUnit.Element, "CallInfo"),
                CountDescendants(compileUnit.Element, "Part"),
                CountDescendants(compileUnit.Element, "Wire"),
                DistinctTargets(networkReferences, "read"),
                DistinctTargets(networkReferences, "write"),
                DistinctCallTargets(networkReferences)));
        }

        return new ProgramSemanticParseResult(networks, references);
    }

    private static IReadOnlyList<ProgramReferenceRecord> ParseNetworkReferences(
        XElement compileUnit,
        ProgramBlockComponent component,
        string networkId,
        int networkIndex,
        string title)
    {
        var references = new List<ProgramReferenceRecord>();
        var accessByUid = compileUnit
            .Descendants()
            .Where(element => element.Name.LocalName == "Access")
            .Select(element => new AccessRecord(
                GetUid(element),
                GetSymbolName(element),
                ((string?)element.Attribute("Scope"))?.Trim() ?? string.Empty))
            .Where(item => !string.IsNullOrWhiteSpace(item.Uid))
            .GroupBy(item => item.Uid, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var classifiedAccessUids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var callInfo in compileUnit.Descendants().Where(element => element.Name.LocalName == "CallInfo"))
        {
            var calleeName = GetCalleeName(callInfo);
            var calleeBlockType = ((string?)callInfo.Attribute("BlockType"))?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(calleeName))
            {
                references.Add(new ProgramReferenceRecord(
                    networkId,
                    component.Name,
                    networkIndex,
                    title,
                    calleeName,
                    "block",
                    "call",
                    string.Empty,
                    string.Empty,
                    calleeName,
                    calleeBlockType,
                    GetInstanceDb(callInfo),
                    component.ExportedFile));
            }

            foreach (var binding in GetCallParameterBindings(callInfo, compileUnit, accessByUid))
            {
                if (string.IsNullOrWhiteSpace(binding.Symbol))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(binding.AccessUid))
                {
                    classifiedAccessUids.Add(binding.AccessUid);
                }

                references.Add(new ProgramReferenceRecord(
                    networkId,
                    component.Name,
                    networkIndex,
                    title,
                    binding.Symbol,
                    "symbol",
                    GetAccessName(binding.Section),
                    binding.Scope,
                    binding.Parameter,
                    calleeName,
                    calleeBlockType,
                    GetInstanceDb(callInfo),
                    component.ExportedFile));
            }
        }

        foreach (var inferredAccess in InferStandaloneAccesses(compileUnit, accessByUid, classifiedAccessUids))
        {
            classifiedAccessUids.Add(inferredAccess.AccessUid);
            references.Add(new ProgramReferenceRecord(
                networkId,
                component.Name,
                networkIndex,
                title,
                inferredAccess.Symbol,
                "symbol",
                inferredAccess.Access,
                inferredAccess.Scope,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                component.ExportedFile));
        }

        foreach (var access in accessByUid.Values)
        {
            if (classifiedAccessUids.Contains(access.Uid) ||
                string.IsNullOrWhiteSpace(access.Symbol) ||
                string.Equals(access.Scope, "LiteralConstant", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            references.Add(new ProgramReferenceRecord(
                networkId,
                component.Name,
                networkIndex,
                title,
                access.Symbol,
                "symbol",
                "unknown",
                access.Scope,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                component.ExportedFile));
        }

        return references;
    }

    private static IEnumerable<StandaloneAccessClassification> InferStandaloneAccesses(
        XElement compileUnit,
        IReadOnlyDictionary<string, AccessRecord> accessByUid,
        HashSet<string> classifiedAccessUids)
    {
        var partByUid = compileUnit
            .Descendants()
            .Where(element => element.Name.LocalName == "Part")
            .Select(element => new PartRecord(
                GetUid(element),
                ((string?)element.Attribute("Name"))?.Trim() ?? string.Empty))
            .Where(item => !string.IsNullOrWhiteSpace(item.Uid) && !string.IsNullOrWhiteSpace(item.Name))
            .GroupBy(item => item.Uid, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var classificationsByUid = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var wire in compileUnit.Descendants().Where(element => element.Name.LocalName == "Wire"))
        {
            var accessUids = wire
                .Descendants()
                .Where(element => element.Name.LocalName == "IdentCon")
                .Select(element => ((string?)element.Attribute("UId"))?.Trim() ?? string.Empty)
                .Where(uid =>
                    !string.IsNullOrWhiteSpace(uid) &&
                    accessByUid.ContainsKey(uid) &&
                    !classifiedAccessUids.Contains(uid))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (accessUids.Length == 0)
            {
                continue;
            }

            var wireClassifications = wire
                .Descendants()
                .Where(element => element.Name.LocalName == "NameCon")
                .Select(element => new
                {
                    Uid = ((string?)element.Attribute("UId"))?.Trim() ?? string.Empty,
                    Pin = ((string?)element.Attribute("Name"))?.Trim() ?? string.Empty
                })
                .Where(item => !string.IsNullOrWhiteSpace(item.Uid) && !string.IsNullOrWhiteSpace(item.Pin))
                .Select(item =>
                {
                    if (!partByUid.TryGetValue(item.Uid, out var part))
                    {
                        return string.Empty;
                    }

                    return GetStandaloneAccessName(part.Name, item.Pin);
                })
                .Where(access => !string.IsNullOrWhiteSpace(access))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (wireClassifications.Length == 0)
            {
                continue;
            }

            foreach (var accessUid in accessUids)
            {
                if (!classificationsByUid.TryGetValue(accessUid, out var accessSet))
                {
                    accessSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    classificationsByUid.Add(accessUid, accessSet);
                }

                foreach (var access in wireClassifications)
                {
                    accessSet.Add(access);
                }
            }
        }

        foreach (var entry in classificationsByUid)
        {
            if (!accessByUid.TryGetValue(entry.Key, out var accessRecord) ||
                string.IsNullOrWhiteSpace(accessRecord.Symbol))
            {
                continue;
            }

            var access = CollapseAccessKinds(entry.Value);
            if (string.IsNullOrWhiteSpace(access))
            {
                continue;
            }

            yield return new StandaloneAccessClassification(
                entry.Key,
                accessRecord.Symbol,
                accessRecord.Scope,
                access);
        }
    }

    private static IEnumerable<CallParameterBinding> GetCallParameterBindings(
        XElement callInfo,
        XElement compileUnit,
        IReadOnlyDictionary<string, AccessRecord> accessByUid)
    {
        var callParent = callInfo.Parent;
        if (callParent != null && callParent.Name.LocalName == "Call")
        {
            var callUid = GetUid(callParent);
            if (string.IsNullOrWhiteSpace(callUid))
            {
                yield break;
            }

            var parameters = callInfo
                .Elements()
                .Where(element => element.Name.LocalName == "Parameter")
                .Select(parameter => new
                {
                    Name = ((string?)parameter.Attribute("Name"))?.Trim() ?? string.Empty,
                    Section = ((string?)parameter.Attribute("Section"))?.Trim() ?? string.Empty
                })
                .Where(parameter => !string.IsNullOrWhiteSpace(parameter.Name))
                .ToDictionary(parameter => parameter.Name, parameter => parameter.Section, StringComparer.OrdinalIgnoreCase);

            foreach (var wire in compileUnit.Descendants().Where(element => element.Name.LocalName == "Wire"))
            {
                var nameCon = wire
                    .Descendants()
                    .FirstOrDefault(element =>
                        element.Name.LocalName == "NameCon" &&
                        string.Equals(((string?)element.Attribute("UId"))?.Trim(), callUid, StringComparison.OrdinalIgnoreCase));
                if (nameCon == null)
                {
                    continue;
                }

                var parameterName = ((string?)nameCon.Attribute("Name"))?.Trim() ?? string.Empty;
                if (!parameters.TryGetValue(parameterName, out var section))
                {
                    continue;
                }

                var identCon = wire.Descendants().FirstOrDefault(element => element.Name.LocalName == "IdentCon");
                var accessUid = ((string?)identCon?.Attribute("UId"))?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(accessUid) || !accessByUid.ContainsKey(accessUid))
                {
                    continue;
                }

                var access = accessByUid[accessUid];
                yield return new CallParameterBinding(accessUid, access.Symbol, access.Scope, parameterName, section);
            }

            yield break;
        }

        foreach (var parameter in callInfo.Elements().Where(element => element.Name.LocalName == "Parameter"))
        {
            var parameterName = ((string?)parameter.Attribute("Name"))?.Trim() ?? string.Empty;
            var section = ((string?)parameter.Attribute("Section"))?.Trim() ?? string.Empty;
            foreach (var access in parameter.Descendants().Where(element => element.Name.LocalName == "Access"))
            {
                var accessUid = GetUid(access);
                var symbol = GetSymbolName(access);
                if (string.IsNullOrWhiteSpace(symbol))
                {
                    continue;
                }

                yield return new CallParameterBinding(
                    accessUid,
                    symbol,
                    ((string?)access.Attribute("Scope"))?.Trim() ?? string.Empty,
                    parameterName,
                    section);
            }
        }
    }

    private static string GetAccessName(string section)
    {
        if (string.Equals(section, "Input", StringComparison.OrdinalIgnoreCase))
        {
            return "read";
        }

        if (string.Equals(section, "Output", StringComparison.OrdinalIgnoreCase))
        {
            return "write";
        }

        if (string.Equals(section, "InOut", StringComparison.OrdinalIgnoreCase))
        {
            return "inout";
        }

        return "unknown";
    }

    private static string GetStandaloneAccessName(string partName, string pinName)
    {
        if (string.IsNullOrWhiteSpace(partName) || string.IsNullOrWhiteSpace(pinName))
        {
            return string.Empty;
        }

        if (MatchesAny(partName, "Contact", "Compare", "Limit", "ContactF"))
        {
            return string.Equals(pinName, "operand", StringComparison.OrdinalIgnoreCase) ? "read" : string.Empty;
        }

        if (MatchesAny(partName, "Coil", "SCoil", "RCoil"))
        {
            return string.Equals(pinName, "operand", StringComparison.OrdinalIgnoreCase) ? "write" : string.Empty;
        }

        if (MatchesAny(partName, "Sr", "Rs"))
        {
            if (string.Equals(pinName, "operand", StringComparison.OrdinalIgnoreCase))
            {
                return "write";
            }

            if (MatchesAny(pinName, "s", "s1", "r", "r1"))
            {
                return "read";
            }

            return string.Empty;
        }

        if (MatchesAny(partName, "TON", "TOF", "TP"))
        {
            if (MatchesAny(pinName, "IN", "PT"))
            {
                return "read";
            }

            if (MatchesAny(pinName, "Q", "ET"))
            {
                return "write";
            }

            return string.Empty;
        }

        return string.Empty;
    }

    private static string CollapseAccessKinds(IReadOnlyCollection<string> accessKinds)
    {
        if (accessKinds.Count == 0)
        {
            return string.Empty;
        }

        var hasRead = accessKinds.Contains("read", StringComparer.OrdinalIgnoreCase);
        var hasWrite = accessKinds.Contains("write", StringComparer.OrdinalIgnoreCase);
        if (hasRead && hasWrite)
        {
            return "inout";
        }

        if (hasRead)
        {
            return "read";
        }

        if (hasWrite)
        {
            return "write";
        }

        return accessKinds.FirstOrDefault() ?? string.Empty;
    }

    private static bool MatchesAny(string value, params string[] candidates)
    {
        return candidates.Any(candidate => string.Equals(value, candidate, StringComparison.OrdinalIgnoreCase));
    }

    private static string GetCalleeName(XElement callInfo)
    {
        var name = ((string?)callInfo.Attribute("Name"))?.Trim();
        if (!string.IsNullOrWhiteSpace(name))
        {
            return name!;
        }

        return GetInstanceDb(callInfo);
    }

    private static string GetInstanceDb(XElement callInfo)
    {
        var instance = callInfo.Elements().FirstOrDefault(element => element.Name.LocalName == "Instance");
        if (instance == null)
        {
            return string.Empty;
        }

        var components = instance
            .Descendants()
            .Where(element => element.Name.LocalName == "Component")
            .Select(element => ((string?)element.Attribute("Name"))?.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();

        return components.Length == 0 ? string.Empty : string.Join(".", components);
    }

    private static string GetSymbolName(XElement access)
    {
        var components = access
            .Descendants()
            .Where(element => element.Name.LocalName == "Symbol")
            .Take(1)
            .Descendants()
            .Where(element => element.Name.LocalName == "Component")
            .Select(element => ((string?)element.Attribute("Name"))?.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();

        return components.Length == 0 ? string.Empty : string.Join(".", components);
    }

    private static string GetUid(XElement element)
    {
        return ((string?)element.Attribute("UId"))?.Trim() ?? string.Empty;
    }

    private static string GetBlockLanguage(XDocument document)
    {
        var block = document.Descendants().FirstOrDefault(IsProgramBlockElement);
        return block == null ? string.Empty : GetDirectAttributeValue(block, "ProgrammingLanguage");
    }

    private static bool IsProgramBlockElement(XElement element)
    {
        return element.Name.LocalName == "SW.Blocks.OB" ||
            element.Name.LocalName == "SW.Blocks.FC" ||
            element.Name.LocalName == "SW.Blocks.FB";
    }

    private static string GetDirectAttributeValue(XElement element, string name)
    {
        return element
            .Elements()
            .FirstOrDefault(child => child.Name.LocalName == "AttributeList")
            ?.Elements()
            .FirstOrDefault(child => child.Name.LocalName == name)
            ?.Value
            .Trim() ?? string.Empty;
    }

    private static string GetPreferredMultilingualText(XElement owner, string compositionName)
    {
        var text = owner
            .Descendants()
            .FirstOrDefault(element =>
                element.Name.LocalName == "MultilingualText" &&
                string.Equals((string?)element.Attribute("CompositionName"), compositionName, StringComparison.OrdinalIgnoreCase));

        if (text == null)
        {
            return string.Empty;
        }

        var items = text
            .Descendants()
            .Where(element => element.Name.LocalName == "MultilingualTextItem")
            .Select(item => new
            {
                Culture = GetDirectAttributeValue(item, "Culture"),
                Text = GetDirectAttributeValue(item, "Text")
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.Text))
            .ToArray();

        return items.FirstOrDefault(item => string.Equals(item.Culture, "en-GB", StringComparison.OrdinalIgnoreCase))?.Text
            ?? items.FirstOrDefault(item => string.Equals(item.Culture, "en-US", StringComparison.OrdinalIgnoreCase))?.Text
            ?? items.FirstOrDefault()?.Text
            ?? string.Empty;
    }

    private static int CountDescendants(XElement element, string localName)
    {
        return element.Descendants().Count(descendant => descendant.Name.LocalName == localName);
    }

    private static IReadOnlyList<string> DistinctTargets(IReadOnlyList<ProgramReferenceRecord> references, string access)
    {
        return references
            .Where(reference =>
                string.Equals(reference.TargetKind, "symbol", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(reference.Access, access, StringComparison.OrdinalIgnoreCase))
            .Select(reference => reference.To)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<string> DistinctCallTargets(IReadOnlyList<ProgramReferenceRecord> references)
    {
        return references
            .Where(reference =>
                string.Equals(reference.TargetKind, "block", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(reference.Access, "call", StringComparison.OrdinalIgnoreCase))
            .Select(reference => reference.To)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private sealed class CallParameterBinding
    {
        public CallParameterBinding(string accessUid, string symbol, string scope, string parameter, string section)
        {
            AccessUid = accessUid;
            Symbol = symbol;
            Scope = scope;
            Parameter = parameter;
            Section = section;
        }

        public string AccessUid { get; }

        public string Symbol { get; }

        public string Scope { get; }

        public string Parameter { get; }

        public string Section { get; }
    }

    private sealed class AccessRecord
    {
        public AccessRecord(string uid, string symbol, string scope)
        {
            Uid = uid;
            Symbol = symbol;
            Scope = scope;
        }

        public string Uid { get; }

        public string Symbol { get; }

        public string Scope { get; }
    }

    private sealed class PartRecord
    {
        public PartRecord(string uid, string name)
        {
            Uid = uid;
            Name = name;
        }

        public string Uid { get; }

        public string Name { get; }
    }

    private sealed class StandaloneAccessClassification
    {
        public StandaloneAccessClassification(string accessUid, string symbol, string scope, string access)
        {
            AccessUid = accessUid;
            Symbol = symbol;
            Scope = scope;
            Access = access;
        }

        public string AccessUid { get; }

        public string Symbol { get; }

        public string Scope { get; }

        public string Access { get; }
    }
}
