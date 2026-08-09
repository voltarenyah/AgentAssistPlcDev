# Workbench App Assistant

The service reads its optional model configuration from the environment. No API
key is placed in LangGraph state or checkpoint storage.

```text
DEEPSEEK_API_KEY=<provided by the host/UI later>
DEEPSEEK_BASE_URL=https://api.deepseek.com
DEEPSEEK_MODEL=deepseek-v4-flash
APP_ASSISTANT_PROMPT_APPEND=<optional project-specific guidance>
APP_ASSISTANT_APIHOST_URL=http://127.0.0.1:5239
```

For compatibility with the existing AgentLoop configuration, the key may also
be supplied as `DeepSeek__ApiKey`, `DeepSeek:ApiKey`, or `deepSeekApiKey`.
The canonical `DEEPSEEK_API_KEY` is used when those legacy names are absent.

Without `DEEPSEEK_API_KEY`, the service uses the deterministic fallback answers,
which keeps local development and approval-flow tests available without a model
credential. The model is used only for read-only orientation and recommendations;
worktree mutations still require the LangGraph interrupt/approval path.
