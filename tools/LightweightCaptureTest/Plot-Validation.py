"""Plot paired root-process GPU counters; never turn unavailable values into zero."""
import argparse
import csv
import json
from pathlib import Path

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt


def value(row, key):
    return float(row[key]) if row.get(key, "") != "" else None


def slope(rows, key):
    points = [(int(r["cycle"]), value(r, key)) for r in rows]
    points = [(x, y) for x, y in points if y is not None]
    if len(points) < 2:
        return None
    xm = sum(x for x, _ in points) / len(points)
    ym = sum(y for _, y in points) / len(points)
    return sum((x - xm) * (y - ym) for x, y in points) / sum((x - xm) ** 2 for x, _ in points)


def analyze(rows):
    capture = [r for r in rows if r["phase"] == "after5s"]
    active = [r for r in rows if r["phase"] == "offscreen_overlay"]
    result = {"pid": int(rows[0]["pid"]), "cycles": len(capture), "phases": {}}
    for r in rows:
        if int(r["cycle"]) == 0:
            result["phases"][r["phase"]] = {k: value(r, k) for k in (
                "dedicatedResidentMiB", "dedicatedCommittedMiB", "sharedResidentMiB", "privateMiB")}
    for key in ("dedicatedResidentMiB", "dedicatedCommittedMiB", "privateMiB"):
        samples = [value(r, key) for r in capture]
        valid = [v for v in samples if v is not None]
        peaks = [value(r, key) for r in capture + active]
        peaks = [v for v in peaks if v is not None]
        result[key] = {"first": samples[0], "last": samples[-1], "minimum": min(valid),
                       "maximum": max(valid), "peakActiveOrAfter": max(peaks),
                       "netGrowth": samples[-1] - samples[0], "slopePerCycle": slope(capture, key)}
    result["counterQueryFailures"] = sum(int(r["queryFailures"]) for r in rows)
    result["postCaptureHandles"] = {k: [int(capture[0][k]), int(capture[-1][k])] for k in ("gdiHandles", "userHandles")}
    return result


parser = argparse.ArgumentParser()
parser.add_argument("--series", action="append", required=True, help="Label=CSV")
parser.add_argument("--output", type=Path, required=True)
args = parser.parse_args()
args.output.mkdir(parents=True, exist_ok=True)
datasets = []
for item in args.series:
    label, path = item.split("=", 1)
    with open(path, encoding="utf-8-sig", newline="") as stream:
        rows = list(csv.DictReader(stream))
    datasets.append((label, rows))

plt.rcParams.update({"font.family": "DejaVu Sans", "font.size": 10,
                     "axes.spines.top": False, "axes.spines.right": False,
                     "axes.grid": True, "grid.alpha": .16, "figure.facecolor": "#fafbf7",
                     "axes.facecolor": "#fafbf7", "savefig.facecolor": "#fafbf7"})
colors = ["#9a7771", "#506c48", "#4086a1"]
fig, ax = plt.subplots(figsize=(10, 5.2), layout="constrained")
for i, (label, rows) in enumerate(datasets):
    points = [r for r in rows if r["phase"] == "after5s"]
    for key, style, kind in [("dedicatedResidentMiB", "-", "resident"),
                             ("dedicatedCommittedMiB", "--", "committed")]:
        ax.plot([int(r["cycle"]) for r in points], [value(r, key) for r in points],
                style, color=colors[i % len(colors)], lw=2, label=f"{label}: {kind}")
    active = [r for r in rows if r["phase"] == "offscreen_overlay"]
    ax.scatter([int(r["cycle"]) for r in active], [value(r, "dedicatedResidentMiB") for r in active],
               color=colors[i % len(colors)], marker=".", s=20, alpha=.6)
ax.axhline(120, color="#688b49", lw=1, label="Preferred budget: 120 MiB")
ax.axhline(150, color="#777d72", lw=1, ls=":", label="Upper budget: 150 MiB")
ax.set(xlabel="Synthetic 4K cycle (5 s after close)", ylabel="Root-process dedicated GPU memory (MiB)",
       title="Ordinary lightweight capture: resident and committed", xlim=(.5, 20.5), ylim=(0, None))
ax.set_xticks([1, 5, 10, 15, 20])
ax.legend(fontsize=8, ncol=2)
fig.savefig(args.output / "dedicated-vram.png", dpi=180)
fig.savefig(args.output / "dedicated-vram.svg")
plt.close(fig)

phases = ["hidden_startup", "offscreen_webui_loaded", "webui_released5s",
          "offscreen_webui_reopened", "reopened_webui_released5s", "ocr_webui_released5s"]
labels = ["Tray start", "WebUI open", "WebUI closed\n+5 s", "WebUI reopen", "Closed again\n+5 s", "OCR closed\n+5 s"]
fig, ax = plt.subplots(figsize=(10, 5), layout="constrained")
for i, (label, rows) in enumerate(datasets):
    by_phase = {r["phase"]: r for r in rows if int(r["cycle"]) == 0}
    y = [value(by_phase[p], "dedicatedResidentMiB") if p in by_phase else float("nan") for p in phases]
    ax.plot(range(len(phases)), y, "o-", lw=2, color=colors[i % len(colors)], label=label)
ax.axhline(120, color="#688b49", lw=1, ls=":")
ax.set_xticks(range(len(phases)), labels)
ax.set(ylabel="Root-process dedicated GPU memory (MiB)", title="Main/OCR GUI lifecycle", ylim=(0, None))
ax.legend(fontsize=9)
fig.savefig(args.output / "gui-lifecycle.png", dpi=180)
plt.close(fig)
(args.output / "summary.json").write_text(json.dumps({label: analyze(rows) for label, rows in datasets},
                                                     indent=2), encoding="utf-8")
