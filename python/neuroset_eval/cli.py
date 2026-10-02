from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

from .config import load_suite
from .models import PreviewedRequest
from .runner import EvaluationRunner


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        prog="neuroset-eval",
        description="Transparent OpenAI-compatible prompt evaluation and A/B testing.",
    )
    subparsers = parser.add_subparsers(dest="command", required=True)
    preview_parser = subparsers.add_parser("preview", help="Render exact requests without sending them.")
    preview_parser.add_argument("suite", type=Path)
    run_parser = subparsers.add_parser("run", help="Preview, approve, send, score, and export a suite.")
    run_parser.add_argument("suite", type=Path)
    run_parser.add_argument("--output-dir", type=Path, default=Path("evaluation-results"))
    run_parser.add_argument("--yes", action="store_true", help="Approve all printed requests for authorized CI.")

    args = parser.parse_args(argv)
    try:
        suite = load_suite(args.suite)
        runner = EvaluationRunner()
        previews = runner.preview(suite)
        _print_previews(previews)
        if args.command == "preview":
            print(f"\nPreviewed {len(previews)} request(s); nothing was sent.")
            return 0

        if not args.yes:
            answer = input(f"\nType SEND to approve these {len(previews)} exact request(s): ").strip()
            if answer != "SEND":
                print("Cancelled; nothing was sent.")
                return 2

        results, paths = runner.run(
            previews,
            suite,
            approved_hashes={preview.approval_hash for preview in previews},
            output_dir=args.output_dir,
        )
        successful = sum(result.error is None for result in results)
        print(f"\nCompleted {successful}/{len(results)} request(s).")
        for name, path in paths.items():
            print(f"{name}: {path.resolve()}")
        return 0 if successful == len(results) else 1
    except (OSError, ValueError, PermissionError, json.JSONDecodeError) as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 2


def _print_previews(previews: list[PreviewedRequest]) -> None:
    for index, preview in enumerate(previews, start=1):
        print(f"\n=== Request {index}/{len(previews)} ===")
        print(
            f"case={preview.case_id} variant={preview.variant_id} "
            f"endpoint={preview.endpoint_id} model={preview.request.model}"
        )
        print(f"approval_hash={preview.approval_hash}")
        for warning in preview.warnings:
            print(f"WARNING: {warning}")
        for transform in preview.transforms:
            print(f"\n[{transform.transform_name}] {transform.summary}")
            if transform.diff:
                print(transform.diff)
        print("\nFinal request:")
        print(json.dumps(preview.request.to_audit_dict(), ensure_ascii=False, indent=2, sort_keys=True))


if __name__ == "__main__":
    raise SystemExit(main())
