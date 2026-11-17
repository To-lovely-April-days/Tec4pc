# -*- coding: utf-8 -*-
"""
S1 对拍夹具生成：把几场闭环（外环 = plan/outer_m.M，就是 C# LiveOuter）里每个外环拍喂给判据的输入（t, tr, sp, wall, tau）和
reading_check.ReadingCheck 逐拍的输出（verdict / which / 全部内部量）存成 CSV，C# 的 ReadingCheck 逐拍喂同样的输入、逐拍比输出。
  python3 make_fixtures.py [输出目录，缺省 ./fixtures]
每场一个 <名>.csv + 汇总 manifest.json（每场拍数、第一次 stop / alarm 在哪一拍、哪一条、源头）。
数的写法：repr（最短能原样读回的十进制，C# double.Parse(s, CultureInfo.InvariantCulture) 逐位读回）；NaN 写 NaN；布尔写 0 / 1。
比法建议：verdict / which / valid / heat 逐拍相同；浮点量 |C# − Py| ≤ 1e-9 + 1e-9·|Py|（exp 的最后一位可能不同）。
判据全程「只记不停」（闭环不因判到而停控），所以判到以后的拍也有：用来核判到之后内部量照样一致。

场次：
  twin_frozen30 / 55 / 70  C# 刻画现状那个闭环（det/twin.py，LiveOuterClosedLoopTests.Simulate 的逐行移植）60 ℃ 稳 10 min 后读数冻在 30 / 55 / 70
  twin_ramp_m04            同一闭环、吸热速率每小时涨 0.4 K/min（300 min，不许停不许报）
  bench_charge60_15K       试算台 60 ℃ 保温一次加冷料掉 15 K（不许停）
  bench_portion40_4x3K     40 ℃ 分 4 次、每次 3 K、隔 120 s（不许停）
  bench_lift60_180         60 ℃ 保温探头提出液面（τp 180 s → B 停）
  bench_swap60_40          60 ℃ 保温中途读数换成一台 40 ℃ 保温的釜（→ B 停）
  bench_nanfreeze60_m1     60 ℃ 保温冻在 目标 −1、每 180 s 读不到一拍（→ A 停，NaN 不重起参考）
  synth_edges              人造：读数断 40 s（> gap 重起）、±inf、τ̂ 跳变、读数恰好平在最低处（取最早那一刻）、两拍隔 70 s（影子重摆、判据从头来）、目标改变
"""
import csv
import json
import math
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, 'bench'))
import _paths  # noqa: F401,E402
from reading_check import ReadingCheck, Params, NAN  # noqa: E402

COLS = ['t', 'tr', 'sp', 'wall', 'tau', 'verdict', 'which', 'valid', 'y30', 'ra', 'a_ts', 't_ref', 'y_ref', 'heat', 'd', 't_pk', 't_seg', 'rise', 'r_pk', 'u']


def fmt(v):
    if isinstance(v, bool):
        return '1' if v else '0'
    if isinstance(v, str):
        return v
    if v is None:
        return 'NaN'
    v = float(v)
    if v != v:
        return 'NaN'
    if math.isinf(v):
        return 'Infinity' if v > 0 else '-Infinity'
    return repr(v)


class Tap:
    """挂在 Guard 上的「判据」：把这一拍的输入记下来、喂 ReadingCheck、记输出；从不停控。"""
    name = 'tap'
    stops = False
    trip_s = None

    def __init__(self):
        self.chk = ReadingCheck(Params())
        self.rows = []

    def reset(self, obs, m):
        pass

    def update(self, obs, m, u):
        if obs.t < 0:
            return False
        tr = obs.tr if obs.tr is not None else NAN
        b = self.chk.step(obs.t, tr, obs.sp, m.wall, m.tau)
        self.rows.append([obs.t, tr, obs.sp, m.wall, m.tau, b.verdict, b.which, b.valid, b.y30, b.ra, b.a_ts, b.t_ref, b.y_ref, b.heat,
                          b.d, b.t_pk, b.t_seg, b.rise, b.r_pk, b.u])
        return False


