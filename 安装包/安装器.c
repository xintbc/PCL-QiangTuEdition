/* ============================================================================
   安装器.c —— 「PCL 枪战突击修改版」单文件安装程序
   ----------------------------------------------------------------------------
   一条命令式的一键安装：所有游戏文件以 RCDATA 资源内嵌在本 exe 里，
   玩家双击 → 选目录 → 装好 → 桌面出图标。不再有 zip + bat + vbs 那一套。

   为什么自己写而不用 NSIS/Inno：本机没有这些工具，而这活儿本来就不复杂——
   解资源、建目录、建快捷方式，Win32 全都能干，还顺带解决三个老问题：
     ① 安装目录让玩家自己选（默认 %LOCALAPPDATA%\枪战突击，已装过默认换个名）
        —— 第二份装到别处，WebView2 的数据目录跟着 exe 走，模组/存档天然隔离，
           再也不会"第二个客户端读出第一个的模组"（以前 bat 一律装进同一目录）
     ② 桌面快捷方式动态指向**本次**安装的目录（以前 vbs 写死 %LOCALAPPDATA%）
     ③ 装完可以直接启动，进度看得见

   资源布局（由 打包安装包.py 生成 安装器资源.rc）：
     ID 1..N        RCDATA  每个内嵌文件一份（按清单顺序编号）
     ID 900         RCDATA  清单：UTF-8 文本，每行「编号\t相对路径\t大小」
                             （相对路径统一用 / ，装的时候换成 \ ）

   卸载：安装器.exe /卸载 <目录>  （删目录 + 删桌面快捷方式）
   位数：只支持 64 位 Windows。32 位系统启动时给指引（网页版不挑位数）。
   ========================================================================= */
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shlobj.h>
#include <shellapi.h>
#include <objbase.h>
#include <commctrl.h>
#include <stdio.h>
#include <shobjidl.h>

#define 资源清单号   900
#define 最大文件数   2048

/* ---------------- 内嵌文件清单（内存态） ---------------- */
typedef struct {
    int  编号;
    char 相对路径[512];      /* UTF-8，用 / 分隔 */
    DWORD 大小;
} 清单项;

static 清单项 g_清单[最大文件数];
static int   g_清单数 = 0;
static BYTE *g_数据[最大文件数];       /* 指向资源内存，不用释放 */
static DWORD g_数据大[最大文件数];

/* ---------------- 向导状态 ---------------- */
static HWND  g_窗;
static HFONT g_字;
static int   g_页 = 0;                    /* 0欢迎 1选目录 2安装中 3完成 */
static WCHAR g_目录[MAX_PATH * 2] = { 0 };
static BOOL  g_建快捷方式 = TRUE;
static BOOL  g_立即启动 = TRUE;
static BOOL  g_装好了 = FALSE;
static HWND  g_进度条;
static long long g_总字节 = 0, g_已写 = 0;

static HINSTANCE g_实例;

/* ---------------------------------------------------------------- 小工具 */

/* 路径规范化：正斜杠 → 反斜杠、去掉尾部反斜杠（保留 "C:\" 这种）。
   ⚠️ 必须做：安装目录从命令行/浏览框来时可能带正斜杠（D:/xx），
   IShellLinkW::SetPath 对混合斜杠会存出坏链接（实测 GetPath 只读出 "D"，
   桌面快捷方式双击无效——用户报"创建快捷方式的方式不对"）。 */
static void 规整路径(WCHAR *路径)
{
    for (WCHAR *p = 路径; *p; p++) if (*p == L'/') *p = L'\\';
    int len = lstrlenW(路径);
    while (len > 3 && 路径[len - 1] == L'\\') 路径[--len] = 0;
}

