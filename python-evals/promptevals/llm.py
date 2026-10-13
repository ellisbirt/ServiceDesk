"""Provider interface and normalized LLM responses with retries and JSON parsing."""

from __future__ import annotations

import asyncio
import json
import os
import random
import time
from dataclasses import dataclass, field
from typing import Any, Protocol

import anthropic

from .config import ModelSettings


class ServedModelMismatch(Exception):
    """The response came from a different model than the one requested."""


class RetriesExhausted(Exception):
    def __init__(self, attempts: int, last: Exception):
        super().__init__(f"gave up after {attempts} attempts: {last!r}")
        self.attempts = attempts
        self.last = last


class StructuredOutputError(Exception):
    """A structured-output call did not return parseable JSON."""


@dataclass
class LLMResponse:
    text: str
    model: str
    stop_reason: str | None
    usage: dict[str, int]
    latency_s: float
    attempts: int
    thinking: str = ""
    stop_details: dict[str, Any] | None = None
    request_id: str | None = None
    served_by_fallback: bool = False
    data: Any = None

    @property
    def refused(self) -> bool:
        return self.stop_reason == "refusal"

    @property
    def truncated(self) -> bool:
        return self.stop_reason == "max_tokens"


@dataclass
class UsageMeter:
    """Accumulates usage across several calls (e.g. all judge calls for one case)."""

    model: str | None = None
    usage: dict[str, int] = field(default_factory=dict)

    def add(self, r: LLMResponse) -> None:
        self.model = r.model
        for k, v in r.usage.items():
            self.usage[k] = self.usage.get(k, 0) + v


class Provider(Protocol):
    async def complete(
        self,
        settings: ModelSettings,
        *,
        messages: list[dict[str, Any]],
        system: str | None,
        output_schema: dict[str, Any] | None,
        stream: bool,
        allow_fallback: bool,
    ) -> LLMResponse: ...


def _usage_value(usage: Any, key: str) -> int:
    value = getattr(usage, key, 0) or 0
    return int(value)


def _model_matches(requested: str, served: str) -> bool:
    return served == requested or served.startswith(requested + "-") or requested.startswith(served)


def _json_schema_instruction(system: str | None, schema: dict[str, Any]) -> str:
    instruction = "Return only a JSON object matching this JSON Schema:\n" + json.dumps(schema)
    return f"{system}\n\n{instruction}" if system else instruction


class AnthropicProvider:
    def __init__(self, client: Any | None = None):
        self.client = client or anthropic.AsyncAnthropic(max_retries=0)

    async def complete(
        self,
        settings: ModelSettings,
        *,
        messages: list[dict[str, Any]],
        system: str | None,
        output_schema: dict[str, Any] | None,
        stream: bool,
        allow_fallback: bool,
    ) -> LLMResponse:
        kwargs: dict[str, Any] = {
            "model": settings.model,
            "max_tokens": settings.max_tokens,
            "messages": messages,
        }
        if system:
            kwargs["system"] = system
        output_config: dict[str, Any] = {}
        if settings.effort:
            output_config["effort"] = settings.effort
        if output_schema is not None:
            output_config["format"] = {"type": "json_schema", "schema": output_schema}
        if output_config:
            kwargs["output_config"] = output_config
        if settings.thinking_display:
            kwargs["thinking"] = {"type": "adaptive", "display": settings.thinking_display}

        use_fallbacks = bool(settings.fallbacks) and allow_fallback
        if use_fallbacks:
            kwargs["betas"] = ["server-side-fallback-2026-07-01"]
            kwargs["fallbacks"] = settings.fallbacks
        api = self.client.beta.messages if use_fallbacks else self.client.messages

        start = time.perf_counter()
        if stream:
            async with api.stream(**kwargs) as response_stream:
                msg = await response_stream.get_final_message()
        else:
            msg = await api.create(**kwargs)
        latency = time.perf_counter() - start

        served_by_fallback = any(
            getattr(item, "type", None) == "fallback_message"
            for item in (getattr(msg.usage, "iterations", None) or [])
        )
        if not served_by_fallback and not _model_matches(settings.model, msg.model):
            raise ServedModelMismatch(f"requested {settings.model}, served {msg.model}")

        text_parts, thinking_parts = [], []
        for block in msg.content:
            if block.type == "text":
                text_parts.append(block.text)
            elif block.type == "thinking" and getattr(block, "thinking", ""):
                thinking_parts.append(block.thinking)

        stop_details = getattr(msg, "stop_details", None)
        if stop_details is not None and hasattr(stop_details, "model_dump"):
            stop_details = stop_details.model_dump()
        usage = {
            key: _usage_value(msg.usage, key)
            for key in (
                "input_tokens",
                "output_tokens",
                "cache_read_input_tokens",
                "cache_creation_input_tokens",
            )
        }
        return LLMResponse(
            text="".join(text_parts),
            model=msg.model,
            stop_reason=msg.stop_reason,
            usage=usage,
            latency_s=round(latency, 3),
            attempts=1,
            thinking="\n".join(thinking_parts),
            stop_details=stop_details,
            request_id=getattr(msg, "_request_id", None),
            served_by_fallback=served_by_fallback,
        )


