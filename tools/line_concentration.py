#!/usr/bin/env python3
"""Spectral line concentration: fraction of energy in the top 2% of FFT bins.
Line spectra (pitched/modal) concentrate energy in few bins; noise spreads it."""
import sys, wave, struct, math

def read_wav(path):
    w = wave.open(path, 'rb')
    n, ch = w.getnframes(), w.getnchannels()
    raw = w.readframes(n); w.close()
    s = struct.unpack(f'<{n*ch}h', raw)
    if ch == 2:
        s = [(s[i]+s[i+1])/2 for i in range(0, len(s), 2)]
    return [x/32768.0 for x in s], w.getframerate()

def fft(x):
    n = len(x)
    if n == 1: return x
    ev = fft(x[0::2]); od = fft(x[1::2])
    out = [0j]*n
    for k in range(n//2):
        t = complex(math.cos(-2*math.pi*k/n), math.sin(-2*math.pi*k/n))*od[k]
        out[k] = ev[k]+t; out[k+n//2] = ev[k]-t
    return out

for path in sys.argv[1:]:
    x, sr = read_wav(path)
    N = 16384
    vals = []
    for start in (len(x)//4, len(x)//2, 3*len(x)//4):
        seg = [x[start+i]*(0.5-0.5*math.cos(2*math.pi*i/N)) for i in range(N)]
        p = sorted((abs(v)**2 for v in fft([complex(v,0) for v in seg])[1:N//2]), reverse=True)
        top = sum(p[:len(p)//50])  # top 2% of bins
        # participation ratio: effective number of equally-weighted spectral lines
        pr = (sum(p)**2) / (sum(v*v for v in p) + 1e-30)
        vals.append((top/(sum(p)+1e-20), pr))
    lc = sum(v[0] for v in vals)/len(vals)
    pr = sum(v[1] for v in vals)/len(vals)
    print(f"{path.split('/')[-1]:32s} line-concentration={lc:.3f}  effective-lines={pr:6.1f}")
