# -*- coding: utf-8 -*-
"""
S1「釜内读数跟夹套对不上」定稿判据——Python 参考实现（给 C# 对拍用）。2026-11-17。

只用新外环自己的量和釜内读数，每个外环拍（新外环出了一份新读数的那一拍，2 s）喂一次：
    step(t, tr, sp, wall, tau)
      t    现场秒（新外环影子自己的秒表，单调增）
      tr   这一拍喂给新外环的釜内原始读数（℃）；读不到给 NaN / None
      sp   回路此刻的釜内目标（℃）
      wall 新外环的釜壁估计 Ŵ（LiveOuterReadout.Wall，℃）
      tau  新外环此刻的 τ̂（LiveOuterReadout.Tau，s）
    → Beat：verdict = 'ok' | 'stop' | 'alarm'，which = 'A' / 'B' / 'AB' / ''，加上全部内部量。

两条（详见 README.md 第 1 节）：
  A「读数没跟着动」：读数 30 s 平均从参考起一个 x0 都没动，而「最慢的釜」按最近一段夹套推的量（从参考起、最多回看 τ̂₀ 秒 = 出厂釜时间常数）该挪 R_A：
      R_A ≥ r0  → 'stop'（夹套往上推、读数不升：读数在本釜以下卡住 / 读的不是本釜——再推会把釜烧过头）
      R_A ≤ −r0 → 'alarm'（夹套往下推、读数不降：跟「慢慢变强的放热」从 Tj / Tr 上分不开，按用户「放热只报不停」只报警）
  B「加热时读数往下掉、而且不回」：只看「读数 < 目标 且 Ŵ > 读数」的拍（热只能往釜里流）：
      D = 最近 τc 秒里这些拍的读数最高值 y_pk − 此刻读数；
      这一段往下走的起点 t_seg（从最低处回升过 x0、又往下走 x0 就算新的一段，起点 = 回升到的最高那一拍），这一段的最低 y_min（最早到这么低的那一刻 t_min）；
      u = R(t_seg) − (此刻 − y_min) + (y_pk − y_min)·(1 − e^{−(t − t_min)/τc})
        = 最慢的釜「t_seg 起按夹套推的量升、掉下去的那一截当成 t_min 那一刻一下子丢掉的热（加冷料）、之后按 τc 往回补」比读数高多少；
      D ≥ D0 且 u ≥ r0 → 'stop'（掉了 D0 以上、夹套还在往里推热，读数连最慢的釜该回的都没回）。
  「最慢的釜」= Ŵ 再过一段 T̂w 的一阶、再过一段 τc = TauHi·τ̂₀ 的一阶，从「回看起点」那一刻静止起算：
      R(ts) = Σ_{t_k > ts} ΔŴ_k · h(t − t_k)，h(s) = 1 − a·e^{−s/τc} + b·e^{−s/T̂w}，a = τc ÷ (τc − T̂w)，b = T̂w ÷ (τc − T̂w)
      实现：每拍记 (t_k, Ŵ_k, Ec_k, Ew_k)，Ec_k = Ec_{k−1}·e^{−Δt/τc} + ΔŴ_k，Ew 同理（T̂w）；
      R(ts) = (Ŵ_now − Ŵ_k*) − a·(Ec_now − e^{−(t − t_k*)/τc}·Ec_k*) + b·(Ew_now − e^{−(t − t_k*)/T̂w}·Ew_k*)，k* = t_k ≤ ts 的最后一拍。
读数 NaN / ±inf 的拍：只记 Ŵ（夹套推的照算），A / B 都不动、不判（参考留着）；两次有效读数隔了 > gap（= OuterLossGrace 30 s）才把 A 的参考、B 的队列清掉；
两拍之间隔了 > reseat（60 s = 影子断档重摆）整个从头来。

这个文件只有判据本身（纯 Python、无第三方库、不读钟、不用随机数）：同样的输入逐位一样的输出。
试算台的接线在 bench/adapter.py；对拍夹具在 make_fixtures.py。
"""
import math
from collections import deque
from dataclasses import dataclass

NAN = float('nan')


