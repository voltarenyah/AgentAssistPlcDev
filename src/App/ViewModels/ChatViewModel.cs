using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Agent.Chat;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace App.ViewModels;

/// <summary>
/// Chat panel view model (buildnote/plan/agent.md): first-run DeepSeek API key setup, adjustable
/// chat parameters (model/thinking/effort/temperature/top_p), and a streaming tool-calling
/// conversation via <see cref="AgentLoop"/>. Tool calls stream in as gray entries; chain-of-thought
/// streams into "thinking" entries.
/// </summary>
public partial class ChatViewModel : ObservableObject
{
    /// <summary>One chat display line. Kind: user | assistant | thinking | tool | error | info.</summary>
    public sealed record ChatEntry(string Kind, string Text)
    {
        public static ChatEntry User(string text) => new("user", text);
        public static ChatEntry Assistant(string text) => new("assistant", text);
        public static ChatEntry Thinking(string text) => new("thinking", text);
        public static ChatEntry Tool(string text) => new("tool", text);
        public static ChatEntry Error(string text) => new("error", text);
        public static ChatEntry Info(string text) => new("info", text);
    }

    private readonly Func<string> contextProvider;
    private AppSettings? settings;
    private McpToolCatalog? catalog;
    private DeepSeekClient? client;
    private AgentLoop? loop;
    private CancellationTokenSource? runCancellation;

    // Streaming state: live entries updated in place as deltas arrive, finalized per model round.
    private readonly StringBuilder streamReasoning = new();
    private readonly StringBuilder streamContent = new();
    private int reasoningEntryIndex = -1;
    private int contentEntryIndex = -1;

    public ChatViewModel(Func<string> contextProvider)
    {
        this.contextProvider = contextProvider;
    }

    [ObservableProperty]
    private bool isKeyConfigured;

    [ObservableProperty]
    private bool showKeySetup = true;

    [ObservableProperty]
    private string keyStatus = "No API key configured";

    [ObservableProperty]
    private string inputText = string.Empty;

    [ObservableProperty]
    private bool isBusy;

    // --- Adjustable chat parameters (persisted to config.json) ---

    [ObservableProperty]
    private string selectedModel = ChatRequestSettings.DefaultModel;

    [ObservableProperty]
    private bool thinkingEnabled = true;

    [ObservableProperty]
    private bool notThinking;

    [ObservableProperty]
    private string selectedEffort = ChatRequestSettings.DefaultReasoningEffort;

    [ObservableProperty]
    private string temperatureText = "1.0";

    [ObservableProperty]
    private string topPText = "1.0";

    public IReadOnlyList<string> AvailableModels { get; } = new[] { "deepseek-v4-flash", "deepseek-v4-pro" };

    public IReadOnlyList<string> AvailableEfforts { get; } = new[] { "high", "max" };

    public ObservableCollection<ChatEntry> Messages { get; } = new();

    partial void OnThinkingEnabledChanged(bool value)
    {
        NotThinking = !value;
    }

    /// <summary>Called once the settings are loaded and the tool catalog is built (servers up).</summary>
    public void Configure(AppSettings settings, McpToolCatalog catalog)
    {
        this.settings = settings;
        this.catalog = catalog;
        SelectedModel = AvailableModels.Contains(settings.DeepSeekModel) ? settings.DeepSeekModel : ChatRequestSettings.DefaultModel;
        ThinkingEnabled = settings.DeepSeekThinkingEnabled;
        NotThinking = !ThinkingEnabled;
        SelectedEffort = AvailableEfforts.Contains(settings.DeepSeekReasoningEffort) ? settings.DeepSeekReasoningEffort : ChatRequestSettings.DefaultReasoningEffort;
        TemperatureText = settings.DeepSeekTemperature.ToString("0.##", CultureInfo.InvariantCulture);
        TopPText = settings.DeepSeekTopP.ToString("0.##", CultureInfo.InvariantCulture);

        IsKeyConfigured = settings.HasDeepSeekApiKey;
        if (IsKeyConfigured)
        {
            CreateLoop(settings.DeepSeekApiKey!);
        }

        ShowKeySetup = !IsKeyConfigured;
        KeyStatus = IsKeyConfigured ? "API key configured" : "No API key — enter it to enable the chat";
        AddEntry(ChatEntry.Info(IsKeyConfigured
            ? "DeepSeek chat ready. Ask about the connected PLC project — answers are grounded in the knowledge base."
            : "DeepSeek chat: enter your API key once (stored in %APPDATA%\\PlcAiAssistant\\config.json). Get a key at platform.deepseek.com."));
    }

