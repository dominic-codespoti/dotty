#!/usr/bin/env python3
import re
import sys
from collections import defaultdict, Counter

log_path = sys.argv[1] if len(sys.argv) > 1 else 'logs/blit.log'

mark_re = re.compile(r"\[MarkRowDirty\] row=(\d+) version=(\d+)")
scroll_re = re.compile(r"\[ScrollGen\] (.*?) -> (\d+)")
lf_re = re.compile(r"\[LF\]")
setcursor_re = re.compile(r"\[SetCursor\] .*in=\((\d+),(\d+)\)")

rows_count = Counter()
rows_max_ver = defaultdict(int)
scroll_events = []
lf_count = 0
setcursor_count = 0

# Detect long repeated bursts: consecutive MarkRowDirty for same row
bursts = []
last_row = None
burst_len = 0

with open(log_path, 'r', errors='replace') as f:
    for i, line in enumerate(f, 1):
        m = mark_re.search(line)
        if m:
            r = int(m.group(1))
            v = int(m.group(2))
            rows_count[r] += 1
            if v > rows_max_ver[r]:
                rows_max_ver[r] = v
            if last_row is None:
                last_row = r
                burst_len = 1
            elif r == last_row:
                burst_len += 1
            else:
                if burst_len > 1:
                    bursts.append((last_row, burst_len))
                last_row = r
                burst_len = 1
            continue
        s = scroll_re.search(line)
        if s:
            reason = s.group(1).strip()
            gen = int(s.group(2))
            scroll_events.append((i, reason, gen))
            continue
        if lf_re.search(line):
            lf_count += 1
            continue
        sc = setcursor_re.search(line)
        if sc:
            setcursor_count += 1
            continue

# finalize last burst
if last_row is not None and burst_len > 1:
    bursts.append((last_row, burst_len))

# produce summary
out_lines = []
out_lines.append(f'Parsed log: {log_path}')
out_lines.append(f'Total distinct rows seen in MarkRowDirty: {len(rows_count)}')
out_lines.append('Top 10 rows by MarkRowDirty count:')
for r, c in rows_count.most_common(10):
    out_lines.append(f'  row={r} count={c} max_version={rows_max_ver[r]}')

out_lines.append(f'Number of ScrollGen events: {len(scroll_events)}')
if scroll_events:
    out_lines.append('Recent ScrollGen events (line,reason,gen):')
    for ev in scroll_events[-10:]:
        out_lines.append(f'  {ev[0]} | {ev[1]} -> {ev[2]}')

out_lines.append(f'LineFeeds seen: {lf_count}')
out_lines.append(f'SetCursor events seen: {setcursor_count}')

out_lines.append(f'Number of MarkRowDirty bursts (consecutive same-row writes): {len(bursts)}')
if bursts:
    out_lines.append('Top bursts:')
    bursts_sorted = sorted(bursts, key=lambda x: x[1], reverse=True)
    for r, l in bursts_sorted[:10]:
        out_lines.append(f'  row={r} consecutive_marks={l}')

summary = '\n'.join(out_lines)
print(summary)

with open('logs/terminalbuffer_summary.txt', 'w') as out:
    out.write(summary + '\n')

# Also write a CSV of per-row counts
with open('logs/markrow_counts.csv', 'w') as csvf:
    csvf.write('row,count,max_version\n')
    for r in sorted(rows_count):
        csvf.write(f'{r},{rows_count[r]},{rows_max_ver[r]}\n')

print('\nWrote logs/terminalbuffer_summary.txt and logs/markrow_counts.csv')
