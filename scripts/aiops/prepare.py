"""Builds compact, model-ready cases from the public AIOps datasets (not redistributed; kept under data/external/aiops).

Inputs (see scripts/aiops/README.md for the download commands):
  data/external/aiops/hdfs/preprocessed/{Event_traces.csv,HDFS.log_templates.csv}   Loghub HDFS_v1
  data/external/aiops/rcaeval/{cases.parquet,re2ob_*/}                              RCAEval RE2-OB

Outputs (JSONL, one case per line, fixed seeds):
  hdfs_test.jsonl, hdfs_calib.jsonl, rca_cases.jsonl, classical_baselines.json
"""
import json
import re
from collections import Counter
from pathlib import Path

import numpy as np
import pandas as pd
from sklearn.feature_extraction import DictVectorizer
from sklearn.linear_model import LogisticRegression
from sklearn.metrics import roc_auc_score

ROOT = Path(__file__).resolve().parents[2] / "data" / "external" / "aiops"
SEED = 42
HDFS_TEST_PER_CLASS = 600
HDFS_CALIB_PER_CLASS = 150


def sig(x, n=3):
    return float(f"{x:.{n}g}") if np.isfinite(x) else None


# ---------- HDFS (Loghub HDFS_v1) ----------
# Events whose template mentions an exception, failure or timeout. Used only by the rule-of-thumb baseline.
EXCEPTION_WORDS = ("exception", "failed", "interrupted", "timed out", "unexpected error", "empty packet")


def load_hdfs():
    d = pd.read_csv(ROOT / "hdfs/preprocessed/Event_traces.csv", usecols=["BlockId", "Label", "Features"])
    tpl = pd.read_csv(ROOT / "hdfs/preprocessed/HDFS.log_templates.csv").set_index("EventId")["EventTemplate"]
    names = {e: re.sub(r"\s+", " ", re.sub(r"\[\*\]", " ", t)).strip() for e, t in tpl.items()}
    d["events"] = d["Features"].str.strip("[]").str.split(",")
    d["y"] = (d["Label"] == "Fail").astype(int)
    return d, names


def hdfs_state(events, names):
    c = Counter(events)
    return {
        "system": "HDFS (Hadoop distributed file system), log events for a single data block",
        "total_events": len(events),
        "event_counts": {names[e]: n for e, n in sorted(c.items(), key=lambda kv: int(kv[0][1:]))},
    }


def sample_classes(d, per_class, exclude, seed):
    parts = []
    for y in (0, 1):
        pool = d[(d.y == y) & ~d.BlockId.isin(exclude)]
        parts.append(pool.sample(per_class, random_state=seed + y))
    return pd.concat(parts)


def hdfs_baselines(d, names, test, exclude):
    exc_ids = {e for e, n in names.items() if any(w in n.lower() for w in EXCEPTION_WORDS)}
    out = {"rule_any_exception_event": {"pred": [int(any(e in exc_ids for e in ev)) for ev in test.events]}}
    pool = d[~d.BlockId.isin(exclude)]
    labels = [int(y) for y in test.y]
    # Learning curve: how many labeled blocks a classical model needs.
    for n_per_class in (25, 100, 500, 4000):
        tr = pd.concat([pool[pool.y == y].sample(n_per_class, random_state=SEED + 100 + y) for y in (0, 1)])
        v = DictVectorizer()
        Xtr = v.fit_transform([dict(Counter(e)) for e in tr.events])
        clf = LogisticRegression(max_iter=2000).fit(Xtr, tr.y)
        p = clf.predict_proba(v.transform([dict(Counter(e)) for e in test.events]))[:, 1]
        out[f"logreg_train_{2 * n_per_class}"] = {"prob": [round(float(x), 5) for x in p], "auc": float(roc_auc_score(labels, p))}
    out["ids"] = list(test.BlockId)
    out["labels"] = labels
    return out


def write_jsonl(path, rows):
    with open(path, "w") as f:
        for r in rows:
            f.write(json.dumps(r, separators=(",", ":")) + "\n")


def prepare_hdfs():
    d, names = load_hdfs()
    test = sample_classes(d, HDFS_TEST_PER_CLASS, set(), SEED)
    calib = sample_classes(d, HDFS_CALIB_PER_CLASS, set(test.BlockId), SEED + 10)
    test = test.sample(frac=1, random_state=SEED)
    calib = calib.sample(frac=1, random_state=SEED)
    for name, df in (("hdfs_test", test), ("hdfs_calib", calib)):
        write_jsonl(ROOT / f"{name}.jsonl", [{"id": r.BlockId, "label": int(r.y), "state": hdfs_state(r.events, names)} for r in df.itertuples()])
    return hdfs_baselines(d, names, test, set(test.BlockId) | set(calib.BlockId))


