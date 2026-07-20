// Ported from PlcSourceExporter (src/PlcSourceExporter.Core/ProgramBlockLogicYamlWriter.cs) — adapted for mcp-knowledge; keep changes minimal to ease future re-syncs.
// Only the FlgNet → SCL-like translator ports (GetNetworkStatementTextByCompileUnitId and everything below);
// the YAML file generation (Write / Serialize / Quote, ProgramBlockLogicYamlResult) stays behind — mcp-knowledge
// stores translated statements as the `logicStatements` network property, not a translate\program-blocks.yaml file.
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Mcp.Knowledge.Parsing;

public static class ProgramBlockLogicYamlWriter
{
    public static IReadOnlyDictionary<string, string> GetNetworkStatementTextByCompileUnitId(string xml, ProgramBlockComponent component)
    {
        if (component == null)
        {
            throw new ArgumentNullException(nameof(component));
        }

        var block = ParseBlock(xml, component);
        return block.Networks
            .Where(network => !string.IsNullOrWhiteSpace(network.CompileUnitId) && network.Statements.Count > 0)
            .GroupBy(network => network.CompileUnitId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => string.Join("\n", group.First().Statements),
                StringComparer.OrdinalIgnoreCase);
    }

    private static LogicBlock ParseBlock(string xml, ProgramBlockComponent component)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(xml);
        }
        catch (XmlException)
        {
            return new LogicBlock(
                component.Name,
                component.Category,
                component.ExportedFile,
                string.Empty,
                new[]
                {
                    LogicNetwork.Untranslated(1, string.Empty, "Network 1", string.Empty, "Malformed XML source.")
                });
        }

        var blockLanguage = GetBlockLanguage(document);
        var networks = document
            .Descendants()
            .Where(element => element.Name.LocalName == "SW.Blocks.CompileUnit")
            .Select((element, index) => ParseNetwork(element, index + 1, blockLanguage))
            .ToArray();

        return new LogicBlock(
            component.Name,
            component.Category,
            component.ExportedFile,
            blockLanguage,
            networks);
    }

    private static LogicNetwork ParseNetwork(XElement compileUnit, int networkIndex, string blockLanguage)
    {
        var compileUnitId = ((string?)compileUnit.Attribute("ID"))?.Trim() ?? string.Empty;
        var title = GetPreferredMultilingualText(compileUnit, "Title");
        if (string.IsNullOrWhiteSpace(title))
        {
            title = $"Network {networkIndex}";
        }

        var language = GetDirectAttributeValue(compileUnit, "ProgrammingLanguage");
        if (string.IsNullOrWhiteSpace(language))
        {
            language = blockLanguage;
        }

        if (IsLadOrFbd(language))
        {
            var flgNet = compileUnit.Descendants().FirstOrDefault(element => element.Name.LocalName == "FlgNet");
            if (flgNet == null)
            {
                if (IsEmptyNetworkSource(compileUnit))
                {
                    return new LogicNetwork(
                        networkIndex,
                        compileUnitId,
                        title,
                        language,
                        "scl-like",
                        "exact",
                        Array.Empty<string>(),
                        Array.Empty<string>());
                }

                return LogicNetwork.Untranslated(
                    networkIndex,
                    compileUnitId,
                    title,
                    language,
                    "Unsupported network language or missing FlgNet source.");
            }

            return TranslateFlgNet(flgNet, networkIndex, compileUnitId, title, language);
        }

        if (string.Equals(language, "SCL", StringComparison.OrdinalIgnoreCase))
        {
            var source = GetCompactSclSource(compileUnit);
            if (!string.IsNullOrWhiteSpace(source))
            {
                return new LogicNetwork(
                    networkIndex,
                    compileUnitId,
                    title,
                    language,
                    "scl",
                    "exact",
                    SplitSclStatements(source),
                    Array.Empty<string>());
            }

            if (HasStructuredTextSource(compileUnit) || IsEmptyNetworkSource(compileUnit))
            {
                return new LogicNetwork(
                    networkIndex,
                    compileUnitId,
                    title,
                    language,
                    "scl",
                    "exact",
                    Array.Empty<string>(),
                    Array.Empty<string>());
            }
        }

        return LogicNetwork.Untranslated(
            networkIndex,
            compileUnitId,
            title,
            language,
            "Unsupported network language or missing FlgNet source.");
    }

    private static LogicNetwork TranslateFlgNet(
        XElement flgNet,
        int networkIndex,
        string compileUnitId,
        string title,
        string language)
    {
        var context = FlgNetContext.Create(flgNet);
        var statements = new List<string>();
        var notes = new List<string>();

        statements.AddRange(context.BuildPartCallStatements(notes));
        statements.AddRange(context.BuildProcedureFunctionCallStatements(notes));

        foreach (var coil in context.Parts.Values.Where(part => IsPlainCoil(part.Name)).OrderBy(part => part.Order))
        {
            var target = context.GetPinAccess(coil.Uid, "operand");
            if (string.IsNullOrWhiteSpace(target))
            {
                notes.Add("Skipped a coil because its operand could not be resolved by symbol name.");
                continue;
            }

            var input = context.EvaluateInput(coil.Uid, "in", notes);
            if (string.IsNullOrWhiteSpace(input))
            {
                notes.Add($"Skipped assignment to {target} because the coil input could not be resolved.");
                continue;
            }

            statements.Add($"{target} := {input};");
        }

        foreach (var coil in context.Parts.Values.Where(part => IsLatchCoil(part.Name)).OrderBy(part => part.Order))
        {
            var statement = context.TryBuildLatchCoilStatement(coil, notes);
            if (!string.IsNullOrWhiteSpace(statement))
            {
                statements.Add(statement);
            }
        }

        statements.AddRange(context.BuildSetResetPartStatements(notes));
        statements.AddRange(context.BuildPulseCoilStatements(notes));

        foreach (var call in context.Calls.OrderBy(call => call.Order))
        {
            var callStatement = context.TryBuildCallStatement(call, notes);
            if (!string.IsNullOrWhiteSpace(callStatement))
            {
                statements.Add(callStatement);
            }
        }

        statements.AddRange(context.BuildControlFlowStatements(notes));
        statements.AddRange(context.BuildIncrementStatements(notes));
        statements.AddRange(context.BuildDirectAssignmentStatements(notes));

        if (statements.Count == 0)
        {
            if (notes.Count == 0)
            {
                notes.Add("No supported coils or resolvable call statements were found.");
            }

            return new LogicNetwork(
                networkIndex,
                compileUnitId,
                title,
                language,
                "scl-like",
                "untranslated",
                Array.Empty<string>(),
                notes);
        }

        return new LogicNetwork(
            networkIndex,
            compileUnitId,
            title,
            language,
            "scl-like",
            notes.Count == 0 ? "exact" : "partial",
            statements,
            notes);
    }

    private static bool IsLadOrFbd(string language)
    {
        return string.Equals(language, "LAD", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(language, "FBD", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(language, "F_LAD", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(language, "F_FBD", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPlainCoil(string partName)
    {
        return MatchesAny(partName, "Coil");
    }

    private static bool IsLatchCoil(string partName)
    {
        return MatchesAny(partName, "SCoil", "RCoil");
    }

    private static bool IsSetResetPart(string partName)
    {
        return MatchesAny(partName, "Sr", "Rs");
    }

    private static bool MatchesAny(string value, params string[] candidates)
    {
        return candidates.Any(candidate => string.Equals(value, candidate, StringComparison.OrdinalIgnoreCase));
    }

    private static string GetCompactSclSource(XElement compileUnit)
    {
        var networkSource = compileUnit.Descendants().FirstOrDefault(element => element.Name.LocalName == "NetworkSource");
        if (networkSource == null)
        {
            return string.Empty;
        }

        var structuredText = RenderStructuredTextSource(networkSource);
        if (!string.IsNullOrWhiteSpace(structuredText))
        {
            return structuredText;
        }

        var text = networkSource
            .Descendants()
            .Where(element => element.Name.LocalName == "Text" || element.Name.LocalName == "Source")
            .Select(element => element.Value)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        if (string.IsNullOrWhiteSpace(text))
        {
            text = networkSource.Value;
        }

        return NormalizeWhitespace(text);
    }

    private static string RenderStructuredTextSource(XElement networkSource)
    {
        var structuredText = networkSource
            .Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "StructuredText");
        if (structuredText == null)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var child in structuredText.Elements())
        {
            AppendStructuredTextElement(builder, child);
        }

        return TrimStructuredText(builder.ToString());
    }

    private static void AppendStructuredTextElement(StringBuilder builder, XElement element)
    {
        switch (element.Name.LocalName)
        {
            case "Blank":
                builder.Append(' ');
                return;
            case "NewLine":
                builder.AppendLine();
                return;
            case "Token":
                builder.Append(((string?)element.Attribute("Text")) ?? element.Value);
                return;
            case "Component":
                builder.Append(FormatComponentName(element));
                foreach (var child in element.Elements())
                {
                    AppendStructuredTextElement(builder, child);
                }

                return;
            case "Instruction":
                builder.Append(((string?)element.Attribute("Name")) ?? element.Value);
                foreach (var child in element.Elements())
                {
                    AppendStructuredTextElement(builder, child);
                }

                return;
            case "Parameter":
                builder.Append(((string?)element.Attribute("Name")) ?? element.Value);
                foreach (var child in element.Elements())
                {
                    AppendStructuredTextElement(builder, child);
                }

                return;
            case "ConstantValue":
                builder.Append(element.Value);
                return;
            default:
                foreach (var child in element.Elements())
                {
                    AppendStructuredTextElement(builder, child);
                }

                if (!element.Elements().Any() && !string.IsNullOrWhiteSpace(element.Value))
                {
                    builder.Append(element.Value.Trim());
                }

                return;
        }
    }

    private static string TrimStructuredText(string source)
    {
        var lines = source
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n')
            .Select(line => line.TrimEnd())
            .ToArray();

        return string.Join("\n", lines).Trim();
    }

    private static string FormatComponentName(XElement component)
    {
        var name = ((string?)component.Attribute("Name"))?.Trim() ?? component.Value ?? string.Empty;
        var slice = ((string?)component.Attribute("SliceAccessModifier"))?.Trim();
        if (string.IsNullOrWhiteSpace(slice))
        {
            return name;
        }

        return $"{name}.%{slice!.ToUpperInvariant()}";
    }

    private static IReadOnlyList<string> SplitSclStatements(string source)
    {
        return string.IsNullOrWhiteSpace(source)
            ? Array.Empty<string>()
            : new[] { source.Trim() };
    }

    private static bool IsEmptyNetworkSource(XElement compileUnit)
    {
        var networkSource = compileUnit.Descendants().FirstOrDefault(element => element.Name.LocalName == "NetworkSource");
        if (networkSource == null)
        {
            return false;
        }

        return !networkSource.Elements().Any() && string.IsNullOrWhiteSpace(networkSource.Value);
    }

    private static bool HasStructuredTextSource(XElement compileUnit)
    {
        return compileUnit.Descendants().Any(element => element.Name.LocalName == "StructuredText");
    }

    private static string NormalizeWhitespace(string value)
    {
        var builder = new StringBuilder();
        var pendingSpace = false;
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(character);
        }

        return builder.ToString().Trim();
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

    private sealed class FlgNetContext
    {
        private readonly Dictionary<PartPin, PartPin> _inputSources;
        private readonly Dictionary<PartPin, string> _pinAccesses;
        private readonly Dictionary<PartPin, string> _powerInputs;
        private readonly IReadOnlyList<DirectAssignment> _directAssignments;
        private readonly Dictionary<PartPin, string> _expressionCache = new();
        private readonly HashSet<PartPin> _visiting = new();

        private FlgNetContext(
            IReadOnlyDictionary<string, PartNode> parts,
            IReadOnlyDictionary<string, AccessNode> accesses,
            IReadOnlyList<CallNode> calls,
            Dictionary<PartPin, PartPin> inputSources,
            Dictionary<PartPin, string> pinAccesses,
            Dictionary<PartPin, string> powerInputs,
            IReadOnlyList<DirectAssignment> directAssignments)
        {
            Parts = parts;
            Accesses = accesses;
            Calls = calls;
            _inputSources = inputSources;
            _pinAccesses = pinAccesses;
            _powerInputs = powerInputs;
            _directAssignments = directAssignments;
        }

        public IReadOnlyDictionary<string, PartNode> Parts { get; }

        public IReadOnlyDictionary<string, AccessNode> Accesses { get; }

        public IReadOnlyList<CallNode> Calls { get; }

        public static FlgNetContext Create(XElement flgNet)
        {
            var orderedElements = flgNet
                .Descendants()
                .Where(element => element.Name.LocalName == "Access" || element.Name.LocalName == "Part" || element.Name.LocalName == "Call")
                .Select((element, index) => new { Element = element, Order = index })
                .ToArray();

            var accesses = orderedElements
                .Where(item => item.Element.Name.LocalName == "Access")
                .Select(item => new AccessNode(
                    GetUid(item.Element),
                    GetAccessValue(item.Element),
                    ((string?)item.Element.Attribute("Scope"))?.Trim() ?? string.Empty))
                .Where(access => !string.IsNullOrWhiteSpace(access.Uid))
                .GroupBy(access => access.Uid, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

            var parts = orderedElements
                .Where(item => item.Element.Name.LocalName == "Part")
                .Select(item => new PartNode(
                    GetUid(item.Element),
                    ((string?)item.Element.Attribute("Name"))?.Trim() ?? string.Empty,
                    item.Order,
                    GetInstanceName(item.Element),
                    item.Element
                        .Elements()
                        .FirstOrDefault(element => element.Name.LocalName == "Equation")
                        ?.Value
                        .Trim() ?? string.Empty,
                    item.Element.Descendants()
                        .Where(element => element.Name.LocalName == "Negated")
                        .Select(element => ((string?)element.Attribute("Name"))?.Trim() ?? string.Empty)
                        .Where(value => !string.IsNullOrWhiteSpace(value))
                        .ToArray()))
                .Where(part => !string.IsNullOrWhiteSpace(part.Uid))
                .GroupBy(part => part.Uid, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

            var calls = orderedElements
                .Where(item => item.Element.Name.LocalName == "Call")
                .Select(item => CreateCallNode(item.Element, item.Order))
                .Where(call => !string.IsNullOrWhiteSpace(call.Uid))
                .ToArray();

            var inputSources = new Dictionary<PartPin, PartPin>();
            var pinAccesses = new Dictionary<PartPin, string>();
            var powerInputs = new Dictionary<PartPin, string>();
            var directAssignments = new List<DirectAssignment>();

            foreach (var item in flgNet.Descendants().Where(element => element.Name.LocalName == "Wire").Select((wire, index) => new { Wire = wire, Order = index }))
            {
                var nameCons = item.Wire
                    .Descendants()
                    .Where(element => element.Name.LocalName == "NameCon")
                    .Select(element => new PartPin(
                        ((string?)element.Attribute("UId"))?.Trim() ?? string.Empty,
                        ((string?)element.Attribute("Name"))?.Trim() ?? string.Empty))
                    .Where(pin => !string.IsNullOrWhiteSpace(pin.PartUid) && !string.IsNullOrWhiteSpace(pin.PinName))
                    .ToArray();

                var accessValues = item.Wire
                    .Descendants()
                    .Where(element => element.Name.LocalName == "IdentCon")
                    .Select(element => ((string?)element.Attribute("UId"))?.Trim() ?? string.Empty)
                    .Where(uid => !string.IsNullOrWhiteSpace(uid) && accesses.ContainsKey(uid))
                    .Select(uid => accesses[uid].Value)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .ToArray();

                if (accessValues.Length == 1)
                {
                    foreach (var pin in nameCons)
                    {
                        pinAccesses[pin] = accessValues[0];
                    }
                }

                if (item.Wire.Descendants().Any(element => element.Name.LocalName == "Powerrail"))
                {
                    foreach (var pin in nameCons.Where(pin => IsInputPin(pin.PinName)))
                    {
                        powerInputs[pin] = "TRUE";
                    }
                }

                var sourcePins = nameCons.Where(pin => IsOutputPin(pin.PinName)).ToArray();
                var targetPins = nameCons.Where(pin => IsInputPin(pin.PinName)).ToArray();
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
            }

            return new FlgNetContext(parts, accesses, calls, inputSources, pinAccesses, powerInputs, directAssignments);
        }

        public string GetPinAccess(string partUid, string pinName)
        {
            return _pinAccesses.TryGetValue(new PartPin(partUid, pinName), out var access) ? access : string.Empty;
        }

        public string EvaluateInput(string partUid, string pinName, List<string> notes)
        {
            var inputPin = new PartPin(partUid, pinName);
            if (_powerInputs.TryGetValue(inputPin, out var powerExpression))
            {
                return powerExpression;
            }

            if (!_inputSources.TryGetValue(inputPin, out var source))
            {
                return string.Empty;
            }

            return EvaluateOutput(source.PartUid, source.PinName, notes);
        }

        public string TryBuildCallStatement(CallNode call, List<string> notes)
        {
            if (string.IsNullOrWhiteSpace(call.Name))
            {
                notes.Add("Skipped a call because its block or instance name could not be resolved.");
                return string.Empty;
            }

            var bindings = new List<string>();
            foreach (var parameter in call.Parameters)
            {
                var isOutput = string.Equals(parameter.Section, "Output", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(parameter.Section, "InOut", StringComparison.OrdinalIgnoreCase);
                var value = isOutput
                    ? GetPinAccess(call.Uid, parameter.Name)
                    : ResolveInputValue(call.Uid, parameter.Name, notes);
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                var separator = isOutput ? " => " : " := ";
                bindings.Add($"{parameter.Name}{separator}{value}");
            }

            if (bindings.Count == 0)
            {
                if (call.Parameters.Count == 0 || HasEnableInput(call.Uid))
                {
                    return BuildCallStatement(call.Uid, call.Name, string.Empty, notes);
                }

                notes.Add($"Skipped call {call.Name} because no parameters could be resolved by symbol name.");
                return string.Empty;
            }

            return BuildCallStatement(call.Uid, call.Name, string.Join(", ", bindings), notes);
        }

        public string TryBuildLatchCoilStatement(PartNode coil, List<string> notes)
        {
            var target = GetPinAccess(coil.Uid, "operand");
            if (string.IsNullOrWhiteSpace(target))
            {
                notes.Add("Skipped a latch coil because its operand could not be resolved by symbol name.");
                return string.Empty;
            }

            var input = EvaluateInput(coil.Uid, "in", notes);
            if (string.IsNullOrWhiteSpace(input))
            {
                notes.Add($"Skipped latch assignment to {target} because the coil input could not be resolved.");
                return string.Empty;
            }

            var value = string.Equals(coil.Name, "SCoil", StringComparison.OrdinalIgnoreCase) ? "TRUE" : "FALSE";
            return $"IF {input} THEN {target} := {value}; END_IF;";
        }

        public IReadOnlyList<string> BuildDirectAssignmentStatements(List<string> notes)
        {
            var statements = new List<string>();
            foreach (var assignment in _directAssignments.OrderBy(assignment => assignment.Order))
            {
                var source = EvaluateOutput(assignment.Source.PartUid, assignment.Source.PinName, notes);
                if (string.IsNullOrWhiteSpace(source))
                {
                    notes.Add($"Skipped direct assignment to {assignment.Target} because the source output could not be resolved.");
                    continue;
                }

                var statement = $"{assignment.Target} := {source};";
                if (Parts.TryGetValue(assignment.Source.PartUid, out var part))
                {
                    if (IsProcedureFunctionPart(part.Name))
                    {
                        continue;
                    }

                    var enable = EvaluateInput(part.Uid, "en", notes);
                    if (!string.IsNullOrWhiteSpace(enable) && !string.Equals(enable, "TRUE", StringComparison.OrdinalIgnoreCase))
                    {
                        statement = $"IF {enable} THEN {statement} END_IF;";
                    }
                }

                statements.Add(statement);
            }

            return statements;
        }

        public IReadOnlyList<string> BuildPartCallStatements(List<string> notes)
        {
            var statements = new List<string>();
            foreach (var part in Parts.Values.Where(part => IsInstanceCallPart(part.Name)).OrderBy(part => part.Order))
            {
                if (string.IsNullOrWhiteSpace(part.InstanceName))
                {
                    notes.Add($"Skipped {part.Name} call because its instance name could not be resolved.");
                    continue;
                }

                var bindings = GetInputBindings(part.Uid, notes);

                if (bindings.Count == 0)
                {
                    if (!HasEnableInput(part.Uid))
                    {
                        notes.Add($"Skipped {part.Name} call {part.InstanceName} because no input pins could be resolved.");
                        continue;
                    }
                }

                statements.Add($"{part.InstanceName}({string.Join(", ", bindings)});");
            }

            return statements;
        }

        public IReadOnlyList<string> BuildSetResetPartStatements(List<string> notes)
        {
            var statements = new List<string>();
            foreach (var part in Parts.Values.Where(part => IsSetResetPart(part.Name)).OrderBy(part => part.Order))
            {
                var target = GetPinAccess(part.Uid, "operand");
                if (string.IsNullOrWhiteSpace(target))
                {
                    notes.Add($"Skipped {part.Name} part because its operand could not be resolved by symbol name.");
                    continue;
                }

                // Siemens Sr part (pins S/R1) is reset dominant, Rs part (pins S1/R) is set
                // dominant: the dominant input's statement is emitted last so it overwrites
                // the other one when both inputs are true.
                var resetDominant = string.Equals(part.Name, "Sr", StringComparison.OrdinalIgnoreCase);
                var set = ResolveInputValue(part.Uid, resetDominant ? "s" : "s1", notes);
                var reset = ResolveInputValue(part.Uid, resetDominant ? "r1" : "r", notes);
                if (string.IsNullOrWhiteSpace(set) && string.IsNullOrWhiteSpace(reset))
                {
                    notes.Add($"Skipped {part.Name} part for {target} because neither set nor reset could be resolved.");
                    continue;
                }

                var setStatement = string.IsNullOrWhiteSpace(set)
                    ? null
                    : $"IF {set} THEN {target} := TRUE; END_IF;";
                var resetStatement = string.IsNullOrWhiteSpace(reset)
                    ? null
                    : $"IF {reset} THEN {target} := FALSE; END_IF;";

                if (resetDominant)
                {
                    if (setStatement != null)
                    {
                        statements.Add(setStatement);
                    }

                    if (resetStatement != null)
                    {
                        statements.Add(resetStatement);
                    }
                }
                else
                {
                    if (resetStatement != null)
                    {
                        statements.Add(resetStatement);
                    }

                    if (setStatement != null)
                    {
                        statements.Add(setStatement);
                    }
                }
            }

            return statements;
        }

        public IReadOnlyList<string> BuildPulseCoilStatements(List<string> notes)
        {
            var statements = new List<string>();
            foreach (var part in Parts.Values.Where(part => string.Equals(part.Name, "PCoil", StringComparison.OrdinalIgnoreCase)).OrderBy(part => part.Order))
            {
                var target = GetPinAccess(part.Uid, "operand");
                var bit = GetPinAccess(part.Uid, "bit");
                var input = EvaluateInput(part.Uid, "in", notes);
                if (string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(bit) || string.IsNullOrWhiteSpace(input))
                {
                    notes.Add("Skipped PCoil because its operand, bit, or input could not be resolved.");
                    continue;
                }

                statements.Add($"{target} := PULSE({input}, {bit});");
            }

            return statements;
        }

        public IReadOnlyList<string> BuildControlFlowStatements(List<string> notes)
        {
            var statements = new List<string>();
            foreach (var part in Parts.Values.Where(part => IsControlFlowPart(part.Name)).OrderBy(part => part.Order))
            {
                var input = EvaluateInput(part.Uid, "in", notes);
                if (string.IsNullOrWhiteSpace(input))
                {
                    notes.Add($"Skipped {part.Name} because its input could not be resolved.");
                    continue;
                }

                if (string.Equals(part.Name, "Jump", StringComparison.OrdinalIgnoreCase))
                {
                    var label = GetPinAccess(part.Uid, "label");
                    if (string.IsNullOrWhiteSpace(label))
                    {
                        notes.Add("Skipped Jump because its label could not be resolved.");
                        continue;
                    }

                    statements.Add($"IF {input} THEN GOTO {label}; END_IF;");
                    continue;
                }

                if (string.Equals(part.Name, "ReturnValue", StringComparison.OrdinalIgnoreCase))
                {
                    var value = ResolveInputValue(part.Uid, "operand", notes);
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        notes.Add("Skipped ReturnValue because its return value could not be resolved.");
                        continue;
                    }

                    statements.Add($"IF {input} THEN RETURN {value}; END_IF;");
                    continue;
                }

                if (string.Equals(part.Name, "ReturnTrue", StringComparison.OrdinalIgnoreCase))
                {
                    statements.Add($"IF {input} THEN RETURN TRUE; END_IF;");
                    continue;
                }

                statements.Add($"IF {input} THEN RETURN; END_IF;");
            }

            return statements;
        }

        public IReadOnlyList<string> BuildIncrementStatements(List<string> notes)
        {
            var statements = new List<string>();
            foreach (var part in Parts.Values.Where(part => string.Equals(part.Name, "Inc", StringComparison.OrdinalIgnoreCase)).OrderBy(part => part.Order))
            {
                var target = GetPinAccess(part.Uid, "operand");
                var enable = EvaluateInput(part.Uid, "en", notes);
                if (string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(enable))
                {
                    notes.Add("Skipped Inc because its operand or enable input could not be resolved.");
                    continue;
                }

                statements.Add(string.Equals(enable, "TRUE", StringComparison.OrdinalIgnoreCase)
                    ? $"{target} := {target} + 1;"
                    : $"IF {enable} THEN {target} := {target} + 1; END_IF;");
            }

            return statements;
        }

        public IReadOnlyList<string> BuildProcedureFunctionCallStatements(List<string> notes)
        {
            var statements = new List<string>();
            foreach (var group in _directAssignments
                .Where(assignment => Parts.TryGetValue(assignment.Source.PartUid, out var part) && IsProcedureFunctionPart(part.Name))
                .GroupBy(assignment => assignment.Source.PartUid, StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => Parts[group.Key].Order))
            {
                var part = Parts[group.Key];
                var bindings = GetInputBindings(part.Uid, notes).ToList();
                bindings.AddRange(group
                    .OrderBy(assignment => GetPinSortKey(assignment.Source.PinName))
                    .ThenBy(assignment => assignment.Source.PinName, StringComparer.OrdinalIgnoreCase)
                    .Select(assignment => $"{assignment.Source.PinName} => {assignment.Target}"));

                statements.Add($"{part.Name}({string.Join(", ", bindings)});");
            }

            return statements;
        }

        private string BuildCallStatement(string callUid, string callName, string bindings, List<string> notes)
        {
            var enable = EvaluateInput(callUid, "en", notes);
            var prefix = string.IsNullOrWhiteSpace(enable) || string.Equals(enable, "TRUE", StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : $"IF {enable} THEN ";
            var suffix = string.IsNullOrWhiteSpace(prefix) ? string.Empty : " END_IF;";
            return $"{prefix}{callName}({bindings});{suffix}";
        }

        private bool HasEnableInput(string partUid)
        {
            return _inputSources.Keys
                .Concat(_powerInputs.Keys)
                .Any(pin => string.Equals(pin.PartUid, partUid, StringComparison.OrdinalIgnoreCase) &&
                    MatchesAny(pin.PinName, "en", "pre"));
        }

        private string EvaluateOutput(string partUid, string pinName, List<string> notes)
        {
            var outputPin = new PartPin(partUid, pinName);
            if (_expressionCache.TryGetValue(outputPin, out var cached))
            {
                return cached;
            }

            if (_visiting.Contains(outputPin))
            {
                notes.Add("Skipped cyclic LAD/FBD wiring while translating a network expression.");
                return string.Empty;
            }

            _visiting.Add(outputPin);
            try
            {
                if (!Parts.TryGetValue(partUid, out var part))
                {
                    var call = Calls.FirstOrDefault(item => string.Equals(item.Uid, partUid, StringComparison.OrdinalIgnoreCase));
                    if (call == null)
                    {
                        notes.Add("Skipped a wire endpoint because its part was not found.");
                        return string.Empty;
                    }

                    if (MatchesAny(pinName, "eno", "out"))
                    {
                        var enable = EvaluateInput(call.Uid, "en", notes);
                        return string.IsNullOrWhiteSpace(enable) ? "TRUE" : enable;
                    }

                    return $"{call.Name}.{NormalizeOutputPinName(pinName)}";
                }

                var expression = EvaluatePartOutput(part, pinName, notes);
                _expressionCache[outputPin] = expression;
                return expression;
            }
            finally
            {
                _visiting.Remove(outputPin);
            }
        }

        private string EvaluatePartOutput(PartNode part, string pinName, List<string> notes)
        {
            if (string.Equals(part.Name, "Contact", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(part.Name, "ContactF", StringComparison.OrdinalIgnoreCase))
            {
                var operand = GetPinAccess(part.Uid, "operand");
                if (string.IsNullOrWhiteSpace(operand))
                {
                    notes.Add("Skipped a contact because its operand could not be resolved by symbol name.");
                    return string.Empty;
                }

                var condition = part.IsNegated("operand") ? $"NOT {operand}" : operand;
                var upstream = EvaluateInput(part.Uid, "in", notes);
                return And(upstream, condition);
            }

            if (string.Equals(part.Name, "PContact", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(part.Name, "NContact", StringComparison.OrdinalIgnoreCase))
            {
                var operand = GetPinAccess(part.Uid, "operand");
                var bit = GetPinAccess(part.Uid, "bit");
                if (string.IsNullOrWhiteSpace(operand) || string.IsNullOrWhiteSpace(bit))
                {
                    notes.Add("Skipped an edge contact because its operand or memory bit could not be resolved.");
                    return string.Empty;
                }

                var upstream = EvaluateInput(part.Uid, "pre", notes);
                var edge = string.Equals(part.Name, "PContact", StringComparison.OrdinalIgnoreCase) ? "PULSE" : "NPULSE";
                return And(upstream, $"{edge}({operand}, {bit})");
            }

            if (IsCompare(part.Name))
            {
                var left = GetPinAccess(part.Uid, "in1");
                var right = GetPinAccess(part.Uid, "in2");
                if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
                {
                    notes.Add($"Skipped compare part {part.Name} because one side could not be resolved.");
                    return string.Empty;
                }

                var compare = $"({left} {GetCompareOperator(part.Name)} {right})";
                var upstream = EvaluateInput(part.Uid, "pre", notes);
                if (string.IsNullOrWhiteSpace(upstream))
                {
                    upstream = EvaluateInput(part.Uid, "in", notes);
                }

                return And(upstream, compare);
            }

            if (string.Equals(part.Name, "Not", StringComparison.OrdinalIgnoreCase))
            {
                var input = EvaluateInput(part.Uid, "in", notes);
                return string.IsNullOrWhiteSpace(input) ? string.Empty : $"NOT ({input})";
            }

            if (string.Equals(part.Name, "O", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(part.Name, "Or", StringComparison.OrdinalIgnoreCase))
            {
                var inputs = _inputSources.Keys
                    .Where(pin => string.Equals(pin.PartUid, part.Uid, StringComparison.OrdinalIgnoreCase) && IsInputPin(pin.PinName))
                    .Concat(_powerInputs.Keys.Where(pin => string.Equals(pin.PartUid, part.Uid, StringComparison.OrdinalIgnoreCase) && IsInputPin(pin.PinName)))
                    .Select(pin => pin.PinName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(GetPinSortKey)
                    .Select(pinName => EvaluateInput(part.Uid, pinName, notes))
                    .Where(value => !string.IsNullOrWhiteSpace(value) && !string.Equals(value, "TRUE", StringComparison.OrdinalIgnoreCase))
                    .ToArray();

                if (inputs.Length == 0)
                {
                    return string.Empty;
                }

                return inputs.Length == 1 ? inputs[0] : $"({string.Join(" OR ", inputs)})";
            }

            if (string.Equals(part.Name, "A", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(part.Name, "And", StringComparison.OrdinalIgnoreCase))
            {
                var inputs = _inputSources.Keys
                    .Where(pin => string.Equals(pin.PartUid, part.Uid, StringComparison.OrdinalIgnoreCase) && IsInputPin(pin.PinName))
                    .Select(pin => pin.PinName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(GetPinSortKey)
                    .Select(pinName => EvaluateInput(part.Uid, pinName, notes))
                    .Where(value => !string.IsNullOrWhiteSpace(value) && !string.Equals(value, "TRUE", StringComparison.OrdinalIgnoreCase))
                    .ToArray();

                if (inputs.Length == 0)
                {
                    return string.Empty;
                }

                return inputs.Length == 1 ? inputs[0] : $"({string.Join(" AND ", inputs)})";
            }

            if (string.Equals(part.Name, "PBox", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(part.Name, "NBox", StringComparison.OrdinalIgnoreCase))
            {
                var input = EvaluateInput(part.Uid, "in", notes);
                if (string.IsNullOrWhiteSpace(input))
                {
                    input = GetPinAccess(part.Uid, "in");
                }

                var bit = GetPinAccess(part.Uid, "bit");
                if (string.IsNullOrWhiteSpace(input) || string.IsNullOrWhiteSpace(bit))
                {
                    notes.Add("Skipped an edge box because its input or memory bit could not be resolved.");
                    return string.Empty;
                }

                var edge = string.Equals(part.Name, "PBox", StringComparison.OrdinalIgnoreCase) ? "PULSE" : "NPULSE";
                return $"{edge}({input}, {bit})";
            }

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

            if (string.Equals(part.Name, "Coil", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(part.Name, "SCoil", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(part.Name, "RCoil", StringComparison.OrdinalIgnoreCase))
            {
                return EvaluateInput(part.Uid, "in", notes);
            }

            if (string.Equals(part.Name, "Move", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(part.Name, "S_Move", StringComparison.OrdinalIgnoreCase))
            {
                if (MatchesAny(pinName, "eno"))
                {
                    var enable = EvaluateInput(part.Uid, "en", notes);
                    return string.IsNullOrWhiteSpace(enable) ? "TRUE" : enable;
                }

                if (!IsMoveOutputPin(pinName))
                {
                    notes.Add($"Skipped {part.Name} output pin '{pinName}' because it is not supported.");
                    return string.Empty;
                }

                var input = GetPinAccess(part.Uid, "in");
                if (string.IsNullOrWhiteSpace(input))
                {
                    input = GetPinAccess(part.Uid, "in1");
                }

                if (string.IsNullOrWhiteSpace(input))
                {
                    notes.Add($"Skipped {part.Name} output because its input could not be resolved.");
                }

                return input;
            }

            if (IsArithmeticPart(part.Name))
            {
                return BuildArithmeticExpression(part, notes);
            }

            if (IsSetResetPart(part.Name) && MatchesAny(pinName, "q", "out"))
            {
                var target = GetPinAccess(part.Uid, "operand");
                if (string.IsNullOrWhiteSpace(target))
                {
                    notes.Add($"Skipped {part.Name} output because its operand could not be resolved by symbol name.");
                }

                return target;
            }

            if (IsFunctionExpressionPart(part.Name))
            {
                if (IsProcedureFunctionPart(part.Name))
                {
                    return $"{part.Name}.{NormalizeOutputPinName(pinName)}";
                }

                var bindings = GetInputBindings(part.Uid, notes);
                if (bindings.Count == 0)
                {
                    notes.Add($"Skipped {part.Name} output because no input pins could be resolved.");
                    return string.Empty;
                }

                return $"{part.Name}({string.Join(", ", bindings)})";
            }

            if (string.Equals(part.Name, "Calc", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(part.Equation))
                {
                    notes.Add("Skipped Calc output because its equation could not be resolved.");
                    return string.Empty;
                }

                var expression = part.Equation;
                foreach (var pinNameInEquation in GetCalcInputNames(part.Equation))
                {
                    var value = GetPinAccess(part.Uid, pinNameInEquation);
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        notes.Add($"Skipped Calc output because {pinNameInEquation} could not be resolved.");
                        return string.Empty;
                    }

                    expression = ReplaceIdentifier(expression, pinNameInEquation, value);
                }

                return expression;
            }

            if (string.Equals(part.Name, "InRange", StringComparison.OrdinalIgnoreCase))
            {
                var min = GetPinAccess(part.Uid, "min");
                var input = GetPinAccess(part.Uid, "in");
                var max = GetPinAccess(part.Uid, "max");
                if (string.IsNullOrWhiteSpace(min) || string.IsNullOrWhiteSpace(input) || string.IsNullOrWhiteSpace(max))
                {
                    notes.Add("Skipped InRange output because min, input, or max could not be resolved.");
                    return string.Empty;
                }

                var upstream = EvaluateInput(part.Uid, "pre", notes);
                return And(upstream, $"({min} <= {input} AND {input} <= {max})");
            }

            notes.Add($"Unsupported LAD/FBD part '{part.Name}'.");
            return string.Empty;
        }

        private static bool IsInputPin(string pinName)
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

            if (MatchesAny(
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
                "K"))
            {
                return true;
            }

            return !IsOutputPin(pinName) && !MatchesAny(pinName, "operand", "bit");
        }

        private static bool IsOutputPin(string pinName)
        {
            return IsMoveOutputPin(pinName) ||
                MatchesAny(pinName, "eno", "q", "Q", "ET", "CV", "QU", "QD", "RET_VAL", "STATUS", "ERROR", "BUSY");
        }

        private static bool IsMoveOutputPin(string pinName)
        {
            return pinName.StartsWith("out", StringComparison.OrdinalIgnoreCase);
        }

        private IReadOnlyList<string> GetInputBindings(string partUid, List<string> notes)
        {
            return GetInputPinNames(partUid)
                .Where(pinName => !MatchesAny(pinName, "en", "pre"))
                .Select(pinName => new
                {
                    PinName = pinName,
                    Value = ResolveInputValue(partUid, pinName, notes)
                })
                .Where(binding => !string.IsNullOrWhiteSpace(binding.Value) && !string.Equals(binding.Value, "TRUE", StringComparison.OrdinalIgnoreCase))
                .Select(binding => $"{binding.PinName} := {binding.Value}")
                .ToArray();
        }

        private IReadOnlyList<string> GetInputValues(string partUid, List<string> notes)
        {
            return GetInputPinNames(partUid)
                .Where(pinName => !MatchesAny(pinName, "en", "pre"))
                .Select(pinName => ResolveInputValue(partUid, pinName, notes))
                .Where(value => !string.IsNullOrWhiteSpace(value) && !string.Equals(value, "TRUE", StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        private IReadOnlyList<string> GetInputPinNames(string partUid)
        {
            return _inputSources.Keys
                .Concat(_pinAccesses.Keys)
                .Concat(_powerInputs.Keys)
                .Where(pin => string.Equals(pin.PartUid, partUid, StringComparison.OrdinalIgnoreCase) && IsInputPin(pin.PinName))
                .Select(pin => pin.PinName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(GetPinSortKey)
                .ThenBy(pinName => pinName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private string ResolveInputValue(string partUid, string pinName, List<string> notes)
        {
            var value = EvaluateInput(partUid, pinName, notes);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            return GetPinAccess(partUid, pinName);
        }

        private string BuildArithmeticExpression(PartNode part, List<string> notes)
        {
            var values = GetInputValues(part.Uid, notes);
            if (values.Count == 0)
            {
                notes.Add($"Skipped {part.Name} output because no input pins could be resolved.");
                return string.Empty;
            }

            if (string.Equals(part.Name, "Neg", StringComparison.OrdinalIgnoreCase))
            {
                return $"(-{values[0]})";
            }

            if (values.Count < 2)
            {
                notes.Add($"Skipped {part.Name} output because fewer than two input pins could be resolved.");
                return string.Empty;
            }

            if (string.Equals(part.Name, "Add", StringComparison.OrdinalIgnoreCase))
            {
                return $"({string.Join(" + ", values)})";
            }

            if (string.Equals(part.Name, "Sub", StringComparison.OrdinalIgnoreCase))
            {
                return $"({string.Join(" - ", values)})";
            }

            if (string.Equals(part.Name, "Mul", StringComparison.OrdinalIgnoreCase))
            {
                return $"({string.Join(" * ", values)})";
            }

            if (string.Equals(part.Name, "Div", StringComparison.OrdinalIgnoreCase))
            {
                return $"({string.Join(" / ", values)})";
            }

            if (string.Equals(part.Name, "Mod", StringComparison.OrdinalIgnoreCase))
            {
                return $"({values[0]} MOD {values[1]})";
            }

            return $"EXPT({values[0]}, {values[1]})";
        }

        private static bool IsArithmeticPart(string partName)
        {
            return MatchesAny(partName, "Add", "Sub", "Mul", "Div", "Mod", "Neg", "Expt");
        }

        private static bool IsFunctionExpressionPart(string partName)
        {
            return MatchesAny(
                partName,
                "Abs",
                "Convert",
                "FillBlockI",
                "LEN",
                "LIMIT",
                "MID",
                "MoveBlockI",
                "Normalize",
                "OutRange",
                "RT_INFO",
                "Runtime",
                "SCALE_D",
                "Scale_X",
                "S_CONV",
                "T_ADD",
                "T_CONV",
                "T_SUB",
                "Trunc",
                "VAL_STRG",
                "RD_LOC_T",
                "RD_SYS_T",
                "WR_SYS_T");
        }

        private static bool IsControlFlowPart(string partName)
        {
            return MatchesAny(partName, "Jump", "Return", "ReturnTrue", "ReturnValue");
        }

        private static bool IsProcedureFunctionPart(string partName)
        {
            return MatchesAny(partName, "RD_LOC_T", "RD_SYS_T");
        }

        private static bool IsInstanceCallPart(string partName)
        {
            return MatchesAny(
                partName,
                "TON",
                "TOF",
                "TONR",
                "TP",
                "R_TRIG",
                "CTU",
                "SET_TIMEZONE",
                "Program_Alarm",
                "ESTOP1",
                "FDBACK",
                "ACK_GL",
                "RDREC",
                "SinaPos");
        }

        private static string NormalizeOutputPinName(string pinName)
        {
            return pinName.ToUpperInvariant();
        }

        private static int GetPinSortKey(string pinName)
        {
            if (pinName.Length > 2 &&
                pinName.StartsWith("in", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(pinName.Substring(2), out var index))
            {
                return index;
            }

            if (pinName.StartsWith("SD_", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(pinName.Substring(3), out var alarmDataIndex))
            {
                return 60 + alarmDataIndex;
            }

            if (MatchesAny(pinName, "mn", "min", "MODE", "L"))
            {
                return 10;
            }

            if (MatchesAny(pinName, "in", "IN", "VALUE", "MEM", "OB", "M"))
            {
                return 20;
            }

            if (MatchesAny(pinName, "mx", "max", "PT", "PV", "INFO", "H"))
            {
                return 30;
            }

            if (MatchesAny(pinName, "CLK", "CU", "CD", "REQ"))
            {
                return 40;
            }

            if (MatchesAny(pinName, "R", "RESET", "LD", "MODE", "LOCALE"))
            {
                return 50;
            }

            if (MatchesAny(pinName, "SIG"))
            {
                return 55;
            }

            if (MatchesAny(pinName, "TIMESTAMP"))
            {
                return 56;
            }

            return int.MaxValue;
        }

        private static string And(string upstream, string condition)
        {
            if (string.IsNullOrWhiteSpace(condition))
            {
                return string.Empty;
            }

            if (string.IsNullOrWhiteSpace(upstream) || string.Equals(upstream, "TRUE", StringComparison.OrdinalIgnoreCase))
            {
                return condition;
            }

            return $"({upstream} AND {condition})";
        }

        private static IReadOnlyList<string> GetCalcInputNames(string equation)
        {
            var names = new List<string>();
            for (var index = 0; index < equation.Length; index++)
            {
                if (index > 0 && (char.IsLetterOrDigit(equation[index - 1]) || equation[index - 1] == '_'))
                {
                    continue;
                }

                if (index + 2 >= equation.Length ||
                    !string.Equals(equation.Substring(index, 2), "IN", StringComparison.OrdinalIgnoreCase) ||
                    !char.IsDigit(equation[index + 2]))
                {
                    continue;
                }

                var end = index + 3;
                while (end < equation.Length && char.IsDigit(equation[end]))
                {
                    end++;
                }

                if (end < equation.Length && (char.IsLetterOrDigit(equation[end]) || equation[end] == '_'))
                {
                    continue;
                }

                names.Add(equation.Substring(index, end - index));
                index = end - 1;
            }

            return names.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(GetPinSortKey).ToArray();
        }

        private static string ReplaceIdentifier(string expression, string identifier, string replacement)
        {
            var builder = new StringBuilder(expression.Length + replacement.Length);
            for (var index = 0; index < expression.Length;)
            {
                if (index + identifier.Length <= expression.Length &&
                    string.Equals(expression.Substring(index, identifier.Length), identifier, StringComparison.OrdinalIgnoreCase))
                {
                    var startsClean = index == 0 || (!char.IsLetterOrDigit(expression[index - 1]) && expression[index - 1] != '_');
                    var endsClean = index + identifier.Length == expression.Length ||
                        (!char.IsLetterOrDigit(expression[index + identifier.Length]) && expression[index + identifier.Length] != '_');
                    if (startsClean && endsClean)
                    {
                        builder.Append(replacement);
                        index += identifier.Length;
                        continue;
                    }
                }

                builder.Append(expression[index]);
                index++;
            }

            return builder.ToString();
        }

        private static bool IsCompare(string partName)
        {
            return MatchesAny(partName, "Eq", "Ne", "Lt", "Le", "Gt", "Ge");
        }

        private static string GetCompareOperator(string partName)
        {
            if (string.Equals(partName, "Eq", StringComparison.OrdinalIgnoreCase))
            {
                return "=";
            }

            if (string.Equals(partName, "Ne", StringComparison.OrdinalIgnoreCase))
            {
                return "<>";
            }

            if (string.Equals(partName, "Lt", StringComparison.OrdinalIgnoreCase))
            {
                return "<";
            }

            if (string.Equals(partName, "Le", StringComparison.OrdinalIgnoreCase))
            {
                return "<=";
            }

            if (string.Equals(partName, "Gt", StringComparison.OrdinalIgnoreCase))
            {
                return ">";
            }

            return ">=";
        }

        private static CallNode CreateCallNode(XElement call, int order)
        {
            var callInfo = call.Elements().FirstOrDefault(element => element.Name.LocalName == "CallInfo");
            var name = ((string?)callInfo?.Attribute("Name"))?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name) && callInfo != null)
            {
                name = GetInstanceDb(callInfo);
            }

            var parameters = callInfo?
                .Elements()
                .Where(element => element.Name.LocalName == "Parameter")
                .Select(parameter => new CallParameter(
                    ((string?)parameter.Attribute("Name"))?.Trim() ?? string.Empty,
                    ((string?)parameter.Attribute("Section"))?.Trim() ?? string.Empty))
                .Where(parameter => !string.IsNullOrWhiteSpace(parameter.Name))
                .ToArray() ?? Array.Empty<CallParameter>();

            return new CallNode(GetUid(call), name, order, parameters);
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
                .Select(FormatComponentName)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToArray();

            return components.Length == 0 ? string.Empty : string.Join(".", components);
        }

        private static string GetInstanceName(XElement part)
        {
            var instance = part.Elements().FirstOrDefault(element => element.Name.LocalName == "Instance");
            if (instance == null)
            {
                return string.Empty;
            }

            var components = instance
                .Descendants()
                .Where(element => element.Name.LocalName == "Component")
                .Select(FormatComponentName)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToArray();

            return components.Length == 0 ? string.Empty : string.Join(".", components);
        }

        private static string GetAccessValue(XElement access)
        {
            var symbol = access
                .Descendants()
                .Where(element => element.Name.LocalName == "Symbol")
                .Take(1)
                .FirstOrDefault();
            if (symbol != null)
            {
                return FormatSymbol(symbol);
            }

            var constantValue = access
                .Descendants()
                .FirstOrDefault(element => element.Name.LocalName == "ConstantValue")
                ?.Value
                .Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(constantValue))
            {
                return constantValue;
            }

            return access
                .Descendants()
                .FirstOrDefault(element => element.Name.LocalName == "Constant")
                ?.Attribute("Name")
                ?.Value
                .Trim()
                ?? access
                    .Descendants()
                    .FirstOrDefault(element => element.Name.LocalName == "Label")
                    ?.Attribute("Name")
                    ?.Value
                    .Trim()
                ?? string.Empty;
        }

        private static string FormatSymbol(XElement symbol)
        {
            var builder = new StringBuilder();
            var needsSeparator = false;
            foreach (var child in symbol.Elements())
            {
                if (child.Name.LocalName == "Component")
                {
                    if (needsSeparator)
                    {
                        builder.Append('.');
                    }

                    builder.Append(FormatSymbolComponent(child));
                    needsSeparator = true;
                    continue;
                }

                if (child.Name.LocalName == "Token")
                {
                    builder.Append(((string?)child.Attribute("Text")) ?? child.Value);
                    needsSeparator = false;
                }
            }

            return builder.ToString();
        }

        private static string FormatSymbolComponent(XElement component)
        {
            var value = FormatComponentName(component);
            if (string.Equals((string?)component.Attribute("AccessModifier"), "Array", StringComparison.OrdinalIgnoreCase))
            {
                var indexes = component
                    .Elements()
                    .Where(element => element.Name.LocalName == "Access")
                    .Select(GetAccessValue)
                    .Where(index => !string.IsNullOrWhiteSpace(index))
                    .ToArray();
                if (indexes.Length > 0)
                {
                    value += $"[{string.Join(", ", indexes)}]";
                }
            }

            return value;
        }

        private static string GetUid(XElement element)
        {
            return ((string?)element.Attribute("UId"))?.Trim() ?? string.Empty;
        }
    }

    private sealed class LogicBlock
    {
        public LogicBlock(string name, string kind, string sourceFile, string programmingLanguage, IReadOnlyList<LogicNetwork> networks)
        {
            Name = name;
            Kind = kind;
            SourceFile = sourceFile;
            ProgrammingLanguage = programmingLanguage;
            Networks = networks;
        }

        public string Name { get; }

        public string Kind { get; }

        public string SourceFile { get; }

        public string ProgrammingLanguage { get; }

        public IReadOnlyList<LogicNetwork> Networks { get; }
    }

    private sealed class LogicNetwork
    {
        public LogicNetwork(
            int networkIndex,
            string compileUnitId,
            string title,
            string language,
            string translationLanguage,
            string confidence,
            IReadOnlyList<string> statements,
            IReadOnlyList<string> notes)
        {
            NetworkIndex = networkIndex;
            CompileUnitId = compileUnitId;
            Title = title;
            Language = language;
            TranslationLanguage = translationLanguage;
            Confidence = confidence;
            Statements = statements;
            Notes = notes;
        }

        public int NetworkIndex { get; }

        public string CompileUnitId { get; }

        public string Title { get; }

        public string Language { get; }

        public string TranslationLanguage { get; }

        public string Confidence { get; }

        public IReadOnlyList<string> Statements { get; }

        public IReadOnlyList<string> Notes { get; }

        public static LogicNetwork Untranslated(int networkIndex, string compileUnitId, string title, string language, string note)
        {
            return new LogicNetwork(
                networkIndex,
                compileUnitId,
                title,
                language,
                "scl-like",
                "untranslated",
                Array.Empty<string>(),
                new[] { note });
        }
    }

    private sealed class PartNode
    {
        private readonly HashSet<string> _negatedPins;

        public PartNode(string uid, string name, int order, string instanceName, string equation, IReadOnlyList<string> negatedPins)
        {
            Uid = uid;
            Name = name;
            Order = order;
            InstanceName = instanceName;
            Equation = equation;
            _negatedPins = new HashSet<string>(negatedPins, StringComparer.OrdinalIgnoreCase);
        }

        public string Uid { get; }

        public string Name { get; }

        public int Order { get; }

        public string InstanceName { get; }

        public string Equation { get; }

        public bool IsNegated(string pinName)
        {
            return _negatedPins.Contains(pinName);
        }
    }

    private sealed class DirectAssignment
    {
        public DirectAssignment(PartPin source, string target, int order)
        {
            Source = source;
            Target = target;
            Order = order;
        }

        public PartPin Source { get; }

        public string Target { get; }

        public int Order { get; }
    }

    private sealed class AccessNode
    {
        public AccessNode(string uid, string value, string scope)
        {
            Uid = uid;
            Value = value;
            Scope = scope;
        }

        public string Uid { get; }

        public string Value { get; }

        public string Scope { get; }
    }

    private sealed class CallNode
    {
        public CallNode(string uid, string name, int order, IReadOnlyList<CallParameter> parameters)
        {
            Uid = uid;
            Name = name;
            Order = order;
            Parameters = parameters;
        }

        public string Uid { get; }

        public string Name { get; }

        public int Order { get; }

        public IReadOnlyList<CallParameter> Parameters { get; }
    }

    private sealed class CallParameter
    {
        public CallParameter(string name, string section)
        {
            Name = name;
            Section = section;
        }

        public string Name { get; }

        public string Section { get; }
    }

    private readonly struct PartPin : IEquatable<PartPin>
    {
        public PartPin(string partUid, string pinName)
        {
            PartUid = partUid;
            PinName = pinName;
        }

        public string PartUid { get; }

        public string PinName { get; }

        public bool Equals(PartPin other)
        {
            return string.Equals(PartUid, other.PartUid, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(PinName, other.PinName, StringComparison.OrdinalIgnoreCase);
        }

        public override bool Equals(object? obj)
        {
            return obj is PartPin other && Equals(other);
        }

        public override int GetHashCode()
        {
            return StringComparer.OrdinalIgnoreCase.GetHashCode(PartUid ?? string.Empty) ^
                StringComparer.OrdinalIgnoreCase.GetHashCode(PinName ?? string.Empty);
        }
    }
}
