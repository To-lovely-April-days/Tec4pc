// Windows 上的窗口：自己开一个普通的 Win32 窗口，把 LVGL 画好的画面**一个像素对一个像素**贴上去。只在 Windows 上编。
//
// 一开始用的是 LVGL 自带的 Windows 驱动，用户 2026-11-24 上机一看「发虚、很卡」。原因有三个，都出在那个驱动上：
//   1. 它把窗口当成可以随便拉大小的，LVGL 的分辨率跟着窗口客户区走，不是我们设计的 1280×720；
//   2. 它每刷一次都把整幅画面用「高质量缩放」贴一遍，不管实际只变了几个字——又慢又糊；
//   3. 程序没告诉系统「我自己管高分屏」，系统缩放 125 % / 150 % 时 Windows 会把整个窗口当图片再放大一次，更糊。
// 所以换成这里的做法：告诉系统不要替我们放大；窗口客户区正好 1280×720（不能拉大小）；只把变了的那一小块原样贴上去。
//
// 放大（--zoom 或全屏时屏幕比 1280×720 大）：整数倍（200 %）用「一个点变四个点」，边缘是清楚的；
// 不是整数倍（比如 150 %）只能插值，会软一些——屏幕分辨率高的机器要清楚，应该用按 2 倍画的那一版界面。

#include <windowsx.h>

namespace win {

static HWND hwnd = nullptr;
static HDC mem = nullptr;                 // 内存里的那张画布（LVGL 往它上面写，窗口从它上面拷）
static uint32_t* bits = nullptr;
static int zoom = 100, ox = 0, oy = 0;    // 放大百分比；全屏时画面居中，左边和上边空出多少
static bool closed = false;

static void blit(HDC dc, int x1, int y1, int x2, int y2) {
    if (zoom == 100) { BitBlt(dc, ox + x1, oy + y1, x2 - x1 + 1, y2 - y1 + 1, mem, x1, y1, SRCCOPY); return; }
    // 放大时多带上周围两个像素：插值要看邻居，只贴正好那一块的话块和块之间会有缝
    x1 = std::max(0, x1 - 2); y1 = std::max(0, y1 - 2); x2 = std::min(W - 1, x2 + 2); y2 = std::min(H - 1, y2 + 2);
    const int dx1 = x1 * zoom / 100, dy1 = y1 * zoom / 100, dx2 = (x2 + 1) * zoom / 100, dy2 = (y2 + 1) * zoom / 100;
    if (zoom % 100 == 0) SetStretchBltMode(dc, COLORONCOLOR);
    else { SetStretchBltMode(dc, HALFTONE); SetBrushOrgEx(dc, 0, 0, nullptr); }
    StretchBlt(dc, ox + dx1, oy + dy1, dx2 - dx1, dy2 - dy1, mem, x1, y1, x2 - x1 + 1, y2 - y1 + 1, SRCCOPY);
}

// LVGL 画完一块就叫它：把这一块贴到窗口上
static void present(int x1, int y1, int x2, int y2) {
    if (!hwnd) return;
    if (HDC dc = GetDC(hwnd)) { blit(dc, x1, y1, x2, y2); ReleaseDC(hwnd, dc); }
}

// 鼠标 / 手指的位置（屏幕坐标）换成界面上的坐标，记给 LVGL 去读
static void pointer(LPARAM lp, bool screen_coords, int down) {
    POINT p{GET_X_LPARAM(lp), GET_Y_LPARAM(lp)};
    if (screen_coords) ScreenToClient(hwnd, &p);
    g_finger.x = (p.x - ox) * 100 / zoom;
    g_finger.y = (p.y - oy) * 100 / zoom;
    if (down >= 0) g_finger.down = down != 0;
}

static LRESULT CALLBACK proc(HWND h, UINT m, WPARAM wp, LPARAM lp) {
    switch (m) {
    case WM_ERASEBKGND: return 1;                 // 不让系统先刷一遍底色：会闪
    case WM_PAINT: {
        PAINTSTRUCT ps; HDC dc = BeginPaint(h, &ps);
        if (ox > 0 || oy > 0) FillRect(dc, &ps.rcPaint, static_cast<HBRUSH>(GetStockObject(BLACK_BRUSH)));
        if (mem) blit(dc, 0, 0, W - 1, H - 1);
        EndPaint(h, &ps);
        return 0;
    }
    // 触摸和鼠标都走「指针」这一套消息（下面 EnableMouseInPointer 打开以后鼠标也算指针），坐标是屏幕坐标
    case WM_POINTERDOWN:   pointer(lp, true, 1); return 0;
    case WM_POINTERUP:     pointer(lp, true, 0); return 0;
    case WM_POINTERUPDATE: pointer(lp, true, IS_POINTER_INCONTACT_WPARAM(wp) ? 1 : 0); return 0;
    case WM_POINTERCAPTURECHANGED: g_finger.down = false; return 0;
    // 老系统上没有指针消息时的退路：普通鼠标消息，坐标已经是窗口里的
    case WM_LBUTTONDOWN: SetCapture(h); pointer(lp, false, 1); return 0;
    case WM_LBUTTONUP:   ReleaseCapture(); pointer(lp, false, 0); return 0;
    case WM_MOUSEMOVE:   pointer(lp, false, -1); return 0;
    case WM_CLOSE: closed = true; DestroyWindow(h); return 0;
    case WM_DESTROY: closed = true; hwnd = nullptr; return 0;
    }
    return DefWindowProcW(h, m, wp, lp);
}

// 告诉系统「高分屏我自己管」：不这样的话系统缩放不是 100 % 时会把整个窗口当图片放大，字就糊了。
// 要在问屏幕多大之前先说：说了以后问到的才是真实的像素数（2560×1600），不然是按缩放折算过的（1707×1067）
static void own_dpi() {
    static bool done = false;
    if (done) return;
    done = true;
    if (!SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)) SetProcessDPIAware();
}

