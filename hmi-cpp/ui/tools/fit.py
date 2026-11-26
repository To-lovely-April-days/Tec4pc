# 「哪一句话会摆到哪个位置上」——给 tec_hmi --fit 用的表：
#   python3 ui/tools/fit.py > /tmp/fit.tsv && ./build/tec_hmi --fit /tmp/fit.tsv --lang en
#
# 为什么要它：提示句和报警句平时屏上没有（要命令被拒、要 U 盘没插、要设备真报警才冒一句），
# 出图和 --lint 都量不到；而它们恰恰是最长的句子，换成英文、德文最容易放不下。
# 抽法：界面代码里 set_text("位置名", 表达式) ——表达式里的整句，加上表达式用到的那几个变量
# 在这个文件里被赋过的整句；表达式里带 error 的，就把控制核心所有拒绝理由都算上（最坏情况全试一遍）。
# 一行三列：位置名、中文原句、占位符填什么（不写就填 180.0；要给每个占位符各填一个就用「｜」分开）
#
# 会多抽：同一个文件里几十处都叫 msg / tip 的局部变量分不开，所以一个位置会多出几句它其实
# 显示不到的话。这是故意的——宁可多量几句，也不要漏掉真会冒出来的那一句。所以报出来的要人看一眼：
# 换成中文再跑一遍（--lang zh）当底子，两边一对比，剩下的才是「只有外文才放不下」的。
#
# 对完之后还剩这几处是没用的，别再去改（2026-11-26 一条条核过）：
#   · ctl.run.t1 / sf.chip / ex.chip —— 这几块牌子的宽度是页面代码按字长现算的（lv_obj_set_width），
#     这里搭页面不跑那段代码，所以按设计稿的宽度量，必然说「出框」
#   · ctl.target / ctl.target.u / f.arr / f.arr.u —— 这几处只放数（f1 / f0 / f2），
#     抽到它名下的那些句子是同文件里别的同名变量带进来的，屏上进不去
#   · cv.desc / cv.seg —— 跟它们并排的 cv.desc、cv.left 真显示那一句的时候是空的（见 cry_page.inc），
#     这里没清，量出来是假重叠。cv.seg 还是「拦长句」的位置（ui_kit.cpp 的 kFitIds）：
#     放不下的时候它就占满自己那一栏，于是必然压到那两段空字上
#   · seq.chip —— 和 ex.chip 一样是现算宽度的胶囊
import pathlib, re, sys, collections

