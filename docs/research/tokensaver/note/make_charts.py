"""Generate the two SVG figures for the tokens-saved technical note.

Standard library only. Reads ../daily-series.csv (the published daily aggregates) and
writes charts/daily-rates.svg and charts/counted-token-value.svg. Also prints the
period summary used in the note's Table 3 so the numbers can be checked against the figure.
"""
from __future__ import annotations

import csv
from datetime import date, timedelta
from pathlib import Path

HERE = Path(__file__).resolve().parent
SERIES = HERE.parent / "daily-series.csv"
OUT = HERE / "charts"

# Palette: dataviz reference slots 1-3 (validated all-pairs on a white surface).
BLUE, ORANGE, AQUA = "#2a78d6", "#eb6834", "#1baf7a"
INK, INK2, MUTED = "#0b0b0b", "#52514e", "#898781"
GRID, AXIS, BAND, SURFACE = "#e1e0d9", "#c3c2b7", "#f1f0ec", "#ffffff"
FONT = "'Segoe UI', system-ui, sans-serif"

START, END = date(2026, 7, 29), date(2026, 10, 1)
NDAYS = (END - START).days  # 64 -> 65 days inclusive


def load_series():
    rows = {"claude": {}, "codex": {}}
    with SERIES.open(newline="", encoding="utf-8") as fh:
        for r in csv.DictReader(fh):
            if not r["requests"]:
                continue
            d = date.fromisoformat(r["date"])
            rows[r["agent"]][d] = (float(r["before_MB"]), float(r["removed_MB"]), float(r["reported_percent"]))
    return rows


def period_summary(rows):
    periods = [
        ("29 Jul - 16 Aug", date(2026, 7, 29), date(2026, 8, 16)),
        ("17 Aug - 29 Aug", date(2026, 8, 17), date(2026, 8, 29)),
        ("30 Aug - 3 Sep", date(2026, 8, 30), date(2026, 9, 3)),
        ("4 Sep - 1 Oct", date(2026, 9, 4), date(2026, 10, 1)),
    ]
    out = []
    for label, a, b in periods:
        line = [label]
        for agent in ("codex", "claude"):
            vals = [v for d, v in rows[agent].items() if a <= d <= b]
            before = sum(v[0] for v in vals)
            removed = sum(v[1] for v in vals)
            line.append((removed, before, 100 * removed / before if before else 0.0))
        out.append(line)
    return out


def esc(text: str) -> str:
    return text.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")


def text(x, y, s, size=10, fill=MUTED, anchor="start", weight=400, extra=""):
    return (f'<text x="{x:.1f}" y="{y:.1f}" font-size="{size}" fill="{fill}" '
            f'text-anchor="{anchor}" font-weight="{weight}" {extra}>{esc(s)}</text>')


