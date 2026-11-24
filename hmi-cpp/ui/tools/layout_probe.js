// 量每一张最外层卡片：卡片自己的框，和它里面所有东西实际画到哪一行。
// 「里面比卡片还高」= 被卡片切掉；「超过 720」= 被屏底切掉。
//
// 为什么有这个：2026-11-25 把顶上的页签条加高（44 → 72）之后，内容区少了 28，
// 我按比例把各卡片压了一下就交了货，结果 PID 整定页底下那排键和右边的「快速升温加码上限」
// 都被切掉了——出图只看了底部一条，没看出来。眼睛靠不住，这事得量。
//
// 用法（对每一张设计稿跑一遍，谁超了就报谁）：
//   for f in ui/design/*.dc.html; do
//     ./ui/tools/evaljs "$f" ui/tools/layout_probe.js /tmp/$(basename $f .dc.html).json 1280 720
//   done
// 然后看每条记录：deep > bot 就是被卡片切了，deep > 720 就是被屏底切了。改完布局必须是 0 处。
(() => {
  const root = document.querySelector('x-dc > div') || document.body;
  const out = [];
  const pr = root.getBoundingClientRect();
  for (const el of root.children) {
    const cs = getComputedStyle(el);
    if (cs.position !== 'absolute') continue;
    const r = el.getBoundingClientRect();
    let deep = r.top;
    const walk = n => {
      for (const c of n.children) {
        const rc = c.getBoundingClientRect();
        const s = getComputedStyle(c);
        if (s.display === 'none' || s.visibility === 'hidden') continue;
        if (rc.height > 0 && rc.bottom > deep) deep = rc.bottom;
        walk(c);
      }
    };
    walk(el);
    out.push({
      tag: el.tagName.toLowerCase(),
      id: el.getAttribute('data-id') || el.getAttribute('data-part') || el.getAttribute('aria-label') || '',
      txt: (el.textContent || '').trim().slice(0, 18),
      top: Math.round(r.top - pr.top),
      h: Math.round(r.height),
      bot: Math.round(r.bottom - pr.top),
      deep: Math.round(deep - pr.top),            // 里面最深画到哪
      clip: cs.overflow === 'hidden' || cs.overflowY === 'hidden',
    });
  }
  return JSON.stringify(out);
})();