ROOT = pathlib.Path(__file__).resolve().parents[2]
HAN = re.compile(r'[一-鿿]')
# 占位符里填的不是数的那几句：按真会填进去的东西量，不然量出来短一截
FILL = {
    '安全层已执行：{0}': '停止控温，搅拌保持',
    '点格子改温度和用时，变化看右边曲线　·　已有 {0} 段（最多 {1} 段）': '12',
    '夹套目标 {0} ℃ · 出力 {1} %（{2}）': '180.0｜100｜TEC 制冷',
    '已选 {0} / {1}': '12',
    '共 {0} 条 · 控制核心这次开机以来的': '24',
    '已加第 {0} 个时间标记': '12',
    '通道 {0} · {1} · 还有 {2} 条': '2｜23:59:59｜9',
    '通道 {0} · {1}': '2｜23:59:59',
    # 串口 / Modbus 那几句：坑里填的是口名、毫秒数、「应答」这种，不是温度
    '{0} ms 内没等到完整{1}——从站没上电、站号 / 波特率不对，或者线断了': '500｜应答',
    '应答烂尾：{0}收到一半后没有新字节——多半是 485 上的干扰把这一帧掐断了': '应答数据',
    '应答站号不对：问的是 {0} 号，答话的是 {1} 号——查一下站号和波特率': '247',
    '从站回了异常码 {0}': '2',
    '打不开串口 {0}{1}': 'COM15｜（系统错误 5：拒绝访问）',
    '读不到串口 {0} 的参数{1}': 'COM15｜（系统错误 5：拒绝访问）',
    '设不了串口 {0} 的参数{1}': 'COM15｜（系统错误 5：拒绝访问）',
    '串口 {0} 写不出去{1}': 'COM15｜（系统错误 5：拒绝访问）',
    '串口 {0} 读出错{1}': 'COM15｜（系统错误 5：拒绝访问）',
    '设置里没有这些设备：{0}': 'ph1、ph2',
    '不认识的命令：{0}': 'temp.start',
    '处理命令时出错：{0}': '温控器或继电器模块没连上',
    '{0} · 固件 {1}': 'RD105｜1.20',
    '认不准：{0} 都对得上，请人工指定': 'pH 电极',
    '认不准：{0}、{1} 都对得上，请人工指定': 'pH 电极｜进料系统',
    '认不准：{0}、{1}、{2} 都对得上，请人工指定': 'pH 电极｜进料系统｜搅拌驱动',
    '认不准：{0}、{1}、{2}、{3} 都对得上，请人工指定': 'pH 电极｜进料系统｜搅拌驱动｜冷水机主板',
    '拷到 U 盘没成功：{0}': '没找到 U 盘：插上以后再点一次',
    '没连上：{0}': '打不开串口',
    '已删除「{0}」': '降温结晶-慢速',
    '「{0}」的参数已保存，重启控制核心后生效': '釜内温控器',
}
DEFAULT_FILL = '180.0'
# 这几个位置量不出有用的东西，别往表里放（放了就是上千条假警报，真的那条反而翻不着）：
#   · seq.chip / ex.chip —— 胶囊的宽度是页面代码按字长现算的，搭一页不跑那段代码
#   · cv.seg —— 它是「拦长句」的位置，长了就占满自己那一栏；而并排的 cv.desc、cv.left
#     真显示那一句的时候是空的（这里没清），于是每一条都报「压字」
# 这三处不靠这张表看，靠 ui_kit.cpp 的 kFitIds：名字还在不在，--fit 自己会报
#   · 只放数的那些格子（ctl.target、f.arr、f.stir…）—— 抽到它们名下的句子是同一个文件里
#     别的同名变量带进来的，屏上进不去；det.title 同理
#   · pg.info —— 右边有话要说的时候，页面代码把左边那句小字收起来了（pid_page.inc 的 pg.sub），
#     这里没收，量出来是假重叠
#   · det.badge —— 牌子的宽度也是现算的；ctl.temp.status.sub 只摆得下「釜内控温 Tr」那三句，
#     抽到它名下的长句是同文件里别的变量带进来的
NOISY = {'seq.chip', 'ex.chip', 'cv.seg', 'pg.info', 'det.badge', 'ctl.temp.status.sub',
         'ctl.target', 'ctl.target.u', 'ctl.target.l', 'ctl.run.t1', 'sf.chip', 'cv.desc',
         'f.arr', 'f.arr.u', 'f.arr.l', 'f.stir', 'f.target.l', 'det.title'}
# 不是 set_text("位置名", "整句") 这么写的，抽不着，手工补上
EXTRA = {
    # 设备连不上时状态行上那一句：核心当作 offline 交过来（apps/tec_core/main.cpp）
    'ctl.temp.status': ['温控器或继电器没连上，不能控温。去「设置 → 通信设置」核对串口'],
}


def lits(s):
    return [m.group(1).replace('\\"', '"').replace('\\\\', '\\')
            for m in re.finditer(r'"((?:[^"\\]|\\.)*)"', s)]


def core_messages():
    """控制核心会原样摆到屏上的话：拒绝命令的理由、报警句，还有串口 / Modbus 抛上来的那些
    （命令出错时核心把它们套进「处理命令时出错：{0}」交给界面，所以也会摆到同一个位置上）"""
    out = []
    def take(text, pat):
        for line in text.splitlines():
            line = re.sub(r'//.*', '', line)
            for m in re.finditer(pat, line):
                for l in lits(m.group(1)):
                    if HAN.search(l) and len(l.strip()) >= 4 and l not in out:
                        out.append(l)
    for f in ('core/app/core_app.cpp', 'core/station/station.cpp',
              'core/station/aux_runner.cpp', 'core/sequence/sequence.cpp'):
        take((ROOT / f).read_text(encoding='utf-8'), r'\b(?:fail|reject|trip)\s*\(([^;]*)')
    for f in ('io/win_serial.cpp', 'io/modbus_rtu.cpp', 'core/app/real_devices.cpp',
              'core/station/aux_hardware.cpp', 'core/app/port_scan.cpp'):
        take((ROOT / f).read_text(encoding='utf-8'),
             r'throw (?:TextError|ModbusError|std::runtime_error)\(([^;]*)')
    # 「控温设备没连上」那一句不是直接写在 fail() 里的：开机时存进 offline_reason，
    # 之后每条开始控温的命令都拿它当拒绝理由（core_app.cpp 的 fail(offline_reason)）。
    # 它又是全工程最长的一句，漏掉它等于这张表白量 —— 所以单独捞一下。
    take((ROOT / 'apps/tec_core/main.cpp').read_text(encoding='utf-8'), r'\boffline\s*=\s*("[^"]*")')
    return out


