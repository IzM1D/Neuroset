from __future__ import annotations

import difflib
import json
from typing import Iterable

from .interfaces import IRequestTransform
from .models import Message, PromptProfile, RequestEnvelope, TransformResult


def _pretty(request: RequestEnvelope) -> str:
    return json.dumps(request.to_audit_dict(), ensure_ascii=False, indent=2, sort_keys=True)


def _diff(before: RequestEnvelope, after: RequestEnvelope) -> str:
    return "\n".join(
        difflib.unified_diff(
            _pretty(before).splitlines(),
            _pretty(after).splitlines(),
            fromfile="before",
            tofile="after",
            lineterm="",
        )
    )


class PromptProfileTransform(IRequestTransform):
    def __init__(self, profile: PromptProfile) -> None:
        self.profile = profile

    @property
    def name(self) -> str:
        return f"profile:{self.profile.id}"

    def apply(self, request: RequestEnvelope) -> TransformResult:
        if not self.profile.system_prompt.strip():
            after = request
            summary = "The empty profile leaves the request unchanged."
        else:
            after = request.with_messages(
                (Message("system", self.profile.system_prompt.strip()), *request.messages)
            )
            summary = f"Prepended the visible system prompt from profile '{self.profile.id}'."
        return TransformResult(self.name, request, after, summary, _diff(request, after))


class ParameterTransform(IRequestTransform):
    def __init__(self, parameters: dict[str, object]) -> None:
        self.parameters = parameters

    @property
    def name(self) -> str:
        return "parameters"

    def apply(self, request: RequestEnvelope) -> TransformResult:
        after = request.with_parameters(self.parameters)
        return TransformResult(
            self.name,
            request,
            after,
            f"Applied visible parameters: {', '.join(sorted(self.parameters)) or '(none)' }.",
            _diff(request, after),
        )


class AssistantPrefillTransform(IRequestTransform):
    """Adds a prefix only to an explicitly isolated compatibility test."""

    def __init__(self, prefill: str, *, isolated_test: bool) -> None:
        self.prefill = prefill
        self.isolated_test = isolated_test

    @property
    def name(self) -> str:
        return "isolated-assistant-prefill"

    def apply(self, request: RequestEnvelope) -> TransformResult:
        if not self.isolated_test:
            raise ValueError("Assistant prefill is allowed only in explicit isolated tests")
        if not self.prefill:
            raise ValueError("Assistant prefill cannot be empty")
        after = request.with_messages((*request.messages, Message("assistant", self.prefill)))
        return TransformResult(
            self.name,
            request,
            after,
            "Appended a visible assistant message for this isolated compatibility test.",
            _diff(request, after),
        )


class RiskSignalScanner:
    """Warns about sensitive test content without rewriting it."""

    DEFAULT_SIGNALS = (
        "ignore previous instructions",
        "disable safety",
        "скрой от пользователя",
    )

    def __init__(self, signals: Iterable[str] | None = None) -> None:
        self.signals = tuple(signal.casefold() for signal in (signals or self.DEFAULT_SIGNALS))

    def scan(self, request: RequestEnvelope) -> tuple[str, ...]:
        text = "\n".join(message.content for message in request.messages).casefold()
        matches = sorted({signal for signal in self.signals if signal and signal in text})
        if not matches:
            return ()
        return (
            "Risk signals detected (content was not modified): " + ", ".join(matches),
            "Review endpoint policy and authorization before sending this test.",
        )


def activate_profile(prompt: str, *, profile_id: str = "custom") -> PromptProfileTransform:
    return PromptProfileTransform(PromptProfile(profile_id, prompt, "User-authored profile"))
