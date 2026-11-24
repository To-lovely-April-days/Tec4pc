"""给拼音字库 ui/src/pinyin_dict.inc 补读音。可以反复跑，已经有的不会重复加。

字库当初是「每个字一个读音」生成的，两件事没照顾到：
1. ü：系统转出来的拼音去掉声调以后 lü 变成了 lu，结果「绿、率、氯、滤、女、略」只能在 lu / nu / lue 底下找到，
   而键盘上 ü 是打 v 的（lv、nv、lve、nve）。这里把这些字补到 v 的音节底下（原来的位置不动）。
2. 多音字：「率」只有 shuai 没有 lv、「还」只有 hai 没有 huan……下面 POLY 是常用多音字的另外几个读音。

用法：python3 ui/tools/add_polyphones.py
"""
import pathlib, re
path = pathlib.Path(__file__).resolve().parent.parent / 'src' / 'pinyin_dict.inc'
UV = {'lv': '绿率氯滤律旅吕铝虑屡缕履侣驴榈闾捋褛膂', 'nv': '女钕恧衄', 'lve': '略掠锊', 'nve': '虐疟'}
POLY = '''
率 shuai|还 huan hai|乐 le yue|称 cheng chen|粘 zhan nian|重 zhong chong|长 chang zhang|行 xing hang|调 tiao diao|降 jiang xiang|量 liang|数 shu shuo|
种 zhong chong|间 jian|相 xiang|处 chu|当 dang|干 gan|分 fen|少 shao|好 hao|没 mei mo|发 fa|空 kong|盛 sheng cheng|朝 chao zhao|传 chuan zhuan|藏 cang zang|
差 cha chai ci|弹 dan tan|都 dou du|恶 e wu|给 gei ji|会 hui kuai|假 jia|将 jiang|教 jiao|结 jie|觉 jue jiao|卷 juan|看 kan|壳 ke qiao|露 lu lou|落 luo la lao|
脉 mai mo|模 mo mu|难 nan|宁 ning|泡 pao|漂 piao|铺 pu|曲 qu|塞 sai se|散 san|色 se shai|厦 sha xia|省 sheng xing|石 shi dan|似 si shi|宿 su xiu|提 ti di|
系 xi ji|吓 xia he|鲜 xian|兴 xing|血 xue xie|咽 yan ye|应 ying|载 zai|扎 zha za|占 zhan|涨 zhang|折 zhe she|正 zheng|中 zhong|转 zhuan|钻 zuan|作 zuo|
澄 cheng deng|冲 chong|臭 chou xiu|畜 chu xu|大 da dai|倒 dao|地 di de|斗 dou|读 du dou|缝 feng|佛 fo fu|冠 guan|观 guan|号 hao|荷 he|横 heng|划 hua|混 hun|
奇 qi ji|济 ji|夹 jia|监 jian|见 jian xian|角 jiao jue|禁 jin|尽 jin|劲 jin jing|卡 ka qia|累 lei|磨 mo|抹 mo ma|蒙 meng|闷 men|秘 mi bi|泊 bo po|薄 bao bo|
背 bei|便 bian pian|别 bie|剥 bo bao|参 can shen cen|曾 ceng zeng|刹 sha cha|颤 chan zhan|场 chang|车 che ju|匙 chi shi|单 dan shan|度 du duo|否 fou pi|
蛤 ha ge|葛 ge|喝 he|核 he hu|糊 hu|华 hua|晃 huang|纪 ji|茄 qie jia|嚼 jiao jue|校 xiao jiao|解 jie xie|菌 jun|咖 ka ga|拉 la|烙 lao luo|勒 le lei|淋 lin|
馏 liu|弄 nong long|喷 pen|屏 ping bing|朴 pu piao|瀑 pu bao|强 qiang jiang|悄 qiao|翘 qiao|切 qie|圈 quan juan|任 ren|撒 sa|丧 sang|扫 sao|汤 tang|
召 zhao shao|拾 shi|识 shi zhi|熟 shu shou|说 shuo shui|踏 ta|帖 tie|拓 tuo ta|委 wei|尾 wei yi|削 xiao xue|旋 xuan|饮 yin|予 yu|与 yu|晕 yun|轧 zha ya|
择 ze zhai|爪 zhao zhua|着 zhe zhao zhuo|属 shu zhu|著 zhu|仔 zi zai|的 de di|了 le liao|得 de dei|和 he huo|为 wei|更 geng|要 yao|只 zhi|乘 cheng sheng|
担 dan|挑 tiao|吐 tu|压 ya|沙 sha|片 pian|通 tong|合 he ge|几 ji|答 da|钉 ding|肚 du|杆 gan|哄 hong|笼 long|论 lun|俩 lia liang|铅 qian yan|铝 lv|
钥 yao yue|汞 gong|锌 xin|钙 gai|镁 mei|锰 meng|钠 na|钾 jia|酯 zhi|醛 quan|酮 tong|醚 mi|胺 an|铵 an|羧 suo|羟 qiang|巯 qiu|酰 xian|苯 ben|烷 wan|烯 xi|炔 que|醇 chun|酚 fen|
氢 qing|氧 yang|氮 dan|氯 lv|氟 fu|溴 xiu|碘 dian|硫 liu|磷 lin|硅 gui|硼 peng|碳 tan|钛 tai|铬 ge|钴 gu|镍 nie|铂 bo|钯 ba|铑 lao|钌 liao|铈 shi|
'''
s = path.read_text(encoding='utf-8')
rows = re.findall(r'^(    \{")([a-z]+)(", ")([^"]+)("\},)$', s, re.M)
d = {py: chars for _, py, _, chars, _ in rows}
added = 0
def put(py, ch):
    """把字加到这个音节底下。多音字都是常用字，插在前几个候选里（第 3 个位置），不要埋到一长串生僻字后面"""
    global added
    cur = d.get(py, '')
    if ch in cur: return
    d[py] = cur[:2] + ch + cur[2:]; added += 1
for py, chars in UV.items():          # 这几个音节整个按上面写的顺序排（常用的在前），原来有的别的字跟在后面
    cur = d.get(py, '')
    added += sum(1 for ch in chars if ch not in cur)
    d[py] = chars + ''.join(ch for ch in cur if ch not in chars)
for item in POLY.replace('\n', '').split('|'):
    if not item.strip(): continue
    ch, *pys = item.split()
    for py in pys: put(py, ch)
head = s[:s.index('static const struct')]
NOTE = '// 另外用 ui/tools/add_polyphones.py 补过读音（ü 打 v 的音节、常用多音字），重新生成字库以后要再跑一遍它。\n'
if NOTE not in head: head += NOTE
body = '\n'.join(f'    {{"{py}", "{d[py]}"}},' for py in sorted(d))
path.write_text(head + 'static const struct { const char* py; const char* chars; } kPinyin[] = {\n' + body + '\n};\n', encoding='utf-8')
print('补了', added, '处，现在', len(d), '个音节')
