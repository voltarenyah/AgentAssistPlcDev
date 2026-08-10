# Workbench App Assistant

The service loads its model configuration once when it starts. Environment
variables take precedence; otherwise it reads the shared Windows configuration
files in this order:

1. `%APPDATA%/AutomationWorkbench/config.json`
2. `%APPDATA%/PlcAiAssistant/config.json`

Keep the API key in the existing `PlcAiAssistant` configuration as the single
source of truth. The desktop host and LangGraph sidecar both resolve that same
key; it is never copied into LangGraph state, checkpoints, logs, or metadata.

```text
DEEPSEEK_API_KEY=<optional override>
DEEPSEEK_BASE_URL=https://api.deepseek.com
DEEPSEEK_MODEL=deepseek-v4-flash
APP_ASSISTANT_PROMPT_APPEND=<optional project-specific guidance>
APP_ASSISTANT_APIHOST_URL=http://127.0.0.1:5239
```

For compatibility with the existing AgentLoop configuration, the key may also
be supplied as `DeepSeek__ApiKey`, `DeepSeek:ApiKey`, or `deepSeekApiKey`.
The canonical `DEEPSEEK_API_KEY` is used when those legacy names are absent.

Without a key in either source, the service uses deterministic fallback answers.
`GET /health` reports `modelConfigured` and `modelMode` without exposing the key.
The model is used only for read-only orientation and recommendations; worktree
mutations still require the LangGraph interrupt/approval path.