/* 目录合法性：不能装在盘符根 / 系统目录 / Windows 目录里 */
static BOOL 目录靠谱(const WCHAR *目录, WCHAR *原因, int 原因容量)
{
    if (!目录 || !目录[0] || lstrlenW(目录) < 3) {
        lstrcpynW(原因, L"请先选一个安装目录。", 原因容量); return FALSE;
    }
    WCHAR 干[MAX_PATH * 2];
    lstrcpynW(干, 目录, MAX_PATH * 2);
    for (WCHAR *p = 干; *p; p++) if (*p == L'/') *p = L'\\';
    int len = lstrlenW(干);
    while (len > 3 && 干[len - 1] == L'\\') 干[--len] = 0;

    /* 盘符根（C:\、D:\）：装这儿太乱，玩家也容易误删 */
    if (len == 3 && 干[1] == L':') {
        lstrcpynW(原因, L"不能直接装在盘符根目录（比如 D:\\）。\n\n请选一个文件夹（不存在会自动建），比如「D:\\PCL枪战突击修改版」。", 原因容量);
        return FALSE;
    }
    /* 系统目录（大小写不敏感、按整段路径比：\windows、\program files…） */
    const WCHAR *禁[] = { L"\\windows", L"\\program files", L"\\program files (x86)", L"\\users", L"\\$recycle.bin" };
    for (int i = 0; i < 5; i++) {
        int n = lstrlenW(禁[i]);
        for (const WCHAR *p = 干; *p; p++) {
            if ((p == 干 || p[-1] == L'\\') && (p[n] == 0 || p[n] == L'\\')) {
                BOOL 同 = TRUE;
                for (int k = 0; k < n; k++) {
                    WCHAR a = p[k], b = 禁[i][k];
                    if (a >= L'A' && a <= L'Z') a += 32;
                    if (b >= L'A' && b <= L'Z') b += 32;
                    if (a != b) { 同 = FALSE; break; }
                }
                if (同) {
                    _snwprintf(原因, 原因容量 - 1, L"这个目录是系统目录，不能装游戏。\n\n%s", 干);
                    return FALSE;
                }
            }
        }
    }
    return TRUE;
}

static void 宽窄互转(const WCHAR *宽, char *窄, int 窄容量)
{
    WideCharToMultiByte(CP_UTF8, 0, 宽, -1, 窄, 窄容量, NULL, NULL);
}

/* 读资源到内存（返回指针，生命周期=进程，别 free）。
   ⚠️ 类型名必须是 RT_RCDATA（rc 里写的就是标准 RCDATA），不是自定义字符串 ——
      写错就 FindResource 直接失败，表现为"安装包数据损坏"。 */
static const BYTE *取资源(int 号, DWORD *大小)
{
    HRSRC r = FindResourceW(g_实例, MAKEINTRESOURCEW(号), RT_RCDATA);
    if (!r) return NULL;
    HGLOBAL h = LoadResource(g_实例, r);
    if (!h) return NULL;
    DWORD s = SizeofResource(g_实例, r);
    const BYTE *p = (const BYTE *)LockResource(h);
    if (!p || !s) return NULL;
    if (大小) *大小 = s;
    return p;
}

/* 读清单资源，解析进 g_清单 */
static BOOL 读清单(void)
{
    DWORD 大小 = 0;
    const BYTE *p = 取资源(资源清单号, &大小);
    if (!p) return FALSE;
    int i, 行起 = 0;
    for (i = 0; i <= (int)大小 && g_清单数 < 最大文件数; i++) {
        if (i == (int)大小 || p[i] == '\n') {
            int 行长 = i - 行起;
            if (行长 > 0) {
                char 行[600] = { 0 };
                int n = 行长 < 599 ? 行长 : 599;
                memcpy(行, p + 行起, n);
                /* 去掉可能的 \r */
                char *cr = strchr(行, '\r'); if (cr) *cr = 0;
                /* 编号 \t 路径 \t 大小 */
                char *t1 = strchr(行, '\t');
                if (t1) {
                    *t1 = 0;
                    char *t2 = strchr(t1 + 1, '\t');
                    if (t2) {
                        *t2 = 0;
                        清单项 *it = &g_清单[g_清单数];
                        it->编号 = atoi(行);
                        strncpy(it->相对路径, t1 + 1, sizeof(it->相对路径) - 1);
                        it->大小 = (DWORD)_atoi64(t2 + 1);
                        DWORD 实大小 = 0;
                        const BYTE *d = 取资源(it->编号, &实大小);
                        if (d) {
                            g_数据[g_清单数] = (BYTE *)d;
                            g_数据大[g_清单数] = 实大小;
                            g_清单数++;
                            g_总字节 += 实大小;
                        }
                    }
                }
            }
            行起 = i + 1;
        }
    }
    return g_清单数 > 0;
}

/* 逐级建目录 */
static void 建目录(const WCHAR *路径)
{
    WCHAR 临时[MAX_PATH * 2];
    lstrcpynW(临时, 路径, MAX_PATH * 2);
    for (WCHAR *p = 临时 + 3; *p; p++) {
        if (*p == L'\\') { *p = 0; CreateDirectoryW(临时, NULL); *p = L'\\'; }
    }
    CreateDirectoryW(临时, NULL);
}

