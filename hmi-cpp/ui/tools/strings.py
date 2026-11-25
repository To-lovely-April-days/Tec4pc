# 把界面上要翻的句子抽出来，汇成一张表：ui/assets/lang/strings.tsv
#   python3 ui/tools/strings.py
#
# 抽两处：
#   1. ui/gen/pages_1.inc 里 txt(…) 画的字——设计稿翻过来的那些，界面上的标题、按钮、说明都在这儿
#   2. ui/src/*.inc 和 main.cpp 里 set_text(…, "整句") ——运行时写上去的、本身就是一整句的那些
# 拼出来的句子（"通道 " + n + " 正在控温"）抽不了：对不上表，只能一条条改成带占位符的整句，
# 这里只统计还有多少条，好知道离「全翻完」还差多远。
#
# 再跑一遍不会把已经填好的译文冲掉：原表读进来，老条目保留，新条目追加，没人用的条目移到文件末尾注释掉。
import pathlib, re, sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
OUT = ROOT / 'ui/assets/lang/strings.tsv'
LANGS = ['en', 'ja', 'ko', 'de', 'fr', 'es', 'ru']
HAN = re.compile(r'[一-鿿]')
ONE = re.compile(r'[\u4e00-\u9fff]')      # 单个汉字：拼音键盘上的候选字，不是界面上的字


def c_literals(text):
    """C++ 源码里的字符串字面量（会把 \" 之类还原回来）"""
    for m in re.finditer(r'"((?:[^"\\]|\\.)*)"', text):
        yield m.group(1).replace('\\"', '"').replace('\\\\', '\\').replace('\\n', '\n')


def collect():
    want, frag = [], 0
    seen = set()

    # 语言自己的名字不翻：English 在哪种语言里都写作 English
    NAMES = {'简体中文', '日本語', '한국어'}

    def add(s, ime_risk=False):
        if s in NAMES:
            return
        # 设计稿里的单个汉字不收：那是拼音键盘上的候选字（结 / 界 / 接…），运行时由词典填，不是界面上的字。
        # 界面代码里的单字要收——「开 / 关」这种是真的要翻
        if ime_risk and ONE.fullmatch(s):
            return
        if s and HAN.search(s) and s not in seen:
            seen.add(s)
            want.append(s)

    # 1. 设计稿翻过来的页面代码。只认 txt() 的第四个参数（画出来的那句话）
    gen = (ROOT / 'ui/gen/pages_1.inc').read_text(encoding='utf-8')
    # txt / txta / txta_shift 的参数个数不一样，字在第几个也不一样：
    # 直接找「函数名后面第一个字符串」——前面那些参数里不会有引号
    for m in re.finditer(r'\btxt[a-z_]*\([^"\n]*"((?:[^"\\]|\\.)*)"', gen):
        add(m.group(1).replace('\\"', '"').replace('\\\\', '\\'), True)

    # 2. 控制核心里那几台设备的名字：界面上照抄着显示，不翻的话通信那一页全是中文
    # 控制核心里几句界面上会原样显示的（炉次名之类）：不在界面代码里，但屏上看得见
    for t in ('手动控温',):
        add(t)

    # 一行一行地找：整篇一起找的话，某一行里的 '"' 这种字符字面量会把引号配对带偏，
    # 后面的字符串就全认错了（2026-11-25 踩过：整篇扫出来一条设备名都没有）
    for line in (ROOT / 'core/data/store.cpp').read_text(encoding='utf-8').splitlines():
        line = re.sub(r'//.*', '', line)
        for lit in c_literals(line):
            if len(lit) < 40 and not re.search(r'[;{}()=]', lit):      # 只要像个名字的
                add(lit)

    # 3. 控制核心给出来的话：拒绝某条命令的原因、报警、限值说明——这些都会原样摆到屏上。
    # 太短的不要：那是写进 CSV 事件日志的类别（「标记」「快照」「步骤」），屏上看不见。
    # run_recorder.cpp 整个不要：它写的是文件名和表头，核心自己落盘用的，跟界面语言无关
    for f in ('core/app/core_app.cpp', 'core/station/station.cpp', 'core/station/aux_runner.cpp', 'core/sequence/sequence.cpp'):
        for line in (ROOT / f).read_text(encoding='utf-8').splitlines():
            line = re.sub(r'//.*', '', line)
            for lit in c_literals(line):
                if 4 <= len(lit) < 90 and '%' not in lit:
                    add(lit)

    # 3. 运行时写上去的字。只收「整句就是一个字面量」的，拼出来的留给以后
    for f in sorted((ROOT / 'ui/src').glob('*.inc')) + [ROOT / 'ui/src/main.cpp']:
        src = f.read_text(encoding='utf-8')
        src = re.sub(r'//[^\n]*', '', src)                      # 注释里的中文不算
        # set_text(…) 这一整句里的字面量都收，但这一句里有 + 的不收：
        # 那是拼出来的（"通道 " + 号 + " 正在控温"），半截话翻不了，得先改成带占位符的整句。
        # 这样写才收得到三选一那种： set_text(id, a ? "完整" : b ? "未齐" : "未译")
        for m in re.finditer(r'set_text(?:_raw)?\([^,]*,([^;]*?)\);', src):
            call = m.group(1)          # 只看逗号后面那一段：第一个参数里的 + 是拼 data-id，不是拼句子
            if '+' in call:
                continue
            for lit in re.finditer(r'"((?:[^"\\]|\\.)*)"', call):
                add(lit.group(1).replace('\\"', '"').replace('\\\\', '\\'))
        # 核心不答话时顶上的那句默认话：std::string("没存进去") 这种，最后也是 set_text 摆上去的
        for m in re.finditer(r'std::string\(\s*"((?:[^"\\]|\\.)*)"\s*\)', src):
            add(m.group(1).replace('\\"', '"'))
        for m in re.finditer(r'fmt\(\s*"((?:[^"\\]|\\.)*)"', src):      # 带占位符的整句
            add(m.group(1).replace('\\"', '"'))
        for m in re.finditer(r'tr\(\s*"((?:[^"\\]|\\.)*)"', src):       # ui::tr("…") 单独拎出来翻的
            add(m.group(1).replace('\\"', '"'))
        # 拼出来的有多少：set_text(… , "…" + 别的) 这种
        frag += len(re.findall(r'set_text\(\s*[^,]+,\s*"(?:[^"\\]|\\.)*"\s*\+', src))
        frag += len(re.findall(r'set_text\(\s*[^,]+,\s*[A-Za-z_][\w:.\[\]()]*\s*\+\s*"', src))
    return want, frag


