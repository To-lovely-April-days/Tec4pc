#!/usr/bin/env python3
"""生成两张「釜体状态贴图」设计稿：ui/design/FxMain.dc.html（总览那只釜的尺寸）、FxCtl.dc.html（设备控制页那只）。

这两张稿只出图、不出页面代码（gen.py 见名字以 Fx 打头就跳过页面代码），出的是贴在釜体照片上的那几层：
升温的橙色加热带、降温的青色虚线圈（三帧，轮着贴就像在流）、超限的红边、搅拌桨（六帧，三片叶子转 120°
就是一整圈）、加料（四帧，一滴落下去、液面荡开一圈）。每张贴图都和釜体照片同一个框、同一个 viewBox，
运行时贴在照片同一个位置上就天然对齐（见 ui/src/vessel_fx.inc）。

外圈的形状不是手画的圆角矩形：从釜体照片上一行一行量出玻璃外缘，左边一行一个点、右边按中线镜像。
手画的圆角矩形底下和釜底对不上（用户 2026-11-26 一眼看出来的）。

    用法：python3 ui/tools/make_fx.py          # 改完这个脚本跑一遍，再跑 ui/gen_all.sh 重新切图

换釜体照片的话这里会跟着重量，不用改代码；但照片里**不能有搅拌桨**（桨是单独一层才能转），
换图的人要先把桨抹掉、底下的液体补平。
"""
import base64, io, pathlib, re
from PIL import Image

UI = pathlib.Path(__file__).resolve().parent.parent
DESIGN = UI / 'design'

# 釜体照片从总览那张设计稿里取：它就是真机上贴的那张，量出来的轮廓才对得上
src = (DESIGN / 'Main.dc.html').read_text(encoding='utf-8')
b64 = re.search(r'data-id="main\.1\.vessel" src="data:image/png;base64,([^"]*)"', src).group(1)
img = Image.open(io.BytesIO(base64.b64decode(b64))).convert('RGBA')
W, H = img.size                      # 照片本身的像素（贴图的 viewBox 就用它，和照片一个坐标系）
A = img.load()

GLASS_TOP, GLASS_BOTTOM = 362, 668   # 玻璃筒身从哪到哪（盖子以下、釜底以上），照片坐标
RING_FROM, BAND_FROM = 366, 580      # 整圈从哪开始画；加热带只画底下这一段


def edges():
    """逐行量玻璃外缘：左边一行一个点，另外算出玻璃的中线（釜在照片里不一定居中，
    按画布中线镜像会偏 —— 要按量到的左右壁算）"""
    left, right = {}, {}
    for y in range(GLASS_TOP, GLASS_BOTTOM):
        xs = [x for x in range(W) if A[x, y][3] > 96]
        if xs:
            left[y], right[y] = min(xs), max(xs)
    straight = (GLASS_TOP + GLASS_BOTTOM) // 2          # 筒身中段，左右壁是直的
    return left, (left[straight] + right[straight]) / 2.0


EDGE, CX = edges()


def trace(y_from):
    """把量到的外缘拼成一条贴着釜壁的线：左边从上往下，右边按中线镜像从下往上"""
    edge = EDGE
    pts, last = [], None
    for y in sorted(k for k in edge if k >= y_from):
        x = edge[y]
        if last is None or x != last or y == max(edge):
            pts.append((x, y)); last = x
    right = [(2 * CX - x, y) for x, y in reversed(pts)]
    return 'M%g,%g' % pts[0] + ''.join('L%g,%g' % p for p in pts[1:] + right)


RING, BAND = trace(RING_FROM), trace(BAND_FROM)

# 搅拌桨：叶片形状照着照片里那三片描的，画在正圆里再压扁成椭圆 —— 从斜上方看下去就是这个样子。
# 1.993 = 303 / 152：样稿是按 152 宽画的，换算到照片坐标
BLADE = 'M5 -3.5Q19 -17 26.5 -7Q29 7 16 13.5Q7 11 5 3.5Z'
PROP_CX, PROP_CY, PROP_K = 150, 627, 0.4          # 桨毂在照片上的位置、压扁多少
DROP_X, DROP_TOP, SURF = 223, 390, 495            # 加料口、滴起点、液面


