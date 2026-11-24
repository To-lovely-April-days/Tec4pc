"""把 ui/tools/phrases.txt 里的词配上拼音，生成 ui/src/phrase_dict.inc（文字键盘的词库）。

拼音用系统自带的汉字转拼音整词一起转（只能在 Mac 上跑），所以多音字是按词定的读音（「重结晶」是 chong，「重量」是 zhong）。
个别转错的写在下面 FIX 里。词里的字必须都在 GB2312 之内——屏上的字体只留了这些字，之外的会显示成空格。

用法：python3 ui/tools/gen_phrases.py
"""
import pathlib, subprocess, sys
here = pathlib.Path(__file__).resolve().parent
FIX = {'重结晶': 'chong jie jing', '重复': 'chong fu', '重量': 'zhong liang', '质量': 'zhi liang', '含量': 'han liang', '总量': 'zong liang', '当量': 'dang liang', '流量': 'liu liang',
       '称量': 'cheng liang', '调节': 'tiao jie', '转速': 'zhuan su', '转移': 'zhuan yi', '转化率': 'zhuan hua lv', '速率': 'su lv', '升温速率': 'sheng wen su lv', '降温速率': 'jiang wen su lv',
       '收率': 'shou lv', '产率': 'chan lv', '电导率': 'dian dao lv', '还原': 'huan yuan', '粘度': 'nian du', '分子筛': 'fen zi shai', '悬浮液': 'xuan fu ye', '降温': 'jiang wen',
       '行': 'xing', '氯仿': 'lv fang', '氯化钠': 'lv hua na', '氯化钾': 'lv hua jia', '氯化钙': 'lv hua gai', '氯化铵': 'lv hua an', '二氯甲烷': 'er lv jia wan', '率': 'lv'}
UV = {'lv': '绿率氯滤律旅吕铝虑屡缕履侣驴', 'nv': '女', 'lve': '略掠', 'nve': '虐疟'}
words = []
for line in (here / 'phrases.txt').read_text(encoding='utf-8').splitlines():
    if line.startswith('#'): continue
    for w in line.split():
        if w not in words: words.append(w)
bad = []
for w in words:
    for c in w:
        try: c.encode('gb2312')
        except UnicodeEncodeError: bad.append(f'{w}（{c}）')
if bad: sys.exit('这些词里有字体里没有的字：' + ' '.join(bad))
out = subprocess.run(['swift', str(here / 'gen_pinyin.swift'), '--words'], input='\n'.join(words), capture_output=True, text=True, check=True).stdout
rows = []
for line in out.splitlines():
    w, py = line.split('\t')
    py = FIX.get(w, py).replace('ü', 'v').replace('lüe', 'lve')
    syl = py.split()
    if len(syl) != len(w): sys.exit(f'「{w}」的拼音个数对不上：{py}')
    for i, c in enumerate(w):          # ü 在键盘上打 v：系统转出来的是去了两点的 u，这里改回来
        for v, cs in UV.items():
            if c in cs: syl[i] = v
    rows.append((''.join(syl), ''.join(s[0] for s in syl), w))
body = '\n'.join(f'    {{"{f}", "{a}", "{w}"}},' for f, a, w in rows)
(here.parent / 'src' / 'phrase_dict.inc').write_text(
    '// 文字键盘的词库：ui/tools/gen_phrases.py 从 ui/tools/phrases.txt 生成的，不要手改。\n'
    '// 每行：整词的拼音（连着写）、各字拼音的头一个字母（简拼）、词。排在前面的先出。\n'
    'static const struct { const char* py; const char* ab; const char* text; } kPhrases[] = {\n' + body + '\n};\n', encoding='utf-8')
print(len(rows), '个词')