/* 把一个清单项写到目标目录 */
static BOOL 写文件(int 序号, const WCHAR *目标目录)
{
    char 相对[512];
    lstrcpynA(相对, g_清单[序号].相对路径, 512);
    for (char *s = 相对; *s; s++) if (*s == '/') *s = '\\';

    WCHAR 宽相对[MAX_PATH] = { 0 };
    MultiByteToWideChar(CP_UTF8, 0, 相对, -1, 宽相对, MAX_PATH);
    WCHAR 全路径[MAX_PATH * 2];
    _snwprintf(全路径, MAX_PATH * 2 - 1, L"%s\\%s", 目标目录, 宽相对);

    /* 先建父目录 */
    WCHAR 父[MAX_PATH * 2];
    lstrcpynW(父, 全路径, MAX_PATH * 2);
    WCHAR *斜 = wcsrchr(父, L'\\');
    if (斜) { *斜 = 0; 建目录(父); }

    HANDLE f = CreateFileW(全路径, GENERIC_WRITE, 0, NULL, CREATE_ALWAYS,
        FILE_ATTRIBUTE_NORMAL, NULL);
    if (f == INVALID_HANDLE_VALUE) return FALSE;
    DWORD 写 = 0;
    BOOL ok = WriteFile(f, g_数据[序号], g_数据大[序号], &写, NULL);
    CloseHandle(f);
    if (ok) g_已写 += g_数据大[序号];
    return ok;
}

/* 建快捷方式（桌面 + 开始菜单都建），路径动态指向本次安装目录。
   ⚠️ SetPath 前必须规整路径：混合斜杠会存出坏链接（GetPath 只读出盘符）。 */
static BOOL 建快捷方式于(const WCHAR *目录, const WCHAR *lnk全路径)
{
    WCHAR 目标exe[MAX_PATH * 2];
    _snwprintf(目标exe, MAX_PATH * 2 - 1, L"%s\\Plain Craft Launcher 2.exe", 目录);

    IShellLinkW *link = NULL;
    if (!SUCCEEDED(CoCreateInstance(&CLSID_ShellLink, NULL, CLSCTX_INPROC_SERVER,
            &IID_IShellLinkW, (void **)&link))) return FALSE;
    link->lpVtbl->SetPath(link, 目标exe);
    link->lpVtbl->SetWorkingDirectory(link, 目录);
    link->lpVtbl->SetIconLocation(link, 目标exe, 0);
    link->lpVtbl->SetDescription(link, L"PCL枪战突击修改版");

    IPersistFile *pf = NULL;
    BOOL ok = FALSE;
    if (SUCCEEDED(link->lpVtbl->QueryInterface(link, &IID_IPersistFile, (void **)&pf))) {
        ok = SUCCEEDED(pf->lpVtbl->Save(pf, lnk全路径, TRUE));
        pf->lpVtbl->Release(pf);
    }
    link->lpVtbl->Release(link);
    return ok;
}

static BOOL 建全部快捷方式(const WCHAR *安装目录)
{
    BOOL 桌 = FALSE, 菜 = FALSE;
    WCHAR 桌面[MAX_PATH * 2] = { 0 };
    if (SUCCEEDED(SHGetFolderPathW(NULL, CSIDL_DESKTOPDIRECTORY, NULL, 0, 桌面))) {
        WCHAR lnk[MAX_PATH * 2];
        _snwprintf(lnk, MAX_PATH * 2 - 1, L"%s\\PCL枪战突击修改版.lnk", 桌面);
        /* 桌面上已有同名快捷方式就不覆盖（可能是别的一份安装） */
        if (GetFileAttributesW(lnk) == INVALID_FILE_ATTRIBUTES)
            桌 = 建快捷方式于(安装目录, lnk);
        else 桌 = TRUE;
    }
    WCHAR 菜单[MAX_PATH * 2] = { 0 };
    if (SUCCEEDED(SHGetFolderPathW(NULL, CSIDL_PROGRAMS, NULL, 0, 菜单))) {
        WCHAR 目录[MAX_PATH * 2], lnk[MAX_PATH * 2];
        _snwprintf(目录, MAX_PATH * 2 - 1, L"%s\\PCL枪战突击修改版", 菜单);
        建目录(目录);
        _snwprintf(lnk, MAX_PATH * 2 - 1, L"%s\\PCL枪战突击修改版.lnk", 目录);
        菜 = 建快捷方式于(安装目录, lnk);
        /* 开始菜单里再放一个卸载入口 */
        WCHAR 卸[MAX_PATH * 2];
        _snwprintf(卸, MAX_PATH * 2 - 1, L"%s\\卸载 PCL枪战突击修改版.lnk", 目录);
        WCHAR 自己[MAX_PATH * 2] = { 0 };
        GetModuleFileNameW(NULL, 自己, MAX_PATH * 2);
        if (自己[0]) {
            IShellLinkW *link = NULL;
            if (SUCCEEDED(CoCreateInstance(&CLSID_ShellLink, NULL, CLSCTX_INPROC_SERVER,
                    &IID_IShellLinkW, (void **)&link))) {
                link->lpVtbl->SetPath(link, 自己);
                WCHAR 参数行[MAX_PATH * 2];
                _snwprintf(参数行, MAX_PATH * 2 - 1, L"/卸载 \"%s\"", 安装目录);
                link->lpVtbl->SetArguments(link, 参数行);
                link->lpVtbl->SetDescription(link, L"卸载《PCL枪战突击修改版》");
                IPersistFile *pf = NULL;
                if (SUCCEEDED(link->lpVtbl->QueryInterface(link, &IID_IPersistFile, (void **)&pf))) {
                    pf->lpVtbl->Save(pf, 卸, TRUE);
                    pf->lpVtbl->Release(pf);
                }
                link->lpVtbl->Release(link);
            }
        }
    }
    return 桌 || 菜;
}

