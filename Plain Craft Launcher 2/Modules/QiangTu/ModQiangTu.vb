' ============================================================================
'  PCL 枪战突击修改版 · 游戏数据层（ModQiangTu.vb）
'  ----------------------------------------------------------------------------
'  核心思路（用户口径：直接改 PCL，别另做界面）：
'    · 把 PCL 眼里的「.minecraft」指向 <启动器目录>\枪战突击\
'    · 让 versions 目录直接当枪战突击的版本目录（每个版本一个文件夹 + 一个占位 JSON）
'      → PCL 原版界面（版本列表 / 实例管理 / 模组 / 存档）全部原样复用，不改美术不改排布
'    · 「启动」拦在 McLaunchStart 之前：枪战突击版本直接开 index.html，不走 Java
'    · 下载走 PCL 自带的 LoaderDownload（多线程分片）
'    · 账号：启动器自己管一份账号表（账号.json），启动时注入进游戏
' ============================================================================

Imports System.Diagnostics
Imports System.IO
Imports System.IO.Compression
Imports System.Net
Imports System.Text
Imports System.Threading
Imports System.Threading.Tasks
Imports Newtonsoft.Json.Linq

Public Module ModQiangTu

    ' ---------------- 常量 ----------------
    ''' <summary>枪战突击发布中心（GitHub Pages）。</summary>
    Public Const 发布中心 As String = "https://xintbc.github.io/qiangtuskill/"

    ''' <summary>版本名前缀（PCL 里显示成「枪战突击 v1.7.0」）。</summary>
    Public Const 实例前缀 As String = "枪战突击 v"

    ''' <summary>
    ''' 游戏根目录（= PCL 眼里的 .minecraft）。
    ''' </summary>
    Public ReadOnly Property 游戏根目录 As String
        Get
            Dim 根 = Path.Combine(Path.GetDirectoryName(GetType(ModQiangTu).Assembly.Location), "枪战突击")
            Try
                Directory.CreateDirectory(根)
                Directory.CreateDirectory(Path.Combine(根, "versions"))
            Catch
            End Try
            Return 根
        End Get
    End Property

    ''' <summary>版本库 = 游戏根\versions（就是 PCL 的版本目录）。</summary>
    Public ReadOnly Property 版本库目录 As String
        Get
            Return Path.Combine(游戏根目录, "versions")
        End Get
    End Property

    ''' <summary>版本在 PCL 里的显示名（也是目录名）。</summary>
    Public Function 实例名(项 As 版本信息) As String
        Return 实例前缀 & 项.版本
    End Function

    ''' <summary>判断某个 PCL 版本实例是不是枪战突击的。</summary>
    Public Function 是枪战突击实例(名 As String) As Boolean
        If String.IsNullOrEmpty(名) Then Return False
        Return 名.StartsWith("枪战突击", StringComparison.OrdinalIgnoreCase)
    End Function

    ' ---------------- 数据结构 ----------------
    Public Class 版本信息
        Public Property 版本 As String = ""
        Public Property 文件 As String = ""
        Public Property 大小 As Long = 0
        Public Property Md5 As String = ""
        Public Property 日期 As String = ""
        Public Property 说明 As String = ""
        Public Property 类型 As String = "正式"      ' 正式 / 预览（PCL 里按 release / snapshot 分组）

        ''' <summary>这个版本在本地是否已装好（目录存在且有 index.html）。</summary>
        Public ReadOnly Property 已安装 As Boolean
            Get
                Dim 目录 = 本版本目录(Me)
                Return File.Exists(Path.Combine(目录, "index.html")) OrElse
                       File.Exists(Path.Combine(目录, "多文件版", "index.html"))
            End Get
        End Property

        Public ReadOnly Property 显示大小 As String
            Get
                If 大小 >= 1048576 Then Return (大小 / 1048576.0).ToString("0.0") & " MB"
                If 大小 >= 1024 Then Return (大小 / 1024).ToString("0") & " KB"
                Return 大小.ToString() & " B"
            End Get
        End Property
    End Class

    Public Class 模组信息
        Public Property 名字 As String = ""
        Public Property 版本 As String = ""
        Public Property 大小 As Long = 0
        Public Property Md5 As String = ""
        Public Property 兼容 As String = ""
    End Class

    ''' <summary>某个版本的目录（版本库\枪战突击 vX.Y.Z）。</summary>
    Public Function 本版本目录(项 As 版本信息) As String
        Return Path.Combine(版本库目录, 实例名(项))
    End Function

    ''' <summary>按实例名（PCL 传过来的）找本地目录。</summary>
    Public Function 实例目录(名 As String) As String
        Return Path.Combine(版本库目录, 名)
    End Function

    ' ---------------- 拉清单 ----------------
    ''' <summary>从发布中心拉版本 + 模组清单。失败返回空表（调用方给提示）。</summary>
    Public Function 拉清单(Optional ByRef 模组表 As List(Of 模组信息) = Nothing) As List(Of 版本信息)
        Dim 结果 As New List(Of 版本信息)
        模组表 = New List(Of 模组信息)
        Try
            Dim json = 取文本(发布中心 & "manifest.json")
            Dim 根 = JObject.Parse(json)

            Dim 版本数组 = TryCast(根("版本"), JArray)
            If 版本数组 IsNot Nothing Then
                For Each 项 As JObject In 版本数组
                    结果.Add(New 版本信息 With {
                        .版本 = If(项("版本")?.ToString(), ""),
                        .文件 = If(项("文件")?.ToString(), ""),
                        .大小 = If(项("大小") IsNot Nothing, 项("大小").Value(Of Long)(), 0L),
                        .Md5 = If(项("md5")?.ToString(), ""),
                        .日期 = If(项("日期")?.ToString(), ""),
                        .说明 = If(项("说明")?.ToString(), ""),
                        .类型 = If(项("类型")?.ToString(), "正式")
                    })
                Next
            End If

            Dim 模组数组 = TryCast(根("模组"), JArray)
            If 模组数组 IsNot Nothing Then
                For Each 项 As JObject In 模组数组
                    模组表.Add(New 模组信息 With {
                        .名字 = If(项("名字")?.ToString(), ""),
                        .版本 = If(项("版本")?.ToString(), ""),
                        .大小 = If(项("大小") IsNot Nothing, 项("大小").Value(Of Long)(), 0L),
                        .Md5 = If(项("md5")?.ToString(), ""),
                        .兼容 = If(项("兼容")?.ToString(), "")
                    })
                Next
            End If
        Catch ex As Exception
            Logger.Warn(ex, "枪战突击：拉取清单失败")
        End Try
        Return 结果
    End Function

    ''' <summary>
    ''' 给某个版本目录写 PCL 认得的「版本 JSON」占位文件（没它 PCL 不把它当版本）。
    ''' 内容按最小 MC 版本结构写，PCL 只用来显示名字/图标，不会真的去启动 Java（启动被我们拦了）。
    ''' </summary>
    Public Sub 写版本占位Json(项 As 版本信息)
        Try
            Dim 目录 = 本版本目录(项)
            Directory.CreateDirectory(目录)
            Dim 名 = 实例名(项)
            Dim 路径 = Path.Combine(目录, 名 & ".json")
            ' 老格式（id 写成了实例名）要重写，不然 PCL 一直认不出来
            If File.Exists(路径) Then
                Try
                    Dim 旧 = JObject.Parse(File.ReadAllText(路径, Encoding.UTF8))
                    If If(旧("id")?.ToString(), "") = 名 Then
                        File.Delete(路径)
                    Else
                        Return
                    End If
                Catch
                    Try : File.Delete(路径) : Catch : End Try
                End Try
            End If
            ' ⚠️ 关键：PCL 会用 JSON 里的 id 去解析「MC 版本号」（Version.VanillaName）。
            '    解析不出来（比如 id 写成「枪战突击 v1.7.0」）→ VanillaName = "Unknown"
            '    → State 被判成 Error，版本列表里直接不显示（这就是「扫描不到游戏本体」的根因）。
            '    所以 id 固定写成一个合法 MC 版本号，显示名走目录名（Name）——两者可以不同：
            '    PCL 找 JSON 用的是「目录名.json」，列表里显示的也是目录名。
            Dim obj As New JObject From {
                {"id", "1.12.2"},
                {"type", If(项.类型 = "预览", "snapshot", "release")},
                {"time", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")},
                {"releaseTime", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")},
                {"libraries", New JArray()},
                {"mainClass", "qiangtu.launcher"},
                {"minecraftArguments", ""},
                {"assets", "1.12"},
                {"assetIndex", New JObject From {{"id", "1.12"}, {"totalSize", 0}}}
            }
            File.WriteAllText(路径, obj.ToString(), New UTF8Encoding(False))
        Catch ex As Exception
            Logger.Warn(ex, "写版本占位 JSON 失败")
        End Try
    End Sub

    ''' <summary>本地已装的枪战突击版本数（用于判断要不要先下载）。</summary>
    Public Function 本地版本数() As Integer
        Dim 数 As Integer = 0
        Try
            If Not Directory.Exists(版本库目录) Then Return 0
            For Each d In Directory.GetDirectories(版本库目录)
                If 是枪战突击实例(Path.GetFileName(d)) Then 数 += 1
            Next
        Catch
        End Try
        Return 数
    End Function

    ''' <summary>扫本地已装的枪战突击版本目录（用于补占位 JSON）。</summary>
    Public Sub 修补所有占位Json()
        Try
            If Not Directory.Exists(版本库目录) Then Return
            For Each d In Directory.GetDirectories(版本库目录)
                Dim 名 = Path.GetFileName(d)
                If Not 是枪战突击实例(名) Then Continue For
                Dim 路径 = Path.Combine(d, 名 & ".json")
                If File.Exists(路径) Then Continue For
                Dim 版本 = 名.Substring(实例前缀.Length)
                写版本占位Json(New 版本信息 With {.版本 = 版本})
            Next
        Catch ex As Exception
            Logger.Warn(ex, "修补占位 JSON 失败")
        End Try
    End Sub

    ' ---------------- 下载 ----------------
    ' 分片线程共享的已下载字节数（Interlocked 累加，字段而不是局部变量——闭包 ByRef 不稳）
    Private _已下载字节 As Long

    ''' <summary>分片线程每收到一块就累加（Interlocked 原子加）。</summary>
    Private Sub 累加字节(增 As Long)
        Interlocked.Add(_已下载字节, 增)
    End Sub

    ''' <summary>
    ''' 下载并安装一个版本：多线程下载 zip → 解压 → 写占位 JSON。
    '''
    ''' ⚠️ 不用 PCL 的 LoaderDownload 了：它的 Progress 到 0.97/0.98 就停住不动
    '''    （内部还有校验/收尾，进度不对外更新），玩家看到的就是「卡在 98%」。
    '''    这里自己实现分片下载，进度是真实字节比，下载占 0~85%，解压占 85~100%。
    ''' </summary>
    Public Sub 下载并安装(项 As 版本信息, Optional 进度 As Action(Of Double, String) = Nothing)
        ' 每个版本一个独立目录（枪战突击 v1.7.0 / v1.8.0 …… 天然共存，互不覆盖）
        Dim 目录 = 本版本目录(项)
        Directory.CreateDirectory(目录)
        Dim zip路径 = Path.Combine(目录, "_下载.zip")
        Dim url = 发布中心 & "版本/" & Uri.EscapeDataString(项.文件)

        If File.Exists(zip路径) Then
            Try : File.Delete(zip路径) : Catch : End Try
        End If

        ' ① 下载（0~85%）
        下载文件(url, zip路径, 进度)

        If Not File.Exists(zip路径) Then Throw New FileNotFoundException("下载失败：临时文件不存在")

        ' ② 解压（85~100%，带条目进度）
        '    解压成功才删 zip：万一失败，zip 还在目录里，玩家可以自己解压/我们下次重试。
        '    以前用 Finally 无条件删 → 解压一出错包就没了，玩家只能重新下。
        解压Zip(zip路径, 目录, 进度)
        Try : File.Delete(zip路径) : Catch : End Try

        写版本占位Json(项)
        Try
            File.WriteAllText(Path.Combine(目录, ".已安装"), 项.版本 & vbCrLf & DateTime.Now.ToString("yyyy-MM-dd HH:mm"))
        Catch
        End Try
        进度?.Invoke(1.0, "已装好")
    End Sub

    ''' <summary>
    ''' 自己实现的多线程分片下载。进度是**真实已下载字节 / 总字节**。
    '''   · 先用 Range: bytes=0-0 探总长（比 HEAD 兼容性好；不支持 Range 的源自动退单线程）
    '''   · 8 线程各下一段，写同一个文件的各自偏移（每个线程独立句柄，避免共享指针乱序）
    ''' </summary>
    Public Sub 下载文件(URL As String, 目标 As String, Optional 外进度 As Action(Of Double, String) = Nothing)
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 Or SecurityProtocolType.Tls11

        ' ★ 关掉系统代理：本机 IE 代理设成了 127.0.0.1:80 但根本没服务在监听，
        '    .NET 默认会去连它 → 每次都是"连接被拒绝"，表现就是下载不出东西 / 卡在某个百分比。
        '    这里统一直连（发布中心是公网 GitHub Pages，直连可达）。
        WebRequest.DefaultWebProxy = Nothing

        ' ① 探总长 / 是否支持分片
        Dim 总长 As Long = -1
        Dim 支持分片 As Boolean = False
        Dim 探 = CType(WebRequest.Create(URL), HttpWebRequest)
        探.Method = "GET"
        探.UserAgent = "QiangTuLauncher/1.0"
        探.Timeout = 30000
        探.ReadWriteTimeout = 30000
        探.Proxy = Nothing
        探.AddRange(0, 0)
        Try
            Using 响 = CType(探.GetResponse(), HttpWebResponse)
                If 响.StatusCode = HttpStatusCode.PartialContent Then
                    支持分片 = True
                    Dim 范围 = 响.Headers("Content-Range")
                    If Not String.IsNullOrEmpty(范围) Then
                        Dim 斜 = 范围.LastIndexOf("/"c)
                        If 斜 > 0 Then Long.TryParse(范围.Substring(斜 + 1), 总长)
                    End If
                Else
                    总长 = 响.ContentLength
                End If
            End Using
        Catch ex As Exception
            Throw New Exception("连不上发布中心（" & ex.Message & "）")
        End Try

        外进度?.Invoke(0, "开始下载…")

        ' ② 太小 or 不支持分片 → 单线程（进度照样真实）
        If Not 支持分片 OrElse 总长 < 262144 Then
            If 总长 <= 0 Then
                ' 连长度都不知道：单线程整下，进度只报已下载字节
                Dim 已 As Long = 0
                Dim 请 = CType(WebRequest.Create(URL), HttpWebRequest)
                请.UserAgent = "QiangTuLauncher/1.0"
                请.Timeout = 60000
                请.Proxy = Nothing
                Using 响 = CType(请.GetResponse(), HttpWebResponse)
                    Using 入 = 响.GetResponseStream()
                        Using 出 As New FileStream(目标, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)
                            Dim 块(65535) As Byte
                            Dim n As Integer
                            Do
                                n = 入.Read(块, 0, 块.Length)
                                If n <= 0 Then Exit Do
                                出.Write(块, 0, n)
                                已 += n
                                外进度?.Invoke(0.5, "下载中 " & FormatSize(已))
                            Loop
                        End Using
                    End Using
                End Using
                外进度?.Invoke(1.0, "下载完成 " & FormatSize(已))
                Return
            End If
            ' 知道长度但只能整下
            Dim 请2 = CType(WebRequest.Create(URL), HttpWebRequest)
            请2.UserAgent = "QiangTuLauncher/1.0"
            请2.Timeout = 60000
            请2.Proxy = Nothing
            Using 响 = CType(请2.GetResponse(), HttpWebResponse)
                Using 入 = 响.GetResponseStream()
                    Using 出 As New FileStream(目标, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)
                        Dim 块(65535) As Byte, 已 As Long = 0
                        Dim n As Integer = 入.Read(块, 0, 块.Length)
                        While n > 0
                            出.Write(块, 0, n)
                            已 += n
                            外进度?.Invoke(已 / 总长, "下载中 " & FormatSize(已) & " / " & FormatSize(总长))
                            n = 入.Read(块, 0, 块.Length)
                        End While
                    End Using
                End Using
            End Using
            Return
        End If

        ' ③ 分片：预建文件到总长，各线程写自己的区间
        Using 预 As New FileStream(目标, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)
            预.SetLength(总长)
        End Using

        Dim 线程数 As Integer = 8
        If 总长 < 1024 * 1024 Then 线程数 = 4
        Dim 每段 As Long = 总长 \ 线程数
        _已下载字节 = 0
        Dim 出错 As String = ""
        Dim 锁 As New Object()

        Dim 任务s As New List(Of Task)
        For i = 0 To 线程数 - 1
            Dim 号 = i
            Dim 起 As Long = 每段 * 号
            Dim 止 As Long = If(号 = 线程数 - 1, 总长 - 1, 每段 * (号 + 1) - 1)
            任务s.Add(Task.Run(Sub()
                                  Try
                                      下载一段(URL, 目标, 起, 止, AddressOf 累加字节)
                                  Catch ex As Exception
                                      SyncLock 锁
                                          If 出错 = "" Then 出错 = ex.Message
                                      End SyncLock
                                  End Try
                              End Sub))
        Next

        ' 主线程边等边报进度
        Do While Not Task.WaitAll(任务s.ToArray(), 150)
            Dim 已 = Interlocked.Read(_已下载字节)
            Dim 比例 As Double = If(总长 > 0, CDbl(已) / 总长, 0)
            外进度?.Invoke(Math.Min(比例, 1.0), "下载中 " & FormatSize(已) & " / " & FormatSize(总长) & "（" & 线程数 & " 线程，" & 比例.ToString("P0") & "）")
        Loop

        If 出错 <> "" Then Throw New Exception("下载失败：" & 出错)
        If Interlocked.Read(_已下载字节) < 总长 Then
            Throw New Exception("下载不完整（" & FormatSize(Interlocked.Read(_已下载字节)) & " / " & FormatSize(总长) & "），请重试")
        End If
        外进度?.Invoke(1.0, "下载完成 " & FormatSize(总长))
    End Sub

    ''' <summary>下一个区间（失败重试 2 次）。</summary>
    Private Sub 下载一段(URL As String, 目标 As String, 起 As Long, 止 As Long, 累加 As Action(Of Long))
        Dim 最后一次 As Exception = Nothing
        For 试 = 1 To 3
            Try
                Dim 请 = CType(WebRequest.Create(URL), HttpWebRequest)
                请.Method = "GET"
                请.UserAgent = "QiangTuLauncher/1.0"
                请.Timeout = 30000
                请.ReadWriteTimeout = 30000
                请.Proxy = Nothing
                请.AddRange(起, 止)
                Using 响 = CType(请.GetResponse(), HttpWebResponse)
                    Using 入 = 响.GetResponseStream()
                        Using 出 As New FileStream(目标, FileMode.Open, FileAccess.Write, FileShare.ReadWrite)
                            出.Seek(起, SeekOrigin.Begin)
                            Dim 块(32767) As Byte
                            Dim n As Integer = 入.Read(块, 0, 块.Length)
                            While n > 0
                                出.Write(块, 0, n)
                                累加(n)
                                n = 入.Read(块, 0, 块.Length)
                            End While
                        End Using
                    End Using
                End Using
                Return
            Catch ex As Exception
                最后一次 = ex
                Thread.Sleep(400 * 试)
            End Try
        Next
        Throw New Exception("分段下载失败（" & 起 & "-" & 止 & "）：" & If(最后一次 Is Nothing, "未知", 最后一次.Message))
    End Sub

    ''' <summary>字节数 → 好读的文本。</summary>
    Private Function FormatSize(字节 As Long) As String
        If 字节 >= 1048576 Then Return (字节 / 1048576.0).ToString("0.0") & " MB"
        If 字节 >= 1024 Then Return (字节 / 1024.0).ToString("0") & " KB"
        Return 字节.ToString() & " B"
    End Function

    ''' <summary>
    ''' zip 解压到指定目录（覆盖同名文件，防目录穿越）。
    ''' 带进度：解压占整体 85~100%（下载占 0~85%），进度条不会停在 97% 装死。
    ''' </summary>
    Public Sub 解压Zip(zip路径 As String, 目标目录 As String, Optional 进度 As Action(Of Double, String) = Nothing)
        Directory.CreateDirectory(目标目录)
        Using 档案 As ZipArchive = ZipFile.OpenRead(zip路径)
            Dim 全部 As Integer = 档案.Entries.Count
            Dim 做 As Integer = 0
            进度?.Invoke(0.85, "解压中 0/" & 全部)
            For Each 条目 As ZipArchiveEntry In 档案.Entries
                做 += 1
                If String.IsNullOrEmpty(条目.Name) Then Continue For
                Dim 目标 = Path.GetFullPath(Path.Combine(目标目录, 条目.FullName.Replace("/"c, "\"c)))
                If Not 目标.StartsWith(Path.GetFullPath(目标目录), StringComparison.OrdinalIgnoreCase) Then Continue For
                Directory.CreateDirectory(Path.GetDirectoryName(目标))
                条目.ExtractToFile(目标, True)
                ' 每 3 个报一次（刷太勤反而卡 UI）
                If 做 Mod 3 = 0 OrElse 做 = 全部 Then
                    进度?.Invoke(0.85 + 0.15 * 做 / Math.Max(1, 全部), "解压中 " & 做 & "/" & 全部)
                End If
            Next
        End Using
    End Sub

    ' ---------------- 模组 ----------------
    ''' <summary>把模组下载到指定版本的 mode\模组\ 目录。</summary>
    Public Sub 下载模组(模组 As 模组信息, 目标版本 As 版本信息, Optional 进度 As Action(Of Double, String) = Nothing)
        Dim 版本目录 = 本版本目录(目标版本)
        Dim 模组目录 = Path.Combine(版本目录, "mode", "模组")
        If Not Directory.Exists(Path.Combine(版本目录, "mode")) AndAlso
           Directory.Exists(Path.Combine(版本目录, "多文件版", "mode")) Then
            模组目录 = Path.Combine(版本目录, "多文件版", "mode", "模组")
        End If
        Directory.CreateDirectory(模组目录)

        Dim 目标 = Path.Combine(模组目录, 模组.名字)
        Dim url = 发布中心 & "模组/" & Uri.EscapeDataString(模组.名字)
        Dim 任务 As New LoaderDownload("模组 " & 模组.名字, New List(Of NetFile) From {New NetFile({url}, 目标)})
        任务.Start()
        While 任务.State = LoadState.Waiting OrElse 任务.State = LoadState.Loading
            进度?.Invoke(任务.Progress, "下载中 " & 任务.Progress.ToString("P0"))
            Thread.Sleep(150)
        End While
        If 任务.State = LoadState.Failed Then
            Dim 原因 As String = "未知错误"
            If 任务.Error IsNot Nothing Then 原因 = 任务.Error.Message
            Throw New Exception("模组下载失败：" & 原因)
        End If
        任务.WaitForExit()
    End Sub

    ' ---------------- 启动 ----------------
    ''' <summary>
    ''' 启动某个实例（PCL 点「启动游戏」时会走到这）。
    ''' 参数是 PCL 的实例名；账号会自动带上（启动器那份账号表里当前选中的）。
    ''' </summary>
    Public Sub 启动实例(实例名 As String, Optional 账号 As 账号信息 = Nothing)
        Dim 目录 = 实例目录(实例名)
        Dim 参 As String = 账号参数(账号)     ' 形如 "?pclname=xx&pclcode=yy"

        ' 【特殊兼容·核心】PCL 原版启动是「拼 Java 命令行 → 起 java.exe 进程」，
        ' 我们的游戏是 Three.js 网页，完全没有 Java。所以启动这一步必须整体换掉：
        '   ① 优先跑版本目录里自带的 枪战突击.exe（内嵌 WebView2 外壳，双击即玩那种）
        '      账号通过命令行参数传进去 → exe 拼到 index.html?pclname=… 后面
        '   ② 没有 exe（老版本包）→ 退化成用默认浏览器打开 index.html（照样能玩）
        ' 绝不走 McLaunchStart / Java，那条路在枪战突击上永远起不来。
        Dim 壳 = Path.Combine(目录, "枪战突击.exe")
        If Not File.Exists(壳) Then 壳 = Path.Combine(目录, "多文件版", "枪战突击.exe")
        If File.Exists(壳) Then
            Dim 起点 As New ProcessStartInfo(壳)
            起点.WorkingDirectory = Path.GetDirectoryName(壳)
            起点.UseShellExecute = True
            If 参 <> "" Then 起点.Arguments = 参
            Process.Start(起点)
            Return
        End If

        ' 回退：没有外壳 exe（老版本包 / 下载不完整），用默认浏览器打开。
        ' ⚠️ 传本地路径而不是 file:// URI —— ShellExecute 对 file:// 有时会报
        '    「Windows 找不到该路径」，本地路径最稳。
        Dim 入口 = Path.Combine(目录, "index.html")
        If Not File.Exists(入口) Then 入口 = Path.Combine(目录, "多文件版", "index.html")
        If Not File.Exists(入口) Then
            Throw New FileNotFoundException(
                "这个版本还没下载好（目录里既没有 枪战突击.exe 也没有 index.html）。" & vbCrLf &
                vbCrLf & "目录：" & 目录 & vbCrLf &
                "请点右边的「重新下载」把它补全。")
        End If
        Process.Start(New ProcessStartInfo(入口) With {.UseShellExecute = True, .WorkingDirectory = Path.GetDirectoryName(入口)})
    End Sub

    ''' <summary>启动一个版本（内部用）。</summary>
    Public Sub 启动版本(项 As 版本信息, Optional 账号 As 账号信息 = Nothing)
        启动实例(实例名(项), 账号)
    End Sub

    ''' <summary>
    ''' 取一个 URL 的文本（自己实现，显式禁用系统代理）。
    ''' ⚠️ 不用 PCL 的 NetRequestByLoader：它走系统代理，本机代理是死的 127.0.0.1:80，
    '''    会一直"连接被拒绝"，玩家看到的就是清单拉不出来 / 下载不出东西。
    ''' </summary>
    Public Function 取文本(URL As String) As String
        WebRequest.DefaultWebProxy = Nothing
        Dim 请 = CType(WebRequest.Create(URL), HttpWebRequest)
        请.Method = "GET"
        请.UserAgent = "QiangTuLauncher/1.0"
        请.Timeout = 30000
        请.ReadWriteTimeout = 30000
        请.Proxy = Nothing
        ' 关缓存：发布中心刚更新时别拿到旧清单
        请.CachePolicy = New Net.Cache.RequestCachePolicy(Net.Cache.RequestCacheLevel.NoCacheNoStore)
        Using 响 = CType(请.GetResponse(), HttpWebResponse)
            Using 入 = New StreamReader(响.GetResponseStream(), Encoding.UTF8)
                Return 入.ReadToEnd()
            End Using
        End Using
    End Function

    ''' <summary>删掉一个版本的本地文件（保留版本库根）。</summary>
    Public Sub 删除版本(项 As 版本信息)
        Dim 目录 = 本版本目录(项)
        If Directory.Exists(目录) Then Directory.Delete(目录, True)
    End Sub

    ''' <summary>模组目录（枪战突击的模组放在 mode\模组\，兼容包里多套一层「多文件版\」的情况）。</summary>
    Private Function 模组目录Of(项 As 版本信息) As String
        Dim 目录 = 本版本目录(项)
        Dim 甲 = Path.Combine(目录, "mode", "模组")
        Dim 乙 = Path.Combine(目录, "多文件版", "mode", "模组")
        If Directory.Exists(甲) Then Return 甲
        If Directory.Exists(乙) Then Return 乙
        Return 甲
    End Function

    ''' <summary>这个版本当前装了哪些模组（文件名列表）。</summary>
    Public Function 已装模组(项 As 版本信息) As List(Of String)
        Dim 结果 As New List(Of String)
        Try
            Dim d = 模组目录Of(项)
            If Directory.Exists(d) Then
                For Each f In Directory.GetFiles(d, "*.js")
                    结果.Add(Path.GetFileName(f))
                Next
            End If
        Catch ex As Exception
            Logger.Warn(ex, "统计已装模组失败")
        End Try
        Return 结果
    End Function

    ''' <summary>删掉某个版本里的一个模组。</summary>
    Public Sub 删除模组(项 As 版本信息, 文件名 As String)
        Dim d = 模组目录Of(项)
        Dim 全 = Path.Combine(d, 文件名)
        If File.Exists(全) Then
            File.Delete(全)
        Else
            Throw New FileNotFoundException("找不到这个模组文件：" & 文件名)
        End If
    End Sub

    ' ==========================================================================
    '  账号管理
    '  ------------------------------------------------------------------------
    '    · 启动器自己维护一份账号表（不在各版本目录里）→ 所有版本天然共用
    '    · 下新版本不用抄账号：启动时把选中账号拼进 URL，游戏端写进 localStorage
    '    · 支持多账号（跟 PCL 一样能切换）
    '  账号文件：exe 同目录 \ 账号.json
    ' ==========================================================================
    Public Class 账号信息
        Public Property 名字 As String = ""
        Public Property 身份码 As String = ""
        Public Property Cid As String = ""
        Public Property 备注 As String = ""

        ''' <summary>下拉框里显示的文字。</summary>
        Public ReadOnly Property 显示 As String
            Get
                Dim 尾巴 As String = ""
                If Not String.IsNullOrEmpty(身份码) Then 尾巴 = "  [" & 身份码 & "]"
                If Not String.IsNullOrEmpty(备注) Then 尾巴 = 尾巴 & "  " & 备注
                Return 名字 & 尾巴
            End Get
        End Property
    End Class

    Public ReadOnly Property 账号文件 As String
        Get
            Return Path.Combine(Path.GetDirectoryName(GetType(ModQiangTu).Assembly.Location), "账号.json")
        End Get
    End Property

    ''' <summary>读账号表（文件不存在就返回空表，不报错）。</summary>
    Public Function 读账号(ByRef 当前 As String) As List(Of 账号信息)
        Dim 结果 As New List(Of 账号信息)
        当前 = ""
        Try
            If Not File.Exists(账号文件) Then Return 结果
            Dim 文本 = File.ReadAllText(账号文件, Encoding.UTF8)
            Dim 根 = JObject.Parse(文本)
            当前 = If(根("当前")?.ToString(), "")
            Dim 数组 = TryCast(根("账号"), JArray)
            If 数组 IsNot Nothing Then
                For Each 项 As JObject In 数组
                    结果.Add(New 账号信息 With {
                        .名字 = If(项("名字")?.ToString(), ""),
                        .身份码 = If(项("身份码")?.ToString(), ""),
                        .Cid = If(项("cid")?.ToString(), ""),
                        .备注 = If(项("备注")?.ToString(), "")
                    })
                Next
            End If
        Catch ex As Exception
            Logger.Warn(ex, "读账号.json 失败")
        End Try
        Return 结果
    End Function

    ''' <summary>写账号表（第一次保存时给账号生成固定 cid）。</summary>
    Public Sub 写账号(表 As List(Of 账号信息), 当前 As String)
        Try
            Dim 数组 As New JArray
            For Each a In 表
                If String.IsNullOrEmpty(a.Cid) Then a.Cid = Guid.NewGuid().ToString("N")
                数组.Add(New JObject From {
                    {"名字", a.名字}, {"身份码", a.身份码}, {"cid", a.Cid}, {"备注", a.备注}
                })
            Next
            Dim 根 As New JObject From {{"当前", 当前}, {"账号", 数组}}
            File.WriteAllText(账号文件, 根.ToString(), New UTF8Encoding(False))
        Catch ex As Exception
            Logger.Warn(ex, "写账号.json 失败")
        End Try
    End Sub

    ''' <summary>把账号拼成 URL 查询串（启动游戏时注入用）。空账号返回空串。</summary>
    Public Function 账号参数(a As 账号信息) As String
        If a Is Nothing OrElse String.IsNullOrEmpty(a.名字) Then Return ""
        Dim 串 As String = "?pclname=" & Uri.EscapeDataString(a.名字)
        If Not String.IsNullOrEmpty(a.身份码) Then 串 = 串 & "&pclcode=" & Uri.EscapeDataString(a.身份码)
        If Not String.IsNullOrEmpty(a.Cid) Then 串 = 串 & "&pclcid=" & Uri.EscapeDataString(a.Cid)
        Return 串
    End Function

    ''' <summary>当前选中的账号（给启动时用）。</summary>
    Public Function 当前账号() As 账号信息
        Try
            Dim 当前名 As String = ""
            Dim 表 = 读账号(当前名)
            If 表.Count = 0 Then Return Nothing
            For Each a In 表
                If a.名字 = 当前名 Then Return a
            Next
            Return 表(0)
        Catch
            Return Nothing
        End Try
    End Function

End Module