class OpenAICompatibleProvider:
    def __init__(self, client: Any, *, max_tokens_parameter: str = "max_tokens"):
        self.client = client
        self.max_tokens_parameter = max_tokens_parameter

    async def complete(
        self,
        settings: ModelSettings,
        *,
        messages: list[dict[str, Any]],
        system: str | None,
        output_schema: dict[str, Any] | None,
        stream: bool,
        allow_fallback: bool,
    ) -> LLMResponse:
        del stream, allow_fallback
        request_messages = ([{"role": "system", "content": system}] if system else []) + messages
        if output_schema is not None:
            request_messages[0:1] = [{
                "role": "system",
                "content": _json_schema_instruction(system, output_schema),
            }]
        kwargs: dict[str, Any] = {
            "model": settings.model,
            self.max_tokens_parameter: settings.max_tokens,
            "messages": request_messages,
        }
        if output_schema is not None:
            kwargs["response_format"] = {"type": "json_object"}

        start = time.perf_counter()
        response = await self.client.chat.completions.create(**kwargs)
        latency = time.perf_counter() - start
        choice = response.choices[0]
        message = choice.message
        reason = choice.finish_reason
        if getattr(message, "refusal", None):
            reason = "refusal"
        elif reason == "length":
            reason = "max_tokens"
        elif reason == "stop":
            reason = "end_turn"

        usage = getattr(response, "usage", None)
        prompt_details = getattr(usage, "prompt_tokens_details", None)
        completion_details = getattr(usage, "completion_tokens_details", None)
        normalized_usage = {
            "input_tokens": _usage_value(usage, "prompt_tokens"),
            "output_tokens": _usage_value(usage, "completion_tokens"),
            "cache_read_input_tokens": _usage_value(prompt_details, "cached_tokens"),
            "cache_creation_input_tokens": 0,
        }
        return LLMResponse(
            text=getattr(message, "content", None) or getattr(message, "refusal", "") or "",
            model=getattr(response, "model", None) or settings.model,
            stop_reason=reason,
            usage=normalized_usage,
            latency_s=round(latency, 3),
            attempts=1,
            request_id=getattr(response, "id", None),
            stop_details={
                "completion_tokens_details": completion_details.model_dump()
            } if completion_details and hasattr(completion_details, "model_dump") else None,
        )


class OpenAIProvider(OpenAICompatibleProvider):
    pass


class GrokProvider(OpenAICompatibleProvider):
    pass


class MicrosoftProvider(OpenAICompatibleProvider):
    pass


class OllamaProvider(OpenAICompatibleProvider):
    pass


class LMStudioProvider(OpenAICompatibleProvider):
    pass


