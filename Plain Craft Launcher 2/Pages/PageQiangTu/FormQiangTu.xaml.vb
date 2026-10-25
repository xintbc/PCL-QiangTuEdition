' ============================================================================
'  PCL 枪战突击修改版 · 主窗口（FormQiangTu.xaml.vb）
'  ----------------------------------------------------------------------------
'  版本列表 / 下载 / 启动 / 模组下载，全部走 ModQiangTu 数据层。
'  下载用 PCL 原有的多线程下载器（LoaderDownload），不改原 PCL 逻辑。
' ============================================================================

Imports System.ComponentModel
Imports System.Diagnostics
Imports System.Threading.Tasks
Imports System.Windows
Imports System.Windows.Controls

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
        AddHandler Loaded, Sub() 刷新清单()
        AddHandler BtnRefresh.Click, Sub() 刷新清单()
        AddHandler BtnDownload.Click, Sub() 下载选中()
        AddHandler BtnLaunch.Click, Sub() 启动选中()
        AddHandler BtnDelete.Click, Sub() 删除选中()
        AddHandler LstVersion.SelectionChanged, Sub() 更新按钮状态()
        AddHandler LstVersion.MouseDoubleClick, Sub() 启动选中()
    End Sub

    ' ---------------- 清单 ----------------
    Private Sub 刷新清单()
        If 忙 Then Return
        BarProgress.IsIndeterminate = True
        LabStatus.Text = "正在从发布中心读取版本清单…"
        LstVersion.IsEnabled = False

        Dim 后 As New Action(Sub()
                                 Try
                                     Dim 新模组 As List(Of ModQiangTu.模组信息) = Nothing
                                     Dim 列表 = ModQiangTu.拉清单(新模组)
                                     版本表 = 列表
                                     模组表 = If(新模组, New List(Of ModQiangTu.模组信息))

                                     Dim 行s As New List(Of 版本行)
                                     For Each v In 版本表
                                         行s.Add(New 版本行 With {
                                             .标题 = "v" & v.版本,
                                             .副标题 = v.日期 & " · " & v.显示大小 & If(String.IsNullOrEmpty(v.说明), "", " · " & v.说明),
                                             .状态 = If(v.已安装, "● 已安装", ""),
                                             .源 = v
                                         })
                                     Next
                                     LstVersion.ItemsSource = 行s

                                     Dim 模组行s As New List(Of 模组行)
                                     For Each m In 模组表
                                         模组行s.Add(New 模组行 With {
                                             .名字 = m.名字,
                                             .说明 = If(String.IsNullOrEmpty(m.兼容), "通用", m.兼容) & " · " & (m.大小 / 1024).ToString("0") & " KB",
                                             .源 = m
                                         })
                                     Next
                                     LstMod.ItemsSource = 模组行s

                                     LabStatus.Text = $"共 {版本表.Count} 个版本 / {模组表.Count} 个模组 · 版本库：{ModQiangTu.版本库目录}"
                                     If 版本表.Count > 0 Then LstVersion.SelectedIndex = 0
                                 Catch ex As Exception
                                     LabStatus.Text = "读取清单失败：" & ex.Message
                                 Finally
                                     BarProgress.IsIndeterminate = False
                                     LstVersion.IsEnabled = True
                                     更新按钮状态()
                                 End Try
                             End Sub)
        ' 后台跑，别卡界面
        Task.Run(后)
    End Sub

    ' ---------------- 选中项 ----------------
    Private Function 选中版本() As ModQiangTu.版本信息
        Dim 行 = TryCast(LstVersion.SelectedItem, 版本行)
        Return If(行?.源, Nothing)
    End Function

    Private Sub 更新按钮状态()
        Dim v = 选中版本()
        BtnDownload.IsEnabled = (v IsNot Nothing) AndAlso Not 忙
        BtnLaunch.IsEnabled = (v IsNot Nothing) AndAlso v.已安装 AndAlso Not 忙
        BtnDelete.IsEnabled = (v IsNot Nothing) AndAlso v.已安装 AndAlso Not 忙
    End Sub

    ' ---------------- 下载 ----------------
    Private Sub 下载选中()
        Dim v = 选中版本()
        If v Is Nothing Then Return
        If 忙 Then Return
        忙 = True : 更新按钮状态()
        BarProgress.IsIndeterminate = False
        BarProgress.Value = 0
        LabProgress.Text = $"正在下载 v{v.版本}…"

        Dim 后 As New Action(Sub()
                                 Try
                                     ModQiangTu.下载并安装(v, Sub(比例, 文本)
                                                                   Dispatcher.Invoke(Sub()
                                                                                         Try
                                                                                             BarProgress.Value = 比例
                                                                                             LabProgress.Text = $"v{v.版本} · {文本}"
                                                                                         Catch
                                                                                         End Try
                                                                                     End Sub)
                                                          )
                                     Dispatcher.Invoke(Sub()
                                                           LabProgress.Text = $"v{v.版本} 已装好，点「开始游戏」"
                                                           刷新清单()
                                                       End Sub)
                                 Catch ex As Exception
                                     Dispatcher.Invoke(Sub()
                                                           LabProgress.Text = "下载失败：" & ex.Message
                                                           MessageBox.Show(ex.Message, "下载失败", MessageBoxButton.OK, MessageBoxImage.Warning)
                                                       End Sub)
                                 Finally
                                     Dispatcher.Invoke(Sub() 忙 = False : 更新按钮状态())
                                 End Try
                             End Sub)
        Task.Run(后)
    End Sub

    ' ---------------- 启动 / 删除 ----------------
    Private Sub 启动选中()
        Dim v = 选中版本()
        If v Is Nothing Then Return
        Try
            ModQiangTu.启动版本(v)
            LabProgress.Text = $"已启动 v{v.版本}"
        Catch ex As Exception
            MessageBox.Show(ex.Message, "启动失败", MessageBoxButton.OK, MessageBoxImage.Warning)
        End Try
    End Sub

    Private Sub 删除选中()
        Dim v = 选中版本()
        If v Is Nothing Then Return
        If MessageBox.Show($"确定删除本地版本 v{v.版本} 吗？" & vbCrLf & "（包含这个版本里装的模组）",
                           "删除版本", MessageBoxButton.YesNo, MessageBoxImage.Question) <> MessageBoxResult.Yes Then Return
        Try
            ModQiangTu.删除版本(v)
            刷新清单()
            LabProgress.Text = $"已删除 v{v.版本}"
        Catch ex As Exception
            MessageBox.Show(ex.Message, "删除失败", MessageBoxButton.OK, MessageBoxImage.Warning)
        End Try
    End Sub

    ' ---------------- 模组下载 ----------------
    Private Sub BtnModDownload_Click(sender As Object, e As RoutedEventArgs)
        Dim 行 = TryCast(TryCast(sender, Button).Tag, 模组行)
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

        忙 = True : 更新按钮状态()
        BarProgress.IsIndeterminate = False : BarProgress.Value = 0
        LabProgress.Text = $"正在下载模组 {行.源.名字}…"

        Dim 后 As New Action(Sub()
                                 Try
                                     ModQiangTu.下载模组(行.源, v, Sub(比例, 文本)
                                                                     Dispatcher.Invoke(Sub()
                                                                                           Try
                                                                                               BarProgress.Value = 比例
                                                                                               LabProgress.Text = $"{行.源.名字} · {文本}"
                                                                                           Catch
                                                                                           End Try
                                                                                       End Sub)
                                                                )
                                     Dispatcher.Invoke(Sub()
                                                           LabProgress.Text = $"模组 {行.源.名字} 已装到 v{v.版本}"
                                                       End Sub)
                                 Catch ex As Exception
                                     Dispatcher.Invoke(Sub()
                                                           LabProgress.Text = "模组下载失败：" & ex.Message
                                                           MessageBox.Show(ex.Message, "下载失败", MessageBoxButton.OK, MessageBoxImage.Warning)
                                                       End Sub)
                                 Finally
                                     Dispatcher.Invoke(Sub() 忙 = False : 更新按钮状态())
                                 End Try
                             End Sub)
        Task.Run(后)
    End Sub

    ' ---------------- 关于 ----------------
    ''' <summary>点署名里的赞助链接 → 用浏览器打开（PCL 分发许可要求给出赞助链接）。</summary>
    Private Sub 关于_赞助链接(sender As Object, e As System.Windows.Navigation.RequestNavigateEventArgs)
        Try
            Process.Start(New ProcessStartInfo(e.Uri.AbsoluteUri) With {.UseShellExecute = True})
        Catch
        End Try
        e.Handled = True
    End Sub

End Class
