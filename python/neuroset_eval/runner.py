from __future__ import annotations

import hashlib
import json
from pathlib import Path
from typing import Iterable

from .client import OpenAICompatibleClient
from .evaluators import HeuristicResponseEvaluator
from .interfaces import IResponseEvaluator
from .models import PreviewedRequest, PromptProfile, RequestEnvelope, RunResult, SuiteConfig
from .reporting import AuditLog, export_results
from .transforms import AssistantPrefillTransform, ParameterTransform, PromptProfileTransform, RiskSignalScanner


class EvaluationRunner:
    def __init__(
        self,
        *,
        client: OpenAICompatibleClient | None = None,
        evaluator: IResponseEvaluator | None = None,
        scanner: RiskSignalScanner | None = None,
    ) -> None:
        self.client = client or OpenAICompatibleClient()
        self.evaluator = evaluator or HeuristicResponseEvaluator()
        self.scanner = scanner or RiskSignalScanner()

    def preview(self, suite: SuiteConfig) -> list[PreviewedRequest]:
        previews: list[PreviewedRequest] = []
        for endpoint in suite.endpoints:
            for model in endpoint.models:
                for case in suite.cases:
                    for variant in suite.variants:
                        request = RequestEnvelope(
                            base_url=endpoint.base_url,
                            endpoint_path=endpoint.endpoint_path,
                            api_key_env=endpoint.api_key_env,
                            extra_headers=endpoint.extra_headers,
                            model=model,
                            messages=case.messages,
                            metadata={
                                "case_id": case.id,
                                "variant_id": variant.id,
                                "endpoint_id": endpoint.id,
                                "isolated_test": case.isolated_test,
                                **dict(case.metadata),
                            },
                        )
                        transform_results = []
                        transforms = [
                            PromptProfileTransform(
                                PromptProfile("global", suite.global_system_prompt, "Editable global system prompt")
                            ),
                            PromptProfileTransform(suite.profiles[variant.profile_id]),
                            ParameterTransform(dict(variant.parameters)),
                        ]
                        if case.assistant_prefill is not None:
                            transforms.append(
                                AssistantPrefillTransform(case.assistant_prefill, isolated_test=case.isolated_test)
                            )
                        for transform in transforms:
                            result = transform.apply(request)
                            transform_results.append(result)
                            request = result.after

                        previews.append(
                            PreviewedRequest(
                                case_id=case.id,
                                variant_id=variant.id,
                                endpoint_id=endpoint.id,
                                request=request,
                                transforms=tuple(transform_results),
                                warnings=self.scanner.scan(request),
                                approval_hash=request_hash(request),
                            )
                        )
        return previews

    def run(
        self,
        previews: Iterable[PreviewedRequest],
        suite: SuiteConfig,
        *,
        approved_hashes: set[str],
        output_dir: str | Path,
    ) -> tuple[list[RunResult], dict[str, Path]]:
        target = Path(output_dir)
        target.mkdir(parents=True, exist_ok=True)
        audit = AuditLog(target / "audit.jsonl")
        cases = {case.id: case for case in suite.cases}
        results: list[RunResult] = []

        for preview in previews:
            current_hash = request_hash(preview.request)
            if current_hash != preview.approval_hash:
                raise ValueError(f"Request changed after preview: {preview.request.request_id}")
            if preview.approval_hash not in approved_hashes:
                raise PermissionError(f"Request was not approved: {preview.request.request_id}")

            audit.append(
                {
                    "event": "request_sent",
                    "approval_hash": preview.approval_hash,
                    "case_id": preview.case_id,
                    "variant_id": preview.variant_id,
                    "endpoint_id": preview.endpoint_id,
                    "request": preview.request.to_audit_dict(),
                }
            )
            try:
                response = self.client.complete(preview.request)
                score = self.evaluator.evaluate(preview.request, response, cases[preview.case_id])
                result = RunResult(
                    preview.case_id,
                    preview.variant_id,
                    preview.endpoint_id,
                    preview.request,
                    response,
                    score,
                    preview.approval_hash,
                )
                audit.append(
                    {
                        "event": "response_received",
                        "request_id": preview.request.request_id,
                        "status_code": response.status_code,
                        "latency_ms": response.latency_ms,
                        "usage": dict(response.usage),
                        "response": response.content,
                        "score": {
                            "refusal": score.refusal,
                            "quality": score.quality,
                            "relevance": score.relevance,
                            "expectation": score.expectation,
                            "overall": score.overall,
                            "notes": score.notes,
                        },
                    }
                )
            except Exception as exc:
                result = RunResult(
                    preview.case_id,
                    preview.variant_id,
                    preview.endpoint_id,
                    preview.request,
                    None,
                    None,
                    preview.approval_hash,
                    str(exc),
                )
                audit.append(
                    {"event": "request_failed", "request_id": preview.request.request_id, "error": str(exc)}
                )
            results.append(result)

        paths = export_results(results, target)
        paths["audit"] = audit.path
        return results, paths


def request_hash(request: RequestEnvelope) -> str:
    fingerprint = {**request.to_audit_dict(), "extra_headers": dict(request.extra_headers)}
    canonical = json.dumps(fingerprint, ensure_ascii=False, sort_keys=True, separators=(",", ":"))
    return hashlib.sha256(canonical.encode("utf-8")).hexdigest()