def main():
    core = core_messages()
    cand = collections.defaultdict(list)
    files = sorted((ROOT / 'ui/src').glob('*.inc')) + [ROOT / 'ui/src/main.cpp']
    for f in files:
        if f.name in ('lint.inc', 'phrase_dict.inc', 'pinyin_dict.inc'):
            continue
        src = re.sub(r'//[^\n]*', '', f.read_text(encoding='utf-8'))
        # 这个文件里，哪个变量被赋过哪些整句。一个文件一张表：同名的局部变量（msg、tip、sub）
        # 到处都有，全工程混成一张的话每个位置都会多出几十条别处的话
        var = collections.defaultdict(list)
        asgn = []
        for m in re.finditer(r'([A-Za-z_]\w*(?:\[[^\]]*\])?(?:\.[A-Za-z_]\w*(?:\[[^\]]*\])?)*)\s*=\s*([^;]*);', src):
            key = re.sub(r'\[[^\]]*\]', '', m.group(1))
            for l in lits(m.group(2)):
                if HAN.search(l):
                    var[key].append(l)
            if re.search(r'"error"|e\.what', m.group(2)):
                var[key].append('@CORE@')          # 这儿会摆核心给的话，最坏情况全试
            asgn.append((key, m.group(2)))
        # 核心的话常常是转一道手才摆上去的：msg 里装的是核心给的理由，
        # 再 tip = msg.empty() ? … : msg，屏上写的是 tip。不往下传的话，
        # 导出页、序列页、PID 页那几处就白白漏掉了（2026-11-27 查「长句压住右边的图」时发现的）
        for _ in range(4):
            grew = False
            for key, rhs in asgn:
                # 只往「本来就装整句话」的变量里传：同一行里赋过中文整句的才算。
                # 不加这条的话 x = y + 1 这种也跟着染上，数字格子也会被当成会摆长句的位置
                if '@CORE@' in var.get(key, ()) or not any(v != '@CORE@' for v in var.get(key, ())):
                    continue
                for v in re.finditer(r'[A-Za-z_]\w*(?:\[[^\]]*\])?(?:\.[A-Za-z_]\w*(?:\[[^\]]*\])?)*', rhs):
                    if '@CORE@' in var.get(re.sub(r'\[[^\]]*\]', '', v.group(0)), ()):
                        var[key].append('@CORE@'); grew = True; break
            if not grew:
                break
        for m in re.finditer(r'set_text(?:_raw)?\(\s*"([^"]+)"\s*,([^;]*?)\)\s*;', src):
            wid, expr = m.group(1), m.group(2)
            for l in lits(expr):
                if HAN.search(l):
                    cand[wid].append(l)
            for v in re.finditer(r'[A-Za-z_]\w*(?:\[[^\]]*\])?(?:\.[A-Za-z_]\w*(?:\[[^\]]*\])?)*', expr):
                key = re.sub(r'\[[^\]]*\]', '', v.group(0))
                if key in var:
                    cand[wid].extend(var[key])

    for wid, lst in EXTRA.items():
        cand[wid].extend(lst)
    rows, n = [], 0
    for wid in sorted(cand):
        if wid in NOISY:
            continue
        seen = set()
        for s in cand[wid]:
            for one in (core if s == '@CORE@' else [s]):
                if one in seen:
                    continue
                seen.add(one)
                rows.append((wid, one, FILL.get(one, DEFAULT_FILL) if '{' in one else ''))
        n += 1
    print(f'{n} 个位置，{len(rows)} 条要量（核心的话 {len(core)} 条）', file=sys.stderr)
    for wid, s, fill in rows:
        print(wid + '\t' + s + ('\t' + fill if fill else ''))


main()
