import os
from typing import Any, Mapping

from langchain_openai import ChatOpenAI


DEFAULT_BASE_URL = "https://api.deepseek.com"
DEFAULT_MODEL = "deepseek-v4-flash"


def build_model_from_env(
    environ: Mapping[str, str] | None = None,
) -> tuple[Any | None, dict[str, str]]:
    """Build the optional DeepSeek OpenAI-compatible model without persisting secrets."""

    values = environ if environ is not None else os.environ
    api_key = values.get("DEEPSEEK_API_KEY", "").strip()
    base_url = values.get("DEEPSEEK_BASE_URL", DEFAULT_BASE_URL).strip() or DEFAULT_BASE_URL
    model_name = values.get("DEEPSEEK_MODEL", DEFAULT_MODEL).strip() or DEFAULT_MODEL
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
