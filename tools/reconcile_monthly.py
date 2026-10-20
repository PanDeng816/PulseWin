# -*- coding: utf-8 -*-
"""reconcile_monthly.py — PulseWin 对账历史聚合（v1.22.3 配套）

输入：Data\\reconcile.jsonl（每次渲染「官方 vs 本机」对照卡追加一条）
输出：Data\\reconcile-monthly.md（人看）+ 屏幕摘要

做两件事：
1. 每个渠道 × 窗口，按天取**最后一条**读数（同一天多次打开用量窗只留最后一次）；
2. GOAT 按账期分组，给出每个账期末的「官方已用 / 本机记录 / 差值」——这就是
   「重置残留审计」要的分子：账期一切，账期末的已用量就是被清零的那部分。
   给了 --goat-quota（该账号月池的 token 上限）时，再算「剩余被作废」。

用法：
    python tools\\reconcile_monthly.py
    python tools\\reconcile_monthly.py --goat-quota 5000000000
    python tools\\reconcile_monthly.py --file D:\\...\\reconcile.jsonl
"""
import argparse
import json
import os
import re
import sys
from collections import defaultdict
from datetime import datetime

try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEFAULT_IN = os.path.join(ROOT, "Data", "reconcile.jsonl")
DEFAULT_OUT = os.path.join(ROOT, "Data", "reconcile-monthly.md")


def load(path):
    rows, bad = [], 0
    if not os.path.exists(path):
        return rows, bad
    with open(path, encoding="utf-8-sig", errors="replace") as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            try:
                r = json.loads(line)
            except json.JSONDecodeError:
                bad += 1
                continue
            try:
                r["dt"] = datetime.strptime(r["ts"], "%Y-%m-%dT%H:%M:%S")
            except Exception:
                bad += 1
                continue
            rows.append(r)
    rows.sort(key=lambda r: r["dt"])
    return rows, bad


def fmt(n):
    if n is None:
        return "—"
    for unit, div in (("亿", 1e8), ("万", 1e4)):
        if abs(n) >= div:
            return f"{n / div:.2f}{unit}"
    return f"{n:,}"


def main(argv=None):
    ap = argparse.ArgumentParser(description="PulseWin 对账历史聚合")
    ap.add_argument("--file", default=DEFAULT_IN)
    ap.add_argument("--out", default=DEFAULT_OUT)
    ap.add_argument("--goat-quota", type=float, default=0,
                    help="GOAT 账号月池的 token 上限（用于算重置时作废的额度；0=不计算）")
    args = ap.parse_args(argv)

    rows, bad = load(args.file)
    if not rows:
        print(f"没有可读记录：{args.file}")
        return 1

    days = sorted({r["dt"].date() for r in rows})
    lines = [
        "# PulseWin 对账历史月度聚合",
        "",
        f"> 数据源：`{os.path.basename(args.file)}`，{len(rows)} 条（坏行 {bad}），"
        f"覆盖 {days[0]} ~ {days[-1]}（{len(days)} 天）。",
        "> 口径：每个渠道 × 窗口按天取**最后一条**读数；GOAT 按账期分组，账期末的官方已用即「重置时被清零」的量。",
        "",
    ]

    # 1) 按天 × 渠道 × 窗口：当天最后一条
    by_day = defaultdict(dict)
    for r in rows:
        key = (r["channel"], r["window"])
        by_day[r["dt"].date()][key] = r
    lines += ["## 1. 每日末次读数", "",
              "| 日期 | 渠道 | 窗口 | 官方 | 本机 | 差值 | 判定 |",
              "|---|---|---|---:|---:|---:|---|"]
    for d in days:
        for (ch, win), r in sorted(by_day[d].items()):
            lines.append(
                f"| {d} | {ch} | {win} | {fmt(r['official'])} | {fmt(r['local'])} | "
                f"{fmt(r.get('diff'))} | {r.get('verdict', '')} |")
    lines.append("")

    # 2) GOAT 账期
    goat = [r for r in rows if r["channel"] == "goat"]
    lines += ["## 2. GOAT 账期（重置残留审计的分子）", ""]
    if not goat:
        lines.append("_本文件里没有 goat 记录。_")
    else:
        periods = defaultdict(list)
        for r in goat:
            m = re.search(r"账期自\s*(\S+)", r["window"] or "")
            periods[m.group(1) if m else "未知"].append(r)
        lines += ["| 账期起点 | 记录数 | 末次时间 | 官方已用 | 本机记录 | 差值 | 剩余（按配额） |",
                  "|---|---:|---|---:|---:|---:|---:|"]
        for start, items in sorted(periods.items()):
            last = items[-1]
            remain = ""
            if args.goat_quota > 0:
                left = max(0.0, args.goat_quota - last["official"])
                remain = f"{fmt(left)}（{left / args.goat_quota:.1%}）"
            lines.append(
                f"| {start} | {len(items)} | {last['dt']:%Y-%m-%d %H:%M} | "
                f"{fmt(last['official'])} | {fmt(last['local'])} | {fmt(last.get('diff'))} | {remain or '—'} |")
        if args.goat_quota <= 0:
            lines.append("")
            lines.append("_给了 `--goat-quota` 才算「剩余被作废」；没给时只能看账期末用了多少。_")
    lines.append("")

    # 3) 观察
    lines += ["## 3. 现在能看出的东西", ""]
    chans = sorted({r["channel"] for r in rows})
    lines.append(f"- 记录覆盖渠道：{'、'.join(chans)}。")
    if len(days) < 7:
        lines.append(f"- 只有 {len(days)} 天数据，**还不足以做月度对比**；"
                     "至少攒满一个 GOAT 账期（约 30 天）再看第 2 节。")
    else:
        lines.append(f"- 已有 {len(days)} 天，可看第 1 节的日曲线是否连续。")
    same = [r for r in rows if (r.get("verdict") or "") == "两边一致"]
    lines.append(f"- 「两边一致」占 {len(same)}/{len(rows)}；"
                 "判定为「他端用量」的记录说明官方含其他设备/CLI 的消耗，属正常。")

    os.makedirs(os.path.dirname(args.out), exist_ok=True)
    with open(args.out, "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(lines) + "\n")

    print(f"记录 {len(rows)} 条 / {len(days)} 天 / 坏行 {bad}")
    print(f"渠道：{'、'.join(chans)}")
    print(f"报告已写：{args.out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
