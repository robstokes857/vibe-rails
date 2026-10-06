"""Build only the reconciled 0.3 figures from packaged aggregates (requires matplotlib).

Run from any directory: python docs/research/tokensaver/build_review_figures.py
No private sources, database access, or manuscript generation.
"""
import csv
import json
from datetime import datetime
from pathlib import Path

import matplotlib

matplotlib.use("Agg")
import matplotlib.dates as mdates
import matplotlib.pyplot as plt

ROOT = Path(__file__).resolve().parent
OUT = ROOT / "figures" / "v0.3"
NAVY, TEAL, BLUE, GOLD = "#17324d", "#087f8c", "#407bb0", "#b78023"


def save(fig, name):
    for extension in ("png", "svg"):
        fig.savefig(OUT / f"{name}.{extension}", dpi=180, bbox_inches="tight")
    plt.close(fig)


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    plt.rcParams.update({"font.family": "DejaVu Sans", "font.size": 10,
                         "axes.spines.top": False, "axes.spines.right": False,
                         "text.color": NAVY, "axes.labelcolor": NAVY,
                         "axes.titleweight": "bold", "svg.fonttype": "none"})
    data = json.loads((ROOT / "evidence-v0.3.json").read_text(encoding="utf-8"))
    rows = data["study_a_reported_mixed_units"]
    fig, axes = plt.subplots(1, 2, figsize=(10, 4.4), layout="constrained")
    for ax, key, title, ylabel, color in zip(
        axes,
        ("reported_meter_tally_bytes", "deduplicated_removed_characters"),
        ("Reported meter tally", "Distinct removed content"),
        ("Million bytes", "Million decoded characters"),
        (BLUE, TEAL),
    ):
        values = [r[key] / 1e6 for r in rows]
        bars = ax.bar(["Claude\nAug 28", "Codex\nAug 28-29"], values, color=color, width=.55)
        ax.bar_label(bars, fmt="%.3f", padding=4)
        ax.set(title=title, ylabel=ylabel, ylim=(0, max(values) * 1.24))
        ax.grid(axis="y", alpha=.2)
        ax.set_axisbelow(True)
    fig.suptitle("Study A uses different units; quotients are not replay counts", fontsize=13)
    save(fig, "01-study-a-units")

    with (ROOT / "daily-series.csv").open(newline="", encoding="utf-8") as stream:
        daily = list(csv.DictReader(stream))
    fig, axes = plt.subplots(2, 1, figsize=(11, 6), sharex=True, layout="constrained")
    for ax, agent, color in zip(axes, ("claude", "codex"), (TEAL, BLUE)):
        selected = [r for r in daily if r["agent"] == agent]
        dates = [datetime.fromisoformat(r["date"]) for r in selected]
        values = [float(r["reported_percent"]) if r["reported_percent"] else float("nan")
                  for r in selected]
        ax.plot(dates, values, color=color, marker=".", markersize=4, linewidth=1.4)
        ax.set(ylabel="Reported reduction (%)", title=agent.title())
        ax.set_ylim(bottom=0)
        for date, label in (("2026-08-18", "Historical allowlist annotation"),
                            ("2026-08-30", "File-read mitigation commit")):
            ax.axvline(datetime.fromisoformat(date), color=GOLD, linestyle="--", linewidth=1)
        ax.grid(axis="y", alpha=.2)
    axes[0].annotate("Allowlist annotation", (datetime(2026, 8, 18), 1.7),
                     xytext=(-5, 8), textcoords="offset points", ha="right", fontsize=8)
    axes[0].annotate("Aug 30 mitigation commit", (datetime(2026, 8, 30), 1.7),
                     xytext=(5, 8), textcoords="offset points", ha="left", fontsize=8)
    axes[-1].xaxis.set_major_locator(mdates.WeekdayLocator(interval=1))
    axes[-1].xaxis.set_major_formatter(mdates.DateFormatter("%b %d"))
    fig.suptitle("Daily meter rates, July 29 - October 1, 2026\n"
                 "Different panel scales; October 1 partial; commit date is not deployment evidence",
                 fontsize=12)
    save(fig, "04-daily-series")

    fig, ax = plt.subplots(figsize=(8, 4.3), layout="constrained")
    q = [i / 100 for i in range(101)]
    for ratio, color in ((1, TEAL), (2, BLUE), (4, GOLD)):
        ax.plot(q, [1 - v * ratio for v in q], label=f"H/G = {ratio}", color=color)
    ax.axhline(0, color=NAVY, linewidth=.8)
    ax.set(xlabel="Causal probability of additional recovery, q (assumed)",
           ylabel="Normalized monetary net saving", xlim=(0, 1),
           title="Hypothetical recovery sensitivity: 1 - q(H/G), K = 0")
    ax.legend(title="Assumed recovery cost / gross benefit")
    ax.grid(alpha=.2)
    save(fig, "06-break-even")


if __name__ == "__main__":
    main()
