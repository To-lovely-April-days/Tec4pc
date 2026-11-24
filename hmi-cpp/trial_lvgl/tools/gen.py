#!/usr/bin/env python3
"""把设计稿页面的元素清单（evaljs + dump.js 导出的 json）翻成 LVGL 的 C 代码。
试做用：目的是看 LVGL 画同一张设计稿能还原到几成，所以位置、颜色、圆角、阴影全部照设计稿来，不手调。
用法：gen.py <设计稿 html> <页面名> <缩放 1|2>"""
import json, re, subprocess, sys, pathlib
from PIL import Image
html, name, S = pathlib.Path(sys.argv[1]), sys.argv[2], int(sys.argv[3])
here = pathlib.Path(__file__).resolve().parent.parent; B = here / 'build'; (B / 'img').mkdir(parents=True, exist_ok=True)
SHOT = sys.argv[4]
subprocess.run([str(here / 'tools/evaljs'), str(html), str(here / 'tools/dump.js'), str(B / f'{name}.json')], check=True, capture_output=True)
D = json.load(open(B / f'{name}.json'))
# 图标和图片：把页面里别的东西都藏起来，分别在白底、黑底上各拍一张，反推出带透明度的图
src = html.read_text(encoding='utf-8')
for tag, bg in (('w', '#fff'), ('k', '#000')):
    css = f'<style>*{{visibility:hidden!important}} svg,svg *,img{{visibility:visible!important}} x-dc>div{{background:{bg}!important}} body{{background:{bg}!important}}</style>'
    (B / f'_{name}_{tag}.html').write_text(src.replace('</body>', css + '</body>'), encoding='utf-8')
    subprocess.run([SHOT, str(B / f'_{name}_{tag}.html'), str(B / f'_{name}_{tag}.png'), '1280', '720', '1'], check=True, capture_output=True)
Wp = Image.open(B / f'_{name}_w.png').convert('RGB'); Kp = Image.open(B / f'_{name}_k.png').convert('RGB')
def cut(n):
    x0, y0 = round(n['x'] * 2) - 2, round(n['y'] * 2) - 2; x1, y1 = round((n['x'] + n['w']) * 2) + 2, round((n['y'] + n['h']) * 2) + 2
    w, k = Wp.crop((x0, y0, x1, y1)), Kp.crop((x0, y0, x1, y1)); out = Image.new('RGBA', w.size)
    pw, pk, po = w.load(), k.load(), out.load()
    for y in range(w.size[1]):
        for x in range(w.size[0]):
            a = 255 - max(pw[x, y][i] - pk[x, y][i] for i in range(3)); a = max(0, min(255, a))
            po[x, y] = tuple(min(255, round(pk[x, y][i] * 255 / a)) for i in range(3)) + (a,) if a else (0, 0, 0, 0)
    if S == 1: out = out.resize((out.size[0] // 2, out.size[1] // 2), Image.LANCZOS)
    f = B / 'img' / f'{name}_{n["img"]}_{S}.png'; out.save(f); return f'A:build/img/{f.name}', -1 * S   # 四周多切了 1 个设计像素
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
L = []; emit = L.append
info = {}
for n in D:
    i, p = n['i'], n['p']; par = info.get(p, {'ax': 0, 'ay': 0, 'bl': 0, 'bt': 0})
    ax, ay = R(n['x']), R(n['y']); rx, ry = ax - par['ax'] - par['bl'], ay - par['ay'] - par['bt']
    w, h = R(n['x'] + n['w']) - ax, R(n['y'] + n['h']) - ay; P = f'o[{p}]' if p >= 0 else 'scr'; opa = n['opa']
    if n['tag'] == '#text':
        c, a = col(n['color']); t = n['text'].replace('\\', '\\\\').replace('"', '\\"')
        cx, cy = R(n['x'] + n['w'] / 2) - par['ax'] - par['bl'], R(n['y'] + n['h'] / 2) - par['ay'] - par['bt']
        emit(f'txt({P}, {cx}, {cy}, "{t}", {int(n["fw"])}, {n["fs"]:g}, {c}, {int(a * opa * 255 + .5)}, {R(n["ls"])}, {w if n["lines"] > 1 else 0});'); continue
    if 'img' in n:
        f, d = cut(n); emit(f'im({P}, {rx + d}, {ry + d}, "{f}");'); continue
    info[i] = {'ax': ax, 'ay': ay, 'bl': 0, 'bt': 0}
    emit(f'o[{i}] = mk({P}, {rx}, {ry}, {w}, {h}, {n["clip"]});')
    c, a = col(n['bg'])
    if a > 0: emit(f'bg(o[{i}], {c}, {int(a * opa * 255 + .5)});')
    if n['bgi']: print('没处理的背景图', n['bgi'], file=sys.stderr)
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
        elif blur == 0:
            emit(f'ol(o[{i}], {c}, {A}, {max(1, R(spread))});')
        else:
            emit(f'sh(o[{i}], {c}, {A}, {R(blur)}, {R(x)}, {R(y)}, {R(spread)});')
(B / f'{name}_{S}.inc').write_text('\n'.join(L) + '\n', encoding='utf-8')
print(f'{name}: {len(D)} 个元素 → {len(L)} 行')
