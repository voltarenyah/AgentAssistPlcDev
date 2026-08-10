import json
import os
from pathlib import Path
from typing import Any, Mapping

from langchain_openai import ChatOpenAI


DEFAULT_BASE_URL = "https://api.deepseek.com"
DEFAULT_MODEL = "deepseek-v4-flash"

_CONFIG_NAMES = (
    "AutomationWorkbench/config.json",
    "PlcAiAssistant/config.json",
)


def _first_value(values: Mapping[str, str], *names: str, default: str = "") -> str:
    for name in names:
        value = values.get(name, "").strip()
        if value:
            return value
    return default


def _config_paths(values: Mapping[str, str]) -> tuple[Path, ...]:
    """Return the two shared Windows app configuration locations."""

    appdata = values.get("APPDATA", "").strip()
    if not appdata:
        return ()
    return tuple(Path(appdata) / name for name in _CONFIG_NAMES)


def _config_value(config: Mapping[str, Any], *names: str) -> str:
    lowered = {str(key).lower(): value for key, value in config.items()}
    for name in names:
        value = lowered.get(name.lower())
        if isinstance(value, str) and value.strip():
            return value.strip()

    deepseek = lowered.get("deepseek")
    if isinstance(deepseek, Mapping):
        nested = {str(key).lower(): value for key, value in deepseek.items()}
        for name in names:
            short_name = name.rsplit(":", 1)[-1].lower()
            value = nested.get(short_name)
            if isinstance(value, str) and value.strip():
                return value.strip()
    return ""


def _load_shared_config(values: Mapping[str, str]) -> dict[str, str]:
    """Read shared settings without logging or persisting the credential."""

    result: dict[str, str] = {}
    for path in _config_paths(values):
        try:
            with path.open(encoding="utf-8") as stream:
                config = json.load(stream)
        except (OSError, json.JSONDecodeError):
            continue
        if not isinstance(config, Mapping):
            continue

        for setting, names in {
            "api_key": ("deepSeekApiKey", "DeepSeek:ApiKey"),
            "base_url": ("deepSeekBaseUrl", "DeepSeek:BaseUrl"),
            "model": ("deepSeekModel", "DeepSeek:Model"),
        }.items():
            if setting not in result:
                value = _config_value(config, *names)
                if value:
                    result[setting] = value
    return result


def build_model_from_env(
    environ: Mapping[str, str] | None = None,
) -> tuple[Any | None, dict[str, str]]:
    """Build the optional DeepSeek OpenAI-compatible model without persisting secrets."""

    values = environ if environ is not None else os.environ
    shared_config = _load_shared_config(values)
    api_key = _first_value(
        values,
        "DeepSeek__ApiKey",
        "DeepSeek:ApiKey",
        "deepSeekApiKey",
        "DEEPSEEK_API_KEY",
        default=shared_config.get("api_key", ""),
    )
    base_url = _first_value(
        values,
        "DEEPSEEK_BASE_URL",
        "DeepSeek__BaseUrl",
        "DeepSeek:BaseUrl",
        default=shared_config.get("base_url", DEFAULT_BASE_URL),
    )
    model_name = _first_value(
        values,
        "DEEPSEEK_MODEL",
        "deepSeekModel",
        "chatSettings:model",
        default=shared_config.get("model", DEFAULT_MODEL),
    )
    metadata = {
        "provider": "deepseek",
        "model": model_name,
        "mode": "llm" if api_key else "deterministic-fallback",
    }
    if not api_key:
        return None, metadata
    return (
        ChatOpenAI(
            api_key=api_key,
            base_url=base_url,
            model=model_name,
            temperature=0,
            max_retries=2,
        ),
        metadata,
    )
