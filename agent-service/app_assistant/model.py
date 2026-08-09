import os
from typing import Any, Mapping

from langchain_openai import ChatOpenAI


DEFAULT_BASE_URL = "https://api.deepseek.com"
DEFAULT_MODEL = "deepseek-v4-flash"


def _first_value(values: Mapping[str, str], *names: str, default: str = "") -> str:
    for name in names:
        value = values.get(name, "").strip()
        if value:
            return value
    return default


def build_model_from_env(
    environ: Mapping[str, str] | None = None,
) -> tuple[Any | None, dict[str, str]]:
    """Build the optional DeepSeek OpenAI-compatible model without persisting secrets."""

    values = environ if environ is not None else os.environ
    api_key = _first_value(
        values,
        "DeepSeek__ApiKey",
        "DeepSeek:ApiKey",
        "deepSeekApiKey",
        "DEEPSEEK_API_KEY",
    )
    base_url = _first_value(
        values,
        "DEEPSEEK_BASE_URL",
        "DeepSeek__BaseUrl",
        "DeepSeek:BaseUrl",
        default=DEFAULT_BASE_URL,
    )
    model_name = _first_value(
        values,
        "DEEPSEEK_MODEL",
        "deepSeekModel",
        "chatSettings:model",
        default=DEFAULT_MODEL,
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