@dataclass(frozen=True)
class Params:
    x0: float = 0.02        # K：「读数动了」——2 个字（宇电 0.01 K 一个字）
    r0: float = 0.25        # K：最慢的釜该动的量
    d0: float = 8.0         # K：加热中读数往下掉多少才算「掉了」（唯一的工艺数，要用户签）
    tau_c: float = 2850.0   # s：TauHi × τ̂₀ = 3 × 950（新算法认得出的最慢的釜）
    tw: float = 170.0       # s：T̂w 出厂数（Ŵ 已经滞后夹套 T̂w，再多一段 = 真釜壁慢到两倍）
    a_win: float = 950.0    # s：A 只算最近这么久里夹套推的量 = τ̂₀ 出厂数（LiveOuterParams.Tau0）；<= 0 = 用此刻的 τ̂（试过，快釜漏判，见 README）
    win: float = 30.0       # s：读数尾随平均的窗
    gap: float = 30.0       # s：两次有效读数最多隔多久还接着算（= OuterLossGrace 缺省；C# 传回路那个设定）
    reseat: float = 60.0    # s：两拍之间隔了这么久 = 新外环影子断档重摆了 Ŵ（LiveOuterShadow.ReseatGapS），判据整个从头来

    @property
    def hist_s(self):
        """Ŵ 历史要留多久：B 回看 τc（A 回看 τ̂₀ < τc）。"""
        return self.tau_c


@dataclass
class Beat:
    verdict: str            # 'ok' / 'stop' / 'alarm'
    which: str              # 'A' / 'B' / 'AB' / ''
    valid: bool             # 这一拍读数有效、判据算了
    y30: float              # 读数 30 s 平均
    ra: float               # A：最慢的釜从回看起点起该挪多少（K，带号）
    a_ts: float             # A：回看起点（现场秒）= max(参考时刻, t − τ̂₀)；NaN = 刚重起参考
    t_ref: float            # A：参考时刻
    y_ref: float            # A：参考时刻的 y30
    heat: bool              # B：这一拍是不是「加热拍」（y30 < 目标 且 Ŵ > y30）
    d: float                # B：最近 τc 秒加热拍的最高 − 此刻（不是加热拍 = 0）
    t_pk: float             # B：窗里最高那一拍（NaN = 没有）
    t_seg: float            # B：这一段往下走的起点（回看起点，不早于 t − τc）
    rise: float             # B：这一段掉到底以后回升了多少
    r_pk: float             # B：最慢的釜从 t_seg 起按夹套推的量该升多少
    u: float                # B：最慢的釜比读数高多少（≥ r0 且 d ≥ d0 判）


