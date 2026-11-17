# -*- coding: utf-8 -*-
"""C# 对拍测试的样子（Python 版自检）：读 fixtures/*.csv 的输入列，喂一个新的 ReadingCheck，逐拍比输出列。
verdict / which / valid / heat 逐拍相同；浮点 |x − 期望| ≤ 1e-9 + 1e-9·|期望|（这里同一份实现应当逐位相同）。  python3 verify_fixtures.py [目录]"""
import csv
import json
import math
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from reading_check import ReadingCheck, Params  # noqa: E402

FL = ['y30', 'ra', 'a_ts', 't_ref', 'y_ref', 'd', 't_pk', 't_seg', 'rise', 'r_pk', 'u']


def num(s):
    return float(s)          # 'NaN' / 'Infinity' / '-Infinity' / repr 都能原样读回


def main(d):
    man = json.load(open(os.path.join(d, 'manifest.json'), encoding='utf-8'))
    bad = 0
    for run in man['runs']:
        chk = ReadingCheck(Params())
        n = worst = exact = 0
        for row in csv.DictReader(open(os.path.join(d, run['file']), encoding='utf-8')):
            b = chk.step(num(row['t']), num(row['tr']), num(row['sp']), num(row['wall']), num(row['tau']))
            n += 1
            if (b.verdict, b.which, '1' if b.valid else '0', '1' if b.heat else '0') != (row['verdict'], row['which'], row['valid'], row['heat']):
                bad += 1
                print('  %s 拍 %d：%s/%s ≠ %s/%s' % (run['name'], n, b.verdict, b.which, row['verdict'], row['which']))
            for k in FL:
                got, exp = getattr(b, k), num(row[k])
                if exp != exp:
                    ok = got != got
                else:
                    ok = abs(got - exp) <= 1e-9 + 1e-9 * abs(exp)
                    worst = max(worst, abs(got - exp))
                    exact += got == exp
                if not ok:
                    bad += 1
                    print('  %s 拍 %d %s：%r ≠ %r' % (run['name'], n, k, got, exp))
        print('%-24s %5d 拍  最大差 %.1e  （拍数对 manifest：%s）' % (run['name'], n, worst, 'ok' if n == run['beats'] else 'ERR'))
    print('不一致 %d 处' % bad)
    return bad


if __name__ == '__main__':
    sys.exit(1 if main(sys.argv[1] if len(sys.argv) > 1 else os.path.join(HERE, 'fixtures')) else 0)