# ---------- RCAEval RE2-OB ----------
SERVICES = ["adservice", "cartservice", "checkoutservice", "currencyservice", "emailservice", "frontend",
            "paymentservice", "productcatalogservice", "recommendationservice", "redis", "shippingservice"]


def pct(a, b):
    if abs(a) > 1e-9:
        return (b - a) / a * 100
    return 0.0 if abs(b) < 1e-9 else 1e6


def rca_state(case_dir):
    inj = int(open(case_dir / "inject_time.txt").read())
    m = pd.read_parquet(case_dir / "metrics.parquet")
    pre, post = m[m.time < inj], m[m.time >= inj]
    rows = []
    for col in m.columns[1:]:
        a, b = pre[col].astype(float), post[col].astype(float)
        score = abs(b.mean() - a.mean()) / (a.std() + 0.05 * abs(a.mean()) + 1e-9)
        svc, _, kind = col.partition("_")
        rows.append((score, svc, kind, a.mean(), b.mean()))
    rows.sort(key=lambda r: -r[0])
    metrics = [{"service": s, "metric": k, "before": sig(a), "after": sig(b), "change_pct": sig(pct(a, b))}
               for _, s, k, a, b in rows[:10]]

    t = pd.read_parquet(case_dir / "traces.parquet", columns=["serviceName", "startTimeMillis", "duration"])
    t["after"] = (t.startTimeMillis // 1000) >= inj
    g = t.groupby(["serviceName", "after"]).duration.mean().unstack()
    traces = [{"service": svc, "mean_span_ms_before": sig(r[False] / 1000), "mean_span_ms_after": sig(r[True] / 1000),
               "change_pct": sig(pct(r[False], r[True]))} for svc, r in g.iterrows() if np.isfinite(r[False]) and np.isfinite(r[True])]
    traces.sort(key=lambda r: -abs(r["change_pct"]))

    lg = pd.read_parquet(case_dir / "logs.parquet", columns=["timestamp", "container_name"])
    dur_pre, dur_post = inj - m.time.min(), m.time.max() - inj + 1
    lc = lg.assign(after=lg.timestamp >= inj).groupby(["container_name", "after"]).size().unstack(fill_value=0)
    logs = []
    for svc, r in lc.iterrows():
        a, b = r.get(False, 0) / dur_pre, r.get(True, 0) / dur_post
        logs.append({"service": svc, "log_lines_per_s_before": sig(a), "log_lines_per_s_after": sig(b), "change_pct": sig(pct(a, b))})
    logs.sort(key=lambda r: -abs(r["change_pct"]))

    return {
        "system": "Online Boutique microservices on Kubernetes (11 services). An alert fired at the start of the 'after' window; telemetry is summarized as before vs after.",
        "minutes_before_and_after": [round(dur_pre / 60, 1), round(dur_post / 60, 1)],
        "top_metric_changes": metrics,
        "trace_span_duration_by_service": traces[:6],
        "log_volume_by_service": logs[:6],
    }, rows


def prepare_rca():
    idx = pd.read_parquet(ROOT / "rcaeval/cases.parquet")
    idx = idx[idx.dataset == "RE2-OB"].sort_values("case")
    cases, heur = [], []
    for r in idx.itertuples():
        state, rows = rca_state(ROOT / "rcaeval" / r.case)
        cases.append({"id": r.case, "service": r.root_cause_service, "fault": r.fault, "state": state})
        best = rows[0]
        heur.append({"service_pred": best[1] if best[1] in SERVICES else "frontend",
                     "fault_pred": {"cpu": "cpu", "mem": "mem", "diskio": "disk", "socket": "socket"}.get(best[2], "delay")})
    write_jsonl(ROOT / "rca_cases.jsonl", cases)
    return {"n": len(cases),
            "service_acc": float(np.mean([h["service_pred"] == c["service"] for h, c in zip(heur, cases)])),
            "fault_acc": float(np.mean([h["fault_pred"] == c["fault"] for h, c in zip(heur, cases)])),
            "ids": [c["id"] for c in cases], "service_pred": [h["service_pred"] for h in heur],
            "fault_pred": [h["fault_pred"] for h in heur]}


if __name__ == "__main__":
    base = {"hdfs": prepare_hdfs(), "rca_heuristic": prepare_rca()}
    (ROOT / "classical_baselines.json").write_text(json.dumps(base))
    h = base["hdfs"]
    print("hdfs test:", len(h["ids"]), {k: round(v["auc"], 4) for k, v in h.items() if isinstance(v, dict) and "auc" in v})
    print("hdfs rule acc:", np.mean(np.array(h["rule_any_exception_event"]["pred"]) == np.array(h["labels"])))
    print("rca heuristic:", {k: v for k, v in base["rca_heuristic"].items() if k.endswith("acc") or k == "n"})