// 主屏幕上能用的地方有多大（真实像素）。全屏时是整块屏；开窗口时要扣掉任务栏
static void usable(bool fullscreen, int& w, int& h, int& left, int& top) {
    own_dpi();
    left = top = 0; w = GetSystemMetrics(SM_CXSCREEN); h = GetSystemMetrics(SM_CYSCREEN);
    RECT r;
    if (!fullscreen && SystemParametersInfoW(SPI_GETWORKAREA, 0, &r, 0)) { left = r.left; top = r.top; w = r.right - r.left; h = r.bottom - r.top; }
}

// 自适应屏幕（用户 2026-11-24：2560×1600、缩放 150 % 的电脑上要能自己适应）：
// 界面有两份，tec_hmi 按 1280×720 画，tec_hmi_2x 按 2560×1440 画（字和线是按大尺寸现画的，不是放大出来的）。
// 这里是 1 倍那一份启动时做的事：屏幕放得下 2 倍那一份、它又在旁边，就改成启动它（参数原样带过去），自己等它退出再退——
// 这样不管是双击还是工作站启动，用的都是同一个 tec_hmi.exe，不用人去挑。返回 true = 已经交给 2 倍那一份跑完了
static bool hand_over_to_2x(bool fullscreen) {
    int w, h, l, t; usable(fullscreen, w, h, l, t);
    if (w < 2 * W || h < 2 * H) return false;
    wchar_t path[MAX_PATH];
    const DWORD n = GetModuleFileNameW(nullptr, path, MAX_PATH);
    if (n == 0 || n >= MAX_PATH) return false;
    std::wstring exe(path);
    const size_t cut = exe.find_last_of(L"\\/");
    exe = (cut == std::wstring::npos ? L"" : exe.substr(0, cut + 1)) + L"tec_hmi_2x.exe";
    if (GetFileAttributesW(exe.c_str()) == INVALID_FILE_ATTRIBUTES) return false;       // 旁边没有 2 倍那一份：就用自己
    std::wstring cmd = GetCommandLineW();
    STARTUPINFOW si{}; si.cb = sizeof si;
    PROCESS_INFORMATION pi{};
    if (!CreateProcessW(exe.c_str(), cmd.data(), nullptr, nullptr, FALSE, 0, nullptr, nullptr, &si, &pi)) return false;
    WaitForSingleObject(pi.hProcess, INFINITE);       // 等它退出再退：启动我们的人（工作站）是盯着这个进程判断「界面还开着没有」的
    CloseHandle(pi.hThread); CloseHandle(pi.hProcess);
    return true;
}

