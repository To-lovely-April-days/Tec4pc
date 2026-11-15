# -*- coding: utf-8 -*-
"""
第 0 步 · 合成对拍场：覆盖真机对拍（../golden/real/）走不到的分支。怎么用、什么时候要重生成见本目录 README.md。

真机对拍那 14 场只有：目标整场不变、每拍都有读数、side 恒为 'H'（报警永远不响）、没有斜坡。
这里用简单的一阶 / 二阶对象自己造输入（全部确定性，没有随机数），逐拍喂定稿的外环 outer_m.M（缺省参数），
把「输入全量 + 内部量」写成 csv；C# 版拿同样的输入一拍一拍喂，内部量应当逐拍对得上。

只依赖：标准库 + 本目录的 outer_m.py + outer_api.py。

用法：
    python3 gen_synth_golden.py                       → 写 ../golden/synth/<场名>.csv，打印覆盖表，全部断言过了才返回 0
    python3 gen_synth_golden.py --coverage-out x.md    → 另把覆盖表写到 x.md
    python3 gen_synth_golden.py --debug-dir d/         → 另写一份「加宽」的逐拍内部量（%.17g，含第二路的 T₂ / b₂ / θ / P、回归的 6 个累加量等），C# 对不上时二分用
    python3 gen_synth_golden.py --no-trace             → 不挂插桩再跑一遍，只核 csv 逐字节一样（证明插桩不改结果）

喂法（跟试算台 bench/sim.py、d2/simx.py 一样）：
    每拍造一个 Obs；first = 1 的那一拍先调 reset(obs) 再调 step(obs)（同一个 obs）；其余拍只调 step(obs)。
    sp_final / sp_rate 列空着 = 这一拍没给（Obs 上没有这个属性，或者是 None）→ 外环按「终点 = 此刻目标、坡速 = 0」算。
    tr 列空着 = 这一拍没有读数（Python 里是 None 或 NaN，两样在 M 里等价）。
    pin 列（只有 s12 有）= 1：这一拍喂的目标是占位的（不在控，目标 = 当时的读数），跟 τ 有关的这一拍整个停着。step 之前先摆四样：
        est.ref = obs.sp；est.spf_prev 不是 None 的话 est.spf_prev = obs.sp；est.g_sp = 0；P[0][2] = P[2][0] = P[1][2] = P[2][1] = 0
    —— 这就是 C# 的 LiveOuter.PinTarget(sp)（Python 定稿没有这个入口，outer_m.py 一行没改；first = 1 的拍次序是 reset → pin → step）。
    喂进去的每个数都先按 csv 的打印精度取整再喂（t、dt 3 位；sp、sp_final、tr、tj 4 位；sp_rate 6 位），
    所以 C# 从 csv 里读回来的输入跟 Python 喂的逐位一样（脚本里有断言）。

csv 列：
    输入：t_s,dt_s,sp,sp_final,sp_rate,tr,tj,side,inner_sat_low,tec_withheld,output_on,first
    内部量（step 返回之后取）：wall,xr,b,tau,tau_ekf,k,p_up,g_sp,to_eff,u（没夹过的夹套目标）,alarm（这一拍 step 返回了报警文字 = 1）,alarm_on
    s12 在行尾多一列 pin（见上）；s01…s11 的列一个不多，重生成跟原来逐字节一样。
    打印精度跟真机对拍一样：wall / xr / b / k / p_up / g_sp / u 是 %.9f；tau / tau_ekf / to_eff 是 %.6f。

插桩：sys.settrace 逐行跟 outer_m.py（按行号挂钩，所以先核 outer_m.py 的 md5 是定稿那一份），
    在关键行上读局部变量，记「这一拍走了哪条分支」。不改 outer_m.py，不重写它的任何算式。
"""
import argparse
import hashlib
import math
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
# 参考实现就在本目录（reference/）：outer_m.py（定稿，原样拷贝）+ outer_api.py（最小的 Obs / OuterLoop 定义）。
OUTER_M_PATH = os.path.join(HERE, 'outer_m.py')
OUTER_M_MD5 = '46e5ed1f62d32808ec864d72674ad067'          # 定稿（改动方案 A.2）；插桩按这一份的行号挂
sys.path.insert(0, HERE)
sys.dont_write_bytecode = True
import outer_m                     # noqa: E402
from outer_api import Obs          # noqa: E402

OUT_DIR = os.path.join(os.path.dirname(HERE), 'golden', 'synth')
SIZE_LIMIT = 660 * 1000                # 合成对拍总大小上限。原来是 600 KB（11 场 559 KB）；2026-11-14 加 s12（占位目标）后放到 660 KB，夹具合计仍在 1.5 MB 以内
HEADER = ('t_s,dt_s,sp,sp_final,sp_rate,tr,tj,side,inner_sat_low,tec_withheld,output_on,first,'
          'wall,xr,b,tau,tau_ekf,k,p_up,g_sp,to_eff,u,alarm,alarm_on')
ABSENT = object()                  # sp_final / sp_rate 这一拍「没给」（Obs 上不设这个属性）
NAN = float('nan')
P = outer_m.PARAMS
TAU0 = P['tau']
TAU_HI = P['tau_hi'] * TAU0        # 2850
TAU_LO = P['tau_lo'] * TAU0        # 380


def q(x, nd):
    """按 csv 的打印精度取整（喂进去的就是印出来的那个数）。"""
    return float('%.*f' % (nd, x))


# ───────────────────────── 造输入用的对象（简单、确定） ─────────────────────────
class Plant:
    """夹套 → 釜壁（一阶滞后 tw）→ 釜内（一阶 tau，维持温差 b = b0 + beta·(T − T0)），外加可选的放热 q（K/s）。
    夹套自己：一阶跟夹套目标（常数 tin），升 / 降速率各有上限（顶在降速上限上 = 「制冷顶满」）。"""

    def __init__(self, T, tj, tau, tw, b0, beta=0.1, tin=25.0, up=0.25, dn=0.12):
        self.T, self.tj, self.w = T, tj, tj
        self.tau, self.tw, self.b0, self.beta, self.T0 = tau, tw, b0, beta, T
        self.tin, self.up, self.dn = tin, up, dn
        self.sat_low = False

    def b_true(self):
        return self.b0 + self.beta * (self.T - self.T0)

    def step(self, dt, u=None, q_exo=0.0, tj_force=None):
        n = max(1, int(round(dt / 0.25)))
        h = dt / n
        sat = False
        for _ in range(n):
            if tj_force is not None:
                self.tj = tj_force
            elif u is not None:
                r = (u - self.tj) / self.tin
                if r > self.up:
                    r = self.up
                if r < -self.dn:
                    r = -self.dn
                    sat = True
                self.tj += h * r
            self.w += h * (self.tj - self.w) / self.tw
            self.T += h * ((self.w - self.T - self.b_true()) / self.tau + q_exo)
        self.sat_low = sat


def ripple(k, amp=0.01):
    """读数上的小锯齿（确定性）：0, +amp, 0, −amp, …"""
    return (0.0, amp, 0.0, -amp)[k % 4]


def clamp_u(u):
    return min(200.0, max(-50.0, u))


