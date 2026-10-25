' ============================================================================
'  PCL 枪战突击修改版 · 主窗口（FormQiangTu.xaml.vb）
'  ----------------------------------------------------------------------------
'  版本列表 / 下载 / 启动 / 模组下载，全部走 ModQiangTu 数据层。
'  下载用 PCL 原有的多线程下载器（LoaderDownload），不改原 PCL 逻辑。
'
'  ⚠️ VB 的坑：多行 lambda 必须自己写 End Sub（一行 Sub(...) 后面直接换行是语法错误）。
'     这里进度回调统一用独立方法（AddressOf），不用嵌套 lambda。
' ============================================================================

Imports System.ComponentModel
Imports System.Diagnostics
Imports System.Threading.Tasks
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Navigation

Public Class FormQiangTu

    ' 列表绑定用的行对象（WPF 绑定需要可读属性）
    Public Class 版本行
        Public Property 标题 As String = ""
        Public Property 副标题 As String = ""
        Public Property 状态 As String = ""
        Public Property 源 As ModQiangTu.版本信息 = Nothing
    End Class

    Public Class 模组行
        Public Property 名字 As String = ""
        Public Property 说明 As String = ""
        Public Property 源 As ModQiangTu.模组信息 = Nothing
    End Class

    Private 版本表 As New List(Of ModQiangTu.版本信息)
    Private 模组表 As New List(Of ModQiangTu.模组信息)
    Private 忙 As Boolean = False

    Public Sub New()
        InitializeComponent()
        AddHandler Loaded, AddressOf 窗口加载
        AddHandler BtnRefresh.Click, AddressOf 刷新清单按钮
        AddHandler BtnDownload.Click, AddressOf 下载选中
        AddHandler BtnLaunch.Click, AddressOf 启动选中
        AddHandler BtnDelete.Click, AddressOf 删除选中
        AddHandler LstVersion.SelectionChanged, AddressOf 选中变化
        AddHandler LstVersion.MouseDoubleClick, AddressOf 双击启动
    End Sub

    Private Sub 窗口加载(sender As Object, e As RoutedEventArgs)
        刷新清单()
    End Sub

    Private Sub 选中变化(sender As Object, e As SelectionChangedEventArgs)
        更新按钮状态()
    End Sub

    Private Sub 双击启动(sender As Object, e As System.Windows.Input.MouseButtonEventArgs)
        启动选中(sender, e)
    End Sub

    ' ---------------- 清单 ----------------
    Private Sub 刷新清单按钮(sender As Object, e As RoutedEventArgs)
        刷新清单()
    End Sub

    Private Sub 刷新清单()
        If 忙 Then Return
        BarProgress.IsIndeterminate = True
        LabStatus.Text = "正在从发布中心读取版本清单…"
        LstVersion.IsEnabled = False
        Task.Run(AddressOf 拉清单任务)
    End Sub

    Private Sub 拉清单任务()
        Dim 新模组 As New List(Of ModQiangTu.模组信息)
        Dim 列表 As List(Of ModQiangTu.版本信息) = Nothing
        Dim 出错 As String = ""
        Try
            列表 = ModQiangTu.拉清单(新模组)
        Catch ex As Exception
            出错 = ex.Message
        End Try
        Dispatcher.Invoke(Sub()
                              Try
                                  If 出错 <> "" Then
                                      LabStatus.Text = "读取清单失败：" & 出错
                                  Else
                                      版本表 = 列表
                                      模组表 = 新模组
                                      绑定列表()
                                  End If
                                  BarProgress.IsIndeterminate = False
                                  LstVersion.IsEnabled = True
                                  更新按钮状态()
                              Catch
                              End Try
                          End Sub)
    End Sub

    Private Sub 绑定列表()
        Dim 行s As New List(Of 版本行)
        For Each v In 版本表
            Dim 副 As String = v.日期 & " · " & v.显示大小
            If Not String.IsNullOrEmpty(v.说明) Then 副 = 副 & " · " & v.说明
            行s.Add(New 版本行 With {
                .标题 = "v" & v.版本,
                .副标题 = 副,
                .状态 = If(v.已安装, "● 已安装", ""),
                .源 = v
            })
        Next
        LstVersion.ItemsSource = 行s

        Dim 模组行s As New List(Of 模组行)
        For Each m In 模组表
            Dim 兼容 As String = If(String.IsNullOrEmpty(m.兼容), "通用", m.兼容)
            模组行s.Add(New 模组行 With {
                .名字 = m.名字,
                .说明 = 兼容 & " · " & (m.大小 / 1024).ToString("0") & " KB",
                .源 = m
            })
        Next
        LstMod.ItemsSource = 模组行s

        LabStatus.Text = $"共 {版本表.Count} 个版本 / {模组表.Count} 个模组 · 版本库：{ModQiangTu.版本库目录}"
        If 版本表.Count > 0 Then LstVersion.SelectedIndex = 0
    End Sub

    ' ---------------- 选中项 ----------------
    Private Function 选中版本() As ModQiangTu.版本信息
        Dim 行 = TryCast(LstVersion.SelectedItem, 版本行)
        If 行 Is Nothing Then Return Nothing
        Return 行.源
    End Function

    Private Sub 更新按钮状态()
        Dim v = 选中版本()
        Dim 有 As Boolean = (v IsNot Nothing)
        BtnDownload.IsEnabled = 有 AndAlso Not 忙
        If 有 Then
            BtnLaunch.IsEnabled = v.已安装 AndAlso Not 忙
            BtnDelete.IsEnabled = v.已安装 AndAlso Not 忙
        Else
            BtnLaunch.IsEnabled = False
            BtnDelete.IsEnabled = False
        End If
    End Sub

    ' ---------------- 进度回调（独立方法，避免多行 lambda 语法坑） ----------------
    Private Sub 显示进度(比例 As Double, 文本 As String)
        Try
            Dispatcher.Invoke(Sub()
                                  BarProgress.Value = Math.Max(0, Math.Min(1, 比例))
                                  LabProgress.Text = 文本
                              End Sub)
        Catch
        End Try
    End Sub

    ' ---------------- 下载 ----------------
    Private Sub 下载选中(sender As Object, e As RoutedEventArgs)
        Dim v = 选中版本()
        If v Is Nothing Then Return
        If 忙 Then Return
        忙 = True
        更新按钮状态()
        BarProgress.IsIndeterminate = False
        BarProgress.Value = 0
        LabProgress.Text = "正在下载 v" & v.版本 & "…"
        Task.Run(Sub() 下载任务(v))
    End Sub

    Private Sub 下载任务(v As ModQiangTu.版本信息)
        Dim 出错 As String = ""
        Try
            ModQiangTu.下载并安装(v, AddressOf 显示进度)
        Catch ex As Exception
            出错 = ex.Message
        End Try
        Dispatcher.Invoke(Sub()
                              Try
                                  If 出错 <> "" Then
                                      LabProgress.Text = "下载失败：" & 出错
                                      MessageBox.Show(出错, "下载失败", MessageBoxButton.OK, MessageBoxImage.Warning)
                                  Else
                                      LabProgress.Text = "v" & v.版本 & " 已装好，点「开始游戏」"
                                  End If
                                  忙 = False
                                  刷新清单()
                              Catch
                              End Try
                          End Sub)
    End Sub

    ' ---------------- 启动 / 删除 ----------------
    Private Sub 启动选中(sender As Object, e As RoutedEventArgs)
        Dim v = 选中版本()
        If v Is Nothing Then Return
        Try
            ModQiangTu.启动版本(v)
            LabProgress.Text = "已启动 v" & v.版本
        Catch ex As Exception
            MessageBox.Show(ex.Message, "启动失败", MessageBoxButton.OK, MessageBoxImage.Warning)
        End Try
    End Sub

    Private Sub 删除选中(sender As Object, e As RoutedEventArgs)
        Dim v = 选中版本()
        If v Is Nothing Then Return
        Dim 问 As String = "确定删除本地版本 v" & v.版本 & " 吗？" & vbCrLf & "（包含这个版本里装的模组）"
        If MessageBox.Show(问, "删除版本", MessageBoxButton.YesNo, MessageBoxImage.Question) <> MessageBoxResult.Yes Then Return
        Try
            ModQiangTu.删除版本(v)
            LabProgress.Text = "已删除 v" & v.版本
            刷新清单()
        Catch ex As Exception
            MessageBox.Show(ex.Message, "删除失败", MessageBoxButton.OK, MessageBoxImage.Warning)
        End Try
    End Sub

    ' ---------------- 模组下载 ----------------
    Private Sub BtnModDownload_Click(sender As Object, e As RoutedEventArgs)
        Dim 钮 = TryCast(sender, Button)
        If 钮 Is Nothing Then Return
        Dim 行 = TryCast(钮.Tag, 模组行)
        If 行 Is Nothing OrElse 行.源 Is Nothing Then Return

        Dim v = 选中版本()
        If v Is Nothing Then
            MessageBox.Show("先在左边选一个版本，模组会装进那个版本的 mode\模组\ 目录。",
                            "选个版本", MessageBoxButton.OK, MessageBoxImage.Information)
            Return
        End If
        If Not v.已安装 Then
            MessageBox.Show("这个版本还没下载，先下载版本再装模组。", "版本未安装", MessageBoxButton.OK, MessageBoxImage.Information)
            Return
        End If
        If 忙 Then Return

        忙 = True
        更新按钮状态()
        BarProgress.IsIndeterminate = False
        BarProgress.Value = 0
        LabProgress.Text = "正在下载模组 " & 行.源.名字 & "…"
        Dim 目标模组 = 行.源
        Task.Run(Sub() 模组任务(目标模组, v))
    End Sub

    Private Sub 模组任务(模组 As ModQiangTu.模组信息, v As ModQiangTu.版本信息)
        Dim 出错 As String = ""
        Try
            ModQiangTu.下载模组(模组, v, AddressOf 显示进度)
        Catch ex As Exception
            出错 = ex.Message
        End Try
        Dispatcher.Invoke(Sub()
                              Try
                                  If 出错 <> "" Then
                                      LabProgress.Text = "模组下载失败：" & 出错
                                      MessageBox.Show(出错, "下载失败", MessageBoxButton.OK, MessageBoxImage.Warning)
                                  Else
                                      LabProgress.Text = "模组 " & 模组.名字 & " 已装到 v" & v.版本
                                  End If
                                  忙 = False
                                  更新按钮状态()
                              Catch
                              End Try
                          End Sub)
    End Sub

    ' ---------------- 关于 ----------------
    ''' <summary>点署名里的赞助链接 → 用浏览器打开（PCL 分发许可要求给出赞助链接）。</summary>
    Private Sub 关于_赞助链接(sender As Object, e As RequestNavigateEventArgs)
        Try
            Process.Start(New ProcessStartInfo(e.Uri.AbsoluteUri) With {.UseShellExecute = True})
        Catch
        End Try
        e.Handled = True
    End Sub

End Class