def daily_chart(rows) -> str:
    W, H = 660, 392
    x0, x1 = 58, 646
    dx = (x1 - x0) / NDAYS

    def X(d: date) -> float:
        return x0 + (d - START).days * dx

    panels = [
        # agent, title, top, height, ymax, ticks, colour, recent share label
        ("codex", "Codex CLI", 30, 150, 30.0, [0, 10, 20, 30], ORANGE, "Gray rule: 3.39% of bytes, 4 Sep – 1 Oct"),
        ("claude", "Claude Code", 236, 112, 4.0, [0, 1, 2, 3, 4], BLUE, "Gray rule: 0.19% of bytes, 4 Sep – 1 Oct"),
    ]
    recent = {"codex": 3.39, "claude": 0.19}
    parts = [f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {W} {H}" width="{W}" height="{H}" '
             f'font-family="{FONT}" role="img" aria-label="Daily share of request bytes removed by TokenSaver">',
             f'<rect width="{W}" height="{H}" fill="{SURFACE}"/>']

    for agent, title, top, height, ymax, ticks, colour, ref_label in panels:
        bottom = top + height

        def Y(v: float) -> float:
            return bottom - (min(v, ymax) / ymax) * height

        # Shaded regimes (drawn first, under everything).
        bands = [(date(2026, 8, 17), date(2026, 8, 29), ["File reads truncated", "(mitigation commit 30 Aug)"])]
        if agent == "codex":
            bands.insert(0, (date(2026, 8, 1), date(2026, 8, 16), ["Codex exec output", "not yet eligible"]))
        for a, b, label in bands:
            bx0, bx1 = X(a) - dx / 2, X(b) + dx / 2
            parts.append(f'<rect x="{bx0:.1f}" y="{top}" width="{bx1 - bx0:.1f}" height="{height}" fill="{BAND}"/>')
            for i, line in enumerate(label):
                parts.append(text((bx0 + bx1) / 2, top + 12 + 11 * i, line, size=9, fill=INK2, anchor="middle"))

        # Gridlines and y ticks.
        for t in ticks:
            y = Y(t)
            colour_line = AXIS if t == 0 else GRID
            parts.append(f'<line x1="{x0}" x2="{x1}" y1="{y:.1f}" y2="{y:.1f}" stroke="{colour_line}" stroke-width="1"/>')
            parts.append(text(x0 - 8, y + 3.5, f"{t}%", size=10, anchor="end", extra='style="font-variant-numeric: tabular-nums"'))

        # Panel title (the single series is named by the title; no legend box needed).
        parts.append(f'<line x1="{x0}" x2="{x0 + 14}" y1="{top - 11}" y2="{top - 11}" stroke="{colour}" stroke-width="2.5" stroke-linecap="round"/>')
        parts.append(text(x0 + 20, top - 7, title, size=11.5, fill=INK, weight=600))
        # Key for the reference rule, kept in the header row so it never sits on the data.
        parts.append(text(x1, top - 7, ref_label, size=9, fill=INK2, anchor="end"))

        # Series line, broken at missing days.
        series = rows[agent]
        segment: list[str] = []
        segments: list[list[str]] = []
        for i in range(NDAYS + 1):
            d = START + timedelta(days=i)
            if d in series:
                segment.append(f"{X(d):.1f},{Y(series[d][2]):.1f}")
            elif segment:
                segments.append(segment)
                segment = []
        if segment:
            segments.append(segment)
        for seg in segments:
            if len(seg) == 1:
                cx, cy = seg[0].split(",")
                parts.append(f'<circle cx="{cx}" cy="{cy}" r="2" fill="{colour}"/>')
            else:
                parts.append(f'<polyline points="{" ".join(seg)}" fill="none" stroke="{colour}" '
                             f'stroke-width="2" stroke-linejoin="round" stroke-linecap="round"/>')

        # Reference line: byte-weighted share over the meter window.
        rx0, rx1 = X(date(2026, 9, 4)) - dx / 2, X(END) + dx / 2
        ry = Y(recent[agent])
        parts.append(f'<line x1="{rx0:.1f}" x2="{rx1:.1f}" y1="{ry:.1f}" y2="{ry:.1f}" stroke="{INK2}" stroke-width="1"/>')

    # Shared x axis under the lower panel.
    for d, label in [(date(2026, 8, 1), "1 Aug"), (date(2026, 8, 15), "15 Aug"), (date(2026, 9, 1), "1 Sep"),
                     (date(2026, 9, 15), "15 Sep"), (date(2026, 10, 1), "1 Oct")]:
        x = X(d)
        parts.append(f'<line x1="{x:.1f}" x2="{x:.1f}" y1="348" y2="352" stroke="{AXIS}" stroke-width="1"/>')
        parts.append(text(x, 366, label, size=10, anchor="middle"))
    parts.append(text((x0 + x1) / 2, 386, "Day (UTC), 29 July – 1 October 2026; 1 October is a partial day", size=9.5, anchor="middle"))
    parts.append("</svg>")
    return "\n".join(parts)


