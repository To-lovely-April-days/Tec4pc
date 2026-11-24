(function () {
  // 把页面里每个元素导出成一条记录：在父元素里的位置、底色、圆角、阴影、文字。svg 和 img 当成整张图。
  var root = document.querySelector('x-dc > div') || document.body.firstElementChild;
  var out = [], imgs = 0;
  function px(s) { return parseFloat(s) || 0; }
  function walk(el, parent, opa) {
    var cs = getComputedStyle(el); if (cs.display === 'none') return;
    var r = el.getBoundingClientRect(); var tag = el.tagName.toLowerCase();
    var o = opa * (parseFloat(cs.opacity));
    var rec = { i: out.length, p: parent, tag: tag, x: r.left, y: r.top, w: r.width, h: r.height, opa: o };
    out.push(rec);
    if (tag === 'svg' || tag === 'img') { rec.img = imgs++; return; }
    rec.bg = cs.backgroundColor; rec.bgi = cs.backgroundImage === 'none' ? '' : cs.backgroundImage.slice(0, 80);
    var br = cs.borderTopLeftRadius; rec.rad = br.indexOf('%') >= 0 ? Math.min(r.width, r.height) * parseFloat(br) / 100 : px(br);
    rec.shadow = cs.boxShadow === 'none' ? '' : cs.boxShadow; rec.clip = cs.overflow === 'hidden' ? 1 : 0;
    rec.outline = cs.outlineStyle !== 'none' ? [cs.outlineColor, px(cs.outlineWidth), px(cs.outlineOffset), cs.outlineStyle] : null;
    for (var n = el.firstChild; n; n = n.nextSibling) {
      if (n.nodeType === 3 && n.nodeValue.trim()) {
        var raw = n.nodeValue, lead = raw.length - raw.replace(/^\s+/, '').length, tail = raw.length - raw.replace(/\s+$/, '').length;
        var rg = document.createRange(); rg.setStart(n, lead); rg.setEnd(n, raw.length - tail); var rs = rg.getClientRects(); if (!rs.length) continue;
        var b = rg.getBoundingClientRect();
        out.push({ i: out.length, p: rec.i, tag: '#text', text: n.nodeValue.replace(/[ \t\r\n]+/g, ' ').replace(/^ +| +$/g, ''), x: b.left, y: b.top, w: b.width, h: b.height, lines: rs.length, opa: o,
                   color: cs.color, fs: px(cs.fontSize), fw: parseInt(cs.fontWeight), ls: px(cs.letterSpacing) });
      } else if (n.nodeType === 1) walk(n, rec.i, o);
    }
  }
  walk(root, -1, 1);
  return JSON.stringify(out);
})()
