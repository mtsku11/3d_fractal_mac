#!/usr/bin/env python3
"""Quick spectral diagnostics for Track D listening WAVs.

For each WAV: spectral flatness (0=tonal, 1=white noise), spectral centroid,
harmonicity (normalized autocorrelation peak in the pitch range), and
activity ratio (fraction of 50 ms windows above -40 dBFS).
"""
import sys, wave, struct, math

def read_wav(path):
    w = wave.open(path, 'rb')
    n, ch, sr = w.getnframes(), w.getnchannels(), w.getframerate()
    raw = w.readframes(n)
    w.close()
    s = struct.unpack(f'<{n*ch}h', raw)
    # mono mixdown
    if ch == 2:
        s = [(s[i] + s[i+1]) / 2 for i in range(0, len(s), 2)]
    return [x / 32768.0 for x in s], sr

def fft(x):
    n = len(x)
    if n == 1:
        return x
    ev = fft(x[0::2]); od = fft(x[1::2])
    out = [0j] * n
    for k in range(n // 2):
        t = complex(math.cos(-2*math.pi*k/n), math.sin(-2*math.pi*k/n)) * od[k]
        out[k] = ev[k] + t
        out[k + n//2] = ev[k] - t
    return out

def analyze(path):
    x, sr = read_wav(path)
    N = 4096
    hop = sr  # one window per second
    flat_vals, cent_vals, harm_vals = [], [], []
    for start in range(sr, len(x) - N, hop):
        seg = [x[start+i] * (0.5 - 0.5*math.cos(2*math.pi*i/N)) for i in range(N)]
        rms = math.sqrt(sum(v*v for v in seg) / N)
        if rms < 1e-4:
            continue
        sp = fft([complex(v, 0) for v in seg])
        mag = [abs(sp[k]) for k in range(1, N//2)]
        p = [m*m + 1e-20 for m in mag]
        logmean = math.exp(sum(math.log(v) for v in p) / len(p))
        arimean = sum(p) / len(p)
        flat_vals.append(logmean / arimean)
        total = sum(mag)
        cent_vals.append(sum((k+1) * sr / N * m for k, m in enumerate(mag)) / total)
        # harmonicity: max normalized autocorr for lags 40 Hz..1 kHz
        raw = x[start:start+N]
        mean = sum(raw)/N
        raw = [v - mean for v in raw]
        e0 = sum(v*v for v in raw) + 1e-12
        best = 0.0
        lag = int(sr/1000)
        maxlag = int(sr/40)
        while lag <= maxlag:
            ac = sum(raw[i]*raw[i+lag] for i in range(0, N-lag, 4))
            en = sum(raw[i+lag]*raw[i+lag] for i in range(0, N-lag, 4)) + 1e-12
            e0s = sum(raw[i]*raw[i] for i in range(0, N-lag, 4)) + 1e-12
            best = max(best, ac / math.sqrt(en*e0s))
            lag = max(lag+1, int(lag*1.06))
        harm_vals.append(best)
    # activity: 50 ms windows above -40 dBFS
    win = int(0.05 * sr)
    act = above = 0
    for s0 in range(0, len(x)-win, win):
        seg = x[s0:s0+win]
        r = math.sqrt(sum(v*v for v in seg)/win)
        act += 1
        if 20*math.log10(r + 1e-12) > -40:
            above += 1
    name = path.split('/')[-1]
    if flat_vals:
        fl = sum(flat_vals)/len(flat_vals)
        ce = sum(cent_vals)/len(cent_vals)
        hm = sum(harm_vals)/len(harm_vals)
        print(f"{name:32s} flatness={fl:.4f}  centroid={ce:7.1f} Hz  harmonicity={hm:.3f}  active={above/act*100:5.1f}%")
    else:
        print(f"{name:32s} (essentially silent)")

if __name__ == '__main__':
    for p in sys.argv[1:]:
        analyze(p)
