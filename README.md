# decision-1-aiops

Scenario harness for exploring `Microsoft-Decision-1` (Microsoft Foundry) on **trading desk decisions**, plus a blogpost draft.

`Decision-1` is not a chat model: you send a `state` plus typed questions (`noul` yes/no, `choice`, `score`) to
`<resource-endpoint>/providers/microsoft/v1/systemone` and get probabilities back, with no free text.

## Configure (user-secrets, stored outside the repo)

```bash
cd src/DecisionExamples
dotnet user-secrets set FOUNDRY_ENDPOINT "https://<resource>.services.ai.azure.com"
dotnet user-secrets set FOUNDRY_API_KEY "<key>"     # or skip and use `az login`
dotnet user-secrets set FOUNDRY_MODEL "Decision-1"  # deployment name (default)
```

Env vars with the same names override user-secrets.

## Run

```bash
dotnet run --project src/DecisionExamples -- --runs=10 [--scenario=injection] [--probe]
```

`--probe` prints the raw API response for one scenario. Requests retry on 429/5xx honoring `Retry-After`.

Per scenario we record: pass rate vs. an expectation, p50 latency, max spread of the answer across repeated
identical calls, and for `choice` questions how many answers change when option order is reversed.
Raw responses go to `results/run-*.json` (git-ignored).

## Scenarios (`src/DecisionExamples/Scenarios.cs`)
Exposure cap (raw vs. computed-in-state), prompt injection + benign control, trading halt, stale data,
action routing (clear signal, daily-loss limit, conflicting signals with an ESCALATE option),
severity scoring (high/low), and a combined three-question request.

## Benchmark (`--benchmark`)
`Dataset.cs` holds 44 labeled cases (39 clear, 5 ambiguous) in five categories: compliance, injection,
tradability, data-quality, action. They include boundary values, distractors, misleading metadata, negation,
Portuguese, and enriched-state variants. Each question is also asked with two rewordings (and reversed
option order for `choice`).

```bash
dotnet run --project src/DecisionExamples -- --benchmark --baseline=gpt-4.1-nano --repeats=3
```

The baseline is a chat deployment on the same resource (`/openai/v1/chat/completions`, temperature 0, JSON output
with a self-reported confidence). Output: accuracy by category, robustness, AUC/Brier/ECE, confidence on ambiguous
cases, latency/tokens, a threshold sweep, and the list of misses, saved to `results/benchmark-*.md|json`.

## External benchmarks (`--external`)
Public datasets, downloaded at runtime from the Hugging Face datasets-server into `data/external/` (git-ignored; not
redistributed, since FOMC is CC BY-NC-4.0):

| Dataset | License | Task | Used |
|---|---|---|---|
| `deepset/prompt-injections` | Apache-2.0 | injection yes/no | full test split (116); train sample for threshold calibration |
| `zeroshot/twitter-financial-news-sentiment` | MIT | bearish/bullish/neutral | 30 per label from validation |
| `gtfintechlab/fomc_communication` (Shah et al., ACL 2023) | CC BY-NC-4.0 | dovish/hawkish/neutral | 30 per label from test |

```bash
dotnet run --project src/DecisionExamples -- --external --baseline=gpt-4.1-nano --per-label=30
```

Sampling is stratified with a fixed seed. For the injection set, the decision threshold is picked on a held-out train
sample and then scored on the test split. Labels are the datasets' own; the injection labels include task-switching
prompts, so treat absolute numbers carefully. To avoid tripping other models' abuse monitoring, the injection set is
sent to Decision-1 only; baselines skip it unless you pass `--adversarial-baselines`.

## AIOps benchmark (`--aiops`)
Scenarios for an SRE team, on public data (downloaded at runtime into `data/external/aiops/`, git-ignored, not
redistributed). Prepared with Python, evaluated with the same C# harness:

| Dataset | License | Task | Used |
|---|---|---|---|
| Loghub HDFS_v1 (Xu et al., SOSP'09; Zhu et al., ISSRE'23) | free for research/academic use, cite the papers | does a block's log activity indicate an anomaly? (yes/no) | 600 normal + 600 anomalous blocks; 150 + 150 held out to pick a threshold |
| RCAEval RE2-OB (Pham et al.) | MIT | root-cause service (11 options) and fault type (6 options) | all 90 failure cases |

```bash
python -m venv .venv && .venv/bin/pip install -r scripts/aiops/requirements.txt
.venv/bin/python scripts/aiops/download.py      # HDFS: only 2 files via range requests; RCAEval: re2ob suite
.venv/bin/python scripts/aiops/prepare.py       # builds compact states + classical baselines
dotnet run --project src/DecisionExamples -- --aiops --baseline=gpt-4.1-nano,<reasoning-deployment>:100
```

Notes:
- The state is computed before the call (event counts per template for HDFS; before/after deltas of metrics, trace
  durations and log volume for RCA), since Decision-1 is weak at arithmetic over raw numbers.
- The dataset's `Type` column is derived from the label, so it is not used.
- Non-LLM references: a rule ("any exception event"), logistic regression on event counts trained on 50 to 8000 labeled
  blocks, and a "largest metric deviation" rule for RCA. Results include Wilson CIs and exact McNemar tests.
- `name:N` caps a baseline at N cases per category (useful for slow reasoning deployments).
- No adversarial or jailbreak content is sent to any model in the AIOps runs.

### Enriched HDFS state and calibrated threshold (`--aiops-diag`, `--aiops-final`)
The first HDFS run was poor for Decision-1 (49%), so we diagnosed it on the calibration split (`--aiops-diag`): a
computed lifecycle summary (allocations, replica writes started/completed, terminated acks, registered replicas,
missing steps, error events) plus a question phrased as "is the lifecycle complete?" helps a lot. `--aiops-final`
runs the chosen variants on the full test split, with the threshold picked on the calibration split:

```bash
dotnet run --project src/DecisionExamples -- --aiops-final --baseline=gpt-4.1-nano,<reasoning-deployment>:100
# add --skip-decision to run only the baselines
```

The lifecycle summary encodes HDFS domain knowledge (replication factor 3), i.e. feature engineering.

## Blogpost
`blog/draft.md`

> Illustrative research only; not financial advice.
