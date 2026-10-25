' ============================================================================
'  PCL 枪战突击修改版 · 游戏数据层（ModQiangTu.vb）
'  ----------------------------------------------------------------------------
'  把原版 PCL 的「Minecraft 版本管理」换成「枪战突击版本管理」：
'    · 版本清单从发布中心 manifest.json 拉取（不是 MC 版本 JSON）
'    · 下载用 PCL 原有的多线程下载器 LoaderDownload（分片并发）
'    · 解压用 System.IO.Compression（zip）
'    · 「启动」= 用默认浏览器打开该版本的 index.html（不是调 Java）
'
'  说明：本文件是第三方基于 PCL 二次创作的新增代码，不修改原 PCL 逻辑，
'        只在旁边加一套独立的枪战突击数据通路，降低改坏原项目的风险。
' ============================================================================

Imports System.Diagnostics
Imports System.IO
Imports System.IO.Compression
Imports System.Threading
Imports System.Net
Imports Newtonsoft.Json.Linq

Public Module ModQiangTu

    ' ---------------- 常量 ----------------
    ''' <summary>枪战突击发布中心（GitHub Pages）。</summary>
    Public Const 发布中心 As String = "https://xintbc.github.io/qiangtuskill/"

    ''' <summary>版本库根目录：每个版本一个独立子目录。</summary>
    Public ReadOnly Property 版本库目录 As String
        Get
            Dim 根 = Path.Combine(Path.GetDirectoryName(GetType(ModQiangTu).Assembly.Location), "枪战突击版本库")
            Directory.CreateDirectory(根)
            Return 根
        End Get
    End Property

    ' ---------------- 数据结构 ----------------
    Public Class 版本信息
        Public Property 版本 As String = ""
        Public Property 文件 As String = ""
        Public Property 大小 As Long = 0
        Public Property Md5 As String = ""
        Public Property 日期 As String = ""
        Public Property 说明 As String = ""

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

    ''' <summary>某个版本的目录（版本库\枪战突击_vX.Y.Z）。</summary>
    Public Function 本版本目录(项 As 版本信息) As String
        Return Path.Combine(版本库目录, "枪战突击_v" & 项.版本)
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
                        .说明 = If(项("说明")?.ToString(), "")
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

    ' ---------------- 下载 ----------------
    ''' <summary>
    ''' 下载并安装一个版本：下载 zip（PCL 多线程下载器）→ 解压到独立目录 → 写已装标记。
    ''' </summary>
    ''' <param name="项">要装的版本</param>
    ''' <param name="进度">进度回调（0~1、当前速度文本），可为 Nothing</param>
    Public Sub 下载并安装(项 As 版本信息, Optional 进度 As Action(Of Double, String) = Nothing)
        Dim 目录 = 本版本目录(项)
        Directory.CreateDirectory(目录)
        Dim zip路径 = Path.Combine(目录, "_下载.zip")
        Dim url = 发布中心 & "版本/" & Uri.EscapeDataString(项.文件)

        ' 用 PCL 自带的多线程下载器（LoaderDownload 内部就是分片并发）
        Dim 任务 As New LoaderDownload($"枪战突击 v{项.版本}", New List(Of NetFile) From {New NetFile({url}, zip路径)})
        任务.Start()
        ' ⚠️ LoaderBase 没有进度事件（只有 Progress 属性）→ 轮询汇报，别用不存在的 ProgressStateChanged
        While 任务.State = LoadState.Waiting OrElse 任务.State = LoadState.Loading
            进度?.Invoke(任务.Progress, $"下载中 {任务.Progress:P0}")
            Thread.Sleep(150)
        End While
        If 任务.State = LoadState.Failed Then
            Throw New Exception("下载失败：" & If(任务.Error?.Message, "未知错误"))
        End If
        任务.WaitForExit()

        进度?.Invoke(1.0, "解压中…")

        ' 解压（标准 zip；PCL 的 ModCompress 也能做，这里用 BCL 更稳）
        If File.Exists(zip路径) Then
            Try
                解压Zip(zip路径, 目录)
            Finally
                Try : File.Delete(zip路径) : Catch : End Try
            End Try
        Else
            Throw New FileNotFoundException("下载失败：临时文件不存在")
        End If

        ' 已装标记
        Try
            File.WriteAllText(Path.Combine(目录, ".已安装"), 项.版本 & vbCrLf & DateTime.Now.ToString("yyyy-MM-dd HH:mm"))
        Catch
        End Try
        进度?.Invoke(1.0, "已装好")
    End Sub

    ''' <summary>zip 解压到指定目录（覆盖同名文件）。</summary>
    Public Sub 解压Zip(zip路径 As String, 目标目录 As String)
        Directory.CreateDirectory(目标目录)
        Using 档案 As ZipArchive = ZipFile.OpenRead(zip路径)
            For Each 条目 As ZipArchiveEntry In 档案.Entries
                ' 目录条目跳过
                If String.IsNullOrEmpty(条目.Name) Then Continue For
                Dim 目标 = Path.GetFullPath(Path.Combine(目标目录, 条目.FullName.Replace("/"c, "\"c)))
                ' 防目录穿越（zip slip）
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
        ' 兼容包结构：有的版本 zip 里带一层「多文件版\」
        If Not Directory.Exists(Path.Combine(版本目录, "mode")) AndAlso
           Directory.Exists(Path.Combine(版本目录, "多文件版", "mode")) Then
            模组目录 = Path.Combine(版本目录, "多文件版", "mode", "模组")
        End If
        Directory.CreateDirectory(模组目录)

        Dim 目标 = Path.Combine(模组目录, 模组.名字)
        Dim url = 发布中心 & "模组/" & Uri.EscapeDataString(模组.名字)
        Dim 任务 As New LoaderDownload($"模组 {模组.名字}", New List(Of NetFile) From {New NetFile({url}, 目标)})
        任务.Start()
        While 任务.State = LoadState.Waiting OrElse 任务.State = LoadState.Loading
            进度?.Invoke(任务.Progress, $"下载中 {任务.Progress:P0}")
            Thread.Sleep(150)
        End While
        If 任务.State = LoadState.Failed Then
            Throw New Exception("模组下载失败：" & If(任务.Error?.Message, "未知错误"))
        End If
        任务.WaitForExit()
    End Sub

    ''' <summary>这个版本当前装了哪些模组（文件名列表）。</summary>
    Public Function 已装模组(项 As 版本信息) As List(Of String)
        Dim 结果 As New List(Of String)
        Dim 目录 = 本版本目录(项)
        Dim 候选 As String() = {
            Path.Combine(目录, "mode", "模组"),
            Path.Combine(目录, "多文件版", "mode", "模组")
        }
        For Each d In 候选
            If Directory.Exists(d) Then
                For Each f In Directory.GetFiles(d, "*.js")
                    结果.Add(Path.GetFileName(f))
                Next
                Exit For
            End If
        Next
        Return 结果
    End Function

    ' ---------------- 启动 ----------------
    ''' <summary>启动一个版本：打开它的 index.html（用系统默认浏览器）。</summary>
    Public Sub 启动版本(项 As 版本信息)
        Dim 目录 = 本版本目录(项)
        Dim 入口 = Path.Combine(目录, "index.html")
        If Not File.Exists(入口) Then 入口 = Path.Combine(目录, "多文件版", "index.html")
        If Not File.Exists(入口) Then
            Throw New FileNotFoundException("这个版本还没下载完（找不到 index.html）")
        End If
        Process.Start(New ProcessStartInfo(入口) With {.UseShellExecute = True})
    End Sub

    ''' <summary>删掉一个版本的本地文件（保留版本库根）。</summary>
    Public Sub 删除版本(项 As 版本信息)
        Dim 目录 = 本版本目录(项)
        If Directory.Exists(目录) Then Directory.Delete(目录, True)
    End Sub

End Module