    [RelayCommand]
    private void SaveApiKey(PasswordBox? passwordBox)
    {
        var key = passwordBox?.Password?.Trim() ?? string.Empty;
        if (settings == null)
        {
            AddEntry(ChatEntry.Error("Settings not loaded yet — wait for server startup to finish."));
            return;
        }

        if (key.Length == 0)
        {
            AddEntry(ChatEntry.Error("API key must not be empty."));
            return;
        }

        try
        {
            AppSettings.SaveDeepSeekApiKey(key);
        }
        catch (Exception ex)
        {
            AddEntry(ChatEntry.Error($"Could not save config.json: {ex.Message}"));
            return;
        }

        passwordBox!.Clear();
        CreateLoop(key);
        IsKeyConfigured = true;
        ShowKeySetup = false;
        KeyStatus = "API key configured";
        AddEntry(ChatEntry.Info("API key saved. DeepSeek chat ready."));
    }

    [RelayCommand]
    private void ChangeKey()
    {
        ShowKeySetup = true;
        KeyStatus = "Enter the new key (overwrites the stored one)";
    }

    [RelayCommand]
    private void KeepKey()
    {
        ShowKeySetup = !IsKeyConfigured;
        KeyStatus = IsKeyConfigured ? "API key configured" : "No API key — enter it to enable the chat";
    }

