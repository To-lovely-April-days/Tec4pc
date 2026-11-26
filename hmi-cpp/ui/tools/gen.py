#!/usr/bin/env python3
"""把设计稿（HTML）翻成 LVGL 的界面代码。
设计稿是界面的唯一来源：位置、颜色、圆角、阴影、字号都从设计稿里读，不在 C++ 里手调。
设计稿里 data-id 标的文字 / 元素，程序运行时可以按这个名字改；data-act 标的元素点了会把这个动作名交给程序。

用法：gen.py <设计稿目录> <出图工具 shot> <缩放 1|2> 页面名...
产出：ui/gen/pages_<缩放>.inc（每页一个 build_<页面名> 函数）、ui/assets/img/<页面>_<n>_<缩放>.png（图标和图片）
"""
import json, re, subprocess, sys, pathlib, shutil
from PIL import Image
design, SHOT, S = pathlib.Path(sys.argv[1]), sys.argv[2], int(sys.argv[3]); pages = sys.argv[4:]
UI = pathlib.Path(__file__).resolve().parent.parent; B = UI / 'build'; IMG = UI / 'assets' / 'img'
B.mkdir(exist_ok=True); IMG.mkdir(parents=True, exist_ok=True)
# 让浏览器用产品里真用的那两套字体排版：字宽不一样，位置就不一样，必须和 LVGL 里用的是同一套
fl = B / 'fonts'
if not fl.exists(): fl.symlink_to(UI / 'assets' / 'fonts')
FACE = ''.join(f"@font-face{{font-family:'UiLatin';src:url('fonts/inter_{w}.ttf');font-weight:{w}}}@font-face{{font-family:'UiCjk';src:url('fonts/noto_sc_{w}.ttf');font-weight:{w}}}" for w in (400, 500, 600, 700))
FONTCSS = f"<style>{FACE} x-dc>div{{font-family:'UiLatin','UiCjk',sans-serif !important}}</style>"

def col(c):
    m = re.match(r'rgba?\(([^)]+)\)', c); v = [float(t) for t in m.group(1).split(',')]
    return '0x%02X%02X%02X' % tuple(int(t) for t in v[:3]), (v[3] if len(v) > 3 else 1.0)
def shadows(s):
    out = []
    for part in re.split(r',(?![^(]*\))', s):
        part = part.strip()
        if not part: continue
        c = re.match(r'rgba?\([^)]+\)', part).group(0); rest = part[len(c):].split(); nums = [float(t.replace('px', '')) for t in rest[:4]]
        out.append((c, *nums, 'inset' in rest))
    return out
def R(v): return int(v * S + 0.5) if v >= 0 else -int(-v * S + 0.5)
def cstr(t): return t.replace('\\', '\\\\').replace('"', '\\"')

