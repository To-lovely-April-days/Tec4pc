# -*- coding: utf-8 -*-
"""
合并后的外环（plan）：以做法一的骨架为底（定增益二阶观测器实时算维持温差 b̂ + 比例），并进来的东西都带开关，方便并排试。

    夹套目标 = 目标 + b̂ + g(目标 − T̂)            g：斜率 K − 1；升温方向平滑封顶 P上限（tanh）；降温方向不封
    K = 余量 × (τ̂ + T̂w)² ÷ (4·τ̂·T̂w)，P上限 = ρ·τ̂ ÷ K
    b̂、T̂：二阶观测器（釜壁估计 Ŵ = 夹套读数过一阶滞后 T̂w；τ̂·dT̂/dt = Ŵ − T̂ − b̂ + 读数校正）
    τ̂：按实测连续算（几种算法并排，tau_mode 选），范围 τ̂₀ × [tau_lo, tau_hi]

参数只有一组（PARAMS），全场景共用。变体只通过文件末尾那几个具名工厂给（并排试用），出成绩用 M。
跑：cd ../bench && python3 run_suite.py --outer ../plan/outer_m.py:M --tag m --out ../plan/out
"""
import math
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
BENCH = os.path.join(os.path.dirname(HERE), 'bench')
if BENCH not in sys.path:
    sys.path.insert(0, BENCH)
from outer_api import OuterLoop  # noqa: E402

PARAMS = dict(
    # ── 出厂三个数（每台机器、每个工位一次）──
    tau=950.0,         # ① τ̂₀：釜的时间常数的起点（s）
    tw=170.0,          # ② T̂w：夹套 → 釜壁的滞后（s，纯滞后并在里面）
    rho=0.017,         # ③ ρ：夹套在常用高温、零出力时自己每秒凉多少（K/s）
    # ── 设计常数（所有机器一样）──
    k_margin=0.75,     # K 取「刚好不冲过」那个值的几成（0.9 快约 10 min，失配下冲过的台次多十几倍，见 out/cand/）
    to=150.0,          # 观测器时间常数（s）
    eps0=2.0,          # 读数离模型多远（K）时观测器快一倍；0 = 不变速
    to_min=20.0,
    eps_lp_s=20.0,     # 变速看的是「读数 − 模型」的这么多秒的平均，不是当拍值（一个坏点带不动它）；0 = 看当拍值（做法一原样）
    med3=True,         # 釜内读数先过 3 点中值（最近三个外环拍取中间那个）：只坏一个点的读数进不来
    cap='tanh',        # 'tanh' 平滑封顶；'hard' 硬夹
    p_up=None,         # 给了数 = P上限 就用这个数（K）；None = ρ·τ̂ ÷ K
    lead=0.0,          # 做法三的预估项（× T̂w·g′·模型斜率）；0 = 不用
    k_hi_rel=None,     # K 最多是出厂值算出来的多少倍；None = 不封
    tau_mode='hyb',    # 'fixed' 定死；'reg' 做法一的回归；'ekf' 另起一路三个状态的卡尔曼；'hyb' 两路都算：往大改回归说了算，往小改要两路都同意
    tau_lo=0.4, tau_hi=3.0,
    gate_sp=True,      # 认 τ 只在「目标变动引起的过渡」里认（权重 d² ÷ (d² + e0²)）
    e0=0.2,
    # 'ekf' 那一路
    r=5e-2, q_b=1e-4, p0_b=4.0, p0_th_rel=0.3, q_th_rel=0.0, sig_delta=120.0, tcorr=60.0,
    reopen_e1=5.0,     # 目标（终点）一拍里变了 Δ：对 τ̂ 的把握按 Δ² ÷ (Δ² + e1²) 放回先验（新的一段升降温 = 新的一次认 τ 的机会；Δ 小就基本不放）
    # 'reg' 那一路（做法一原样）
    tf=100.0, tm=600.0, ta=120.0, reg_r=0.3, z0=3e-4, sig_d=100.0, beta=0.1,
    ta_down_mult=1.0,  # τ̂ 往小里改比往大里改慢多少倍（往小改错了会冲过，往大改错了只是慢）
    # 放热报警
    alarm_full_s=60.0, alarm_rise_k=0.02, alarm_wall=True, alarm_src='xr',
    ramp_ff=1.0,
)


