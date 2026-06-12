#!/usr/bin/env python3
"""Temporal envelope character: crest of the 10 ms RMS envelope.
Crackle/impulses → spiky (high p95/median); sustained ringing → smooth (≈1)."""
import sys, wave, struct, math

for path in sys.argv[1:]:
    w = wave.open(path, 'rb')
    n, ch, sr = w.getnframes(), w.getnchannels(), w.getframerate()
    raw = w.readframes(n); w.close()
    s = struct.unpack(f'<{n*ch}h', raw)
    if ch == 2:
        s = [(s[i]+s[i+1])/2 for i in range(0, len(s), 2)]
    x = [v/32768.0 for v in s]
    win = int(0.010 * sr)
    env = []
    for s0 in range(sr, len(x)-win, win):  # skip first second (attack)
        seg = x[s0:s0+win]
        env.append(math.sqrt(sum(v*v for v in seg)/win))
    env.sort()
    med = env[len(env)//2] + 1e-9
    p95 = env[int(len(env)*0.95)]
    print(f"{path.split('/')[-1]:32s} envelope p95/median={p95/med:5.2f}")