def prop(angle):
    g = ''.join('<g transform="rotate(%d) scale(1.993)"><path d="%s" fill="#F8FBFD" stroke="#D9E2EA" '
                'stroke-width="0.9" stroke-linejoin="round"></path></g>' % (angle + i * 120, BLADE) for i in range(3))
    return ('<g transform="translate(%d %d) scale(1 %g)">%s</g>'
            '<ellipse cx="%d" cy="%d" rx="11" ry="4.4" fill="#EEF3F7" stroke="#D9E2EA" stroke-width="1.4"></ellipse>'
            % (PROP_CX, PROP_CY, PROP_K, g, PROP_CX, PROP_CY))


def dose(i):
    y = DROP_TOP + (SURF - DROP_TOP) * i / 4.0
    rx = 12 + i * 15                              # 上一滴荡开的那圈，越荡越大越淡
    return ('<circle cx="%d" cy="%g" r="6.4" fill="#2F86CC" opacity="0.9"></circle>'
            '<ellipse cx="%d" cy="%d" rx="%g" ry="%g" fill="none" stroke="#2F86CC" stroke-width="3.2" opacity="%g"></ellipse>'
            % (DROP_X, y, DROP_X, SURF, rx, rx * 0.34, 0.75 - i * 0.17))


def page(title, tag, w, h, per_row, pitch_x, pitch_y):
    items = [('fx.%s.stir%d' % (tag, i), prop(i * 20)) for i in range(6)]
    items.append(('fx.%s.heat' % tag,
                  '<path d="%s" stroke="#E8590C" stroke-width="9.97" stroke-linecap="round"></path>' % BAND))
    for i, off in enumerate((0, 4.67, 9.33)):
        items.append(('fx.%s.cool%d' % (tag, i),
                      '<path d="%s" stroke="#1098AD" stroke-width="5.98" stroke-linecap="round" '
                      'stroke-dasharray="14 14" stroke-dashoffset="%g"></path>' % (RING, -off)))
    items.append(('fx.%s.alarm' % tag,
                  '<path d="%s" stroke="#D64545" stroke-width="8.97" stroke-linecap="round"></path>' % RING))
    for i in range(4):
        items.append(('fx.%s.dose%d' % (tag, i), dose(i)))
    body = []
    for n, (did, inner) in enumerate(items):
        # 整排往里挪 6 px，彼此也离开 6 px 以上：切图是按元素的框往外多裁 2 px 的，
        # 贴着页面边的那一张会把页面外面裁进来，出来的图四周带一圈黑（踩过）
        x, y = 6 + (n % per_row) * pitch_x, 6 + (n // per_row) * pitch_y
        body.append('<svg data-id="%s" width="%g" height="%g" viewBox="0 0 %d %d" fill="none" '
                    'style="position:absolute;left:%dpx;top:%dpx;display:block">%s</svg>' % (did, w, h, W, H, x, y, inner))
    return """<!doctype html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<title>%s</title>
<script src="./support.js"></script>
</head>
<body>
<x-dc>
<helmet>
<style>
body{margin:0}
</style>
</helmet>
<div style="position: relative; width: 1280px; height: 720px; overflow: hidden; background: #FFFFFF">%s</div>
</x-dc>
<script type="text/x-dc" data-dc-script data-props='{"$preview":{"width":1280,"height":720}}'>
class Component extends DCLogic {
renderVals() {
return {};
}
}
</script>
</body>
</html>
""" % (title, ''.join(body))


# 两张稿里贴图的宽高 = 两页上釜体照片的宽高（Main 是 123×275，Ctl 是 152×339），一行摆几个按 1280 宽排得下算
(DESIGN / 'FxMain.dc.html').write_text(page('釜体状态贴图 · 总览尺寸', 'm', 123, 275, 9, 129, 290), encoding='utf-8')
(DESIGN / 'FxCtl.dc.html').write_text(page('釜体状态贴图 · 设备控制尺寸', 'c', 152, 339, 8, 158, 345), encoding='utf-8')
print('写出 FxMain.dc.html / FxCtl.dc.html（轮廓 %d 个点，加热带 %d 个点）' % (RING.count('L'), BAND.count('L')))