def read_old():
    rows, head = {}, LANGS
    if OUT.exists():
        lines = OUT.read_text(encoding='utf-8-sig').splitlines()
        if lines:
            head = lines[0].split('\t')[1:]
            for ln in lines[1:]:
                if not ln.strip() or ln.startswith('# 下面这些'):
                    continue
                if ln.startswith('# '):
                    ln = ln[2:]
                c = ln.split('\t')
                rows[c[0]] = {h: (c[i + 1] if i + 1 < len(c) else '') for i, h in enumerate(head)}
    return rows, head


def main():
    want, frag = collect()
    old, head = read_old()
    for h in LANGS:
        if h not in head:
            head.append(h)
    OUT.parent.mkdir(parents=True, exist_ok=True)
    out = ['\t'.join(['中文'] + head)]
    for s in want:
        tr = old.get(s, {})
        out.append('\t'.join([s] + [tr.get(h, '') for h in head]))
    gone = [s for s in old if s not in set(want)]
    if gone:
        out.append('')
        out.append('# 下面这些界面上已经没有了，留着免得以后改回来又要重翻；要清掉直接删行')
        for s in gone:
            out.append('# ' + '\t'.join([s] + [old[s].get(h, '') for h in head]))
    OUT.write_text('﻿' + '\n'.join(out) + '\n', encoding='utf-8')
    done = sum(1 for s in want if old.get(s, {}).get('en'))
    print(f'{OUT.relative_to(ROOT)}：{len(want)} 条要翻，英文已填 {done} 条'
          + (f'，淘汰 {len(gone)} 条' if gone else ''))
    print(f'另有约 {frag} 处是拼出来的句子，表里收不了——要翻得先改成带占位符的整句')


main()
