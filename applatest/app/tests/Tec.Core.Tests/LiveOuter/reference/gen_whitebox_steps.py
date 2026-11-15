# -*- coding: utf-8 -*-
"""
第 0 步 · 白盒向量：逐拍对拍（../golden/real、../golden/synth）走不到的那几支。

定稿参数下、任何喂得出来的输入都走不到这几支（gen_synth_golden.py 的插桩证实了），所以只能把内部状态直接摆好再走一整拍：
  · 第二路相关系数夹到 ±0.999（math.copysign 那一行）：三对 (T₂,b₂) (T₂,θ) (b₂,θ) × 正负 × 有 / 没读数里夹得到的那些；
  · 第二路 P₂₂ 贴下限 1e-18；
  · 第二路 θ 正好在边上（没越过）时相关照清（判的是 ≤ / ≥；对拍场里 θ 都是越过去再被夹回来的）；
  · 第一路权重和 ≤ 1e-9 提前 return（釜壁变化率摆成天文数字）；
  · 比例那一份 P上限 ≤ 1e-9 提前 return（出厂数 ρ 配成 0 才会到）；
  · 3 点中值缓冲里只有两个不一样的数时取大的那个（照标准喂法两个数总是相等，分不出取大取小）。

做法：新建 M → reset(o0) → step(o0)（标准的第一拍）→ 把内部状态改成要的样子 → 记「调用前」→ step(o1)（不是第一拍，整拍都走）→ 记「调用后」。
C# 那边：new LiveOuter → Reset(i0) → Step(i0, first: true) → ImportState(调用前) → Step(i1) → ExportState() 跟「调用后」比。
60 s 窗不在状态里：两边都是标准第一拍留下的那两笔，一样。

输出 ../golden/whitebox/whitebox-steps.json；数一律是 repr 出来的字符串（17 位有效数字，C# 用 double.Parse(…, InvariantCulture) 读回来逐位一样）。
状态的键名 = C# LiveOuterState 的属性名。

  python3 -B gen_whitebox_steps.py           生成；每个向量都要用插桩确认走到了点名的那一支，没走到就返回非 0
"""
import json
import math
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.dont_write_bytecode = True
sys.path.insert(0, HERE)
import gen_synth_golden as G      # noqa: E402  （借它的插桩 Tracer；它 import 时会把 outer_m / outer_api 装进来）

outer_m = G.outer_m
Obs = G.Obs
R = repr
OUT = os.path.join(os.path.dirname(HERE), 'golden', 'whitebox', 'whitebox-steps.json')


def obs(t, sp, tr, tj, first=False, side='H', sat_low=False, withheld=False, output_on=True):
    return Obs(t=t, dt=2.0, sp=sp, tr=tr, tj=tj, tj_sp_prev=tj, duty=0.0, pid_out=0.0, side=side, output_on=output_on,
               inner_sat_high=False, inner_sat_low=sat_low, tec_withheld=withheld, valve_open=False, first=first)


def state(m):
    """M 的全部标量状态 → {C# 属性名: repr 字符串 / bool / int}。"""
    P = m.P
    med = list(m.med) + [0.0] * (3 - len(m.med))
    d = dict(
        Started=True, Tau=R(m.tau), K=R(m.k), EpsLp=R(m.eps_lp), ToEff=R(m.to_eff), GSp=R(m.g_sp), FullS=R(m.full_s), AlarmOn=bool(m.alarm_on),
        HasSpfPrev=m.spf_prev is not None, SpfPrev=R(0.0 if m.spf_prev is None else m.spf_prev), U=R(m.u),
        Wall=R(m.wall), WallPrev=R(m.wall_prev), WallRate=R(m.wall_rate), Xr=R(m.xr), B=R(m.b), Ref=R(m.ref),
        MedN=len(m.med), M0=R(med[0]), M1=R(med[1]), M2=R(med[2]),
        ET=R(m.eT), EB=R(m.eb), Th=R(m.th), TauE=R(m.tau_e),
        P00=R(P[0][0]), P01=R(P[0][1]), P02=R(P[0][2]), P11=R(P[1][1]), P12=R(P[1][2]), P22=R(P[2][2]),
        F1=R(m.f1), F2=R(m.f2), G1=R(m.g1), G2=R(m.g2), Tr0=R(m.tr0),
        S0=R(m.S[0]), S1=R(m.S[1]), S2=R(m.S[2]), S3=R(m.S[3]), S4=R(m.S[4]), S5=R(m.S[5]))
    # Python 的 3×3 始终对称（C# 只存上三角）：这里核一下
    assert P[1][0] == P[0][1] and P[2][0] == P[0][2] and P[2][1] == P[1][2]
    return d


