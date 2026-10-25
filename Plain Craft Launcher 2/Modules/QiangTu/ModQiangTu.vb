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
            Dim json = NetRequestByLoader(发布中心 & "manifest.json", IsJson:=True)
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
    ''' <summary>下载并安装一个版本：下载 zip（PCL 多线程下载器）→ 解压 → 写占位 JSON。</summary>
    Public Sub 下载并安装(项 As 版本信息, Optional 进度 As Action(Of Double, String) = Nothing)
        ' 每个版本一个独立目录（枪战突击 v1.7.0 / v1.8.0 …… 天然共存，互不覆盖）
        Dim 目录 = 本版本目录(项)
        Directory.CreateDirectory(目录)
        Dim zip路径 = Path.Combine(目录, "_下载.zip")
        Dim url = 发布中心 & "版本/" & Uri.EscapeDataString(项.文件)

        Dim 任务 As New LoaderDownload("枪战突击 " & 项.版本, New List(Of NetFile) From {New NetFile({url}, zip路径)})
        任务.Start()
        While 任务.State = LoadState.Waiting OrElse 任务.State = LoadState.Loading
            进度?.Invoke(任务.Progress, "下载中 " & 任务.Progress.ToString("P0"))
            Thread.Sleep(150)
        End While
        If 任务.State = LoadState.Failed Then
            Dim 原因 As String = "未知错误"
            If 任务.Error IsNot Nothing Then 原因 = 任务.Error.Message
            Throw New Exception("下载失败：" & 原因)
        End If
        任务.WaitForExit()

        进度?.Invoke(1.0, "解压中…")

        If File.Exists(zip路径) Then
            Try
                解压Zip(zip路径, 目录)
            Finally
                Try : File.Delete(zip路径) : Catch : End Try
            End Try
        Else
            Throw New FileNotFoundException("下载失败：临时文件不存在")
        End If

        写版本占位Json(项)
        Try
            File.WriteAllText(Path.Combine(目录, ".已安装"), 项.版本 & vbCrLf & DateTime.Now.ToString("yyyy-MM-dd HH:mm"))
        Catch
        End Try
        进度?.Invoke(1.0, "已装好")
    End Sub

    ''' <summary>zip 解压到指定目录（覆盖同名文件，防目录穿越）。</summary>
    Public Sub 解压Zip(zip路径 As String, 目标目录 As String)
        Directory.CreateDirectory(目标目录)
        Using 档案 As ZipArchive = ZipFile.OpenRead(zip路径)
            For Each 条目 As ZipArchiveEntry In 档案.Entries
                If String.IsNullOrEmpty(条目.Name) Then Continue For
                Dim 目标 = Path.GetFullPath(Path.Combine(目标目录, 条目.FullName.Replace("/"c, "\"c)))
                If Not 目标.StartsWith(Path.GetFullPath(目标目录), StringComparison.OrdinalIgnoreCase) Then Continue For
                Directory.CreateDirectory(Path.GetDirectoryName(目标))
                条目.ExtractToFile(目标, True)
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
        Dim 入口 = Path.Combine(目录, "index.html")
        If Not File.Exists(入口) Then 入口 = Path.Combine(目录, "多文件版", "index.html")
        If Not File.Exists(入口) Then
            Throw New FileNotFoundException("这个版本还没下载完（找不到 index.html）：" & 目录)
        End If
        Dim 参 As String = 账号参数(账号)
        Process.Start(New ProcessStartInfo(New Uri(入口).AbsoluteUri & 参) With {.UseShellExecute = True})
    End Sub

    ''' <summary>启动一个版本（内部用）。</summary>
    Public Sub 启动版本(项 As 版本信息, Optional 账号 As 账号信息 = Nothing)
        启动实例(实例名(项), 账号)
    End Sub

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