/* ---------------------------------------------------------------- 界面 */

#define 钮宽 96
static HFONT 建字体(void)
{
    return CreateFontW(-15, 0, 0, 0, FW_NORMAL, 0, 0, 0, DEFAULT_CHARSET,
        OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY,
        DEFAULT_PITCH, L"Microsoft YaHei UI");
}

static HWND 加按钮(const WCHAR *文, int id, int x, int y, int w, int h)
{
    HWND b = CreateWindowW(L"BUTTON", 文, WS_CHILD | WS_VISIBLE | BS_PUSHBUTTON,
        x, y, w, h, g_窗, (HMENU)(INT_PTR)id, g_实例, NULL);
    if (b) SendMessageW(b, WM_SETFONT, (WPARAM)g_字, TRUE);
    return b;
}

static HWND 加静态(const WCHAR *文, int x, int y, int w, int h)
{
    HWND s = CreateWindowW(L"STATIC", 文, WS_CHILD | WS_VISIBLE,
        x, y, w, h, g_窗, NULL, g_实例, NULL);
    if (s) SendMessageW(s, WM_SETFONT, (WPARAM)g_字, TRUE);
    return s;
}

static HWND g_页0控件[4], g_页1控件[6], g_页2控件[3], g_页3控件[6];
static HWND g_目录框, g_浏览钮, g_进度文字;

static void 显示页(int 页)
{
    g_页 = 页;
    int i;
    for (i = 0; i < 4; i++) if (g_页0控件[i]) ShowWindow(g_页0控件[i], 页 == 0 ? SW_SHOW : SW_HIDE);
    for (i = 0; i < 6; i++) if (g_页1控件[i]) ShowWindow(g_页1控件[i], 页 == 1 ? SW_SHOW : SW_HIDE);
    for (i = 0; i < 3; i++) if (g_页2控件[i]) ShowWindow(g_页2控件[i], 页 == 2 ? SW_SHOW : SW_HIDE);
    for (i = 0; i < 6; i++) if (g_页3控件[i]) ShowWindow(g_页3控件[i], 页 == 3 ? SW_SHOW : SW_HIDE);
    SetWindowTextW(g_窗, 页 == 3 ? L"PCL枪战突击修改版 · 安装完成" : L"PCL枪战突击修改版 · 安装");
}

static void 默认目录(WCHAR *出, int 容量)
{
    WCHAR 基[MAX_PATH * 2] = { 0 };
    if (!SUCCEEDED(SHGetFolderPathW(NULL, CSIDL_LOCAL_APPDATA, NULL, 0, 基))) {
        lstrcpynW(基, L"C:\\PCL枪战突击修改版", MAX_PATH * 2);
    }
    _snwprintf(出, 容量 - 1, L"%s\\PCL枪战突击修改版", 基);
    /* 已经装过 → 默认换个名，玩家也可以自己改回覆盖安装 */
    WCHAR 试[MAX_PATH * 2];
    int n = 2;
    while (n < 50) {
        _snwprintf(试, MAX_PATH * 2 - 1, L"%s\\Plain Craft Launcher 2.exe", 出);
        if (GetFileAttributesW(试) == INVALID_FILE_ATTRIBUTES) break;
        _snwprintf(出, 容量 - 1, L"%s\\PCL枪战突击修改版-%d", 基, n);
        n++;
    }
}

