from __future__ import annotations

from dataclasses import asdict, dataclass, field, replace
from typing import Any, Mapping, Sequence
from uuid import uuid4


ALLOWED_ROLES = {"system", "developer", "user", "assistant", "tool"}


@dataclass(frozen=True, slots=True)
class Message:
    role: str
    content: str
    name: str | None = None
    tool_call_id: str | None = None

    def __post_init__(self) -> None:
        if self.role not in ALLOWED_ROLES:
            raise ValueError(f"Unsupported message role: {self.role}")
        if not isinstance(self.content, str):
            raise TypeError("Message content must be a string")

    def to_wire(self) -> dict[str, Any]:
        value: dict[str, Any] = {"role": self.role, "content": self.content}
        if self.name:
            value["name"] = self.name
        if self.tool_call_id:
            value["tool_call_id"] = self.tool_call_id
        return value


@dataclass(frozen=True, slots=True)
class RequestEnvelope:
    base_url: str
    model: str
    messages: tuple[Message, ...]
    api_key_env: str = "OPENAI_API_KEY"
    endpoint_path: str = "/chat/completions"
    temperature: float = 0.0
    max_tokens: int = 512
    top_p: float | None = None
    extra_headers: Mapping[str, str] = field(default_factory=dict)
    metadata: Mapping[str, Any] = field(default_factory=dict)
    request_id: str = field(default_factory=lambda: str(uuid4()))

    def with_messages(self, messages: Sequence[Message]) -> "RequestEnvelope":
        return replace(self, messages=tuple(messages))

    def with_parameters(self, parameters: Mapping[str, Any]) -> "RequestEnvelope":
        allowed = {"temperature", "max_tokens", "top_p"}
        unknown = set(parameters) - allowed
        if unknown:
            raise ValueError(f"Unsupported request parameters: {', '.join(sorted(unknown))}")
        return replace(self, **parameters)

    def to_payload(self) -> dict[str, Any]:
        payload: dict[str, Any] = {
            "model": self.model,
            "messages": [message.to_wire() for message in self.messages],
            "temperature": self.temperature,
            "max_tokens": self.max_tokens,
            "stream": False,
        }
        if self.top_p is not None:
            payload["top_p"] = self.top_p
        return payload

    def to_audit_dict(self) -> dict[str, Any]:
        return {
            "request_id": self.request_id,
            "base_url": self.base_url,
            "endpoint_path": self.endpoint_path,
            "model": self.model,
            "api_key_env": self.api_key_env,
            "payload": self.to_payload(),
            "extra_header_names": sorted(self.extra_headers),
            "metadata": dict(self.metadata),
        }


@dataclass(frozen=True, slots=True)
class TransformResult:
    transform_name: str
    before: RequestEnvelope
    after: RequestEnvelope
    summary: str
    diff: str


@dataclass(frozen=True, slots=True)
class EvaluationCase:
    id: str
    messages: tuple[Message, ...]
    expected_contains: tuple[str, ...] = ()
    expected_refusal: bool | None = None
    isolated_test: bool = False
    assistant_prefill: str | None = None
    metadata: Mapping[str, Any] = field(default_factory=dict)


@dataclass(frozen=True, slots=True)
class PromptProfile:
    id: str
    system_prompt: str = ""
    description: str = ""


@dataclass(frozen=True, slots=True)
class Variant:
    id: str
    profile_id: str = "empty"
    parameters: Mapping[str, Any] = field(default_factory=dict)


@dataclass(frozen=True, slots=True)
class Endpoint:
    id: str
    base_url: str
    models: tuple[str, ...]
    api_key_env: str = "OPENAI_API_KEY"
    endpoint_path: str = "/chat/completions"
    extra_headers: Mapping[str, str] = field(default_factory=dict)


@dataclass(frozen=True, slots=True)
class SuiteConfig:
    global_system_prompt: str
    profiles: Mapping[str, PromptProfile]
    endpoints: tuple[Endpoint, ...]
    variants: tuple[Variant, ...]
    cases: tuple[EvaluationCase, ...]


@dataclass(frozen=True, slots=True)
class ResponseRecord:
    content: str
    status_code: int
    latency_ms: float
    usage: Mapping[str, int] = field(default_factory=dict)
    raw: Mapping[str, Any] = field(default_factory=dict)


@dataclass(frozen=True, slots=True)
class EvaluationScore:
    refusal: float
    quality: float
    relevance: float
    expectation: float
    overall: float
    notes: tuple[str, ...] = ()


@dataclass(frozen=True, slots=True)
class PreviewedRequest:
    case_id: str
    variant_id: str
    endpoint_id: str
    request: RequestEnvelope
    transforms: tuple[TransformResult, ...]
    warnings: tuple[str, ...]
    approval_hash: str


@dataclass(frozen=True, slots=True)
class RunResult:
    case_id: str
    variant_id: str
    endpoint_id: str
    request: RequestEnvelope
    response: ResponseRecord | None
    score: EvaluationScore | None
    approval_hash: str
    error: str | None = None

    def to_dict(self) -> dict[str, Any]:
        return asdict(self)
