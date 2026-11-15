# -*- coding: utf-8 -*-
"""
plan · 给以后写 C# 的人用的对拍数据：把定稿的外环（outer_m.py:M）放到 14 场真机逐拍记录上开环重放，逐拍写出输入和内部量。
C# 版拿同样的输入（t、目标、釜内读数、夹套读数）一拍一拍喂，b̂ / τ̂ / T̂ / 釜壁估计 / 夹套目标应当跟这里逐拍对得上（差 < 1e-6）。
开环：记录里的夹套是现在的程序走出来的；这里只核「同样的读数进去，算出来的是不是同一个数」。
  python3 golden.py → out/golden/<场次>.csv
"""
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(os.path.dirname(HERE), 'evidence'))
sys.path.insert(0, os.path.join(os.path.dirname(HERE), 'bench'))
sys.path.insert(0, HERE)
import core            # noqa: E402
import outer_m         # noqa: E402
from outer_api import Obs  # noqa: E402

out_dir = os.path.join(HERE, 'out', 'golden')
os.makedirs(out_dir, exist_ok=True)
n_rows = 0
for C in core.contexts():
    B = C.B
    est = outer_m.M()
    path = os.path.join(out_dir, C.name + '.csv')
    with open(path, 'w', encoding='utf-8') as fh:
        fh.write('t_s,dt_s,sp,tr,tj,wall,xr,b,tau,tau_ekf,k,p_up,g_sp,to_eff,u\n')
        for i in range(len(B.t)):
            dt = 2.0 if i == 0 else max(0.5, float(B.t[i] - B.t[i - 1]))
            o = Obs(t=float(B.t[i] - B.t[0]), dt=dt, sp=float(B.sp[i]), tr=float(B.tr[i]), tj=float(B.tj[i]), tj_sp_prev=float(B.tj[i]), duty=0.0, pid_out=0.0,
                    side='H', output_on=True, inner_sat_high=False, inner_sat_low=False, tec_withheld=False, valve_open=False, first=(i == 0))
            if i == 0:
                est.reset(o)
            u, _ = est.step(o)
            fh.write('%.3f,%.3f,%.4f,%.4f,%.4f,%.9f,%.9f,%.9f,%.6f,%.6f,%.9f,%.9f,%.9f,%.6f,%.9f\n' % (
                o.t, dt, o.sp, o.tr, o.tj, est.wall, est.xr, est.b, est.tau, est.tau_e, est.k, est._p_up(), est.g_sp, est.to_eff, u))
            n_rows += 1
print('%d 场，%d 拍 → %s' % (len(core.contexts()), n_rows, out_dir))