// 开窗口。want_zoom：0 = 自己定（窗口时 100 %，全屏时放大到屏幕放得下的最大）；frame：要不要系统的标题栏和边框
static bool open(int want_zoom, bool fullscreen, bool frame) {
    own_dpi();
    EnableMouseInPointer(TRUE);

    BITMAPINFO bi{};
    bi.bmiHeader.biSize = sizeof bi.bmiHeader;
    bi.bmiHeader.biWidth = W; bi.bmiHeader.biHeight = -H;      // 负数 = 第一行在最上面，和 LVGL 的画面方向一致
    bi.bmiHeader.biPlanes = 1; bi.bmiHeader.biBitCount = 32; bi.bmiHeader.biCompression = BI_RGB;
    mem = CreateCompatibleDC(nullptr);
    void* p = nullptr;
    HBITMAP bmp = CreateDIBSection(mem, &bi, DIB_RGB_COLORS, &p, nullptr, 0);
    if (!mem || !bmp || !p) return false;
    SelectObject(mem, bmp);
    bits = static_cast<uint32_t*>(p);

    WNDCLASSW wc{};
    wc.lpfnWndProc = proc; wc.hInstance = GetModuleHandleW(nullptr); wc.lpszClassName = L"TecHmiWindow";
    wc.hCursor = LoadCursorW(nullptr, MAKEINTRESOURCEW(32512));      // 32512 = 普通箭头（IDC_ARROW；那个名字在没开 UNICODE 时是窄字符的，这里直接写编号）
    RegisterClassW(&wc);

    int sw, sh, sl, st; usable(fullscreen, sw, sh, sl, st);
    const wchar_t* title = L"HTLAB 多工位反应平台";
    if (fullscreen) {
        zoom = want_zoom > 0 ? want_zoom : std::max(100, std::min(sw * 100 / W, sh * 100 / H));
        ox = std::max(0, (sw - W * zoom / 100) / 2); oy = std::max(0, (sh - H * zoom / 100) / 2);
        hwnd = CreateWindowExW(0, wc.lpszClassName, title, WS_POPUP, 0, 0, sw, sh, nullptr, nullptr, wc.hInstance, nullptr);
    } else {
        zoom = want_zoom > 0 ? want_zoom : 100;
        // 缺省不要系统的标题栏和边框（用户 2026-11-24：自带的窗口不要显示），只有界面本身，摆在屏幕中间；
        // 任务栏上有它，Alt+F4 或者在任务栏上右键能关。加 --frame 才带标题栏（能拖动、有关闭键）。
        // 不管哪种都不能拉大小：画面是按固定尺寸画的
        const DWORD style = frame ? WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX : WS_POPUP | WS_SYSMENU | WS_MINIMIZEBOX;
        RECT r{0, 0, W * zoom / 100, H * zoom / 100};
        AdjustWindowRectEx(&r, style, FALSE, 0);
        const int ww = r.right - r.left, wh = r.bottom - r.top;
        hwnd = CreateWindowExW(0, wc.lpszClassName, title, style, sl + std::max(0, (sw - ww) / 2), st + std::max(0, (sh - wh) / 2), ww, wh,
                               nullptr, nullptr, wc.hInstance, nullptr);
    }
    if (!hwnd) return false;
    ShowWindow(hwnd, SW_SHOW);
    return true;
}

// 把攒着的窗口消息处理掉。窗口被关了返回 false
static bool pump() {
    MSG msg;
    while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) { TranslateMessage(&msg); DispatchMessageW(&msg); }
    return !closed;
}

// 歇一小会儿；有鼠标 / 触摸消息来就马上醒，点下去不用等
static void idle(uint32_t ms) { MsgWaitForMultipleObjects(0, nullptr, FALSE, ms, QS_ALLINPUT); }

}  // namespace win
