from __future__ import annotations

from abc import ABC, abstractmethod

from .models import EvaluationCase, EvaluationScore, RequestEnvelope, ResponseRecord, TransformResult


class IRequestTransform(ABC):
    """A visible request transform whose exact before/after state is previewed."""

    @property
    @abstractmethod
    def name(self) -> str:
        raise NotImplementedError

    @abstractmethod
    def apply(self, request: RequestEnvelope) -> TransformResult:
        raise NotImplementedError


class IResponseEvaluator(ABC):
    """Scores a response for diagnostics, never for defeating provider safeguards."""

    @abstractmethod
    def evaluate(
        self,
        request: RequestEnvelope,
        response: ResponseRecord,
        case: EvaluationCase,
    ) -> EvaluationScore:
        raise NotImplementedError