/* 安装主过程（同步跑，文件小，几秒完） */
static void 执行安装(void)
{
    /* 目录规整 + 合法性校验（正斜杠换反斜杠、拒绝盘符根/系统目录） */
    规整路径(g_目录);
    WCHAR 原因[1024] = { 0 };
    if (!目录靠谱(g_目录, 原因, 1024)) {
        MessageBoxW(g_窗, 原因, L"目录不合适", MB_ICONWARNING);
        显示页(1);
        return;
    }

    建目录(g_目录);
    /* 可写测试 */
    WCHAR 测试文件[MAX_PATH * 2];
    _snwprintf(测试文件, MAX_PATH * 2 - 1, L"%s\\.写入测试", g_目录);
    HANDLE tf = CreateFileW(测试文件, GENERIC_WRITE, 0, NULL, CREATE_ALWAYS,
        FILE_ATTRIBUTE_TEMPORARY, NULL);
    if (tf == INVALID_HANDLE_VALUE) {
        MessageBoxW(g_窗, L"这个目录写不进去。\n\n换一个（比如桌面上的一个新文件夹），或者用管理员身份运行安装器。",
            L"PCL枪战突击修改版", MB_ICONWARNING);
        显示页(1);
        return;
    }
    CloseHandle(tf); DeleteFileW(测试文件);

    显示页(2);
    InvalidateRect(g_窗, NULL, TRUE);
    UpdateWindow(g_窗);

    int i;
    for (i = 0; i < g_清单数; i++) {
        if (!写文件(i, g_目录)) {
            WCHAR 消息[600];
            _snwprintf(消息, 599, L"有个文件没写进去：\n%s\n\n请换个目录再试。", g_目录);
            MessageBoxW(g_窗, 消息, L"PCL枪战突击修改版", MB_ICONERROR);
            显示页(1);
            return;
        }
        if (g_进度条) {
            int 百分比 = g_总字节 ? (int)(g_已写 * 100 / g_总字节) : 100;
            SendMessageW(g_进度条, PBM_SETPOS, 百分比, 0);
        }
        WCHAR 文[300];
        _snwprintf(文, 299, L"正在安装… %lld / %lld KB", g_已写 / 1024, g_总字节 / 1024);
        if (g_进度文字) SetWindowTextW(g_进度文字, 文);
    }

    BOOL 快捷 = FALSE;
    if (g_建快捷方式) {
        快捷 = 建全部快捷方式(g_目录);
        if (!快捷) {
            int r = MessageBoxW(g_窗,
                L"桌面快捷方式没建成（可能被安全软件拦了）。\n\n不影响游戏：安装目录里的「枪战客户端.exe」双击一样能玩。\n\n要再试一次吗？",
                L"PCL枪战突击修改版", MB_ICONWARNING | MB_YESNO);
            if (r == IDYES) 快捷 = 建全部快捷方式(g_目录);
        }
    }

    g_装好了 = TRUE;
    WCHAR 完成语[700];
    _snwprintf(完成语, 699,
        L"装好了：%s\n\n"
        L"· 桌面快捷方式：%s\n"
        L"· 想装第二份（数据/模组互不干扰）：重新运行本安装器，换个目录即可。",
        g_目录, 快捷 ? L"已创建「PCL枪战突击修改版」" : L"未创建（可手动进目录双击 exe）");
    if (g_页3控件[0]) SetWindowTextW(g_页3控件[0], 完成语);
    显示页(3);
}

/* ------------------------------------------------------------- 窗口过程 */