class ReadingCheck:
    """S1 判据。一路一个；开控（锁成新外环）时 reset()；新外环每出一份新读数调一次 step()。"""

    def __init__(self, p: Params = None):
        self.p = p or Params()
        pc = self.p
        self._a = pc.tau_c / (pc.tau_c - pc.tw)
        self._b = pc.tw / (pc.tau_c - pc.tw)
        self.reset()

    def reset(self):
        self._hist = []             # [(t, W, Ec, Ew)]，按 t 递增；前面过期的定期删
        self._h0 = 0                # _hist 里还有效的第一条
        self._ybuf = deque()        # [(t, y)] 最近 win 秒的有效读数
        self._last_ok = None        # 上一次有效读数的时刻
        self._t_ref = None          # A 的参考
        self._y_ref = NAN
        self._maxq = deque()        # B：加热拍的 (t, y30)，y 单调降（队头 = 窗里最高、同值取最晚）
        self._t_last = None         # 上一拍的时刻（判影子重摆）
        self._seg = None            # B：这一段往下走的 [起点 t_seg, 最低 y_min, 最早到最低的 t_min, 'fall'/'rise', 回升最高 y_hi, 它的时刻 t_hi]

    # ── 最慢的釜 ──
    def _push_wall(self, t, w):
        h = self._hist
        if len(h) > self._h0:
            tp, wp, ecp, ewp = h[-1]
            dw = w - wp
            dt = t - tp
            ec = ecp * math.exp(-dt / self.p.tau_c) + dw
            ew = ewp * math.exp(-dt / self.p.tw) + dw
        else:
            ec = ew = 0.0
        h.append((t, w, ec, ew))
        # 留到 t − hist_s 之前的最后一条（回看起点最早 t − τc，要它前面那一拍当 k*）
        lim = t - self.p.hist_s
        while self._h0 + 1 < len(h) and h[self._h0 + 1][0] <= lim:
            self._h0 += 1
        if self._h0 > 4096:
            del h[:self._h0]
            self._h0 = 0

    def _k_star(self, ts):
        """t_k ≤ ts 的最后一拍（没有就是留着的第一拍）。二分。"""
        h = self._hist
        lo, hi = self._h0, len(h) - 1
        if h[lo][0] > ts:
            return lo
        while lo < hi:
            mid = (lo + hi + 1) // 2
            if h[mid][0] <= ts:
                lo = mid
            else:
                hi = mid - 1
        return lo

    def response(self, ts):
        """R(ts)：最慢的釜从 ts 那一刻静止起、按 ts 以后夹套推的量，到此刻该挪多少（K，带号）。"""
        h = self._hist
        t, w, ec, ew = h[-1]
        tk, wk, eck, ewk = h[self._k_star(ts)]
        s = t - tk
        return (w - wk) - self._a * (ec - math.exp(-s / self.p.tau_c) * eck) + self._b * (ew - math.exp(-s / self.p.tw) * ewk)

    # ── 读数 30 s 平均 ──
    def _y30(self, t, y):
        q = self._ybuf
        if y is not None:
            q.append((t, y))
        while q and q[0][0] <= t - self.p.win:
            q.popleft()
        if not q:
            return NAN
        s = 0.0
        for _, v in q:              # 每拍从头加（≤ 15 个数）：不带累加误差，C# 按同样的顺序加逐位一样
            s += v
        return s / len(q)

    # ── 一拍 ──
    def step(self, t, tr, sp, wall, tau):
        p = self.p
        if self._t_last is not None and t - self._t_last > p.reseat:
            self.reset()                                    # 影子断档重摆过：Ŵ 是重新摆的，跟断档前接不上
        self._t_last = t
        self._push_wall(t, wall)
        ok = tr is not None and tr == tr and not math.isinf(tr)
        y30 = self._y30(t, tr if ok else None)
        if not ok or y30 != y30:
            return Beat('ok', '', False, y30, 0.0, NAN, _nz(self._t_ref), self._y_ref, False, 0.0, NAN, NAN, 0.0, 0.0, 0.0)
        if self._last_ok is not None and t - self._last_ok > p.gap:
            # 读数断了超过 gap：断档里釜走到哪不知道，A 的参考、B 的队列都从这一拍重起
            self._t_ref = None
            self._maxq.clear()
            self._seg = None
        self._last_ok = t

        # A
        if self._t_ref is None or abs(y30 - self._y_ref) >= p.x0:
            self._t_ref, self._y_ref = t, y30
            ra, a_ts = 0.0, NAN
        else:
            a_ts = max(self._t_ref, t - (p.a_win if p.a_win > 0 else tau))
            ra = self.response(a_ts)

        # B
        heat = y30 < sp and wall > y30
        mq = self._maxq
        if heat:
            while mq and mq[-1][1] <= y30:
                mq.pop()
            mq.append((t, y30))
            g = self._seg
            if g is None:
                # 刚武装（窗里头一个加热拍）：这一拍就是这一段的起点
                self._seg = [t, y30, t, 'fall', y30, t]
            elif g[3] == 'fall':
                if y30 < g[1]:
                    g[1], g[2] = y30, t                      # 更低：最低处挪到这一拍（同值不挪 = 留最早到这么低的那一刻）
                elif y30 >= g[1] + p.x0:
                    g[3], g[4], g[5] = 'rise', y30, t        # 从最低处回升了 x0：读数在跟着回
            else:
                if y30 >= g[4]:
                    g[4], g[5] = y30, t                      # 回升途中的最高（同值取最晚）
                elif y30 <= g[4] - p.x0:
                    g[0], g[1], g[2], g[3] = g[5], y30, t, 'fall'   # 回升后又往下走 x0：新的一段，起点 = 回升到的最高那一拍
        bw = p.tau_c                                         # B 回看最慢的釜一个时间常数（τc）：比它慢的往下掉不算
        while mq and mq[0][0] < t - bw:
            mq.popleft()
        if not mq:
            self._seg = None
        d = rise = r_pk = u = 0.0
        t_pk = t_seg = NAN
        if heat and mq:
            t_pk, y_pk = mq[0]
            d = y_pk - y30
            g = self._seg
            t_seg, y_min, t_min = max(g[0], t - bw), g[1], g[2]
            rise = y30 - y_min
            r_pk = self.response(t_seg)
            # 最慢的釜：这一段起点 t_seg 起静止、夹套照实推（r_pk）；从窗里最高处掉下去的 y_pk − y_min 当成 t_min 那一刻一下子丢掉的热
            # （加冷料；放在最低点那一刻 = 最宽的算法），之后按 τc 往回补。读数比这个最慢的釜低多少 = u
            u = r_pk - rise + (y_pk - y_min) * (1.0 - math.exp(-(t - t_min) / p.tau_c))

        stop_a = ra >= p.r0
        alarm_a = ra <= -p.r0
        stop_b = heat and d >= p.d0 and u >= p.r0
        which = ('A' if (stop_a or alarm_a) else '') + ('B' if stop_b else '')
        verdict = 'stop' if (stop_a or stop_b) else 'alarm' if alarm_a else 'ok'
        return Beat(verdict, which, True, y30, ra, a_ts, self._t_ref, self._y_ref, heat, d, t_pk, t_seg, rise, r_pk, u)


def _nz(x):
    return NAN if x is None else x
