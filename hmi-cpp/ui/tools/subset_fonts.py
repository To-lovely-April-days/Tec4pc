"""把思源黑体（Noto Sans SC）裁小：只留界面上会出现的字，四个字重从 42 MB 裁到十来 MB，这样字体可以跟着代码进仓库。

留下的字：GB2312 一、二级汉字（6763 个，文字键盘能打出来的就是这些）+ 英文数字 + 中英文标点 + 常用符号（℃ ± − → … 等）。
PC 端如果给配方起了这个范围之外的生僻字，屏上那个字会显示成空格——真遇到了就把字加进下面的 EXTRA 再跑一遍。

用法：python3 ui/tools/subset_fonts.py <没裁过的字体所在目录>     （要先 pip install fonttools）
"""
import pathlib, subprocess, sys
src = pathlib.Path(sys.argv[1]); dst = pathlib.Path(__file__).resolve().parent.parent / 'assets' / 'fonts'
EXTRA = ''
chars = set(EXTRA)
for hi in range(0xB0, 0xF8):
    for lo in range(0xA1, 0xFF):
        try: chars.add(bytes([hi, lo]).decode('gb2312'))
        except UnicodeDecodeError: pass
ranges = [(0x20, 0x7E), (0xA0, 0xFF), (0x2000, 0x206F), (0x2100, 0x214F), (0x2190, 0x21FF), (0x2200, 0x22FF), (0x2460, 0x24FF), (0x2580, 0x25FF), (0x3000, 0x303F), (0xFF00, 0xFFEF)]
cps = {ord(c) for c in chars} | {c for a, b in ranges for c in range(a, b + 1)}
ulist = ','.join(f'U+{c:04X}' for c in sorted(cps))
for w in (400, 500, 600, 700):
    out = dst / f'noto_sc_{w}.ttf'
    subprocess.run([sys.executable, '-m', 'fontTools.subset', str(src / f'noto_sc_{w}.ttf'), f'--unicodes={ulist}', f'--output-file={out}', '--layout-features=', '--no-hinting', '--desubroutinize'], check=True)
    print(out.name, out.stat().st_size // 1024, 'KB')
