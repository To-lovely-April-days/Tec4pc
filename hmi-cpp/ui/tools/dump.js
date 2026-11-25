(function () {
  // 把设计稿页面里每个元素导出成一条记录：位置、底色、圆角、阴影、文字。svg 和 img 当成整张图。
  // 设计稿里标了 data-id 的元素是「程序运行时要改的东西」（数值、状态文字），标了 data-act 的是「点了要有反应的东西」。
  // 设计稿里标了 data-part 的那一块是「页面里的一部分」（比如任务序列页的参数区）：只导出这一块，位置仍按整屏算
  var parts = document.querySelectorAll('[data-part]');      // 一块可以由几个元素组成（比如并排的两张卡）
  var root = document.querySelector('x-dc > div') || document.body.firstElementChild;
  var out = [], imgs = 0;
  function px(s) { return parseFloat(s) || 0; }
  function trimmedRange(n) {
    var raw = n.nodeValue, lead = raw.length - raw.replace(/^\s+/, '').length, tail = raw.length - raw.replace(/\s+$/, '').length;
    var rg = document.createRange(); rg.setStart(n, lead); rg.setEnd(n, raw.length - tail); return rg;
  }
  // 这段文字变长以后哪一边不动，就是按哪一边对齐的：程序改数值时照这个对齐方式重新摆
  function anchorOf(n) {
    var a = trimmedRange(n).getBoundingClientRect(); var old = n.nodeValue;
    n.nodeValue = old.replace(/\s+$/, '') + '00';
    var b = trimmedRange(n).getBoundingClientRect(); n.nodeValue = old;
    if (Math.abs(b.left - a.left) < 0.3) return 'l';
    if (Math.abs(b.right - a.right) < 0.3) return 'r';
    return 'c';
  }
  function walk(el, parent, opa) {
    var cs = getComputedStyle(el); if (cs.display === 'none') return;
    var r = el.getBoundingClientRect(); var tag = el.tagName.toLowerCase();
    var o = opa * (parseFloat(cs.opacity));
    var rec = { i: out.length, p: parent, tag: tag, x: r.left, y: r.top, w: r.width, h: r.height, opa: o, id: el.dataset.id || '', act: el.dataset.act || '' };
    out.push(rec);
    if (tag === 'svg' || tag === 'img') { rec.img = imgs++; return; }
    rec.bg = cs.backgroundColor; rec.bgi = cs.backgroundImage === 'none' ? '' : cs.backgroundImage.slice(0, 80);
    var br = cs.borderTopLeftRadius; rec.rad = br.indexOf('%') >= 0 ? Math.min(r.width, r.height) * parseFloat(br) / 100 : px(br);
    rec.shadow = cs.boxShadow === 'none' ? '' : cs.boxShadow; rec.clip = cs.overflow === 'hidden' ? 1 : 0;
    var firstText = true;
    for (var n = el.firstChild; n; n = n.nextSibling) {
      if (n.nodeType === 3 && n.nodeValue.trim()) {
        var rg = trimmedRange(n); var rs = rg.getClientRects(); if (!rs.length) continue;
        var b = rg.getBoundingClientRect();
        var t = { i: out.length, p: rec.i, tag: '#text', text: n.nodeValue.replace(/[ \t\r\n]+/g, ' ').replace(/^ +| +$/g, ''), x: b.left, y: b.top, w: b.width, h: b.height, lines: rs.length, opa: o,
                  color: cs.color, fs: px(cs.fontSize), fw: parseInt(cs.fontWeight), ls: px(cs.letterSpacing), id: '', anchor: 'c' };
        t.anchor = anchorOf(n);          // 每一段都量：设计稿写死的字换语言以后也要按原来那一边对齐
        if (firstText && rec.id) { t.id = rec.id; }
        firstText = false;
        out.push(t);
      } else if (n.nodeType === 1) walk(n, rec.i, o);
    }
  }
  if (parts.length) { for (var k = 0; k < parts.length; k++) walk(parts[k], -1, 1); } else walk(root, -1, 1);
  return JSON.stringify(out);
})()