static LRESULT CALLBACK 窗口过程(HWND h, UINT m, WPARAM w, LPARAM l)
{
    switch (m) {
    case WM_CREATE: {
        /* ⚠️ 必须先存句柄：CreateWindowExW 是在 WM_CREATE **返回前**同步发出的，
           那时 wWinMain 里的 g_窗 还是 NULL → 下面所有控件以 NULL 为父窗口建
           WS_CHILD 全部失败（表现为"只有一个空窗口，什么都没有"——用户实测）。
           正确做法：WM_CREATE 一进门就把 h 存进 g_窗，控件才有父窗口。 */
        g_窗 = h;
        g_字 = 建字体();
        int x = 24, y = 24;

        /* ---- 页0 欢迎 ---- */
        g_页0控件[0] = 加静态(L"欢迎安装《PCL枪战突击修改版》", x, y, 500, 28);
        {
            HFONT 大 = CreateFontW(-21, 0, 0, 0, FW_BOLD, 0, 0, 0, DEFAULT_CHARSET,
                OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY,
                DEFAULT_PITCH, L"Microsoft YaHei UI");
            if (g_页0控件[0]) SendMessageW(g_页0控件[0], WM_SETFONT, (WPARAM)大, TRUE);
        }
        g_页0控件[1] = 加静态(
            L"这是一个单文件安装程序，游戏本体已经打包在里面，安装过程不联网。\r\n\r\n"
            L"· 安装目录可以自己选 —— 换目录再装一份，两边的存档、模组互不干扰\r\n"
            L"· 支持 Windows 10 / 11（64 位）\r\n"
            L"· 需要系统自带 WebView2 运行时（Win10/11 默认就有）",
            x, y + 40, 500, 150);
        g_页0控件[2] = 加按钮(L"下一步 >", 1002, 440, 340, 钮宽, 30);
        g_页0控件[3] = 加按钮(L"取消", 1003, 340, 340, 钮宽, 30);

        /* ---- 页1 选目录 ---- */
        g_页1控件[0] = 加静态(L"装到哪个目录？", x, y, 500, 24);
        g_目录框 = CreateWindowW(L"EDIT", L"", WS_CHILD | WS_VISIBLE | WS_BORDER | ES_AUTOHSCROLL,
            x, y + 32, 380, 26, h, (HMENU)2001, g_实例, NULL);
        SendMessageW(g_目录框, WM_SETFONT, (WPARAM)g_字, TRUE);
        g_页1控件[1] = g_目录框;
        g_浏览钮 = 加按钮(L"浏览…", 2002, x + 388, y + 32, 72, 26);
        g_页1控件[2] = 加静态(
            L"默认装在本机用户目录下。之前装过的话，这里会自动换个名字（装第二份）；\r\n"
            L"想覆盖安装就把目录改回原来的。", x, y + 70, 500, 44);
        g_页1控件[3] = 加按钮(L"开始安装", 2003, 440, 340, 钮宽, 30);
        g_页1控件[4] = 加按钮(L"< 上一步", 2004, 340, 340, 钮宽, 30);

        /* ---- 页2 进度 ---- */
        g_页2控件[0] = 加静态(L"正在安装，请稍等…", x, y, 400, 24);
        g_进度文字 = 加静态(L"", x, y + 40, 480, 24);
        g_页2控件[1] = g_进度文字;
        g_进度条 = CreateWindowExW(0, PROGRESS_CLASS, L"",
            WS_CHILD | WS_VISIBLE,
            x, y + 76, 512, 22, h, (HMENU)3001, g_实例, NULL);
        SendMessageW(g_进度条, PBM_SETRANGE32, 0, 100);
        g_页2控件[2] = g_进度条;

        /* ---- 页3 完成 ---- */
        g_页3控件[0] = 加静态(L"", x, y, 520, 120);
        g_页3控件[1] = CreateWindowW(L"BUTTON", L"创建桌面快捷方式",
            WS_CHILD | WS_VISIBLE | BS_AUTOCHECKBOX,
            x, y + 170, 240, 22, h, (HMENU)4001, g_实例, NULL);
        SendMessageW(g_页3控件[1], WM_SETFONT, (WPARAM)g_字, TRUE);
        CheckDlgButton(h, 4001, g_建快捷方式 ? BST_CHECKED : BST_UNCHECKED);
        g_页3控件[2] = CreateWindowW(L"BUTTON", L"立即启动游戏",
            WS_CHILD | WS_VISIBLE | BS_AUTOCHECKBOX,
            x, y + 198, 240, 22, h, (HMENU)4002, g_实例, NULL);
        SendMessageW(g_页3控件[2], WM_SETFONT, (WPARAM)g_字, TRUE);
        CheckDlgButton(h, 4002, g_立即启动 ? BST_CHECKED : BST_UNCHECKED);
        g_页3控件[3] = 加静态(
            L"小提示：模组由玩家自己在游戏里装（设置 → 模组管理）。\r\n"
            L"以后想卸载：删掉安装目录 + 桌面图标即可，不留别的东西。",
            x, y + 232, 520, 50);
        g_页3控件[4] = 加按钮(L"完成", 4003, 440, 340, 钮宽, 30);
        g_页3控件[5] = NULL;

        /* 默认目录先填好（页1 用） */
        默认目录(g_目录, MAX_PATH * 2);
        SetWindowTextW(g_目录框, g_目录);

        显示页(0);
        return 0;
    }

    case WM_COMMAND: {
        int id = LOWORD(w);
        if (id == 1003) { DestroyWindow(h); return 0; }                    /* 取消 */
        if (id == 1002) { 显示页(1); return 0; }                           /* 下一步 */
        if (id == 2004) { 显示页(0); return 0; }                           /* 上一步 */
        if (id == 2002) {                                                 /* 浏览… */
            BROWSEINFOW bi;
            memset(&bi, 0, sizeof(bi));
            bi.hwndOwner = h;
            bi.lpszTitle = L"选一个文件夹（不存在的话会自动建）";
            bi.ulFlags = BIF_NEWDIALOGSTYLE | BIF_RETURNONLYFSDIRS | BIF_EDITBOX;
            PIDLIST_ABSOLUTE 项 = SHBrowseForFolderW(&bi);
            if (项) {
                WCHAR 选[MAX_PATH * 2] = { 0 };
                if (SHGetPathFromIDListW(项, 选)) {
                    lstrcpynW(g_目录, 选, MAX_PATH * 2);
                    SetWindowTextW(g_目录框, g_目录);
                }
                CoTaskMemFree(项);
            }
            return 0;
        }
        if (id == 2003) {                                                 /* 开始安装 */
            WCHAR 填的[MAX_PATH * 2] = { 0 };
            GetWindowTextW(g_目录框, 填的, MAX_PATH * 2);
            lstrcpynW(g_目录, 填的, MAX_PATH * 2);
            执行安装();
            return 0;
        }
        if (id == 4001) { g_建快捷方式 = IsDlgButtonChecked(h, 4001) == BST_CHECKED; return 0; }
        if (id == 4002) { g_立即启动 = IsDlgButtonChecked(h, 4002) == BST_CHECKED; return 0; }
        if (id == 4003) {                                                 /* 完成 */
            if (g_立即启动 && g_装好了) {
                WCHAR exe[MAX_PATH * 2];
                _snwprintf(exe, MAX_PATH * 2 - 1, L"%s\\Plain Craft Launcher 2.exe", g_目录);
                ShellExecuteW(NULL, L"open", exe, NULL, g_目录, SW_SHOWNORMAL);
            }
            DestroyWindow(h);
            return 0;
        }
        return 0;
    }
    case WM_DESTROY:
        PostQuitMessage(0);
        return 0;
    }
    return DefWindowProcW(h, m, w, l);
}

