#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""PulseWin 出图回归比对。

用途:改版后重跑 --ui-shot / --card-shot / --rail-shot,与上一版留存的出图目录逐张比像素,
在没有人工肉眼核对的情况下先发现"排版跑版/文字裁切/整体变黑"这类视觉回归。

用法:
    python tools/ui_diff.py <基线目录> <当前目录> [-o report.md] [--threshold 0.001]

判定:
    尺寸不同     -> FAIL(布局或窗口尺寸变了)
    非零像素比例 > threshold -> CHANGED(有改动,需目视确认)
    其余         -> SAME

退出码:0 = 无 FAIL;1 = 有 FAIL。
依赖:Pillow(本机已装)。
"""
import argparse
import os
import sys

try:
    from PIL import Image, ImageChops
except ImportError:
    print("需要 Pillow: python -m pip install pillow")
    sys.exit(2)


def compare(baseline, current):
    a = Image.open(baseline).convert("RGBA")
    b = Image.open(current).convert("RGBA")
    if a.size != b.size:
        return "FAIL", f"尺寸 {a.size} → {b.size}", 1.0
    diff = ImageChops.difference(a, b)
    bbox = diff.getbbox()
    if bbox is None:
        return "SAME", "完全一致", 0.0
    # 统计差异像素比例
    gray = diff.convert("L")
    hist = gray.histogram()
    changed = sum(hist[1:])
    total = a.size[0] * a.size[1]
    ratio = changed / total
    # 最大差异区域给个大致位置
    return "CHANGED", f"差异 {ratio:.4%}(首处 {bbox})", ratio


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("baseline")
    ap.add_argument("current")
    ap.add_argument("-o", "--out", default=None)
    ap.add_argument("--threshold", type=float, default=0.001)
    args = ap.parse_args()

    names = sorted(f for f in os.listdir(args.baseline) if f.lower().endswith(".png"))
    rows, fail = [], 0
    for name in names:
        bp = os.path.join(args.baseline, name)
        cp = os.path.join(args.current, name)
        if not os.path.exists(cp):
            rows.append((name, "MISSING", "当前目录没有同名出图", 1.0))
            fail += 1
            continue
        verdict, note, ratio = compare(bp, cp)
        if verdict == "CHANGED" and ratio <= args.threshold:
            verdict = "SAME"
        if verdict == "FAIL":
            fail += 1
        rows.append((name, verdict, note, ratio))

    print(f"{'文件':42s} {'判定':8s} 说明")
    for name, verdict, note, _ in rows:
        print(f"{name:42s} {verdict:8s} {note}")

    if args.out:
        lines = ["# PulseWin 出图比对", "",
                 f"- 基线：`{args.baseline}`", f"- 当前：`{args.current}`",
                 f"- 阈值：{args.threshold:.4%}（低于此比例视为无变化）", "",
                 "| 文件 | 判定 | 说明 |", "| --- | --- | --- |"]
        for name, verdict, note, _ in rows:
            lines.append(f"| {name} | {verdict} | {note} |")
        lines.append("")
        lines.append(f"结论：{len(rows)} 张，FAIL {fail} 张。")
        open(args.out, "w", encoding="utf-8", newline="\n").write("\n".join(lines))
        print(f"\n报告已写 {args.out}")

    print(f"\n{len(rows)} 张，FAIL {fail} 张")
    return 1 if fail else 0


if __name__ == "__main__":
    sys.exit(main())
