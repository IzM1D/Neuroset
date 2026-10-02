from __future__ import annotations

import json
import os
import time
import urllib.error
import urllib.request
from dataclasses import dataclass
from typing import Any
from urllib.parse import urljoin

from .models import RequestEnvelope, ResponseRecord


class OpenAICompatibleError(RuntimeError):
    def __init__(self, message: str, *, status_code: int | None = None, transient: bool = False) -> None:
        super().__init__(message)
        self.status_code = status_code
        self.transient = transient


@dataclass(slots=True)
class OpenAICompatibleClient:
    timeout_seconds: float = 120.0
    max_transport_retries: int = 2

    def complete(self, request: RequestEnvelope) -> ResponseRecord:
        url = _build_url(request.base_url, request.endpoint_path)
        api_key = os.environ.get(request.api_key_env, "")
        body = json.dumps(request.to_payload(), ensure_ascii=False).encode("utf-8")
        headers = {
            "Accept": "application/json",
            "Content-Type": "application/json",
            "User-Agent": "Neuroset-Eval/1.0",
            **dict(request.extra_headers),
        }
        if api_key:
            headers["Authorization"] = f"Bearer {api_key}"

        last_error: OpenAICompatibleError | None = None
        for attempt in range(self.max_transport_retries + 1):
            started = time.perf_counter()
            try:
                wire_request = urllib.request.Request(url, data=body, headers=headers, method="POST")
                with urllib.request.urlopen(wire_request, timeout=self.timeout_seconds) as response:
                    raw_bytes = response.read()
                    status = response.status
                latency_ms = (time.perf_counter() - started) * 1000
                payload = json.loads(raw_bytes.decode("utf-8"))
                return ResponseRecord(
                    content=_extract_content(payload),
                    status_code=status,
                    latency_ms=latency_ms,
                    usage=_extract_usage(payload),
                    raw=payload,
                )
            except urllib.error.HTTPError as exc:
                raw = exc.read(4096).decode("utf-8", errors="replace")
                transient = exc.code in {408, 429} or exc.code >= 500
                last_error = OpenAICompatibleError(
                    f"Endpoint returned HTTP {exc.code}: {raw}",
                    status_code=exc.code,
                    transient=transient,
                )
            except (urllib.error.URLError, TimeoutError, OSError) as exc:
                last_error = OpenAICompatibleError(str(exc), transient=True)
            except (json.JSONDecodeError, KeyError, TypeError, ValueError) as exc:
                raise OpenAICompatibleError(f"Invalid OpenAI-compatible response: {exc}") from exc

            if not last_error.transient or attempt >= self.max_transport_retries:
                raise last_error
            time.sleep(0.35 * (2**attempt))

        raise last_error or OpenAICompatibleError("Unknown transport failure")


def _build_url(base_url: str, endpoint_path: str) -> str:
    normalized = base_url.rstrip("/") + "/"
    path = endpoint_path.lstrip("/")
    if normalized.rstrip("/").endswith(path.rstrip("/")):
        return normalized.rstrip("/")
    return urljoin(normalized, path)


def _extract_content(payload: dict[str, Any]) -> str:
    choices = payload.get("choices")
    if not isinstance(choices, list) or not choices:
        raise ValueError("response has no choices")
    message = choices[0].get("message", {})
    content = message.get("content", "")
    if isinstance(content, str):
        return content
    if isinstance(content, list):
        parts: list[str] = []
        for item in content:
            if isinstance(item, dict) and isinstance(item.get("text"), str):
                parts.append(item["text"])
        return "".join(parts)
    return str(content or "")


def _extract_usage(payload: dict[str, Any]) -> dict[str, int]:
    usage = payload.get("usage")
    if not isinstance(usage, dict):
        return {}
    result: dict[str, int] = {}
    for key in ("prompt_tokens", "completion_tokens", "total_tokens", "input_tokens", "output_tokens"):
        value = usage.get(key)
        if isinstance(value, int):
            result[key] = value
    return result
