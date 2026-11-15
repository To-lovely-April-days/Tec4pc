# -*- coding: utf-8 -*-
"""
外环插口的最小定义：outer_m.py 只用到这里的 OuterLoop（基类）和 Obs（一拍的输入）。

原件是试算台的 bench/outer_api.py（2026-11-14 那一版）；这里只留这两样，试算台自己的参考外环（PurePI）、
按名字装载外环的 load_factory 都没带过来——对拍数据用不着。Obs 的字段、次序、含义跟原件一字不差。
"""
from dataclasses import dataclass


@dataclass
class Obs:
    t: float                 # 现场秒，从下发目标算
    dt: float                # 离上一次外环更新多少秒（外环周期，2 s）
    sp: float                # 釜内目标（℃）
    tr: float                # 釜内读数（℃）：0.01 K 量化、约 1 s 一个新数、可能带锯齿 / TEC 通电偏低
    tj: float                # 夹套读数（℃）：带读数噪声
    tj_sp_prev: float        # 上一拍给内环的夹套目标（夹过的）；第一拍 = 夹套读数
    duty: float              # 上一拍写出去的占空比（%，带符号：正 = 加热棒，负 = TEC 制冷；TEC 侧已乘过加热比）
    pid_out: float           # 上一拍内环 PID 的输出（PID 那一侧的量，没乘加热比）
    side: str                # 热源侧（继电器）：'H' 加热棒 / 'C' TEC
    output_on: bool          # 输出开着（扳继电器那约 1 s 是关的）
    inner_sat_high: bool     # 内环顶在上限（加热棒顶满）
    inner_sat_low: bool      # 内环贴在下限（TEC 顶满；等水期间下限是 0，贴着 0 也算）
    tec_withheld: bool       # 制冷段挂起：要 TEC 但在等人答开不开冷却水 / 等冷水机最短间隔，回路按只加热、下限钳 0
    valve_open: bool         # 冷却水阀开着
    first: bool              # 这一场的第一拍


class OuterLoop:
    name = 'outer'

    def reset(self, obs: Obs):
        pass

    def step(self, obs: Obs):
        raise NotImplementedError

    def debug(self):
        return None