def write(outdir, name, rows, src):
    path = os.path.join(outdir, name + '.csv')
    with open(path, 'w', encoding='utf-8', newline='') as fh:
        w = csv.writer(fh)
        w.writerow(COLS)
        for r in rows:
            w.writerow([fmt(x) for x in r])
    fs = next((i for i, r in enumerate(rows) if r[5] == 'stop'), None)
    fa = next((i for i, r in enumerate(rows) if r[5] == 'alarm'), None)
    return dict(name=name, file=os.path.basename(path), beats=len(rows), source=src,
                first_stop_beat=fs, first_stop_t=rows[fs][0] if fs is not None else None, first_stop_which=rows[fs][6] if fs is not None else None,
                first_alarm_beat=fa, first_alarm_t=rows[fa][0] if fa is not None else None,
                stop_beats=sum(r[5] == 'stop' for r in rows), alarm_beats=sum(r[5] == 'alarm' for r in rows))


def twin_runs():
    import twin
    from common import Guard
    Base = twin.Vessel
    out = []
    sp, freeze = 60.0, 600.0
    tj0 = sp + Base.bias(sp)
    for fz in (30.0, 55.0, 70.0):
        tap = Tap()
        twin.simulate(sp, tj0, tj0, sp, 70, read_tr=lambda t, real, f=fz: real if t < freeze else f, outer=Guard(detectors=[tap]))
        out.append(('twin_frozen%d' % fz, tap.rows, 'det/twin.py 60 ℃ 稳 10 min 后读数冻在 %g（70 min）' % fz))

    class V(Base):
        def step(self, h, heater, tec):
            super().step(h, heater, tec)
            if self.t >= 600.0:
                self.tr += -0.4 * (self.t - 600.0) / 3600.0 / 60.0 * h
    twin.Vessel = V
    tap = Tap()
    twin.simulate(sp, tj0, tj0, sp, 310, outer=Guard(detectors=[tap]))
    twin.Vessel = Base
    out.append(('twin_ramp_m04', tap.rows, 'det/twin.py 60 ℃ 稳 10 min 后吸热速率每小时涨 0.4 K/min（310 min）'))
    return out


def bench_runs():
    import asim
    import cases as CS
    import faults as FL
    from guard import AGuard
    out = []
    for name, scn, grp, rd, desc in (('bench_charge60_15K', 'charge_T60_15K', 'heatup', 'clean', '试算台 charge_T60_15K（attack/cases.py）'),
                                     ('bench_portion40_4x3K', 'portion_T40_4x3K_120s', 'gate', 'saw0', '试算台 portion_T40_4x3K_120s'),
                                     ('bench_nanfreeze60_m1', 'nanfz_T60_-1_every180s', 'holdpulse', 'clean', '试算台 nanfz_T60_-1_every180s')):
        fam, h, fn = CS.REG[scn]
        d = fn(grp, rd)
        tap = Tap()
        asim.run_one(d['spec'], lambda: AGuard(fault=d.get('fault'), detectors=[tap]), d['minutes'], x=d.get('x'), hooks=d.get('hooks', ()))
        out.append((name, tap.rows, desc + '，釜组 %s，读数 %s' % (grp, rd)))
    # 设计者故障集里的提出液面 / 插错工位（det/collect_faults）
    import collect as C
    import collect_faults as F
    import fsim
    import scenarios as S
    from common import Guard
    from xsim import XOpt
    for name, key, dur, desc, grp, rd in (('bench_lift60_180', 'hold_60', F.DUR_HOLD, ('lift', dict(tau_s=180.0, t_on=1200.0)), 'only1106', 'saw0'),
                                          ('bench_swap60_40', 'hold_60', F.DUR_HOLD, ('swap', dict(other='hold_40', t_on=1200.0)), 'cooldown', 'clean')):
        spec = S.run_spec(C.SCN[key], grp, rd, 'answered')
        spec.duration_s = dur
        fault = F.make_fault(desc, grp, rd)
        tap = Tap()
        fsim.run_f(spec, lambda: Guard(fault=fault, detectors=[tap]), XOpt())
        out.append((name, tap.rows, '设计者故障集 %s %s，釜组 %s，读数 %s' % (key, desc[0], grp, rd)))
    return out