O0 = dict(t=0.0, sp=30.0, tr=30.0, tj=32.0)


def new_m(**kw):
    m = outer_m.M(**kw)
    o = obs(first=True, **O0)
    m.reset(o)
    m.step(o)
    return m


def run(name, m, o1, want, params=None):
    """记调用前 → 挂插桩走一整拍 → 记调用后。want = 必须走到的分支名里要含的字样。"""
    pre = state(m)
    tr = G.Tracer()
    tr.start()
    try:
        u, alarm = m.step(o1)
    finally:
        tr.stop()
    hit = [k for k in tr.c if want in k]
    return dict(
        name=name, params=params or {}, first=dict((k, R(v)) for k, v in O0.items()), pre=pre,
        input=dict(t=R(o1.t), dt=R(o1.dt), sp=R(o1.sp), tr=None if o1.tr is None else R(o1.tr), tj=R(o1.tj),
                   side=o1.side, inner_sat_low=bool(o1.inner_sat_low), tec_withheld=bool(o1.tec_withheld), output_on=bool(o1.output_on)),
        post=state(m), u=R(u), alarm=bool(alarm), branch=hit), bool(hit)


def main():
    vecs = []
    ok = True
    skipped = []

    # ── 第二路：相关系数夹 0.999 ──
    base = dict(p00=0.8, p11=3.0, p22=6e-8)
    for (i, j, key) in ((0, 1, 'p01'), (0, 2, 'p02'), (1, 2, 'p12')):
        for sign in (1.0, -1.0):
            for tr_in in (None, 30.4):
                d = dict(base, p01=0.0, p02=0.0, p12=0.0)
                d[key] = sign * 0.99995 * math.sqrt(d['p%d%d' % (i, i)] * d['p%d%d' % (j, j)])
                m = new_m()
                # 夹套读数 = 釜壁估计 → 釜壁估计这一拍不动；wall_prev 摆低 0.004 → 釜壁变化率 0.002 K/s
                m.wall, m.wall_prev, m.g_sp, m.th = 33.0, 32.996, 0.0, 1.0 / 900.0
                m.eT, m.eb = 30.2, 1.7
                m.P = [[d['p00'], d['p01'], d['p02']], [d['p01'], d['p11'], d['p12']], [d['p02'], d['p12'], d['p22']]]
                nm = '第二路 相关系数夹 0.999：%s %s，%s读数' % (key, '正' if sign > 0 else '负', '有' if tr_in is not None else '没')
                v, got = run(nm, m, obs(2.0, 30.0, tr_in, 33.0), '相关系数夹到')
                if got:
                    vecs.append(v)
                else:
                    skipped.append(nm)          # 有读数时修正那一步会把相关拉下来，夹不到——不算向量

    # ── 第二路：P₂₂ 贴下限 ──
    m = new_m()
    m.wall, m.wall_prev, m.g_sp, m.th = 33.0, 33.0, 0.5, 1.0 / 900.0
    m.eT, m.eb = 30.2, 1.7
    m.P = [[0.01, -0.02, 0.0], [-0.02, 0.5, 0.0], [0.0, 0.0, 1e-19]]
    v, got = run('第二路 P₂₂ 贴下限 1e-18', m, obs(2.0, 30.0, 30.3, 33.0), '对角线贴下限')
    vecs.append(v)
    ok = ok and got

    # ── 第二路：θ 正好在边上（没越过）也要清相关——判的是 ≤ / ≥ ──
    # 没读数的拍 θ 不修，摆在边上就还在边上；P₀₂、P₁₂ 摆成非零，走完这一拍应当是 0。
    lo_th = 1.0 / (outer_m.PARAMS['tau_hi'] * outer_m.PARAMS['tau'])
    hi_th = 1.0 / (outer_m.PARAMS['tau_lo'] * outer_m.PARAMS['tau'])
    for th_edge, tag, what in ((lo_th, 'θ 顶下边', '下边（τ₂ = 3·τ̂₀）'), (hi_th, 'θ 顶上边', '上边（τ₂ = 0.4·τ̂₀）')):
        m = new_m()
        m.wall, m.wall_prev, m.g_sp, m.th = 33.0, 33.0, 0.5, th_edge
        m.eT, m.eb = 30.2, 1.7
        m.P = [[0.05, 0.1, 1e-6], [0.1, 3.0, -2e-5], [1e-6, -2e-5, 6e-8]]
        v, got = run('第二路 θ 正好在%s、没读数：相关照清' % what, m, obs(2.0, 30.0, None, 33.0), tag)
        assert not any('越过' in b for b in v['branch']), v['branch']
        assert float(v['post']['Th']) == th_edge and float(v['post']['P02']) == 0.0 and float(v['post']['P12']) == 0.0
        vecs.append(v)
        ok = ok and got

    # ── 第一路：权重和太小 ──
    m = new_m()
    m.wall, m.wall_prev, m.g_sp = 33.0, 33.0 - 2e7, 1.0          # 釜壁变化率 1e7 K/s：这一拍的权重 ~1e-19
    # 累加量摆成「权重和很小、但方差为正、相关 0.63、回归值 2000 s」：不提前 return 的话 τ̂ 会被往 2000 拉约 3e-7 s——
    # 提前 return 才是 τ̂ 一位不动（累加量全 0 的话后面「方差 ≤ 0」那一支也会 return，分不出来）
    m.S = [4e-10, 0.0, 0.0, 1e-12, 2e-9, 1e-5]
    tau_before = m.tau
    v, got = run('第一路 权重和 ≤ 1e-9 提前 return（τ̂、b̂ 不因回归而动；滤波量和累加量照更新）', m, obs(2.0, 30.0, 30.5, 33.0), '权重和太小')
    assert m.tau == tau_before and float(v['post']['S0']) <= 1e-9 and float(v['post']['S3']) > 0 and float(v['post']['S4']) > 0
    vecs.append(v)
    ok = ok and got

    # ── 比例：P上限 ≤ 1e-9（ρ = 0）──
    for sp, what in ((33.0, 'e > 0 给 0'), (27.0, 'e ≤ 0 照常')):
        m = new_m(rho=0.0)
        v, got = run('比例 P上限 ≤ 1e-9（出厂数 ρ = 0）：%s' % what, m, obs(2.0, sp, 30.0, 32.0), 'P上限 ≤ 1e-9', params=dict(Rho=R(0.0)))
        vecs.append(v)
        ok = ok and (got or sp < 30.0)                           # e ≤ 0 那一个不进这一支，是对照

    # ρ = 0 而且 K 贴着 1（τ̂ 小）：不提前 return 的话是 0·tanh(0 ÷ 0) = NaN（K > 1 时 0·tanh(∞) 碰巧也是 0，分不出来）
    m = new_m(rho=0.0)
    m.tau = 400.0
    m.k = m._k()
    assert m.k == 1.0
    v, got = run('比例 P上限 ≤ 1e-9（ρ = 0）且 K = 1：e > 0 给 0，不是 NaN', m, obs(2.0, 33.0, 30.0, 32.0), 'P上限 ≤ 1e-9', params=dict(Rho=R(0.0)))
    assert float(v['u']) == float(v['u']) and v['post']['K'] == '1.0'
    vecs.append(v)
    ok = ok and got

    # ── 中值：缓冲里只有两个不一样的数 ──
    for tr_in, what in ((29.5, '新来的小：取缓冲里原来那个'), (30.5, '新来的大：取新来的')):
        m = new_m()
        m.med = [30.0]
        v, got = run('中值 缓冲里两个数取大的：%s' % what, m, obs(2.0, 30.0, tr_in, 32.0), '缓冲里 2 个数')
        vecs.append(v)
        ok = ok and got

    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    with open(OUT, 'w', encoding='utf-8', newline='\n') as fh:
        json.dump(vecs, fh, ensure_ascii=False, indent=1)
        fh.write('\n')
    for v in vecs:
        print('  %-70s 走到：%s' % (v['name'], '；'.join(v['branch']) or '（对照，不进这一支）'))
    for s in skipped:
        print('  （没夹到，不出向量）%s' % s)
    n_clamp = sum(1 for v in vecs if any('相关系数夹到' in b for b in v['branch']))
    print('%d 个向量 → %s（%d 字节）；其中相关系数夹 0.999 的 %d 个' % (len(vecs), OUT, os.path.getsize(OUT), n_clamp))
    if n_clamp < 6:
        ok = False
        print('!! 相关系数夹 0.999 的向量不到 6 个')
    print('全部断言：' + ('过' if ok else '没过'))
    return 0 if ok else 1


if __name__ == '__main__':
    sys.exit(main())