/* --------------------------------------------------------------- 卸载模式 */

static int 卸载(int 参数个数, wchar_t **参数)
{
    if (参数个数 < 2) {
        MessageBoxW(NULL, L"用法：本程序 /卸载 <安装目录>", L"枪战突击卸载", MB_ICONINFORMATION);
        return 2;
    }
    const WCHAR *目录 = 参数[1];

    /* 先删桌面快捷方式 */
    WCHAR 桌面[MAX_PATH * 2] = { 0 };
    WCHAR lnk[MAX_PATH * 2];
    if (SUCCEEDED(SHGetFolderPathW(NULL, CSIDL_DESKTOPDIRECTORY, NULL, 0, 桌面))) {
        _snwprintf(lnk, MAX_PATH * 2 - 1, L"%s\\PCL枪战突击修改版.lnk", 桌面);
        DeleteFileW(lnk);
    }

    /* 再删整个安装目录（SHFileOperation，双 \0 结尾） */
    WCHAR 从[MAX_PATH * 2 + 1] = { 0 };
    lstrcpynW(从, 目录, MAX_PATH * 2);
    从[lstrlenW(从) + 1] = 0;
    SHFILEOPSTRUCTW op;
    memset(&op, 0, sizeof(op));
    op.wFunc = FO_DELETE;
    op.pFrom = 从;
    op.fFlags = FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI;

    /* ⚠️ MB_DEFBUTTON2：默认焦点放「取消」上。误按回车不该直接删掉人家的存档。 */
    int r = MessageBoxW(NULL,
        L"确定卸载《枪战突击》电脑客户端吗？\n\n安装目录里的文件会全部删除（包括你在游戏里装的模组和本地存档）。",
        L"枪战突击卸载", MB_ICONWARNING | MB_OKCANCEL | MB_DEFBUTTON2);
    if (r != IDOK) return 1;

    int 结果 = SHFileOperationW(&op);
    if (结果 == 0 && !GetFileAttributesW(目录)) {
        MessageBoxW(NULL, L"卸载完成。桌面图标如还在，手动删掉即可。", L"枪战突击", MB_ICONINFORMATION);
        return 0;
    }
    MessageBoxW(NULL, L"有些文件没删掉（可能游戏正在运行）。\n\n关掉游戏后再试，或手动删掉安装目录。",
        L"PCL枪战突击修改版", MB_ICONWARNING);
    return 3;
}