def beat(dt, sp, tr, tj, sp_final=ABSENT, sp_rate=ABSENT, side='H', sat_low=False, withheld=False, output_on=True, first=False, tag=None, pin=False):
    return dict(dt=dt, sp=sp, tr=tr, tj=tj, sp_final=sp_final, sp_rate=sp_rate, side=side, sat_low=sat_low, withheld=withheld,
                output_on=output_on, first=first, tag=tag, pin=pin)


# ───────────────────────── 各场（生成器：yield 这一拍的输入，收到上一拍的 u） ─────────────────────────
def sc_step_plain():
    """最平常的一场，喂法跟 golden.py 一样（不给 sp_final / sp_rate）：没在控（目标 = 读数）→ 下发 30 → 72（比例那一份贴着 P上限）→ 目标改到读数下面 1.5 K（e ≤ 0）。"""
    pl = Plant(30.0, 31.5, tau=900.0, tw=150.0, b0=1.5)
    u = None
    for k in range(200):
        tr = round(pl.T + ripple(k), 2)
        if k < 12:
            sp = tr                    # 没在控：目标取当时的读数（改动方案 B「什么时候起算」）
        elif k < 150:
            sp = 72.0
        else:
            sp = round(tr - 1.5, 1) if k == 150 else sp     # 目标改到读数下面 1.5 K：e ≤ 0 那一支
        u = yield beat(2.0, sp, tr, round(pl.tj, 3), first=(k == 0))
        pl.step(2.0, clamp_u(u) if k >= 12 else None)


def sc_ramp():
    """升坡、降坡（给 sp_rate、sp_final），两种「谁说了算」各走到；外加几种边角给法。"""
    pl = Plant(30.0, 31.2, tau=950.0, tw=170.0, b0=1.2)
    plan = []
    # (拍数, 起点, 坡速 K/s, 终点, 给法)
    plan += [(60, 30.0, 0.02, 60.0, 'num')]         # 升坡，离终点远：坡那一行小 → min 取坡那一行
    plan += [(50, 52.0, 0.02, 54.0, 'num')]         # 升坡，离终点近（走到终点后 sp = 终点、坡速 0）→ min 取「直接去终点」那一行
    plan += [(8, 54.0, 0.0, 54.0, 'none')]          # 坡停：sp_final / sp_rate 给的是 None
    plan += [(40, 54.0, -0.02, 20.0, 'num')]        # 降坡，离终点远 → max 取坡那一行
    plan += [(45, 33.0, -0.02, 31.5, 'num')]        # 降坡，离终点近 → max 取「直接去终点」那一行
    plan += [(6, 31.5, 0.0, 28.0, 'num')]           # 坡速 0 但终点 ≠ 此刻目标（坡暂停在半路）
    plan += [(6, 28.0, 0.01, 28.0, 'num')]          # 坡速 ≠ 0 但此刻目标 = 终点（升）：min，「直接去终点」赢
    plan += [(6, 28.0, -0.01, 28.0, 'num')]         # 坡速 ≠ 0 但此刻目标 = 终点（降）：注意 Python 这里走的是 min（sp_final >= sp 成立），照搬
    plan += [(10, 28.0, 0.0, 28.0, 'absent')]       # 不给
    k = 0
    u = None
    for n, sp0, rate, spf, how in plan:
        for i in range(n):
            sp = sp0 + rate * 2.0 * i
            if rate > 0:
                sp = min(sp, spf)
            elif rate < 0:
                sp = max(sp, spf)
            r = rate if sp != spf or (i < n and sp0 == spf) else 0.0
            tr = round(pl.T + ripple(k), 2)
            kw = {}
            if how == 'num':
                kw = dict(sp_final=spf, sp_rate=r)
            elif how == 'none':
                kw = dict(sp_final=None, sp_rate=None)
            u = yield beat(2.0, sp, tr, round(pl.tj, 3), first=(k == 0), **kw)
            pl.step(2.0, clamp_u(u))
            k += 1


def sc_retarget():
    """目标中途改：没在控 → 40 → 58（大改）→ 58.5（小改）→ 40（大改，改到釜内以下：e < 0）→ 40.01（极小改）→ 61（大改）。
    把握放开的权重 Δ²/(Δ²+25) 各种大小都有；开头「把握还在先验上」时改目标 = 跳过不放。"""
    pl = Plant(40.0, 43.0, tau=820.0, tw=140.0, b0=3.0)
    sched = {0: None, 10: 58.0, 100: 58.5, 150: 40.0, 225: 40.01, 237: 61.0}
    sp = None
    u = None
    for k in range(280):
        tr = round(pl.T + ripple(k), 2)
        if k in sched and sched[k] is not None:
            sp = sched[k]
        cur = tr if sp is None else sp
        u = yield beat(2.0, cur, tr, round(pl.tj, 3), sp_final=cur, sp_rate=0.0, first=(k == 0))
        pl.step(2.0, clamp_u(u) if sp is not None else None)