def synth():
    """人造输入：不经闭环，直接编 (t, tr, sp, wall, tau)，专门走边角。"""
    chk = ReadingCheck(Params())
    rows = []

    def feed(t, tr, sp, w, tau):
        b = chk.step(t, tr, sp, w, tau)
        rows.append([t, tr, sp, w, tau, b.verdict, b.which, b.valid, b.y30, b.ra, b.a_ts, b.t_ref, b.y_ref, b.heat, b.d, b.t_pk, b.t_seg,
                     b.rise, b.r_pk, b.u])
    t = 0.0
    w = 65.0
    # 1) 稳着、读数在目标下一点（加热拍），τ̂ 950；2 s 一拍，偶尔 2.5 / 3 s
    for k in range(200):
        dt = 2.0 if k % 7 else 2.5
        t += dt
        feed(t, 59.98 + 0.01 * (k % 3 == 0), 60.0, w + 0.001 * k, 950.0)
    # 2) 读数一步跳到 45、平着（最低处取最早那一拍）；夹套往上推；τ̂ 中途跳到 1400
    for k in range(120):
        t += 2.0
        w += 0.08
        feed(t, 45.0, 60.0, w, 950.0 if k < 60 else 1400.0)
    # 3) 读数断 40 s（NaN，> gap 30 s：A 参考、B 队列重起），±inf 也当读不到
    for k in range(20):
        t += 2.0
        w += 0.05
        feed(t, NAN if k % 5 else (math.inf if k % 10 else -math.inf), 60.0, w, 1400.0)
    # 4) 回来：读数慢慢回升（加热拍），中间一拍 NaN（不重起）
    for k in range(150):
        t += 2.0
        w -= 0.01
        feed(t, NAN if k == 70 else 50.0 + 0.02 * k, 60.0, w, 1400.0)
    # 4b) 两拍之间隔 70 s（> 60 s：影子断档重摆，判据整个从头来）、Ŵ 跳了 3 K
    t += 70.0
    w += 3.0
    feed(t, 53.0, 60.0, w, 1400.0)
    # 5) 目标改到 40（读数在目标以上：不是加热拍）、夹套往下推、读数冻住 → A 往负走（报警）
    for k in range(400):
        t += 2.0
        w -= 0.05
        feed(t, 53.0, 40.0, w, 1400.0)
    return [('synth_edges', rows, '人造输入（make_fixtures.synth）：断档 > gap、±inf、τ̂ 跳变、平在最低处、两拍隔 70 s（重摆）、目标改变、读数在目标以上冻住')]


if __name__ == '__main__':
    outdir = sys.argv[1] if len(sys.argv) > 1 else os.path.join(HERE, 'fixtures')
    os.makedirs(outdir, exist_ok=True)
    man = dict(params=Params().__dict__, columns=COLS, compare='verdict / which / valid / heat 逐拍相同；其余浮点 |C# − Py| ≤ 1e-9 + 1e-9·|Py|；NaN 对 NaN',
               generator='make_fixtures.py', reference='reading_check.py', runs=[])
    for name, rows, src in twin_runs() + bench_runs() + synth():
        m = write(outdir, name, rows, src)
        man['runs'].append(m)
        print('%-24s %6d 拍  stop@%s(%s)  alarm@%s' % (name, m['beats'], m['first_stop_t'], m['first_stop_which'], m['first_alarm_t']))
    with open(os.path.join(outdir, 'manifest.json'), 'w', encoding='utf-8') as fh:
        json.dump(man, fh, ensure_ascii=False, indent=1)
