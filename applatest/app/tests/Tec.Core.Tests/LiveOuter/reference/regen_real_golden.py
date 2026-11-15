# -*- coding: utf-8 -*-
"""
第 0 步 · 真机对拍夹具（../golden/real/*.csv.gz）的核对 / 重生成，只依赖标准库 + 本目录的 outer_m.py + outer_api.py。

夹具是 golden.py（本目录留了一份原件备查）在 2026-11-14 拿现场逐拍记录跑出来的，原样拷进来再 gzip。
golden.py 要读仓库外的现场记录和 evidence/core.py，在别的机器上跑不了；这个脚本不靠它们：
夹具每一行的前 5 列（t_s、dt_s、目标、釜内读数、夹套读数）就是当时喂给外环的输入，照 golden.py 的喂法原样再喂一遍即可
（第 0 行先 reset 再 step、first = True；side = 'H'、输出开着、不贴下限、不挂起；不给终点和坡速）。

  python3 -B regen_real_golden.py             核对：拿夹具里的输入重放参考实现，逐拍跟夹具里的内部量比，超出容差返回非 0；顺带印每个文件解压后的 md5
  python3 -B regen_real_golden.py --rewrite   参考实现改了以后：用重放的结果把夹具的内部量那 10 列重写一遍（输入 5 列不动）

容差跟 C# 对拍测试一样：%.9f 的 7 列 1e-9，%.6f 的 3 列 1e-6。
为什么核对不是逐字节：golden.py 当时喂的 t / dt 是没取整的差值（比如 2.1000000000000014），夹具里只印了 3 位小数；
从夹具读回来再喂差在 1e-15 量级，印出来偶尔会有末位翻一位的，所以按容差比。--rewrite 之后就是「从夹具输入算出来的」，再核对是逐位的。
"""
import argparse
import glob
import gzip
import hashlib
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.dont_write_bytecode = True
sys.path.insert(0, HERE)
import outer_m                     # noqa: E402
from outer_api import Obs          # noqa: E402

REAL_DIR = os.path.join(os.path.dirname(HERE), 'golden', 'real')
HEADER = 't_s,dt_s,sp,tr,tj,wall,xr,b,tau,tau_ekf,k,p_up,g_sp,to_eff,u'
FMT = '%.3f,%.3f,%.4f,%.4f,%.4f,%.9f,%.9f,%.9f,%.6f,%.6f,%.9f,%.9f,%.9f,%.6f,%.9f'
TOL = [1e-9, 1e-9, 1e-9, 1e-6, 1e-6, 1e-9, 1e-9, 1e-9, 1e-6, 1e-9]      # wall,xr,b,tau,tau_ekf,k,p_up,g_sp,to_eff,u
NAMES = HEADER.split(',')[5:]


def replay(rows):
    """rows：[(t, dt, sp, tr, tj), …] → [(wall, xr, b, tau, tau_ekf, k, p_up, g_sp, to_eff, u), …]"""
    est = outer_m.M()
    out = []
    for i, (t, dt, sp, tr, tj) in enumerate(rows):
        o = Obs(t=t, dt=dt, sp=sp, tr=tr, tj=tj, tj_sp_prev=tj, duty=0.0, pid_out=0.0, side='H', output_on=True,
                inner_sat_high=False, inner_sat_low=False, tec_withheld=False, valve_open=False, first=(i == 0))
        if i == 0:
            est.reset(o)
        u, _ = est.step(o)
        out.append((est.wall, est.xr, est.b, est.tau, est.tau_e, est.k, est._p_up(), est.g_sp, est.to_eff, u))
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--rewrite', action='store_true')
    a = ap.parse_args()
    paths = sorted(glob.glob(os.path.join(REAL_DIR, '*.csv.gz')))
    if not paths:
        print('没找到夹具：' + REAL_DIR)
        return 2
    ok = True
    n_all = 0
    for path in paths:
        raw = gzip.open(path, 'rb').read()
        lines = raw.decode('utf-8').splitlines()
        assert lines[0] == HEADER, (path, lines[0])
        rows, ref = [], []
        for ln in lines[1:]:
            f = [float(x) for x in ln.split(',')]
            rows.append(tuple(f[:5]))
            ref.append(f[5:])
        got = replay(rows)
        worst = [0.0] * 10
        for g, r in zip(got, ref):
            for j in range(10):
                worst[j] = max(worst[j], abs(g[j] - r[j]))
        bad = [NAMES[j] for j in range(10) if worst[j] > TOL[j]]
        n_all += len(rows)
        print('%s  %5d 拍  解压后 md5 %s  最大差 %s%s' % (
            os.path.basename(path), len(rows), hashlib.md5(raw).hexdigest(),
            ' '.join('%s %.1e' % (NAMES[j], worst[j]) for j in (0, 1, 2, 3, 4, 9)), ('  !! 超容差：' + '、'.join(bad)) if bad else ''))
        ok = ok and not bad
        if a.rewrite:
            text = HEADER + '\n' + ''.join((FMT % (r + g)) + '\n' for r, g in zip(rows, got))
            with open(path, 'wb') as fh:
                with gzip.GzipFile(filename='', mode='wb', fileobj=fh, compresslevel=9, mtime=0) as gz:   # 不带文件名和时间戳：同样的内容出同样的字节
                    gz.write(text.encode('utf-8'))
            print('   已重写')
    print('%d 场，%d 拍：%s' % (len(paths), n_all, '重写完' if a.rewrite else ('都在容差内' if ok else '有超容差的')))
    return 0 if (ok or a.rewrite) else 1


if __name__ == '__main__':
    sys.exit(main())