class M(OuterLoop):
    name = 'm·合并'

    def __init__(self, **kw):
        p = dict(PARAMS)
        p.update(kw)
        self.p = p
        self.tau0 = self.tau = float(p['tau'])
        self.tw = float(p['tw'])
        self.k0 = p['k_margin'] * (self.tau0 + self.tw) ** 2 / (4 * self.tau0 * self.tw)
        self.k = self._k()
        self.wall = self.xr = self.b = 0.0
        self.eps_lp = 0.0
        self.to_eff = p['to']
        self.u = self.pterm = self.gp = 0.0
        self.full_s = 0.0
        self.alarm_on = False
        self.xr_hist = []
        self.raw_hist = []
        self.med = []
        self.ref = None
        self.g_sp = 0.0
        self.started = False
        self.spf_prev = None

    # ── K、P上限 ──
    def _k(self):
        p = self.p
        k = p['k_margin'] * (self.tau + self.tw) ** 2 / (4 * self.tau * self.tw)
        if p['k_hi_rel'] is not None:
            k = min(k, p['k_hi_rel'] * self.k0)
        return max(1.0, k)

    def _p_up(self):
        p = self.p
        if p['p_up'] is not None:
            return float(p['p_up'])
        return p['rho'] * self.tau / self.k

    # ── 第一次拿到读数那一拍（产品里 = 连上设备；试算台里 = 开控第一拍，最坏情形）──
    def reset(self, obs):
        p = self.p
        self.wall = obs.tj
        self.wall_prev = obs.tj
        self.xr = obs.tr
        self.b = obs.tj - obs.tr
        self.ref = obs.tr
        self.med = [obs.tr]
        self.xr_hist = [(obs.t, obs.tr)]
        self.raw_hist = [(obs.t, obs.tr)]
        self.started = True
        # ekf
        self.eT = obs.tr
        self.eb = obs.tj - obs.tr
        self.th = 1.0 / self.tau
        self.tau_e = self.tau
        self.P = [[p['r'], 0.0, 0.0], [0.0, p['p0_b'], 0.0], [0.0, 0.0, (p['p0_th_rel'] * self.th) ** 2]]
        # reg
        self.f1 = self.f2 = obs.tr
        self.tr0 = obs.tr
        self.g1 = self.g2 = obs.tj
        self.S = [0.0] * 6
        self.wall_rate = 0.0

    # ── 观测器 ──
    def _reading(self, obs):
        tr = obs.tr
        if tr is None or tr != tr:
            return None
        if not self.p['med3']:
            return tr
        self.med.append(tr)
        if len(self.med) > 3:
            self.med.pop(0)
        return sorted(self.med)[len(self.med) // 2]

    def _observe(self, obs, tr):
        p = self.p
        dt = obs.dt
        self.wall += (obs.tj - self.wall) * (1.0 - math.exp(-dt / self.tw))
        self.wall_rate = (self.wall - self.wall_prev) / dt
        self.wall_prev = self.wall
        if p['tau_mode'] in ('ekf', 'hyb'):
            self._tau_ekf(dt, tr)
        if tr is not None and p['tau_mode'] in ('reg', 'hyb'):
            self._tau_reg(dt, tr)
        tau = self.tau
        eps = 0.0 if tr is None else tr - self.xr
        to = p['to']
        if p['eps0'] > 0:
            if p['eps_lp_s'] > 0:
                self.eps_lp += (eps - self.eps_lp) * (1.0 - math.exp(-dt / p['eps_lp_s']))
                es = self.eps_lp
            else:
                es = eps
            to = max(p['to_min'], to / (1.0 + (es / p['eps0']) ** 2))
        self.to_eff = to
        l1 = 2.0 / to - 1.0 / tau
        c = tau / (to * to)
        self.xr += dt * ((self.wall - self.xr - self.b) / tau + l1 * eps)
        self.b += dt * (-c * eps)

    def _set_tau(self, tau_new):
        """τ̂ 变了：观测器里「釜内在以多快的速度走」是事实，不跟着跳——把 b̂ 按同一个斜率换算过去。"""
        dtau = tau_new - self.tau
        if dtau != 0.0:
            slope = (self.wall - self.xr - self.b) / self.tau
            self.b -= dtau * slope
            self.tau = tau_new
            self.k = self._k()

    def _tau_ekf(self, dt, tr):
        """另起一路：T、它自己的 b、θ = 1 ÷ τ 三个量，按「读数 − 模型」一起小修。它只把 τ̂ 交出去，它的 T、b 不进控制律。"""
        p = self.p
        g = self.g_sp if p['gate_sp'] else 1.0
        th = self.th
        x = self.wall - self.eT - self.eb
        self.eT += dt * th * x
        f00, f01, f02 = 1.0 - dt * th, -dt * th, dt * x * g
        P = self.P
        r0 = [f00 * P[0][j] + f01 * P[1][j] + f02 * P[2][j] for j in range(3)]
        p00 = r0[0] * f00 + r0[1] * f01 + r0[2] * f02
        p01, p02 = r0[1], r0[2]
        qm = (p['sig_delta'] * self.wall_rate * th * dt) ** 2 * (p['tcorr'] / dt)
        th0 = 1.0 / self.tau0
        v_max = (p['p0_th_rel'] * th0) ** 2
        p22 = P[2][2]
        if p22 < v_max:
            p22 = min(v_max, p22 + g * p['q_th_rel'] * th0 * th0 * dt)
        P = [[p00 + 1e-8 * dt + qm, p01, p02],
             [p01, P[1][1] + p['q_b'] * dt, P[1][2]],
             [p02, P[1][2], p22]]
        if tr is not None:
            sden = P[0][0] + p['r']
            k0, k1, k2 = P[0][0] / sden, P[0][1] / sden, P[0][2] / sden
            inn = tr - self.eT
            self.eT += k0 * inn
            self.eb += k1 * inn
            self.th += k2 * inn
            row = P[0][:]
            kk = (k0, k1, k2)
            P = [[P[i][j] - kk[i] * row[j] for j in range(3)] for i in range(3)]
            P[1][0] = P[0][1] = 0.5 * (P[0][1] + P[1][0])
            P[2][0] = P[0][2] = 0.5 * (P[0][2] + P[2][0])
            P[2][1] = P[1][2] = 0.5 * (P[1][2] + P[2][1])
        lo, hi = 1.0 / (p['tau_hi'] * self.tau0), 1.0 / (p['tau_lo'] * self.tau0)
        if self.th <= lo or self.th >= hi:
            self.th = min(hi, max(lo, self.th))
            P[0][2] = P[2][0] = 0.0
            P[1][2] = P[2][1] = 0.0
        for i in range(3):
            P[i][i] = max(P[i][i], 1e-12 if i < 2 else 1e-18)
        for i, j in ((0, 1), (0, 2), (1, 2)):
            lim = 0.999 * math.sqrt(P[i][i] * P[j][j])
            if abs(P[i][j]) > lim:
                P[i][j] = P[j][i] = math.copysign(lim, P[i][j])
        self.P = P
        self.tau_e = 1.0 / self.th
        if p['tau_mode'] == 'ekf':
            self._set_tau(self.tau_e)

    def _tau_reg(self, dt, tr):
        """做法一的回归（原样搬）：y = F[Ŵ − Tr] − β·(F[Tr] − 起点)，z = F[dTr/dt]，带遗忘的加权协方差。"""
        p = self.p
        a = 1.0 - math.exp(-dt / p['tf'])
        self.f1 += (tr - self.f1) * a
        self.f2 += (self.f1 - self.f2) * a
        self.g1 += (self.wall - self.g1) * a
        self.g2 += (self.g1 - self.g2) * a
        z = (self.f1 - self.f2) / p['tf']
        y = self.g2 - self.f2 - p['beta'] * (self.f2 - self.tr0)
        wr = max(abs(self.g1 - self.g2) / p['tf'], abs(self.wall_rate))
        xx = p['sig_d'] * wr / p['reg_r']
        w = 1.0 / (1.0 + xx * xx)
        d = math.exp(-dt / p['tm'])
        S = self.S
        S[0] = S[0] * d + w
        S[1] = S[1] * d + w * z
        S[2] = S[2] * d + w * y
        S[3] = S[3] * d + w * z * z
        S[4] = S[4] * d + w * z * y
        S[5] = S[5] * d + w * y * y
        if S[0] <= 1e-9:
            return
        czz = S[3] - S[1] * S[1] / S[0]
        czy = S[4] - S[1] * S[2] / S[0]
        cyy = S[5] - S[2] * S[2] / S[0]
        lam = p['tm'] / dt * p['z0'] ** 2
        if czz <= 0.0 or cyy <= 0.0:
            return
        rho = czy / math.sqrt(czz * cyy)
        g = czz / (czz + lam) * max(0.0, rho) ** 2 * (self.g_sp if p['gate_sp'] else 1.0)
        lo = p['tau_lo'] * self.tau0
        if p['tau_mode'] == 'hyb':
            lo = max(lo, min(self.tau0, self.tau_e))      # 往小里改要两路都同意：回归想改到多小，不许小过另一路认的 τ
        tau_ls = min(p['tau_hi'] * self.tau0, max(lo, czy / czz))
        rate = dt / p['ta'] * g
        if tau_ls < self.tau:
            rate /= p['ta_down_mult']
        self._set_tau(self.tau + rate * (tau_ls - self.tau))

    # ── 比例那一份（升温方向平滑封顶）──
    def _g(self, e):
        km = self.k - 1.0
        if e <= 0.0:
            return km * e, km
        pu = self._p_up()
        if self.p['cap'] == 'tanh':
            if pu <= 1e-9:
                return 0.0, 0.0
            t = math.tanh(km * e / pu)
            return pu * t, km * (1.0 - t * t)
        if km * e >= pu:
            return pu, 0.0
        return km * e, km

    def step(self, obs):
        p = self.p
        tr = self._reading(obs)
        if not obs.first:
            self._observe(obs, tr)
        sp = obs.sp
        rate = getattr(obs, 'sp_rate', 0.0) or 0.0          # 斜坡：配方给的目标变化率（K/s）；台阶 = 0
        sp_final = getattr(obs, 'sp_final', None)
        if sp_final is None:
            sp_final = sp
        # 终点变了：对 τ̂ 的把握放开（连续的权重，Δ 小就基本不动）
        if self.spf_prev is not None and p['tau_mode'] in ('ekf', 'hyb') and p['reopen_e1'] > 0:
            dl = sp_final - self.spf_prev
            if dl != 0.0:
                w = dl * dl / (dl * dl + p['reopen_e1'] ** 2)
                v_max = (p['p0_th_rel'] / self.tau0) ** 2
                if self.P[2][2] < v_max:
                    self.P[2][2] += (v_max - self.P[2][2]) * w
        self.spf_prev = sp_final
        # 「目标变动引起的过渡还剩多少」：目标过一阶滞后（常数 = 名义趋近时间常数 τ̂ ÷ K）
        self.ref += (sp - self.ref) * (1.0 - math.exp(-obs.dt / (self.tau / self.k)))
        d = sp - self.ref
        self.g_sp = d * d / (d * d + p['e0'] ** 2)
        # 控制律
        x = self.wall - self.xr - self.b
        g, gp = self._g(sp - self.xr)
        u = sp + self.b + g - p['lead'] * self.tw * gp * x / self.tau
        if rate != 0.0 or sp_final != sp:
            u += p['ramp_ff'] * (self.tau + self.tw) * rate
            gf, gpf = self._g(sp_final - self.xr)
            uf = sp_final + self.b + gf - p['lead'] * self.tw * gpf * x / self.tau
            u = min(u, uf) if sp_final >= sp else max(u, uf)
        self.u, self.pterm, self.gp = u, g, gp
        return u, self._alarm(obs, tr)

    # ── 用户定的放热报警（只报，不动控制）──
    def _alarm(self, obs, tr):
        p = self.p
        self.xr_hist.append((obs.t, self.xr))
        while self.xr_hist[0][0] < obs.t - 60.0:
            self.xr_hist.pop(0)
        if tr is not None:
            self.raw_hist.append((obs.t, tr))
            while self.raw_hist[0][0] < obs.t - 60.0:
                self.raw_hist.pop(0)
        full = obs.side == 'C' and obs.inner_sat_low and not obs.tec_withheld and obs.output_on
        self.full_s = self.full_s + obs.dt if full else 0.0
        if p['alarm_src'] == 'xr':
            rising = self.xr - self.xr_hist[0][1] > p['alarm_rise_k']
        else:
            rising = _slope_per_min(self.raw_hist) > p['alarm_rise_k']
        cond = self.full_s >= p['alarm_full_s'] and rising and (self.wall < self.xr or not p['alarm_wall'])
        out = None
        if cond and not self.alarm_on:
            out = '制冷顶满 60 s 釜内还在升'
        self.alarm_on = cond
        return out

    def debug(self):
        return {'b': self.b, 'wall': self.wall, 'xr': self.xr, 'p': self.pterm, 'tau': self.tau, 'k': self.k, 'gsp': self.g_sp, 'to': self.to_eff,
                'pup': self._p_up()}


def _slope_per_min(buf):
    n = len(buf)
    if n < 3:
        return 0.0
    mx = sum(b[0] for b in buf) / n
    my = sum(b[1] for b in buf) / n
    num = sum((b[0] - mx) * (b[1] - my) for b in buf)
    den = sum((b[0] - mx) ** 2 for b in buf)
    return num / den * 60 if den > 0 else 0.0


# ── 并排 / 灵敏度用的变体（每个只动写明的那一样；出成绩用 M）──
def M_k09():
    return M(k_margin=0.9)


def M_k06():
    return M(k_margin=0.6)


def M_lead():
    return M(lead=1.0, k_margin=0.9)


def M_lead05():
    return M(lead=0.5, k_margin=0.9)


def M_lead11():
    return M(lead=1.0, k_margin=1.1)


def M_lead13():
    return M(lead=1.0, k_margin=1.3)


def M_fixed():
    return M(tau_mode='fixed')


def M_reg_up():
    return M(tau_mode='reg', tau_lo=1.0, tau_hi=3.0)


def M_reg():
    return M(tau_mode='reg', tau_lo=0.4, tau_hi=2.5)


def M_ekf():
    return M(tau_mode='ekf', tau_lo=0.4, tau_hi=2.5)


def M_nospeed():
    return M(eps0=0.0)


def M_rawspeed():
    return M(eps_lp_s=0.0)


def M_nomed3():
    return M(med3=False)


def M_beta0():
    return M(beta=0.0)


def M_hardcap():
    return M(cap='hard')


def M_kcap():
    return M(k_hi_rel=1.35)


def M_p10():
    return M(p_up=10.0)


def M_alarm_literal():
    return M(alarm_wall=False, alarm_src='raw')


# 出厂数取错
def M_tau665():
    return M(tau=665.0)


def M_tau1235():
    return M(tau=1235.0)


def M_tw137():
    return M(tw=137.0)


def M_tw191():
    return M(tw=191.0)


def M_tw120():
    return M(tw=120.0)


def M_tw240():
    return M(tw=240.0)


def M_rho008():
    return M(rho=0.0085)


def M_rho034():
    return M(rho=0.034)


def M_tau665_fixed():
    return M(tau=665.0, tau_mode='fixed')


def M_tau1235_fixed():
    return M(tau=1235.0, tau_mode='fixed')
