"""Builds the results image used in the LinkedIn post (blog/images/results.png). Numbers come from results/aiops-*.md."""
from pathlib import Path

import matplotlib

matplotlib.use("Agg")
import matplotlib.pyplot as plt

OUT = Path(__file__).resolve().parents[2] / "blog" / "images" / "results.png"

DECISION, LLM, REF = "#0F6CBD", "#8A8886", "#C7C6C4"

rca = [("Decision-1", 94, DECISION), ("gpt-4.1-nano", 91, LLM), ("gpt-6.1-sol", 100, LLM), ("Heuristic rule", 82, REF)]
hdfs = [
    ("Decision-1", 86, DECISION),
    ("gpt-4.1-nano", 79, LLM),
    ("gpt-6.1-sol", 66, LLM),
    ("Rule: any exception", 71, REF),
    ("Trained classifier\n(50 labeled blocks)", 95, REF),
]

fig, axes = plt.subplots(1, 2, figsize=(12, 6.27), dpi=100, gridspec_kw={"width_ratios": [4, 5]})
fig.patch.set_facecolor("white")

for ax, title, data in (
    (axes[0], "Root-cause service\n(11 options)", rca),
    (axes[1], "HDFS log anomaly\n(enriched state, calibrated)", hdfs),
):
    labels, vals, colors = zip(*data)
    bars = ax.barh(labels[::-1], vals[::-1], color=colors[::-1], height=0.62)
    for bar, v in zip(bars, vals[::-1]):
        ax.text(v + 1.5, bar.get_y() + bar.get_height() / 2, f"{v}%", va="center", fontsize=15, fontweight="bold")
    ax.set_xlim(0, 112)
    ax.set_title(title, fontsize=16, fontweight="bold", loc="left", pad=14)
    ax.tick_params(axis="y", labelsize=13, length=0)
    ax.set_xticks([])
    for side in ("top", "right", "bottom"):
        ax.spines[side].set_visible(False)

fig.suptitle("Microsoft-Decision-1 on public AIOps data", fontsize=22, fontweight="bold", x=0.02, ha="left", y=0.98)
fig.text(
    0.02,
    0.015,
    "RCA: RCAEval RE2-OB, n=90. HDFS: Loghub HDFS_v1, n=1,200 balanced test blocks (gpt-6.1-sol n=100); threshold calibrated on held-out blocks.",
    fontsize=10,
    color="#605E5C",
)
fig.tight_layout(rect=(0, 0.04, 1, 0.93))
OUT.parent.mkdir(parents=True, exist_ok=True)
fig.savefig(OUT)
print(OUT)
