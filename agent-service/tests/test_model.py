from langchain_openai import ChatOpenAI
import pytest

from app_assistant.model import build_model_from_env


def test_deepseek_model_uses_openai_compatible_environment_configuration():
    model, metadata = build_model_from_env(
        {
            "DEEPSEEK_API_KEY": "provided-later",
            "DEEPSEEK_BASE_URL": "https://api.example.test/v1",
            "DEEPSEEK_MODEL": "deepseek-v4-flash",
        }
    )

    assert isinstance(model, ChatOpenAI)
    assert model.model_name == "deepseek-v4-flash"
    assert model.openai_api_base == "https://api.example.test/v1"
    assert metadata == {
        "provider": "deepseek",
        "model": "deepseek-v4-flash",
        "mode": "llm",
    }


@pytest.mark.parametrize("key_name", [
    "DEEPSEEK_API_KEY",
    "DeepSeek__ApiKey",
    "DeepSeek:ApiKey",
    "deepSeekApiKey",
])
def test_model_accepts_existing_agent_key_environment_names(key_name: str):
    model, metadata = build_model_from_env({key_name: "shared-key"})

    assert isinstance(model, ChatOpenAI)
    assert model.openai_api_key.get_secret_value() == "shared-key"
    assert metadata["mode"] == "llm"
    assert "shared-key" not in str(metadata)


def test_missing_deepseek_key_selects_safe_deterministic_fallback():
    model, metadata = build_model_from_env({})

    assert model is None
    assert metadata == {
        "provider": "deepseek",
        "model": "deepseek-v4-flash",
        "mode": "deterministic-fallback",
    }
