import Cocoa
import WebKit
// 用法：evaljs <html 路径> <js 路径> <输出 json 路径> [宽] [高]
// 在离屏的 WebKit 里打开设计稿页面，跑一段 JS，把它返回的字符串写到文件。用来导出设计稿里每个元素的位置和样式。
let a = CommandLine.arguments
let src = URL(fileURLWithPath: a[1]); let js = try! String(contentsOfFile: a[2], encoding: .utf8); let out = URL(fileURLWithPath: a[3])
let w = a.count > 4 ? Double(a[4])! : 1280; let h = a.count > 5 ? Double(a[5])! : 720
class Nav: NSObject, WKNavigationDelegate {
    func webView(_ v: WKWebView, didFinish n: WKNavigation!) {
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.0) {
            v.evaluateJavaScript(js) { r, e in
                if let s = r as? String { try? s.write(to: out, atomically: true, encoding: .utf8); print("ok \(s.count)") } else { print("fail \(String(describing: e))") }
                exit(0)
            }
        }
    }
}
let app = NSApplication.shared; app.setActivationPolicy(.prohibited)
let web = WKWebView(frame: CGRect(x: 0, y: 0, width: w, height: h))
let win = NSWindow(contentRect: CGRect(x: -3000, y: -3000, width: w, height: h), styleMask: [.borderless], backing: .buffered, defer: false)
win.contentView = web; let nav = Nav(); web.navigationDelegate = nav
web.loadFileURL(src, allowingReadAccessTo: src.deletingLastPathComponent())
DispatchQueue.main.asyncAfter(deadline: .now() + 40) { print("timeout"); exit(2) }
app.run()
