"""PulseWin 用量读取耗时体检（只读，不动任何数据）。

用途：用户报"打开用量窗要等很久"时先跑这个，量出各段成本再动手改。
2026-10-31 就是这样定位到 v1.26.7 的真凶——不是本地库太大（几万行全读 30 ms），
而是 DSH 的 688 个 zstd 会话文件每轮全量解压（5~9 秒）。

用法：python tools\\dsh_read_audit.py
"""

import glob
import os
import sqlite3
import sys
import time

# 控制台是 GBK 时中文会乱码；这里统一用英文标签，中文只出现在注释里
def ro(path):
    uri = "file:" + path.replace("\\", "/") + "?mode=ro"
    return sqlite3.connect(uri, uri=True, timeout=10)


def timeit(label, db, sql, repeat=3):
    if not os.path.exists(db):
        print(f"  {label:38s} MISSING {db}")
        return 0
    best, rows = None, None
    for _ in range(repeat):
        t = time.perf_counter()
        conn = ro(db)
        try:
            rows = conn.execute(sql).fetchall()
        finally:
            conn.close()
        ms = (time.perf_counter() - t) * 1000
        best = ms if best is None else min(best, ms)
    print(f"  {label:38s} rows={len(rows):>8,}  best={best:8.1f} ms")
    return best


def main():
    home = os.path.expanduser("~")
    project = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    data = os.path.join(project, "Data")

    print("== PulseWin repository (Data\\usage-history.sqlite) ==")
    repo = os.path.join(data, "usage-history.sqlite")
    if os.path.exists(repo):
        print(f"  file size {os.path.getsize(repo)/1048576:.1f} MB")
    timeit("SELECT key FROM seen", repo, "SELECT key FROM seen")
    timeit("SELECT * FROM agg_day", repo, "SELECT * FROM agg_day")
    timeit("SELECT * FROM agg_hour", repo, "SELECT * FROM agg_hour")

    print("\n== ZCode source (~/.zcode/cli/db/db.sqlite) ==")
    zc = os.path.join(home, ".zcode", "cli", "db", "db.sqlite")
    if os.path.exists(zc):
        print(f"  file size {os.path.getsize(zc)/1048576:.1f} MB")
    timeit("model_usage (18 cols)", zc,
           "SELECT id, session_id, model_id, provider_id, started_at, completed_at, input_tokens,"
           " output_tokens, reasoning_tokens, cache_read_input_tokens, cache_creation_input_tokens,"
           " computed_total_tokens, duration_ms, time_to_first_token_ms, tool_call_count,"
           " retry_count, status FROM model_usage")
    timeit("session (id,parent,directory,path,title)", zc,
           "SELECT id, parent_id, directory, path, title FROM session")

    print("\n== OpenCode source (~/.local/share/opencode/opencode.db) ==")
    oc = os.path.join(home, ".local", "share", "opencode", "opencode.db")
    if os.path.exists(oc):
        print(f"  file size {os.path.getsize(oc)/1048576:.1f} MB")
    timeit("message (5 cols)", oc, "SELECT id, session_id, time_created, data, NULL FROM message")

    print("\n== DSH sessions (~/.dsh/sessions) ==")
    root = os.path.join(home, ".dsh", "sessions")
    files = []
    if os.path.isdir(root):
        for ws in os.listdir(root):
            wdir = os.path.join(root, ws)
            if not os.path.isdir(wdir):
                continue
            for s in os.listdir(wdir):
                sdir = os.path.join(wdir, s)
                if not os.path.isdir(sdir):
                    continue
                for f in glob.glob(os.path.join(sdir, "session.v*.jsonl.zstd")):
                    st = os.stat(f)
                    files.append((st.st_size, st.st_mtime, ws))
    total = sum(f[0] for f in files)
    print(f"  {len(files)} session files, {total/1048576:.1f} MB")
    print("  NOTE: the reader decompresses ALL of these on a cache miss; "
          "measured 5-9 s for 688 files / 216 MB.")

    now = time.time()
    buckets = [("<1d", 0, 0), ("1-7d", 0, 0), ("7-30d", 0, 0), (">30d", 0, 0)]
    for size, mtime, _ws in files:
        age = (now - mtime) / 86400
        idx = 0 if age < 1 else 1 if age < 7 else 2 if age < 30 else 3
        n, b = buckets[idx][1], buckets[idx][2]
        buckets[idx] = (buckets[idx][0], n + 1, b + size)
    for label, n, b in buckets:
        print(f"    {label:6s} {n:5d} files  {b/1048576:7.1f} MB")

    print("\n== DSH parse cache (Data\\dsh-parse-cache.bin) ==")
    cache = os.path.join(data, "dsh-parse-cache.bin")
    if os.path.exists(cache):
        st = os.stat(cache)
        print(f"  {st.st_size/1048576:.2f} MB, written {time.strftime('%Y-%m-%d %H:%M', time.localtime(st.st_mtime))}")
        print(f"  about {st.st_size/max(1, len(files))/1024:.1f} KB per session file")
    else:
        print("  MISSING - next --dsh/--spend will do a full decompress and write it")

    print("\n== how to verify ==")
    print("  PulseWin.exe --dsh     -> Data\\dsh-dump.txt shows cold/warm ms + cache hits/misses")
    print("  PulseWin.exe --spend   -> full ledger build timing (new process each time)")
    print("  healthy: hits == files on the second run; misses only for sessions written since.")


if __name__ == "__main__":
    main()
