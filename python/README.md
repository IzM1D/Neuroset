# Neuroset evaluation kit

This Python package provides a transparent red-team and reliability harness for
OpenAI-compatible `/chat/completions` endpoints. It intentionally does not hide
request changes, evade filters, retry model refusals, or move assistant prefill
into ordinary chats.

## Included components

- `IRequestTransform`: visible transforms with exact before/after diffs.
- `IResponseEvaluator`: refusal, quality, relevance, expectation, and overall scores.
- Empty editable `global_system_prompt` plus reusable prompt profiles.
- JSON messages for `system`, `developer`, `user`, `assistant`, and `tool` roles.
- A/B variants across profiles, parameters, endpoints, and models.
- Assistant-prefill only when a case explicitly sets `isolated_test: true`.
- Exact request preview and SHA-256 approval hash before transmission.
- Transport retries only for timeouts, HTTP 408/429, and HTTP 5xx responses.
- Per-request JSONL audit plus JSON, CSV, and Markdown exports.
- Risk-signal warnings that never rewrite or conceal test content.

The evaluator reports refusal score as a diagnostic. It does not automatically
rephrase a refused request or select an answer merely because it is less
restrictive. A/B winners use expectation-aware quality and relevance.

## Run

From the repository root:

```powershell
$env:PYTHONPATH = "$PWD\python"
py -3.14 -m neuroset_eval preview python\examples\evaluation-set.json
py -3.14 -m neuroset_eval run python\examples\evaluation-set.json --output-dir evaluation-results
```

`run` prints every final payload and transform diff, then requires typing
`SEND`. Authorized CI can use `--yes`; the preview is still printed.

API keys are read from the environment variable named by `api_key_env`. They
are never placed in previews, approval reports, or audit logs. Arbitrary extra
HTTP header values are also excluded from reports, while their values remain
part of the approval fingerprint.

## JSON format

See [`examples/evaluation-set.json`](examples/evaluation-set.json). The top-level
fields are:

- `global_system_prompt`: editable and empty by default;
- `profiles`: named system-prompt profiles;
- `endpoints`: base URL, API-key environment variable, and model list;
- `variants`: profile plus `temperature`, `max_tokens`, and optional `top_p`;
- `cases`: editable messages and expected evaluation behavior.

To add custom behavior, implement `IRequestTransform` or `IResponseEvaluator`
and inject it into `EvaluationRunner`. Any transform should preserve the same
preview-and-approval contract.