code = ['// 这个文件是 ui/tools/gen.py 从设计稿生成的，不要手改；要改界面就改设计稿再重新生成。\n']
for name in pages:
    src = (design / f'{name}.dc.html').read_text(encoding='utf-8').replace('</body>', FONTCSS + '</body>')
    (B / f'{name}.html').write_text(src, encoding='utf-8')
    subprocess.run([str(UI / 'tools/evaljs'), str(B / f'{name}.html'), str(UI / 'tools/dump.js'), str(B / f'{name}.json')], check=True, capture_output=True)
    D = json.load(open(B / f'{name}.json'))
    # 图标和图片：把别的东西都藏起来，分别在白底、黑底上各拍一张，反推出带透明度的图
    for tag, bg in (('w', '#fff'), ('k', '#000')):
        scope = '[data-part] ' if name.endswith('Part') else ''      # 只要这一块里的图，页面上别的图标不能拍进来
        css = f'<style>*{{visibility:hidden!important}} {scope}svg,{scope}svg *,{scope}img{{visibility:visible!important}} x-dc>div{{background:{bg}!important}} body{{background:{bg}!important}}</style>'
        (B / f'_{name}_{tag}.html').write_text(src.replace('</body>', css + '</body>'), encoding='utf-8')
        subprocess.run([SHOT, str(B / f'_{name}_{tag}.html'), str(B / f'_{name}_{tag}.png'), '1280', '720', '1'], check=True, capture_output=True)
    Wp = Image.open(B / f'_{name}_w.png').convert('RGB'); Kp = Image.open(B / f'_{name}_k.png').convert('RGB')
    def cut(n):
        x0, y0 = round(n['x'] * 2) - 2, round(n['y'] * 2) - 2; x1, y1 = round((n['x'] + n['w']) * 2) + 2, round((n['y'] + n['h']) * 2) + 2
        w, k = Wp.crop((x0, y0, x1, y1)), Kp.crop((x0, y0, x1, y1)); out = Image.new('RGBA', w.size)
        pw, pk, po = w.load(), k.load(), out.load()
        for y in range(w.size[1]):
            for x in range(w.size[0]):
                a = max(0, min(255, 255 - max(pw[x, y][i] - pk[x, y][i] for i in range(3))))
                po[x, y] = tuple(min(255, round(pk[x, y][i] * 255 / a)) for i in range(3)) + (a,) if a else (0, 0, 0, 0)
        if S == 1: out = out.resize((out.size[0] // 2, out.size[1] // 2), Image.LANCZOS)
        # data-id 以 fx. 打头的（釜上的加热带、虚线圈、搅拌桨这些贴图）：文件名照名字取，不按出现顺序编号。
        # 程序要按名字直接贴它们，用顺序编号的话设计稿一改就串位
        stem = 'fx_' + n['id'][3:].replace('.', '_') if n.get('id', '').startswith('fx.') else f'{name}_{n["img"]}'
        f = IMG / f'{stem}_{S}.png'; out.save(f); return f'A:assets/img/{f.name}', -1 * S
    L = []; emit = L.append; info = {}; last_text = None
    byi = {n['i']: n for n in D}

    def box_of(i):
        """这个元素在哪块「牌子」里：往上找第一个带底色或者边框的祖先，没有就 -1"""
        while i is not None and i >= 0:
            m = byi.get(i)
            if m is None: return -1
            if m['tag'] != '#text':
                if col(m['bg'])[1] > 0 or 'inset' in (m.get('shadow') or ''): return i
            i = m['p']
        return -1

    def is_anc(a, b):
        while b is not None and b >= 0:
            if a == b: return True
            m = byi.get(b)
            if m is None: return False
            b = m['p']
        return False

    def left_neighbour(t):
        """这段字左边 20 px 以内、同一行上，有没有别的元素（不算包着它的那些壳）"""
        y0, y1 = t['y'] + t['h'] * 0.25, t['y'] + t['h'] * 0.75
        for m in D:
            if m['tag'] == '#text' or m['w'] <= 0 or m['h'] <= 0: continue
            if is_anc(m['i'], t['p']) or m['i'] == t['p']: continue
            r = m['x'] + m['w']
            if t['x'] - 20 <= r <= t['x'] + 1 and m['y'] < y1 and m['y'] + m['h'] > y0: return True
        return False

    for n in D:
        i, p = n['i'], n['p']; par = info.get(p, {'ax': 0, 'ay': 0, 'bl': 0, 'bt': 0})
        ax, ay = R(n['x']), R(n['y']); rx, ry = ax - par['ax'] - par['bl'], ay - par['ay'] - par['bt']
        w, h = R(n['x'] + n['w']) - ax, R(n['y'] + n['h']) - ay; P = f'o[{p}]' if p >= 0 else 'scr'; opa = n['opa']
        if n['tag'] == '#text':
            c, a = col(n['color'])
            cx, cy = R(n['x'] + n['w'] / 2) - par['ax'] - par['bl'], R(n['y'] + n['h'] / 2) - par['ay'] - par['bt']
            anc = n.get('anchor', 'c')
            # 设计稿里靠右（或居中）的字，变长是往左伸的。左边紧挨着还有别的东西（单选的小圆点、
            # 按钮里的图标）时就不能往左伸——那是设计稿里整块一起往左挪，我们这儿只能挪字。
            # 这种情况改成靠左：中文位置一个像素都不变（三种对齐方式算出来的落点就是设计稿量到的那个），
            # 变长的时候往右边的空处伸（2026-11-26：英文的「Not controlling」压住控温圆键、「Clock」压住小圆点）
            if anc != 'l' and left_neighbour(n):
                anc = 'l'
            axx = {'l': rx, 'r': rx + w, 'c': cx}[anc]
            args = f'"{cstr(n["text"])}", {int(n["fw"])}, {n["fs"]:g}, {c}, {int(a * opa * 255 + .5)}, {R(n["ls"])}, {w if n["lines"] > 1 else 0}'
            # 同一行里紧跟在前一段字后面的（卡片标题后面那句小字之类）：位置不写死，跟着前一段走。
            # 换成英文以后前面那段变长，写死的话就被压住了（2026-11-25 出图看到的「Device links」压着那句小字）
            # 跟着前一段走，但只在同一块「牌子」里（同一个带底色 / 边框的容器，或者两段都不在任何牌子里）：
            # 隔着两块牌子的两段字，前一段变长该往右让的是后面那块牌子本身，不是牌子里的字——
            # 串上去的话字会跑到牌子外面（2026-11-26 出图看到的「序列存在设备本机 · 最多 6 步」）
            prev = last_text
            after = (prev and anc == 'l' and n['lines'] == 1 and prev['lines'] == 1
                     and box_of(p) == box_of(prev['p'])
                     and abs(prev['cy'] - n['y'] - n['h'] / 2) < 3 and 0 <= n['x'] - prev['right'] <= 48)
            if after:
                call = f'txta_shift({P}, {axx}, {cy}, t[{prev["i"]}], {R(prev["w"])}, {args})'
            else:
                call = f'txta({P}, {axx}, {cy}, \'{anc}\', {args})'
            emit(f't[{i}] = ' + call + ';')
            if n['id']:
                emit(f'ui.reg_text("{cstr(n["id"])}", t[{i}], \'{anc}\', {axx}, {cy});')
                # 跟着前一段走的：把这层关系也告诉界面，前一段的字一换（「通道 1」→ Channel 1），这一段自己往右让
                if after: emit(f'ui.reg_follow("{cstr(n["id"])}", t[{prev["i"]}], {R(prev["w"])});')
            last_text = {'i': i, 'p': p, 'right': n['x'] + n['w'], 'cy': n['y'] + n['h'] / 2, 'lines': n['lines'], 'w': n['w']}
            continue
        if 'img' in n:
            f, d = cut(n); call = f'im({P}, {rx + d}, {ry + d}, "{f}")'
            if n['id']: emit(f'ui.reg_box("{cstr(n["id"])}", {call});')
            else: emit(call + ';')
            if n['act']: emit(f'// 图上的动作 {n["act"]} 由它外面那个元素接')
            continue
        info[i] = {'ax': ax, 'ay': ay, 'bl': 0, 'bt': 0}
        emit(f'o[{i}] = mk({P}, {rx}, {ry}, {w}, {h}, {n["clip"]});')
        c, a = col(n['bg'])
        # 弹窗页的最外层不涂底色：它是叠在别的页面上面的，底下要透出来（暗下来的那一层是它里面的第一个元素）
        if a > 0 and not (p < 0 and name.endswith('Dlg')): emit(f'bg(o[{i}], {c}, {int(a * opa * 255 + .5)});')
        if n['bgi']: print(name, '没处理的背景图', n['bgi'], file=sys.stderr)
        if n['rad'] > 0: emit(f'lv_obj_set_style_radius(o[{i}], {"LV_RADIUS_CIRCLE" if n["rad"] * 2 >= min(n["w"], n["h"]) - .6 else R(n["rad"])}, 0);')
        if n['clip'] and n['rad'] > 0: emit(f'lv_obj_set_style_clip_corner(o[{i}], true, 0);')
        for (c0, x, y, blur, spread, inset) in shadows(n['shadow']):
            c, a = col(c0); A = int(a * opa * 255 + .5)
            if inset and x == 0 and y == 0:
                bw = max(1, R(spread)); emit(f'bd(o[{i}], {c}, {A}, {bw}, LV_BORDER_SIDE_FULL);'); info[i]['bl'] = info[i]['bt'] = bw
            elif inset:
                side = 'LV_BORDER_SIDE_BOTTOM' if y < 0 else 'LV_BORDER_SIDE_TOP' if y > 0 else 'LV_BORDER_SIDE_LEFT' if x > 0 else 'LV_BORDER_SIDE_RIGHT'
                bw = max(1, R(abs(x) + abs(y))); emit(f'bd(o[{i}], {c}, {A}, {bw}, {side});')
                if side.endswith('LEFT'): info[i]['bl'] = bw
                if side.endswith('TOP'): info[i]['bt'] = bw
            elif blur == 0: emit(f'ol(o[{i}], {c}, {A}, {max(1, R(spread))});')
            else: emit(f'sh(o[{i}], {c}, {A}, {R(blur)}, {R(x)}, {R(y)}, {R(spread)});')
        if n['id']: emit(f'ui.reg_box("{cstr(n["id"])}", o[{i}]);')
        if n['act']: emit(f'ui.reg_act(o[{i}], "{cstr(n["act"])}");')
    # Fx 打头的是「贴图稿」：只要它出的图（釜上的加热带、虚线圈、搅拌桨），不要页面代码——
    # 那是一页排开的素材，不是给人看的页面，进了页面表还要白白预热一遍
    if not name.startswith('Fx'):
        code.append(f'static void build_{name}(lv_obj_t* scr, Ui& ui) {{\n    static lv_obj_t* o[{len(D) + 1}];\n    static lv_obj_t* t[{len(D) + 1}];\n    (void)t;\n    ' + '\n    '.join(L) + '\n}\n')
    print(f'{name}: {len(D)} 个元素，{sum(1 for n in D if n.get("id"))} 处可改，{sum(1 for n in D if n.get("act"))} 处可点')
code.append('static const PageDef kPages[] = {\n' + ''.join(f'    {{"{n}", build_{n}}},\n' for n in pages if not n.startswith('Fx')) + '};\n')
(UI / 'gen' / f'pages_{S}.inc').write_text('\n'.join(code), encoding='utf-8')