/* ------------------------------------------------------------------ 入口 */

int WINAPI wWinMain(HINSTANCE 实例, HINSTANCE 上个, PWSTR 命令行, int 显示)
{
    (void)上个;
    g_实例 = 实例;
    CoInitializeEx(NULL, COINIT_APARTMENTTHREADED);

    /* 卸载模式：安装器.exe /卸载 <目录> */
    int 参数个数 = 0;
    wchar_t **参数 = CommandLineToArgvW(GetCommandLineW(), &参数个数);
    if (参数个数 >= 2 && (lstrcmpiW(参数[1], L"/卸载") == 0 || lstrcmpiW(参数[1], L"/uninstall") == 0)) {
        int r = 卸载(参数个数, 参数);
        CoUninitialize();
        return r;
    }

    /* 静默安装：安装器.exe /S <目录>（无界面，直接装；给脚本和高级用户用） */
    if (参数个数 >= 3 && lstrcmpiW(参数[1], L"/S") == 0) {
        if (!读清单()) return 5;
        lstrcpynW(g_目录, 参数[2], MAX_PATH * 2);
        规整路径(g_目录);
        if (!目录靠谱(g_目录, (WCHAR *)L"", 0)) return 7;
        建目录(g_目录);
        int i;
        for (i = 0; i < g_清单数; i++) {
            if (!写文件(i, g_目录)) return 6;
            if (g_进度条) SendMessageW(g_进度条, PBM_SETPOS,
                g_总字节 ? (int)(g_已写 * 100 / g_总字节) : 100, 0);
        }
        建全部快捷方式(g_目录);
        CoUninitialize();
        return 0;
    }

    /* 位数检测：本安装器是 64 位的 */
    SYSTEM_INFO si;
    GetNativeSystemInfo(&si);
    if (si.wProcessorArchitecture == PROCESSOR_ARCHITECTURE_INTEL) {
        MessageBoxW(NULL,
            L"这台电脑是 32 位系统，《枪战突击》电脑客户端目前只提供 64 位版本。\n\n"
            L"32 位系统可以玩网页版：用浏览器打开游戏网页即可（功能一样，也不占地方）。",
            L"枪战突击", MB_ICONINFORMATION);
        CoUninitialize();
        return 4;
    }

    if (!读清单()) {
        MessageBoxW(NULL, L"安装包数据损坏了（读不到内嵌文件）。请重新下载本安装器。",
            L"PCL枪战突击修改版", MB_ICONERROR);
        CoUninitialize();
        return 5;
    }

    INITCOMMONCONTROLSEX icc;
    icc.dwSize = sizeof(icc);
    icc.dwICC = ICC_PROGRESS_CLASS;
    InitCommonControlsEx(&icc);

    WNDCLASSEXW wc;
    memset(&wc, 0, sizeof(wc));
    wc.cbSize = sizeof(wc);
    wc.lpfnWndProc = 窗口过程;
    wc.hInstance = 实例;
    wc.hCursor = LoadCursor(NULL, IDC_ARROW);
    wc.hbrBackground = (HBRUSH)(COLOR_BTNFACE + 1);
    wc.lpszClassName = L"QZST安装器";
    wc.hIcon = LoadIcon(NULL, IDI_APPLICATION);
    RegisterClassExW(&wc);

    g_窗 = CreateWindowExW(0, L"QZST安装器", L"PCL枪战突击修改版 · 安装",
        WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU,
        CW_USEDEFAULT, CW_USEDEFAULT, 596, 430,
        NULL, NULL, 实例, NULL);
    if (!g_窗) return 0;

    ShowWindow(g_窗, 显示 ? 显示 : SW_SHOWNORMAL);
    UpdateWindow(g_窗);

    MSG msg;
    while (GetMessageW(&msg, NULL, 0, 0) > 0) {
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }
    CoUninitialize();
    return 0;
}