    [RelayCommand]
    private void ApplySettings()
    {
        if (settings == null)
        {
            return;
        }

        if (!double.TryParse(TemperatureText, NumberStyles.Float, CultureInfo.InvariantCulture, out var temperature)
            || temperature < 0 || temperature > 2)
        {
            AddEntry(ChatEntry.Error("Temperature must be a number between 0 and 2 (e.g. 1.0)."));
            return;
        }

        if (!double.TryParse(TopPText, NumberStyles.Float, CultureInfo.InvariantCulture, out var topP)
            || topP < 0 || topP > 1)
        {
            AddEntry(ChatEntry.Error("top_p must be a number between 0 and 1 (e.g. 1.0)."));
            return;
        }

        try
        {
            AppSettings.SaveDeepSeekChatSettings(SelectedModel, ThinkingEnabled, SelectedEffort, temperature, topP);
        }
        catch (Exception ex)
        {
            AddEntry(ChatEntry.Error($"Could not save config.json: {ex.Message}"));
            return;
        }

        if (loop != null)
        {
            loop.Settings = new ChatRequestSettings
            {
                Model = SelectedModel,
                ThinkingEnabled = ThinkingEnabled,
                ReasoningEffort = SelectedEffort,
                Temperature = temperature,
                TopP = topP,
            };
        }

        AddEntry(ChatEntry.Info(
            $"Chat settings applied: model={SelectedModel}, thinking={(ThinkingEnabled ? SelectedEffort : "disabled")}" +
            (ThinkingEnabled ? " (temperature/top_p ignored by the API in thinking mode)" : $", temperature={temperature:0.##}, top_p={topP:0.##}")));
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        var text = InputText.Trim();
        if (IsBusy || text.Length == 0)
        {
            return;
        }

        if (loop == null)
        {
            AddEntry(ChatEntry.Error(IsKeyConfigured
                ? "Chat not ready — MCP servers did not start; see the log pane."
                : "Configure the DeepSeek API key first."));
            return;
        }

        InputText = string.Empty;
        AddEntry(ChatEntry.User(text));
        ResetStreamState();
        IsBusy = true;
        runCancellation = new CancellationTokenSource();
        try
        {
            var answer = await loop.RunAsync(text, runCancellation.Token);
            // The final round streamed its content into the live content entry already; if no
            // content arrived at all (edge case), fall back to the returned answer.
            if (contentEntryIndex >= 0)
            {
                UpdateEntry(contentEntryIndex, ChatEntry.Assistant(streamContent.ToString()));
            }
            else if (!string.IsNullOrWhiteSpace(answer))
            {
                AddEntry(ChatEntry.Assistant(answer));
            }
        }
        catch (OperationCanceledException)
        {
            AddEntry(ChatEntry.Info("(cancelled)"));
        }
        catch (DeepSeekAuthException ex)
        {
            AddEntry(ChatEntry.Error(ex.Message));
            IsKeyConfigured = false;
            ShowKeySetup = true;
            KeyStatus = "DeepSeek rejected the key — enter a valid one";
        }
        catch (DeepSeekException ex)
        {
            AddEntry(ChatEntry.Error(ex.Message));
        }
        catch (Exception ex)
        {
            AddEntry(ChatEntry.Error($"ERROR: {ex.Message}"));
        }
        finally
        {
            ResetStreamState();
            IsBusy = false;
            runCancellation.Dispose();
            runCancellation = null;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        runCancellation?.Cancel();
    }

    [RelayCommand]
    private void ExportChat()
    {
        if (loop == null || client == null || catalog == null)
        {
            AddEntry(ChatEntry.Info("Nothing to export — the chat is not ready yet."));
            return;
        }

        if (!loop.History.Any(message => message.Role == "user"))
        {
            AddEntry(ChatEntry.Info("Nothing to export — send at least one message first."));
            return;
        }

        try
        {
            var markdown = ChatSessionExporter.Export(
                loop.History,
                loop.RoundUsages,
                catalog.ToOpenAiToolsJson(),
                catalog.Tools.Count,
                loop.Settings.Model,
                client.RequestUri);
            var path = ChatSessionExporter.ResolveExportPath();
            File.WriteAllText(path, markdown);
            AddEntry(ChatEntry.Info($"Session exported: {path}"));
        }
        catch (Exception ex)
        {
            AddEntry(ChatEntry.Error($"Export failed: {ex.Message}"));
        }
    }

    [RelayCommand]
    private void Clear()
    {
        Dispatch(() =>
        {
            Messages.Clear();
            loop?.ClearHistory();
        });
    }

    private void CreateLoop(string apiKey)
    {
        client = new DeepSeekClient(apiKey, settings!.DeepSeekBaseUrl);
        loop = new AgentLoop(client, catalog!, contextProvider, new ChatRequestSettings
        {
            Model = SelectedModel,
            ThinkingEnabled = ThinkingEnabled,
            ReasoningEffort = SelectedEffort,
            Temperature = settings.DeepSeekTemperature,
            TopP = settings.DeepSeekTopP,
        });
        loop.Progress += line =>
        {
            // A progress line ends a model round: subsequent deltas form a new bubble.
            Dispatch(ResetStreamState);
            AddEntry(ChatEntry.Tool(line));
        };
        loop.StreamDelta += (kind, delta) => Dispatch(() => AppendDelta(kind, delta));
    }

    private void AppendDelta(string kind, string delta)
    {
        if (kind == "reasoning")
        {
            streamReasoning.Append(delta);
            if (reasoningEntryIndex < 0)
            {
                reasoningEntryIndex = Messages.Count;
                AddEntry(ChatEntry.Thinking(streamReasoning.ToString()));
            }
            else
            {
                UpdateEntry(reasoningEntryIndex, ChatEntry.Thinking(streamReasoning.ToString()));
            }
        }
        else
        {
            streamContent.Append(delta);
            if (contentEntryIndex < 0)
            {
                contentEntryIndex = Messages.Count;
                AddEntry(ChatEntry.Assistant(streamContent.ToString()));
            }
            else
            {
                UpdateEntry(contentEntryIndex, ChatEntry.Assistant(streamContent.ToString()));
            }
        }
    }

    private void ResetStreamState()
    {
        streamReasoning.Clear();
        streamContent.Clear();
        reasoningEntryIndex = -1;
        contentEntryIndex = -1;
    }

    /// <summary>
    /// Every chat-list mutation funnels through here: a background-thread add would desync WPF's
    /// ItemsControl ("ItemsControl is inconsistent with its items source" — the 2026-07-19 crash).
    /// </summary>
    private void AddEntry(ChatEntry entry) => Dispatch(() => Messages.Add(entry));

    private void UpdateEntry(int index, ChatEntry entry) => Dispatch(() => Messages[index] = entry);

    private static void Dispatch(System.Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(action);
            return;
        }

        action();
    }
}