def sc_tau_hi():
    """釜比出厂数慢得多（真 τ = 5200 s）：两路都往大认，第二路顶到上限 3 倍（θ 夹在下边、清相关），回归的目标值夹在 3·τ̂₀。dt = 3 s。"""
    pl = Plant(25.0, 26.0, tau=5200.0, tw=170.0, b0=1.0, tin=20.0, up=0.6, dn=0.6)
    u = None
    for k in range(430):
        tr = round(pl.T, 2)
        if k < 4:
            sp = tr
        else:
            sp = (70.0, 40.0)[((k - 4) // 110) % 2]
        u = yield beat(3.0, sp, tr, round(pl.tj, 3), sp_final=sp, sp_rate=0.0, first=(k == 0))
        pl.step(3.0, clamp_u(u) if k >= 4 else None)


def sc_tau_lo():
    """釜比出厂数快得多（真 τ = 150 s）：两路都往小认，第二路顶到下限 0.4 倍，回归跟着到 0.4·τ̂₀ 附近，K 贴下限 1。dt = 3 s。
    然后：第二次 reset（first = 1；钉住 reset 不清 τ̂ / 变速平均 / 门控 / 上一拍终点，第二路从此刻的 τ̂ 起），再改一次目标；
    最后一长段没读数（120 s；夹套不动；两路只按模型往前推、公式照出），读数回来接着修。"""
    pl = Plant(30.0, 30.6, tau=150.0, tw=170.0, b0=0.6, tin=20.0, up=0.6, dn=0.6)
    u = None
    n_main, k_reset, k_lost0, n_lost = 300, 310, 318, LOST_BEATS
    for k in range(k_lost0 + n_lost + 12):
        tr = round(pl.T, 2)
        if k < 4:
            sp = tr
        elif k < n_main:
            sp = (55.0, 35.0)[((k - 4) // 90) % 2]
        else:
            sp = 44.0
        tr_in = None if k_lost0 <= k < k_lost0 + n_lost else tr
        u = yield beat(3.0, sp, tr_in, round(pl.tj, 3), sp_final=sp, sp_rate=0.0, first=(k == 0 or k == k_reset))
        pl.step(3.0, clamp_u(u) if (k >= 4 and tr_in is not None) else None)      # 没读数那一段夹套不动（输出等于停了）


LOST_BEATS = 40


def sc_glitch():
    """60 ℃ 保温，读数坏点：单个 +5 K、单个 −5 K（3 点中值挡在外面）；连坏两个 +5、连坏两个 −5（第二个进得来）。"""
    pl = Plant(60.0, 65.8, tau=950.0, tw=170.0, b0=5.8)
    bad = {30: 5.0, 50: -5.0, 75: 5.0, 76: 5.0, 105: -5.0, 106: -5.0}
    u = None
    for k in range(150):
        tr = round(pl.T + ripple(k) + bad.get(k, 0.0), 2)
        u = yield beat(2.0, 60.0, tr, round(pl.tj, 3), sp_final=60.0, sp_rate=0.0, first=(k == 0))
        pl.step(2.0, clamp_u(u))


def sc_loss():
    """升温途中读数没了：第 2 拍就没（中值缓冲里还只有两个数）、单拍、连 3 拍、连 15 拍（30 s）、一段 NaN。"""
    pl = Plant(35.0, 37.5, tau=950.0, tw=170.0, b0=2.5)
    lost_none = {1} | {25} | set(range(45, 48)) | set(range(80, 95))
    lost_nan = set(range(120, 124)) | {140}
    u = None
    for k in range(170):
        tr = round(pl.T + ripple(k), 2)
        if k in lost_none:
            tr = None
        elif k in lost_nan:
            tr = NAN
        u = yield beat(2.0, 52.0, tr, round(pl.tj, 3), sp_final=52.0, sp_rate=0.0, first=(k == 0))
        pl.step(2.0, clamp_u(u))


def sc_far():
    """读数离模型远：釜内一下掉 25 K 不回来（加了一大份冷料）→ 变速到 To′ = 下限 20 s；随后回到 150 附近；再来一次反向 +12 K（To′ 落在 20–30 s 之间）。"""
    pl = Plant(50.0, 54.5, tau=950.0, tw=170.0, b0=4.5)
    u = None
    for k in range(180):
        if k == 20:
            pl.T -= 25.0
        if k == 110:
            pl.T += 12.0
        tr = round(pl.T + ripple(k), 2)
        u = yield beat(2.0, 50.0, tr, round(pl.tj, 3), sp_final=50.0, sp_rate=0.0, first=(k == 0))
        pl.step(2.0, clamp_u(u))


def sc_alarm_combo():
    """放热报警的条件组合（输入全是照着条件摆的，不走闭环）：dt = 2 s，目标 20 ℃ 不动。
    每一小段让别的条件都成立、只差一个：不报；把那一个补上、顶满计时重新数满 60 s：报。"""
    T = 20.0
    # (拍数, 釜内每拍变多少 K, 夹套, side, sat_low, withheld, output_on, 说明)
    segs = [
        (8, 0.00, 21.0, 'H', False, False, True, '起步，夹套比釜内热'),
        # 1. 真报：TEC 侧顶满、釜内在升、釜壁比釜内冷（头几拍釜壁估计还比釜内热、T̂ 也还没升起来）
        (36, 0.02, 12.0, 'C', True, False, True, '全成立 → 顶满计时正好 60.0 s 那一拍报；之后一直成立不重报'),
        (1, 0.02, 12.0, 'C', True, False, False, '输出关一拍：计时清零、重新武装'),
        (31, 0.02, 12.0, 'C', True, False, True, '再满 60 s → 再报一次'),
        # 2. 差 side
        (12, 0.02, 12.0, 'H', True, False, True, '继电器在加热棒侧：别的都成立，不报'),
        (31, 0.02, 12.0, 'C', True, False, True, '换到 TEC 侧满 60 s → 报'),
        # 3. 差 inner_sat_low
        (12, 0.02, 12.0, 'C', False, False, True, '内环没贴下限：不报'),
        (31, 0.02, 12.0, 'C', True, False, True, '贴上满 60 s → 报'),
        # 4. 差 tec_withheld
        (12, 0.02, 12.0, 'C', True, True, True, '在等水 / 等冷水机间隔：不报'),
        (31, 0.02, 12.0, 'C', True, False, True, '放行满 60 s → 报'),
        # 5. 差 output_on；顶满只有 58 s
        (12, 0.02, 12.0, 'C', True, False, False, '输出关着：不报'),
        (29, 0.02, 12.0, 'C', True, False, True, '开了 58 s：还差一拍，不报'),
        (1, 0.02, 12.0, 'C', True, False, False, '又关一拍：清零'),
        (31, 0.02, 12.0, 'C', True, False, True, '满 60 s → 报'),
        # 6. 差「还在升」：读数掉下去 → T̂ 跟着回头，60 s 里升不到 0.02 → 报着的撤掉；读数再升 → 重新报（顶满计时一直没断）
        (12, -0.12, 12.0, 'C', True, False, True, '釜内读数往下掉'),
        (24, 0.00, 12.0, 'C', True, False, True, '不动'),
        (44, 0.04, 12.0, 'C', True, False, True, '又升起来'),
        # 7. 差「釜壁比釜内冷」：先在加热棒侧把夹套烧热（釜壁估计高过釜内），再切到 TEC 顶满——大降温起步那一分钟
        (20, 0.02, 120.0, 'H', False, False, True, '加热棒侧、夹套 120 ℃：釜壁估计升到釜内以上'),
        (32, 0.02, 50.0, 'C', True, False, True, 'TEC 侧顶满 60 s、釜内在升，但釜壁估计比釜内热：不报'),
        (34, 0.02, -10.0, 'C', True, False, True, '夹套降下来：釜壁估计掉到釜内以下那一拍报'),
    ]
    first = True
    for n, dT, tj, side, sat, wh, on, _ in segs:
        for _i in range(n):
            T += dT
            yield beat(2.0, 20.0, round(T, 2), tj, sp_final=20.0, sp_rate=0.0, side=side, sat_low=sat, withheld=wh, output_on=on, first=first)
            first = False


def sc_exo_dt():
    """冷端保温时来了放热（闭环；制冷速率有限 → 顶满），拍长 0.5 / 1 / 2 / 3 s 混着（外加几拍 2.1 / 2.4 / 0.7）。
    执行机构那几样照真机的次序摆：回路要冷 → 先挂起 40 s（等人答水：tec_withheld = 1，还在加热棒侧，夹套不许降）→ 扳继电器一拍（output_on = 0）→ TEC 侧。
    顶满计时按 dt 累加，数到 60 s 的那一拍正好落在一段没读数的拍里（报警照算、照报）；放热停了以后 T̂ 不升了，报警撤掉。"""
    pl = Plant(10.0, 10.3, tau=950.0, tw=170.0, b0=0.3, tin=25.0, up=0.25, dn=0.02)
    pat = (2.0, 2.0, 0.5, 1.0, 3.0, 2.0, 1.0, 0.5, 3.0, 2.0, 2.1, 2.4, 0.7, 2.0)
    u = None
    t = 0.0
    k = 0
    phase, ask_t = 'heat', 0.0
    cnt = 0.0
    while t < 470.0:
        dt = 0.5 if k == 0 else pat[k % len(pat)]
        exo = 0.012 if 60.0 <= t < 330.0 else 0.0        # 0.72 K/min
        tr = round(pl.T + ripple(k), 2)
        want_cool = u is not None and u < pl.tj - 0.3
        if phase == 'heat' and want_cool:
            phase, ask_t = 'ask', t
        elif phase == 'ask' and t - ask_t >= 40.0:
            phase = 'flip'
        elif phase == 'flip':
            phase = 'cool'
        elif phase == 'cool' and u is not None and u > pl.tj + 1.0:
            phase = 'heat'
        side = 'C' if phase in ('flip', 'cool') else 'H'
        withheld = phase == 'ask'
        on = phase != 'flip'
        sat = pl.sat_low if phase == 'cool' else (phase == 'ask' and want_cool)     # 等水期间下限钳 0，贴着 0 也算贴下限
        full = side == 'C' and sat and not withheld and on
        cnt = cnt + dt if full else 0.0
        if 52.0 <= cnt < 68.0:
            tr = None
        u = yield beat(dt, 10.0, tr, round(pl.tj, 3), sp_final=10.0, sp_rate=0.0, side=side, sat_low=sat, withheld=withheld, output_on=on, first=(k == 0))
        uc = clamp_u(u)
        pl.step(dt, uc if phase == 'cool' else max(uc, pl.tj), q_exo=exo)           # 没接上 TEC 的时候夹套只许升不许降
        t += dt
        k += 1


def sc_dt_mix():
    """升温台阶 + 小改目标，拍长 0.5 / 1 / 2 / 3 s 混着；第一拍 dt = 3 s。"""
    pl = Plant(22.0, 23.5, tau=1100.0, tw=200.0, b0=1.5)
    pat = (0.5, 1.0, 2.0, 3.0, 2.0, 0.5, 0.5, 3.0, 1.0, 2.0, 3.0, 1.0)
    u = None
    for k in range(220):
        dt = 3.0 if k == 0 else pat[k % len(pat)]
        tr = round(pl.T + ripple(k), 2)
        sp = 36.0 if k < 150 else 35.2
        u = yield beat(dt, sp, tr, round(pl.tj, 3), sp_final=sp, sp_rate=0.0, first=(k == 0))
        pl.step(dt, clamp_u(u))


def sc_idle_pin():
    """占位目标（pin 列）：不在控的拍目标 = 当时的读数，step 之前先把过渡参考和上一拍终点摆到它上面（= C# 的 LiveOuter.PinTarget）。
    没在控（含起算那一拍）→ 下发 40 → 55（在控，真 τ = 700：过渡里两路都认动）→ 过渡还没完就停控：夹套自然凉、釜内自己走，
    其间单个坏点 +5 K、连坏两个 −5 K、两拍没读数（占位目标沿用上一个）、加一份冷料（一下掉 15 K）→ 再下发目标（釜内 + 8）。
    喂法跟挂接层一样：不给 sp_final / sp_rate。不在控的拍门控恒 0、把握不放开；下发目标那一拍把握照「新目标 − 上一拍的读数」放开。"""
    pl = Plant(40.0, 42.0, tau=700.0, tw=170.0, b0=2.0)
    bad = {175: 5.0, 190: -5.0, 191: -5.0}
    lost = {200, 201}
    k_ctl0, k_stop, k_charge, k_ctl1, n = 8, 150, 225, 260, 300
    u = None
    sp = None
    for k in range(n):
        if k == k_charge:
            pl.T -= 15.0
        tr = round(pl.T + ripple(k) + bad.get(k, 0.0), 2)
        tr_in = None if k in lost else tr
        idle = k < k_ctl0 or k_stop <= k < k_ctl1
        if idle:
            sp = sp if tr_in is None else tr_in           # 占位目标：当时的读数；读数没了沿用上一个
        elif k < k_stop:
            sp = 55.0
        elif k == k_ctl1:
            sp = round(tr + 8.0, 1)
        u = yield beat(2.0, sp, tr_in, round(pl.tj, 3), first=(k == 0), pin=idle)
        if k_stop <= k < k_ctl1:
            pl.step(2.0, tj_force=25.0 + (pl.tj - 25.0) * math.exp(-2.0 / 900.0))     # 停控：夹套自然凉（往 25 ℃，900 s）
        else:
            pl.step(2.0, clamp_u(u) if not idle else None)


sc_idle_pin.pin_col = True             # 这一场的 csv 行尾多一列 pin


SCENES = [
    ('s01_step_plain', sc_step_plain),
    ('s02_ramp', sc_ramp),
    ('s03_retarget', sc_retarget),
    ('s04_tau_hi', sc_tau_hi),
    ('s05_tau_lo_reseed', sc_tau_lo),
    ('s06_glitch', sc_glitch),
    ('s07_loss', sc_loss),
    ('s08_far', sc_far),
    ('s09_alarm_combo', sc_alarm_combo),
    ('s10_exo_dt', sc_exo_dt),
    ('s11_dt_mix', sc_dt_mix),
    ('s12_idle_pin', sc_idle_pin),
]


# ───────────────────────── 插桩（按 outer_m.py 的行号挂；md5 对不上就不认） ─────────────────────────
class Tracer:
    """逐行事件发生在「这一行执行之前」。要看一行算出来的结果，就挂在它的下一行上。"""

    def __init__(self):
        self.c = {}                 # 分支名 → 这一场走到的拍次数
        self.x = {}                 # 这一场的极值之类
        self.code_file = outer_m.__file__
        self.beat = {}              # 这一拍的临时量

    def hit(self, name, n=1):
        self.c[name] = self.c.get(name, 0) + n

    def ext(self, name, v, fn):
        self.x[name] = v if name not in self.x else fn(self.x[name], v)

    def start(self):
        sys.settrace(self._global)

    def stop(self):
        sys.settrace(None)

    def _global(self, frame, event, arg):
        if frame.f_code.co_filename == self.code_file:
            return self._local
        return None

    def _local(self, frame, event, arg):
        if event != 'line':
            return self._local
        ln = frame.f_lineno
        L = frame.f_locals
        fn = frame.f_code.co_name
        h = self.hit
        if fn == '_reading':
            if ln == 123:
                h('读数·没有（None）' if L['tr'] is None else '读数·没有（NaN）')
            elif ln == 129:
                n = len(L['self'].med)
                med = sorted(L['self'].med)[n // 2]
                h('中值·缓冲里 %d 个数' % n)
                last = self.beat.get('last_med')
                if n == 3 and abs(L['tr'] - med) >= 3.0:
                    h('中值·这一拍的读数没进来（|读数 − 中值| ≥ 3 K）')
                if n == 3 and last is not None and abs(med - last) >= 3.0:
                    h('中值·输出跳了 ≥ 3 K（连坏两点第二个进来 / 真变了）')
                self.beat['last_med'] = med
        elif fn == '_observe':
            if ln == 133:
                h('观测器·跑了')
            elif ln == 140:
                h('第一路回归·跑了')
            elif ln == 141:
                if L['tr'] is None:
                    h('没读数：第一路回归不跑、第二路只推不修、ε = 0')
            elif ln == 151:
                to = L['to']
                self.ext('To′ 最小', to, min)
                if to == P['to_min']:
                    h('变速·To′ 夹在下限 20 s')
                elif to < 30.0:
                    h('变速·To′ 在 20–30 s 之间')
                elif to < 149.0:
                    h('变速·To′ 在 30–149 s 之间')
        elif fn == '_set_tau':
            if ln == 160:
                if L['dtau'] == 0.0:
                    h('τ̂ 没变（Δ = 0）：b̂ 不换算')
            elif ln == 161:
                h('τ̂ 往大改：b̂ 换算' if L['dtau'] > 0 else 'τ̂ 往小改：b̂ 换算')
        elif fn == '_tau_ekf':
            if ln == 170:
                h('第二路·门控 g = 0（不学 θ）' if L['g'] == 0.0 else '第二路·门控 g > 0')
            elif ln == 183:
                h('第二路·P₂₂ 低于先验（q_th_rel = 0，不回涨）')
            elif ln == 188:
                h('第二路·有读数：修')
            elif ln == 200:
                if L['tr'] is None:
                    h('第二路·没读数：只推')
            elif ln == 202:
                s = L['self']
                if s.th <= L['lo']:
                    h('第二路·θ 顶下边（τ₂ = 3·τ̂₀）清相关')
                    if s.th < L['lo']:
                        h('第二路·θ 越过下边被夹回')
                if s.th >= L['hi']:
                    h('第二路·θ 顶上边（τ₂ = 0.4·τ̂₀）清相关')
                    if s.th > L['hi']:
                        h('第二路·θ 越过上边被夹回')
            elif ln == 206:
                i = L['i']
                if L['P'][i][i] < (1e-12 if i < 2 else 1e-18):
                    h('第二路·P 对角线贴下限')
            elif ln == 209:
                i, j = L['i'], L['j']
                self.ext('相关系数最大 |ρ%d%d|' % (i, j), abs(L['P'][i][j]) / (L['lim'] / 0.999), max)
            elif ln == 210:
                h('第二路·相关系数夹到 ±0.999（T₂–b₂ 等，%s）' % ('正' if L['P'][L['i']][L['j']] > 0 else '负'))
        elif fn == '_tau_reg':
            if ln == 238:
                h('第一路·权重和太小：提前 return')
            elif ln == 244:
                h('第一路·方差 ≤ 0：提前 return')
            elif ln == 246:
                if L['rho'] <= 0.0:
                    h('第一路·相关 ≤ 0：这一拍不动 τ̂')
            elif ln == 250:
                lo = L['lo']
                s = L['self']
                if lo == TAU_LO or s.tau_e <= TAU_LO * (1 + 1e-12):
                    h('第一路·下限 = 0.4·τ̂₀（第二路已到底）')
                elif lo == TAU0:
                    h('第一路·下限 = τ̂₀（第二路 ≥ 出厂值：不许往出厂值以下改）')
                else:
                    assert lo == s.tau_e
                    h('第一路·下限 = 第二路的 τ₂（0.4·τ̂₀ < τ₂ < τ̂₀）')
            elif ln == 251:
                raw = L['czy'] / L['czz']
                if raw > TAU_HI:
                    assert L['tau_ls'] == TAU_HI
                    h('第一路·回归值夹在上限 3·τ̂₀')
                elif raw < L['lo']:
                    assert L['tau_ls'] == L['lo']
                    h('第一路·回归值夹在下限')
                else:
                    h('第一路·回归值在限内')
            elif ln == 253:
                h('第一路·往小改那一行（÷ ta_down_mult）')
        elif fn == '_g':
            if ln == 260:
                h('比例·e < 0（线性，不封顶）' if L['e'] < 0 else '比例·e = 0')
            elif ln == 264:
                h('比例·P上限 ≤ 1e-9：提前 return')
            elif ln == 266:
                t = L['t']
                self.ext('tanh 最大', t, max)
                h('比例·e > 0（tanh 封顶）')
                if t > 0.9:
                    h('比例·tanh > 0.9（贴着 P上限）')
            elif ln in (268, 269):
                h('比例·硬夹（不该走到）')
        elif fn == '_k':
            if ln == 86:
                if L['k'] < 1.0:
                    h('K 贴下限 1')
        elif fn == 'step':
            if ln == 275:
                h('step·不是第一拍：跑观测器')
            elif ln == 276:
                if L['obs'].first:
                    h('step·第一拍：不跑观测器')
            elif ln == 282:
                if L['self'].spf_prev is None:
                    h('终点·第一拍（上一拍终点 = 无）')
            elif ln == 284:
                if L['dl'] == 0.0:
                    h('终点·没变')
            elif ln == 286:
                w = L['w']
                h('终点·变了：权重 %s' % ('> 0.5（大改）' if w > 0.5 else '0.001–0.5（小改）' if w > 1e-3 else '< 0.001（极小改）'))
            elif ln == 288:
                h('终点·把握放开（P₂₂ 往先验拉）')
            elif ln == 287:
                if not (L['self'].P[2][2] < L['v_max']):
                    h('终点·变了但把握已在先验上：跳过')
            elif ln == 299:
                self.beat['ramp'] = True
            elif ln == 303:
                if self.beat.pop('ramp', False):
                    sp, spf, rate, uf = L['sp'], L['sp_final'], L['rate'], L['uf']
                    u = L['u']
                    if spf >= sp:
                        kind = '升' if spf > sp else '此刻目标 = 终点'
                        who = '「直接去终点」那一行' if u == uf else '坡那一行'
                        h('斜坡·min（%s）取%s' % (kind, who))
                    else:
                        who = '「直接去终点」那一行' if u == uf else '坡那一行'
                        h('斜坡·max（降）取%s' % who)
                    if rate == 0.0:
                        h('斜坡·坡速 0 但终点 ≠ 此刻目标')
                    if spf == sp:
                        h('斜坡·坡速 %s 0 但此刻目标 = 终点' % ('>' if rate > 0 else '<'))
                else:
                    h('斜坡·不走（坡速 0 且终点 = 此刻目标）')
        elif fn == '_alarm':
            if ln == 310:
                self.beat.setdefault('hist_n', len(L['self'].xr_hist))
            elif ln == 312:
                s = L['self']
                obs = L['obs']
                age = obs.t - s.xr_hist[0][0]
                self.ext('60 s 窗·最老一笔的年龄最大', age, max)
                if age == 60.0:
                    h('60 s 窗·最老一笔正好 60 s（留着，不弹）')
                if self.beat.get('hist_n', 0) > len(s.xr_hist):
                    h('60 s 窗·弹出旧的')
                if L['tr'] is None:
                    h('报警·没读数那一拍照算')
            elif ln == 323:
                s = L['self']
                obs = L['obs']
                full, rising, cond = bool(L['full']), L['rising'], bool(L['cond'])
                wall_cold = s.wall < s.xr
                enough = s.full_s >= P['alarm_full_s']
                self.beat['alarm'] = dict(full=full, rising=rising, cond=cond, wall_cold=wall_cold, enough=enough, full_s=s.full_s,
                                          side=obs.side == 'C', sat=bool(obs.inner_sat_low), nwh=not obs.tec_withheld, on=bool(obs.output_on),
                                          margin_rise=(s.xr - s.xr_hist[0][1]) - P['alarm_rise_k'], margin_wall=s.wall - s.xr, dt=obs.dt,
                                          was_on=s.alarm_on)
                if full:
                    h('报警·顶满计时在累加')
                    if s.full_s == P['alarm_full_s']:
                        h('报警·顶满计时正好 = 60.0 s（≥ 成立）')
            elif ln == 325:
                h('报警·报出来（上升沿）')
        return self._local


class Shadow:
    """报警「只差一个条件」的记账：四个开关量各有一个「假如只有它不成立」的计时（别的三个成立就累加）。"""

    NAMES = (('side', '差 side（在加热棒侧）'), ('sat', '差 内环贴下限'), ('nwh', '差 没被挂起（tec_withheld）'), ('on', '差 输出开着'))

    def __init__(self):
        self.t = {k: 0.0 for k, _ in self.NAMES}
        self.fired = 0
        self.dropped = False

    def feed(self, a, tr, alarm):
        """a = 插桩在 _alarm 里记下的这一拍的条件；返回这一拍要记的分支名。"""
        out = []
        flags = {k: a[k] for k, _ in self.NAMES}
        for k, label in self.NAMES:
            others = all(v for kk, v in flags.items() if kk != k)
            self.t[k] = self.t[k] + a['dt'] if others else 0.0
            if others and not flags[k] and self.t[k] >= P['alarm_full_s'] and a['rising'] and a['wall_cold']:
                out.append('报警·不报：' + label + '，别的都成立满 60 s')
        if a['full'] and not a['enough'] and a['rising'] and a['wall_cold']:
            out.append('报警·不报：差 顶满不够 60 s，别的都成立')
            if a['full_s'] >= P['alarm_full_s'] - 3.0:
                out.append('报警·不报：顶满只差最后一拍（≥ 57 s）')
        if a['enough'] and not a['rising'] and a['wall_cold']:
            out.append('报警·不报：差 T̂ 还在升，别的都成立')
        if a['enough'] and a['rising'] and not a['wall_cold']:
            out.append('报警·不报：差 釜壁比釜内冷（釜壁估计 ≥ T̂），别的都成立')
        if a['cond'] and a['was_on']:
            out.append('报警·条件一直成立：不重报')
        if not a['cond'] and a['was_on']:
            out.append('报警·条件撤了：重新武装')
            self.dropped = True
        if alarm:
            self.fired += 1
            if self.fired >= 2 and self.dropped:
                out.append('报警·撤过以后再报（重新武装管用）')
            if tr is None or tr != tr:
                out.append('报警·没读数的拍上报出来')
            if a['full_s'] - a['dt'] < P['alarm_full_s'] and a['full_s'] != P['alarm_full_s']:
                out.append('报警·顶满计时数到 60 s 的那一拍报（拍长不齐）')
        return out


# ───────────────────────── 跑一场 ─────────────────────────
def run_scene(name, gen_fn, tracer=None, debug_fh=None):
    est = outer_m.M()
    gen = gen_fn()
    pin_col = bool(getattr(gen_fn, 'pin_col', False))
    lines = [HEADER + (',pin' if pin_col else '')]
    prev_pin = False
    shadow = Shadow()
    margins = dict(rise=math.inf, wall=math.inf)
    t = 0.0
    u = None
    k = 0
    stats = dict(tau_min=math.inf, tau_max=-math.inf, taue_min=math.inf, taue_max=-math.inf, to_min=math.inf, alarms=0, n_none=0, u_min=math.inf, u_max=-math.inf)
    try:
        inp = next(gen)
    except StopIteration:
        inp = None
    while inp is not None:
        dt = q(inp['dt'], 3)
        if k > 0:
            t = q(t + dt, 3)
        sp = q(inp['sp'], 4)
        tj = q(inp['tj'], 4)
        tr_in = inp['tr']
        no_read = tr_in is None or tr_in != tr_in
        tr = tr_in if no_read else q(tr_in, 4)
        first = bool(inp['first'])
        pin = bool(inp.get('pin'))
        assert pin_col or not pin, (name, k, '这一场没有 pin 列')
        o = Obs(t=t, dt=dt, sp=sp, tr=tr, tj=tj, tj_sp_prev=tj, duty=0.0, pid_out=0.0, side=inp['side'], output_on=bool(inp['output_on']),
                inner_sat_high=False, inner_sat_low=bool(inp['sat_low']), tec_withheld=bool(inp['withheld']), valve_open=False, first=first)
        spf_s = rate_s = ''
        if inp['sp_final'] is not ABSENT:
            v = inp['sp_final']
            o.sp_final = None if v is None else q(v, 4)
            spf_s = '' if v is None else '%.4f' % o.sp_final
        if inp['sp_rate'] is not ABSENT:
            v = inp['sp_rate']
            o.sp_rate = None if v is None else q(v, 6)
            rate_s = '' if v is None else '%.6f' % o.sp_rate
        # 印出来的输入读回去必须就是喂进去的那个数
        s_in = '%.3f,%.3f,%.4f,%s,%s,%s,%.4f,%s,%d,%d,%d,%d' % (
            t, dt, sp, spf_s, rate_s, '' if no_read else '%.4f' % tr, tj, o.side, o.inner_sat_low, o.tec_withheld, o.output_on, first)
        f = s_in.split(',')
        assert float(f[0]) == t and float(f[1]) == dt and float(f[2]) == sp and float(f[6]) == tj, (name, k)
        assert (f[5] == '') == no_read and (no_read or float(f[5]) == tr), (name, k)
        assert f[3] == '' or float(f[3]) == o.sp_final, (name, k)
        assert f[4] == '' or float(f[4]) == o.sp_rate, (name, k)
        assert dt > 0, (name, k)
        if tracer is not None:
            tracer.beat = {kk: vv for kk, vv in tracer.beat.items() if kk == 'last_med'}
            tracer.start()
        try:
            if first:
                est.reset(o)
            if pin:
                # LiveOuter.PinTarget(sp) 的等价写法：占位目标变了不算「目标变了」，跟 τ 有关的这一拍整个停着——
                # 过渡参考、上一拍的终点都摆到它上面；上一拍留下的门控归零；θ 跟 T₂、b₂ 的相关清零
                est.ref = o.sp
                if est.spf_prev is not None:
                    est.spf_prev = o.sp
                est.g_sp = 0.0
                est.P[0][2] = est.P[2][0] = 0.0
                est.P[1][2] = est.P[2][1] = 0.0
            p22_before, th_before, tau_before = est.P[2][2], est.th, est.tau
            u, alarm = est.step(o)
        finally:
            if tracer is not None:
                tracer.stop()
        alarm_i = 1 if alarm else 0
        if pin:
            # Pin 过的拍：门控正好是 0；τ̂、第二路的 θ、对 τ 的把握逐位不动
            assert est.g_sp == 0.0 and est.ref == o.sp and est.spf_prev == o.sp, (name, k)
            assert est.tau == tau_before and est.th == th_before and est.P[2][2] == p22_before, (name, k, '占位目标的拍跟 τ 有关的量动了')
            assert est.P[0][2] == 0.0 and est.P[1][2] == 0.0, (name, k)
            if tracer is not None:
                tracer.hit('占位目标·先 Pin 再 step（门控 = 0）')
                if first:
                    tracer.hit('占位目标·起算那一拍（reset → pin → step）')
                if no_read:
                    tracer.hit('占位目标·没读数：沿用上一个')
        elif prev_pin and tracer is not None:
            tracer.hit('占位目标·之后下发目标的那一拍')
            if est.P[2][2] > p22_before:
                tracer.hit('占位目标·下发目标那一拍把握放开了')
        prev_pin = pin
        if tracer is not None:
            a = tracer.beat.get('alarm')
            for tag in shadow.feed(a, tr, alarm_i):
                tracer.hit(tag)
            # 报警那一列会不会因为两边数学库差 1 ulp 而翻：记下离门槛最近的距离（只在翻了会改变 cond 的拍上记）
            if a['enough'] and a['wall_cold']:
                margins['rise'] = min(margins['rise'], abs(a['margin_rise']))
            if a['enough'] and a['rising']:
                margins['wall'] = min(margins['wall'], abs(a['margin_wall']))
            if first and k > 0:
                tracer.hit('第二次 reset（对象用过以后再 reset）')
            if dt != 2.0:
                tracer.hit('拍长 ≠ 2 s')
            tracer.ext('拍长种类', frozenset(['%g' % dt]), lambda a_, b_: a_ | b_)
        lines.append(s_in + ',%.9f,%.9f,%.9f,%.6f,%.6f,%.9f,%.9f,%.9f,%.6f,%.9f,%d,%d' % (
            est.wall, est.xr, est.b, est.tau, est.tau_e, est.k, est._p_up(), est.g_sp, est.to_eff, u, alarm_i, 1 if est.alarm_on else 0)
            + (',%d' % pin if pin_col else ''))
        if debug_fh is not None:
            Pm = est.P
            debug_fh.write(','.join([name, '%d' % k, repr(t), repr(dt)] + ['%.17g' % v for v in (
                est.wall, est.wall_rate, est.xr, est.b, est.eps_lp, est.to_eff, est.tau, est.k, est.ref, est.g_sp, u, est.pterm, est.gp,
                est.eT, est.eb, est.th, est.tau_e, Pm[0][0], Pm[0][1], Pm[0][2], Pm[1][1], Pm[1][2], Pm[2][2],
                est.f1, est.f2, est.g1, est.g2, est.S[0], est.S[1], est.S[2], est.S[3], est.S[4], est.S[5], est.full_s)]
                + ['|'.join('%.4f' % m for m in est.med), '%d' % len(est.xr_hist), '%d' % alarm_i, '%d' % est.alarm_on]) + '\n')
        for v in (est.wall, est.xr, est.b, est.tau, est.tau_e, est.k, est.g_sp, est.to_eff, u):
            assert v == v and abs(v) < 1e6, (name, k, '出了 NaN / 发散')
        stats['tau_min'] = min(stats['tau_min'], est.tau)
        stats['tau_max'] = max(stats['tau_max'], est.tau)
        stats['taue_min'] = min(stats['taue_min'], est.tau_e)
        stats['taue_max'] = max(stats['taue_max'], est.tau_e)
        stats['to_min'] = min(stats['to_min'], est.to_eff)
        stats['u_min'] = min(stats['u_min'], u)
        stats['u_max'] = max(stats['u_max'], u)
        stats['alarms'] += alarm_i
        stats['n_none'] += 1 if no_read else 0
        k += 1
        try:
            inp = gen.send(u)
        except StopIteration:
            inp = None
    stats['rows'] = k
    stats['dur'] = t
    stats['margins'] = margins
    stats['k_last'] = est.k
    return '\n'.join(lines) + '\n', stats


# 每一场必须走到的分支（少一条就断言失败）。名字 = Tracer / Shadow 里记的那些。
REQUIRED = {
    's01_step_plain': ['step·第一拍：不跑观测器', '中值·缓冲里 2 个数', '中值·缓冲里 3 个数', '终点·第一拍（上一拍终点 = 无）', '终点·变了但把握已在先验上：跳过',
                       '终点·把握放开（P₂₂ 往先验拉）', '比例·e > 0（tanh 封顶）', '比例·tanh > 0.9（贴着 P上限）', '比例·e < 0（线性，不封顶）', '比例·e = 0',
                       '第二路·门控 g = 0（不学 θ）', '第二路·门控 g > 0', '第一路·方差 ≤ 0：提前 return',
                       '斜坡·不走（坡速 0 且终点 = 此刻目标）', '第一路·回归值在限内'],
    's02_ramp': ['斜坡·min（升）取坡那一行', '斜坡·min（升）取「直接去终点」那一行', '斜坡·max（降）取坡那一行', '斜坡·max（降）取「直接去终点」那一行',
                 '斜坡·坡速 0 但终点 ≠ 此刻目标', '斜坡·坡速 > 0 但此刻目标 = 终点', '斜坡·坡速 < 0 但此刻目标 = 终点', '斜坡·min（此刻目标 = 终点）取坡那一行',
                 '斜坡·min（此刻目标 = 终点）取「直接去终点」那一行', '斜坡·不走（坡速 0 且终点 = 此刻目标）'],
    's03_retarget': ['终点·变了但把握已在先验上：跳过', '终点·变了：权重 > 0.5（大改）', '终点·变了：权重 0.001–0.5（小改）', '终点·变了：权重 < 0.001（极小改）',
                     '终点·把握放开（P₂₂ 往先验拉）', '终点·没变', '比例·e < 0（线性，不封顶）', '比例·e > 0（tanh 封顶）'],
    's04_tau_hi': ['第二路·θ 顶下边（τ₂ = 3·τ̂₀）清相关', '第二路·θ 越过下边被夹回', '第一路·回归值夹在上限 3·τ̂₀', 'τ̂ 往大改：b̂ 换算',
                   '第一路·下限 = τ̂₀（第二路 ≥ 出厂值：不许往出厂值以下改）', '拍长 ≠ 2 s'],
    's05_tau_lo_reseed': ['读数·没有（None）', '第二路·没读数：只推', '第二路·θ 顶上边（τ₂ = 0.4·τ̂₀）清相关', '第二路·θ 越过上边被夹回', '第一路·回归值夹在下限', 'τ̂ 往小改：b̂ 换算',
                          '第一路·下限 = 0.4·τ̂₀（第二路已到底）', '第一路·下限 = 第二路的 τ₂（0.4·τ̂₀ < τ₂ < τ̂₀）', '第一路·往小改那一行（÷ ta_down_mult）',
                          'K 贴下限 1', '第二次 reset（对象用过以后再 reset）', '拍长 ≠ 2 s'],
    's06_glitch': ['τ̂ 没变（Δ = 0）：b̂ 不换算', '第一路·相关 ≤ 0：这一拍不动 τ̂', '中值·这一拍的读数没进来（|读数 − 中值| ≥ 3 K）', '中值·输出跳了 ≥ 3 K（连坏两点第二个进来 / 真变了）'],
    's07_loss': ['读数·没有（None）', '读数·没有（NaN）', '没读数：第一路回归不跑、第二路只推不修、ε = 0', '第二路·没读数：只推', '报警·没读数那一拍照算',
                 '中值·缓冲里 2 个数', '中值·缓冲里 3 个数'],
    's08_far': ['变速·To′ 夹在下限 20 s', '变速·To′ 在 20–30 s 之间', '变速·To′ 在 30–149 s 之间'],
    's09_alarm_combo': ['报警·报出来（上升沿）', '报警·条件一直成立：不重报', '报警·条件撤了：重新武装', '报警·撤过以后再报（重新武装管用）',
                        '报警·不报：差 side（在加热棒侧），别的都成立满 60 s', '报警·不报：差 内环贴下限，别的都成立满 60 s',
                        '报警·不报：差 没被挂起（tec_withheld），别的都成立满 60 s', '报警·不报：差 输出开着，别的都成立满 60 s',
                        '报警·不报：差 顶满不够 60 s，别的都成立', '报警·不报：顶满只差最后一拍（≥ 57 s）', '报警·不报：差 T̂ 还在升，别的都成立',
                        '报警·不报：差 釜壁比釜内冷（釜壁估计 ≥ T̂），别的都成立', '报警·顶满计时正好 = 60.0 s（≥ 成立）',
                        '60 s 窗·最老一笔正好 60 s（留着，不弹）', '60 s 窗·弹出旧的'],
    's10_exo_dt': ['报警·报出来（上升沿）', '报警·条件撤了：重新武装', '报警·没读数那一拍照算', '报警·没读数的拍上报出来', '拍长 ≠ 2 s', '60 s 窗·弹出旧的',
                   '报警·不报：差 顶满不够 60 s，别的都成立', '报警·不报：差 T̂ 还在升，别的都成立', '报警·顶满计时数到 60 s 的那一拍报（拍长不齐）'],
    's11_dt_mix': ['拍长 ≠ 2 s', '终点·变了：权重 0.001–0.5（小改）'],
}
# 定稿参数下走不到的（照搬就行，对拍核不到）：写在这里是为了「走到了反而要报」
UNREACHABLE = ['第一路·权重和太小：提前 return', '比例·P上限 ≤ 1e-9：提前 return', '比例·硬夹（不该走到）', '第二路·P 对角线贴下限']


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--out', default=OUT_DIR)
    ap.add_argument('--coverage-out')
    ap.add_argument('--debug-dir')
    ap.add_argument('--no-trace', action='store_true')
    ap.add_argument('--only')
    a = ap.parse_args()
    md5 = hashlib.md5(open(OUTER_M_PATH, 'rb').read()).hexdigest()
    if md5 != OUTER_M_MD5:
        print('outer_m.py 不是定稿那一份（md5 %s ≠ %s）：插桩按行号挂，不认。' % (md5, OUTER_M_MD5))
        return 2
    assert os.path.samefile(outer_m.__file__, OUTER_M_PATH)
    os.makedirs(a.out, exist_ok=True)
    if a.debug_dir:
        os.makedirs(a.debug_dir, exist_ok=True)
    total = 0
    cover = {}
    stats_all = {}
    ok = True
    for name, fn in SCENES:
        if a.only and a.only not in name:
            continue
        tracer = None if a.no_trace else Tracer()
        dbg = open(os.path.join(a.debug_dir, name + '.wide.csv'), 'w', encoding='utf-8') if a.debug_dir else None
        if dbg:
            dbg.write('scene,k,t,dt,wall,wall_rate,xr,b,eps_lp,to_eff,tau,k_gain,ref,g_sp,u,pterm,gp,eT,eb,th,tau_e,P00,P01,P02,P11,P12,P22,'
                      'f1,f2,g1,g2,S0,S1,S2,S3,S4,S5,full_s,med,n_xr_hist,alarm,alarm_on\n')
        text, st = run_scene(name, fn, tracer, dbg)
        if dbg:
            dbg.close()
        path = os.path.join(a.out, name + '.csv')
        if a.no_trace:
            old = open(path, encoding='utf-8').read()
            same = old == text
            print('%-20s 不挂插桩重跑：%s' % (name, '逐字节一样' if same else '不一样！'))
            ok = ok and same
            continue
        with open(path, 'w', encoding='utf-8') as fh:
            fh.write(text)
        total += len(text.encode('utf-8'))
        cover[name] = tracer
        stats_all[name] = st
        miss = [r for r in REQUIRED.get(name, []) if tracer.c.get(r, 0) == 0]
        bad = [r for r in UNREACHABLE if tracer.c.get(r, 0) > 0]
        print('%-20s %4d 拍 %6.0f s  τ̂ %.0f…%.0f  τ₂ %.0f…%.0f  To′最小 %.1f  报警 %d 次  无读数 %d 拍  K末 %.3f  报警门槛最近距离 升 %.2e / 壁 %.2e  |ρ|最大 %s  u %.1f…%.1f' % (
            name, st['rows'], st['dur'], st['tau_min'], st['tau_max'], st['taue_min'], st['taue_max'], st['to_min'], st['alarms'], st['n_none'],
            st['k_last'], st['margins']['rise'], st['margins']['wall'],
            '/'.join('%.4f' % tracer.x.get('相关系数最大 |ρ%d%d|' % ij, 0.0) for ij in ((0, 1), (0, 2), (1, 2))), st['u_min'], st['u_max']))
        if miss:
            ok = False
            print('   !! 没走到：' + '；'.join(miss))
        if bad:
            ok = False
            print('   !! 走到了本该走不到的：' + '；'.join(bad))
        # 报警那一列不能悬在门槛上（两边数学库差 1 ulp 不许把它翻过来）
        if min(st['margins']['rise'], st['margins']['wall']) < 1e-6:
            ok = False
            print('   !! 报警条件离门槛不到 1e-6：对拍会不稳，改场景')
    if a.no_trace:
        return 0 if ok else 1
    # 覆盖表
    names = [n for n, _ in SCENES if n in cover]
    tags = []
    for n in names:
        for tname in cover[n].c:
            if tname not in tags:
                tags.append(tname)
    tags.sort()
    L = ['| 分支 | ' + ' | '.join(n.split('_')[0] for n in names) + ' |', '|---|' + '---|' * len(names)]
    for tname in tags:
        L.append('| %s | ' % tname + ' | '.join(('%d' % cover[n].c[tname]) if cover[n].c.get(tname) else '' for n in names) + ' |')
    table = '\n'.join(L)
    L2 = ['| 场 | 拍数 | 时长 s | 拍长 | τ̂ 范围 | τ₂ 范围 | To′ 最小 | tanh 最大 | 报警次数 | 无读数拍 | 字节 |', '|---|---|---|---|---|---|---|---|---|---|---|']
    for n in names:
        st = stats_all[n]
        L2.append('| %s | %d | %.0f | %s | %.1f…%.1f | %.1f…%.1f | %.2f | %s | %d | %d | %d |' % (
            n, st['rows'], st['dur'], ' / '.join(sorted(cover[n].x.get('拍长种类', []), key=float)), st['tau_min'], st['tau_max'], st['taue_min'], st['taue_max'],
            st['to_min'], ('%.4f' % cover[n].x['tanh 最大']) if 'tanh 最大' in cover[n].x else '—', st['alarms'], st['n_none'],
            os.path.getsize(os.path.join(a.out, n + '.csv'))))
    table2 = '\n'.join(L2)
    print()
    print(table2)
    print()
    print(table)
    print()
    print('合计 %d 场，%d 拍，%d 字节（上限 %d）' % (len(names), sum(stats_all[n]['rows'] for n in names), total, SIZE_LIMIT))
    never = [r for r in UNREACHABLE if all(cover[n].c.get(r, 0) == 0 for n in names)]
    print('定稿参数下没走到（照搬即可，对拍核不到）：' + '；'.join(never))
    if total > SIZE_LIMIT:
        ok = False
        print('!! 超过大小上限')
    if a.coverage_out:
        with open(a.coverage_out, 'w', encoding='utf-8') as fh:
            fh.write(table2 + '\n\n' + table + '\n')
    print('全部断言：' + ('过' if ok else '没过'))
    return 0 if ok else 1


if __name__ == '__main__':
    sys.exit(main())
