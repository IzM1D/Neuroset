from __future__ import annotations

import csv
import json
from datetime import UTC, datetime
from pathlib import Path
from typing import Iterable

from .models import RunResult


class AuditLog:
    def __init__(self, path: Path) -> None:
        path.parent.mkdir(parents=True, exist_ok=True)
        self.path = path

    def append(self, event: dict[str, object]) -> None:
        record = {"timestamp": datetime.now(UTC).isoformat(), **event}
        with self.path.open("a", encoding="utf-8") as stream:
            stream.write(json.dumps(record, ensure_ascii=False, sort_keys=True) + "\n")


def export_results(results: Iterable[RunResult], output_dir: str | Path) -> dict[str, Path]:
    rows = list(results)
    target = Path(output_dir)
    target.mkdir(parents=True, exist_ok=True)
    json_path = target / "results.json"
    csv_path = target / "results.csv"
    markdown_path = target / "report.md"

    json_path.write_text(
        json.dumps([result.to_dict() for result in rows], ensure_ascii=False, indent=2, sort_keys=True),
        encoding="utf-8",
    )

    with csv_path.open("w", encoding="utf-8-sig", newline="") as stream:
        writer = csv.DictWriter(
            stream,
            fieldnames=[
                "case_id",
                "variant_id",
                "endpoint_id",
                "model",
                "status_code",
                "latency_ms",
                "refusal",
                "quality",
                "relevance",
                "expectation",
                "overall",
                "error",
            ],
        )
        writer.writeheader()
        for result in rows:
            writer.writerow(_flat_row(result))

    markdown_path.write_text(_build_markdown(rows), encoding="utf-8")
    return {"json": json_path, "csv": csv_path, "markdown": markdown_path}


def _flat_row(result: RunResult) -> dict[str, object]:
    response = result.response
    score = result.score
    return {
        "case_id": result.case_id,
        "variant_id": result.variant_id,
        "endpoint_id": result.endpoint_id,
        "model": result.request.model,
        "status_code": response.status_code if response else "",
        "latency_ms": round(response.latency_ms, 2) if response else "",
        "refusal": round(score.refusal, 4) if score else "",
        "quality": round(score.quality, 4) if score else "",
        "relevance": round(score.relevance, 4) if score else "",
        "expectation": round(score.expectation, 4) if score else "",
        "overall": round(score.overall, 4) if score else "",
        "error": result.error or "",
    }


def _build_markdown(results: list[RunResult]) -> str:
    lines = [
        "# Neuroset evaluation report",
        "",
        "Selection is expectation-aware: refusal score is diagnostic and is never used alone to choose an output.",
        "",
        "| Case | Variant | Endpoint | Model | Refusal | Quality | Relevance | Overall | Status |",
        "|---|---|---|---|---:|---:|---:|---:|---|",
    ]
    for result in results:
        score = result.score
        lines.append(
            "| {case} | {variant} | {endpoint} | {model} | {refusal} | {quality} | {relevance} | {overall} | {status} |".format(
                case=_escape(result.case_id),
                variant=_escape(result.variant_id),
                endpoint=_escape(result.endpoint_id),
                model=_escape(result.request.model),
                refusal=f"{score.refusal:.2f}" if score else "-",
                quality=f"{score.quality:.2f}" if score else "-",
                relevance=f"{score.relevance:.2f}" if score else "-",
                overall=f"{score.overall:.2f}" if score else "-",
                status="error" if result.error else "ok",
            )
        )

    lines.extend(["", "## A/B winners", ""])
    successful = [result for result in results if result.score is not None]
    groups: dict[tuple[str, str, str], list[RunResult]] = {}
    for result in successful:
        key = (result.case_id, result.endpoint_id, result.request.model)
        groups.setdefault(key, []).append(result)
    if not groups:
        lines.append("No successful results.")
    else:
        for key, group in sorted(groups.items()):
            winner = max(group, key=lambda item: item.score.overall if item.score else -1)
            lines.append(
                f"- `{key[0]}` / `{key[1]}` / `{key[2]}`: `{winner.variant_id}` "
                f"(overall {winner.score.overall:.2f}, refusal {winner.score.refusal:.2f})."
            )

    lines.extend(["", "## Model comparison", ""])
    by_model: dict[str, list[RunResult]] = {}
    for result in successful:
        by_model.setdefault(result.request.model, []).append(result)
    if not by_model:
        lines.append("No successful results.")
    else:
        lines.extend([
            "| Model | Runs | Refusal | Quality | Relevance | Overall |",
            "|---|---:|---:|---:|---:|---:|",
        ])
        for model, group in sorted(by_model.items()):
            lines.append(
                f"| {_escape(model)} | {len(group)} | {_average(group, 'refusal'):.2f} | "
                f"{_average(group, 'quality'):.2f} | {_average(group, 'relevance'):.2f} | "
                f"{_average(group, 'overall'):.2f} |"
            )

    lines.extend(["", "## Actually sent requests", ""])
    for result in results:
        lines.extend(
            [
                f"### {result.request.request_id}",
                "",
                f"- Case: `{result.case_id}`",
                f"- Variant: `{result.variant_id}`",
                f"- Endpoint: `{result.endpoint_id}`",
                f"- Approval hash: `{result.approval_hash}`",
                "",
                "```json",
                json.dumps(result.request.to_audit_dict(), ensure_ascii=False, indent=2, sort_keys=True),
                "```",
                "",
            ]
        )
    return "\n".join(lines) + "\n"


def _average(results: list[RunResult], name: str) -> float:
    values = [getattr(result.score, name) for result in results if result.score is not None]
    return sum(values) / len(values) if values else 0.0


def _escape(value: str) -> str:
    return value.replace("|", "\\|").replace("\n", " ")