def value_chart() -> str:
    W, H = 660, 318
    x0, x1 = 58, 646
    top, bottom = 18, 252
    mmin, mmax, ymax = 1.0, 50.0, 2.1
    alpha = 0.1

    def X(m: float) -> float:
        return x0 + (m - mmin) / (mmax - mmin) * (x1 - x0)

    def Y(v: float) -> float:
        return bottom - v / ymax * (bottom - top)

    parts = [f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {W} {H}" width="{W}" height="{H}" '
             f'font-family="{FONT}" role="img" aria-label="Avoided cost per actual token appearance as a function of re-sends">',
             f'<rect width="{W}" height="{H}" fill="{SURFACE}"/>']
    for t in [0, 0.5, 1.0, 1.5, 2.0]:
        y = Y(t)
        parts.append(f'<line x1="{x0}" x2="{x1}" y1="{y:.1f}" y2="{y:.1f}" stroke="{AXIS if t == 0 else GRID}" stroke-width="1"/>')
        parts.append(text(x0 - 8, y + 3.5, f"{t:.1f}", size=10, anchor="end", extra='style="font-variant-numeric: tabular-nums"'))
    for m in [1, 10, 20, 30, 40, 50]:
        x = X(m)
        parts.append(f'<line x1="{x:.1f}" x2="{x:.1f}" y1="{bottom}" y2="{bottom + 4}" stroke="{AXIS}" stroke-width="1"/>')
        parts.append(text(x, bottom + 17, str(m), size=10, anchor="middle"))
    parts.append(text((x0 + x1) / 2, bottom + 36, "m = number of requests that carry the removed block (first appearance included)", size=9.5, anchor="middle"))
    parts.append(text(16, (top + bottom) / 2, "Cost / actual token appearance (x base input price)", size=9.5,
                      anchor="middle", extra=f'transform="rotate(-90 16 {(top + bottom) / 2:.1f})"'))

    # Reference lines: base input price and the limit alpha.
    parts.append(f'<line x1="{x0}" x2="{x1}" y1="{Y(1.0):.1f}" y2="{Y(1.0):.1f}" stroke="{INK2}" stroke-width="1"/>')
    parts.append(text(x1, Y(1.0) - 5, "1.0 = base input price (what a list-price conversion assumes)", size=9, fill=INK2, anchor="end"))
    # The limit rule is explained in the figure caption; a label here would sit on the curves.
    parts.append(f'<line x1="{x0}" x2="{x1}" y1="{Y(alpha):.1f}" y2="{Y(alpha):.1f}" stroke="{MUTED}" stroke-width="1"/>')

    curves = [  # slot order: blue, orange, aqua
        (1.25, BLUE, "w = 1.25  Anthropic 5-minute cache write"),
        (1.00, ORANGE, "w = 1.00  no write premium (e.g. OpenAI)"),
        (2.00, AQUA, "w = 2.00  Anthropic 1-hour cache write"),
    ]
    for w, colour, _ in curves:
        pts = []
        m = mmin
        while m <= mmax + 1e-9:
            pts.append(f"{X(m):.1f},{Y((w + alpha * (m - 1)) / m):.1f}")
            m += 0.25
        parts.append(f'<polyline points="{" ".join(pts)}" fill="none" stroke="{colour}" stroke-width="2" '
                     f'stroke-linejoin="round" stroke-linecap="round"/>')
        parts.append(f'<circle cx="{X(1):.1f}" cy="{Y(w):.1f}" r="4" fill="{colour}" stroke="{SURFACE}" stroke-width="2"/>')
        # No per-curve direct labels: the curves converge, so labels would collide. The legend
        # and Table 4 (the table view) carry identity and values, which also covers the
        # low-contrast aqua slot.

    # Legend (always present for two or more series).
    lx, ly = 300, 46
    for i, (_, colour, label) in enumerate(curves):
        y = ly + i * 16
        parts.append(f'<line x1="{lx}" x2="{lx + 18}" y1="{y:.1f}" y2="{y:.1f}" stroke="{colour}" stroke-width="2.5" stroke-linecap="round"/>')
        parts.append(text(lx + 25, y + 3.5, label, size=9.5, fill=INK2))
    parts.append(text(lx, ly - 14, "Cache-read multiplier α = 0.1 in all three curves", size=9, fill=MUTED))
    parts.append("</svg>")
    return "\n".join(parts)


def main() -> None:
    rows = load_series()
    OUT.mkdir(exist_ok=True)
    (OUT / "daily-rates.svg").write_text(daily_chart(rows), encoding="utf-8")
    (OUT / "counted-token-value.svg").write_text(value_chart(), encoding="utf-8")
    print("Period summary (rounded daily rows; removed MB / before MB / share):")
    for label, codex, claude in period_summary(rows):
        print(f"  {label:16s} Codex {codex[0]:8.2f} / {codex[1]:9.1f} = {codex[2]:6.2f}%   "
              f"Claude {claude[0]:6.2f} / {claude[1]:8.1f} = {claude[2]:5.2f}%")
    print("v(m) table:")
    for m in (1, 5, 10, 20, 30, 40, 50):
        vals = "  ".join(f"w={w:g}: {(w + 0.1 * (m - 1)) / m:.3f}" for w in (1.25, 1.0, 2.0))
        print(f"  m={m:2d}  {vals}")


if __name__ == "__main__":
    main()