class ProviderRouter:
    """Lazily creates and caches provider clients for each configured model role."""

    def __init__(self):
        self._providers: dict[tuple[str, str | None, str | None, str | None], Provider] = {}

    def _provider(self, settings: ModelSettings) -> Provider:
        provider_name = settings.provider.lower()
        key = (provider_name, settings.endpoint, settings.api_version, settings.api_key_env)
        if key not in self._providers:
            self._providers[key] = self._create_provider(settings)
        return self._providers[key]

    @staticmethod
    def _create_provider(settings: ModelSettings) -> Provider:
        name = settings.provider.lower()
        if name == "anthropic":
            client_options: dict[str, Any] = {
                "max_retries": 0,
                **_api_key_kwargs(settings, "ANTHROPIC_API_KEY"),
            }
            if settings.endpoint:
                client_options["base_url"] = settings.endpoint
            client = anthropic.AsyncAnthropic(**client_options)
            return AnthropicProvider(client)

        if name not in {"openai", "grok", "azure_openai", "microsoft", "ollama", "lm_studio"}:
            supported = "anthropic, openai, grok, microsoft (azure_openai), ollama, lm_studio"
            raise ValueError(f"unsupported LLM provider {settings.provider!r}; choose from {supported}")

        try:
            from openai import AsyncAzureOpenAI, AsyncOpenAI
        except ImportError as exc:
            raise RuntimeError("Install promptevals with its declared openai dependency to use this provider") from exc

        if name in {"azure_openai", "microsoft"}:
            endpoint = settings.endpoint or os.environ.get("AZURE_OPENAI_ENDPOINT")
            if not endpoint:
                raise ValueError("azure_openai requires endpoint or AZURE_OPENAI_ENDPOINT")
            api_version = settings.api_version or os.environ.get("AZURE_OPENAI_API_VERSION", "2024-10-21")
            client = AsyncAzureOpenAI(
                azure_endpoint=endpoint,
                api_version=api_version,
                **_api_key_kwargs(settings, "AZURE_OPENAI_API_KEY", required=True),
            )
            return MicrosoftProvider(client)

        defaults = {
            "openai": (None, "OPENAI_API_KEY", OpenAIProvider),
            "grok": ("https://api.x.ai/v1", "XAI_API_KEY", GrokProvider),
            "ollama": ("http://localhost:11434/v1", None, OllamaProvider),
            "lm_studio": ("http://localhost:1234/v1", None, LMStudioProvider),
        }
        default_endpoint, default_key_env, provider_type = defaults[name]
        kwargs = _api_key_kwargs(
            settings,
            default_key_env,
            required=default_key_env is not None,
        )
        if default_key_env is None and settings.api_key_env is None:
            kwargs = {"api_key": "local"}
        client = AsyncOpenAI(
            base_url=settings.endpoint or default_endpoint,
            **kwargs,
        )
        token_param = "max_completion_tokens" if name == "openai" else "max_tokens"
        return provider_type(client, max_tokens_parameter=token_param)

    async def complete(
        self,
        settings: ModelSettings,
        *,
        messages: list[dict[str, Any]],
        system: str | None,
        output_schema: dict[str, Any] | None,
        stream: bool,
        allow_fallback: bool,
    ) -> LLMResponse:
        return await self._provider(settings).complete(
            settings,
            messages=messages,
            system=system,
            output_schema=output_schema,
            stream=stream,
            allow_fallback=allow_fallback,
        )


def _api_key_kwargs(
    settings: ModelSettings,
    default_env: str | None,
    *,
    required: bool = False,
) -> dict[str, str]:
    env_name = settings.api_key_env or default_env
    if env_name is None:
        return {}
    value = os.environ.get(env_name)
    if value:
        return {"api_key": value}
    if settings.api_key_env or required:
        raise ValueError(f"API key environment variable {env_name} is not set")
    return {}


def make_client() -> ProviderRouter:
    return ProviderRouter()


def _retryable(error: Exception) -> bool:
    if isinstance(error, (anthropic.APIConnectionError, anthropic.RateLimitError)):
        return True
    if isinstance(error, anthropic.APIStatusError):
        return error.status_code in (408, 409, 429) or error.status_code >= 500
    status = getattr(error, "status_code", None)
    if isinstance(status, int):
        return status in (408, 409, 429) or status >= 500
    return error.__class__.__name__ in {
        "APIConnectionError",
        "APITimeoutError",
        "RateLimitError",
        "InternalServerError",
    }


async def call(
    client: Any,
    settings: ModelSettings,
    *,
    messages: list[dict[str, Any]],
    system: str | None = None,
    output_schema: dict[str, Any] | None = None,
    max_attempts: int = 4,
    stream: bool = False,
    allow_fallback: bool = False,
) -> LLMResponse:
    """Call the selected provider with retries and return a normalized response."""
    provider: Provider = client if hasattr(client, "complete") else AnthropicProvider(client)
    last: Exception | None = None
    for attempt in range(1, max_attempts + 1):
        try:
            response = await provider.complete(
                settings,
                messages=messages,
                system=system,
                output_schema=output_schema,
                stream=stream,
                allow_fallback=allow_fallback,
            )
            response.attempts = attempt
            if not response.served_by_fallback and not _model_matches(settings.model, response.model):
                raise ServedModelMismatch(f"requested {settings.model}, served {response.model}")
            break
        except Exception as error:  # noqa: BLE001 - classified below
            if not _retryable(error) or attempt == max_attempts:
                if _retryable(error):
                    raise RetriesExhausted(attempt, error) from error
                raise
            last = error
            delay = min(60.0, 2 ** (attempt - 1)) * (0.5 + random.random())
            retry_after = getattr(getattr(error, "response", None), "headers", {}) or {}
            if hasattr(retry_after, "get") and retry_after.get("retry-after"):
                try:
                    delay = max(delay, float(retry_after["retry-after"]))
                except ValueError:
                    pass
            await asyncio.sleep(delay)
    else:  # pragma: no cover - loop always breaks or raises
        raise RetriesExhausted(max_attempts, last or RuntimeError("unknown"))

    if output_schema is not None:
        if response.refused or response.truncated:
            raise StructuredOutputError(f"structured call ended with stop_reason={response.stop_reason}")
        try:
            response.data = json.loads(response.text)
        except json.JSONDecodeError as error:
            raise StructuredOutputError(f"could not parse JSON: {response.text[:200]!r}") from error
    return response
