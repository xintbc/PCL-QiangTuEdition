' ============================================================================
'  PCL 枪战突击修改版 · 主窗口逻辑（FormQiangTu.xaml.vb）
'  ----------------------------------------------------------------------------
'  界面用 PCL 的控件与配色（MyButton / MyScrollViewer / MyListItem / ColorBrush*）；
'  业务逻辑全部本项目自己写（ModQiangTu：拉清单 / 下载 / 解压 / 账号 / 模组），
'  不调用 PCL 的 Minecraft 逻辑与下载逻辑。
'
'  ⚠️ VB 坑：多行 lambda 必须写 End Sub；进度回调统一用 AddressOf 独立方法。
' ============================================================================

Imports System.ComponentModel
Imports System.Diagnostics
Imports System.Threading.Tasks
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Input
Imports System.Windows.Media
Imports System.Windows.Navigation

Public Class FormQiangTu

    Private 版本表 As New List(Of ModQiangTu.版本信息)
    Private 模组表 As New List(Of ModQiangTu.模组信息)
    Private 账号表 As New List(Of ModQiangTu.账号信息)
    Private 当前账号名 As String = ""
    Private 选中版本项 As ModQiangTu.版本信息 = Nothing
    Private 忙 As Boolean = False

    Public Sub New()
        InitializeComponent()
        AddHandler Loaded, AddressOf 窗口加载
        AddHandler Closed, AddressOf 窗口关闭
        AddHandler BtnRefresh.Click, AddressOf 刷新按钮
        AddHandler BtnDownload.Click, AddressOf 下载选中
        AddHandler BtnLaunch.Click, AddressOf 启动选中
        AddHandler BtnDelete.Click, AddressOf 删除选中
        AddHandler CmbAccount.SelectionChanged, AddressOf 账号切换
        AddHandler BtnAccAdd.Click, AddressOf 账号新增
        AddHandler BtnAccEdit.Click, AddressOf 账号编辑
    End Sub

    ''' <summary>关掉窗口就退出进程（PCL 的 ShutdownMode 是 OnExplicitShutdown，不主动关会留后台）。</summary>
    Private Sub 窗口关闭(sender As Object, e As EventArgs)
        Try
            System.Windows.Application.Current.Shutdown()
        Catch
        End Try
        Try
            Environment.Exit(0)          ' 兜底：后台还有下载线程时 Shutdown 可能被拦
        Catch
        End Try
    End Sub

    Private Sub 窗口加载(sender As Object, e As RoutedEventArgs)
        ' ⚠️ 必须自己关掉 PCL 的启动画面：原版是 FormMain 在加载完时关的，
        '    我们把启动窗口换成了本窗口，不关的话那块「PCL 图标」会一直浮在屏幕中间。
        Try
            If FrmStart IsNot Nothing Then FrmStart.Close(New TimeSpan(0, 0, 0, 0, 300))
        Catch
        End Try
        载入账号()
        刷新清单()
    End Sub

    ' ==================== 账号 ====================
    Private Sub 载入账号()
        账号表 = ModQiangTu.读账号(当前账号名)
        ' 初始**不**建默认账号（用户要求：第一次打开应该是空的，让自己新增）
        If 账号表.Count > 0 AndAlso String.IsNullOrEmpty(当前账号名) Then 当前账号名 = 账号表(0).名字
        刷新账号下拉()
        If 账号表.Count = 0 Then
            LabProgress.Text = "还没有账号 —— 点右上角「新增」填游戏名字（可建多个，随时切换）"
        End If
    End Sub

    Private Sub 刷新账号下拉()
        Dim 旧 = 当前账号名
        CmbAccount.Items.Clear()
        Dim 选中项 As ModQiangTu.账号信息 = Nothing
        For Each a In 账号表
            CmbAccount.Items.Add(a.显示)
            If a.名字 = 旧 Then 选中项 = a
        Next
        If 账号表.Count = 0 Then
            CmbAccount.Items.Add("（未设置账号）")
            CmbAccount.SelectedIndex = 0
            CmbAccount.IsEnabled = False
            当前账号名 = ""
            Return
        End If
        CmbAccount.IsEnabled = True
        If 选中项 Is Nothing Then 选中项 = 账号表(0)
        If 选中项 IsNot Nothing Then
            CmbAccount.SelectedItem = 选中项.显示
            当前账号名 = 选中项.名字
        End If
    End Sub

    Private Function 选中账号() As ModQiangTu.账号信息
        For Each a In 账号表
            If a.名字 = 当前账号名 Then Return a
        Next
        If 账号表.Count > 0 Then Return 账号表(0)
        Return Nothing
    End Function

    Private Sub 账号切换(sender As Object, e As SelectionChangedEventArgs)
        Try
            Dim 文 = TryCast(CmbAccount.SelectedItem, String)
            If String.IsNullOrEmpty(文) Then Return
            For Each a In 账号表
                If a.显示 = 文 Then
                    当前账号名 = a.名字
                    ModQiangTu.写账号(账号表, 当前账号名)
                    LabProgress.Text = "当前账号：" & a.名字
                    Return
                End If
            Next
        Catch
        End Try
    End Sub

    Private Sub 账号新增(sender As Object, e As RoutedEventArgs)
        Logger.Info("枪战突击：点到了「新增账号」")
        Dim 名 = 输入框("新增账号", "游戏里的名字（1~8 个字）：", "")
        If String.IsNullOrEmpty(名) Then Return
        名 = 名.Trim()
        For Each a In 账号表
            If a.名字 = 名 Then
                MessageBox.Show("已经有同名账号了。", "重名", MessageBoxButton.OK, MessageBoxImage.Information)
                Return
            End If
        Next
        账号表.Add(New ModQiangTu.账号信息 With {.名字 = 名})
        当前账号名 = 名
        ModQiangTu.写账号(账号表, 当前账号名)
        刷新账号下拉()
        LabProgress.Text = "已新增账号 " & 名 & "；想填身份码点「编辑」"
    End Sub

    Private Sub 账号编辑(sender As Object, e As RoutedEventArgs)
        Dim a = 选中账号()
        If a Is Nothing Then Return
        Dim 名 = 输入框("编辑账号", "名字（1~8 个字）：", a.名字)
        If String.IsNullOrEmpty(名) Then Return
        Dim 码 = 输入框("编辑账号", "身份码（6 位，可留空）：", a.身份码)
        Dim 注 = 输入框("编辑账号", "备注（可留空）：", a.备注)
        a.名字 = 名.Trim()
        a.身份码 = If(码, "").Trim().ToUpper()
        a.备注 = If(注, "").Trim()
        当前账号名 = a.名字
        ModQiangTu.写账号(账号表, 当前账号名)
        刷新账号下拉()
        LabProgress.Text = "账号已保存：" & a.名字
    End Sub

    ' ==================== 清单 ====================
    Private Sub 刷新按钮(sender As Object, e As RoutedEventArgs)
        刷新清单()
    End Sub

    Private Sub 刷新清单()
        If 忙 Then Return
        BarProgress.IsIndeterminate = True
        LabStatus.Text = "正在读取版本清单…"
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
                                      重建列表()
                                  End If
                                  BarProgress.IsIndeterminate = False
                              Catch
                              End Try
                          End Sub)
    End Sub

    ' ==================== 列表（PCL 的 MyListItem） ====================
    Private Sub 重建列表()
        PanVersionList.Children.Clear()
        Dim 第一项 As MyListItem = Nothing
        For Each v In 版本表
            Dim 项 As New MyListItem With {
                .Title = "枪战突击 v" & v.版本 & If(v.类型 = "预览", "（预览）", ""),
                .Info = v.日期 & " · " & v.显示大小 & If(v.已安装, " · 已安装", ""),
                .Type = MyListItem.CheckType.Clickable,
                .MinPaddingRight = 24
            }
            AddHandler 项.Click, AddressOf 版本项点击
            项.Tag = v
            PanVersionList.Children.Add(项)
            If 第一项 Is Nothing Then 第一项 = 项
        Next

        刷新模组列表(Nothing)

        LabStatus.Text = "共 " & 版本表.Count & " 个版本 · 版本库：" & ModQiangTu.版本库目录
        ' 便于无界面验证：日志里能看到列表真的建出来了
        Logger.Info($"枪战突击界面：版本项 {PanVersionList.Children.Count} 个 / 模组项 {PanModList.Children.Count} 个")
        If 第一项 IsNot Nothing Then 选定版本(TryCast(第一项.Tag, ModQiangTu.版本信息))
    End Sub

    ''' <summary>按选中版本刷新模组区：先列**这个版本已装**的，再列能下载的。</summary>
    Private Sub 刷新模组列表(v As ModQiangTu.版本信息)
        PanModList.Children.Clear()
        Dim 已装 As New List(Of String)
        If v IsNot Nothing AndAlso v.已安装 Then
            Try
                已装 = ModQiangTu.已装模组(v)
            Catch
            End Try
        End If

        ' 已装的排前面（带 ✔ 已装）
        For Each 名 In 已装
            Dim 项 As New MyListItem With {
                .Title = "✔ " & 名,
                .Info = "已装在这个版本里 · 点我可删除",
                .Type = MyListItem.CheckType.Clickable,
                .MinPaddingRight = 24
            }
            AddHandler 项.Click, AddressOf 已装模组点击
            项.Tag = New Object() {名, v}
            PanModList.Children.Add(项)
        Next

        ' 再列发布中心里可下载的（已装的标出来，不重复列）
        Dim 可下 As Integer = 0
        For Each m In 模组表
            If 已装.Contains(m.名字) Then Continue For
            可下 += 1
            Dim 项 As New MyListItem With {
                .Title = m.名字,
                .Info = If(String.IsNullOrEmpty(m.兼容), "通用", m.兼容) & " · " & (m.大小 / 1024).ToString("0") & " KB · 点我下载",
                .Type = MyListItem.CheckType.Clickable,
                .MinPaddingRight = 24
            }
            AddHandler 项.Click, AddressOf 模组项点击
            项.Tag = m
            PanModList.Children.Add(项)
        Next

        If v Is Nothing Then
            LabModHint.Text = "先选一个版本"
        ElseIf Not v.已安装 Then
            LabModHint.Text = "版本未安装 · 可下载 " & 可下 & " 个"
        Else
            LabModHint.Text = "已装 " & 已装.Count & " 个 · 可下载 " & 可下 & " 个"
        End If
    End Sub

    ''' <summary>点已装模组 = 删除它（带确认）。</summary>
    Private Sub 已装模组点击(sender As Object, e As MouseButtonEventArgs)
        Dim 项 = TryCast(sender, MyListItem)
        If 项 Is Nothing Then Return
        Dim 包 = TryCast(项.Tag, Object())
        If 包 Is Nothing OrElse 包.Length < 2 Then Return
        Dim 名 = TryCast(包(0), String)
        Dim v = TryCast(包(1), ModQiangTu.版本信息)
        If String.IsNullOrEmpty(名) OrElse v Is Nothing Then Return
        If MessageBox.Show("从这个版本里删除模组「" & 名 & "」吗？", "删除模组",
                           MessageBoxButton.YesNo, MessageBoxImage.Question) <> MessageBoxResult.Yes Then Return
        Try
            ModQiangTu.删除模组(v, 名)
            LabProgress.Text = "已删除模组 " & 名
            刷新模组列表(v)
        Catch ex As Exception
            MessageBox.Show(ex.Message, "删除失败", MessageBoxButton.OK, MessageBoxImage.Warning)
        End Try
    End Sub

    Private Sub 版本项点击(sender As Object, e As MouseButtonEventArgs)
        Dim 项 = TryCast(sender, MyListItem)
        If 项 Is Nothing Then Return
        选定版本(TryCast(项.Tag, ModQiangTu.版本信息))
    End Sub

    Private Sub 选定版本(v As ModQiangTu.版本信息)
        If v Is Nothing Then Return
        选中版本项 = v
        LabVerTitle.Text = "枪战突击 v" & v.版本 & If(v.类型 = "预览", "（预览版）", "（正式版）")
        LabVerInfo.Text = v.日期 & " · " & v.显示大小 & vbCrLf & v.说明 & vbCrLf &
            If(v.已安装, "✔ 已安装在本机", "尚未下载 —— 点右边「下载」")
        BtnLaunch.IsEnabled = v.已安装 AndAlso Not 忙
        BtnDelete.IsEnabled = v.已安装 AndAlso Not 忙
        BtnDownload.IsEnabled = Not 忙
        ' 已装过也能再下一次（覆盖更新/修复）：按钮文案跟着变，别让玩家以为只能下一次
        BtnDownload.Text = If(v.已安装, "重新下载", "下载")
        刷新模组列表(v)
    End Sub

    ' ==================== 下载 ====================
    Private Sub 下载选中(sender As Object, e As RoutedEventArgs)
        Dim v = 选中版本项
        If v Is Nothing Then Return
        If 忙 Then Return
        忙 = True
        BtnDownload.IsEnabled = False : BtnLaunch.IsEnabled = False
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
                                      BarProgress.IsIndeterminate = False
                                      BarProgress.Value = 1
                                      LabProgress.Text = "v" & v.版本 & " 已装好（可重复下载 / 多版本共存），点「启动游戏」"
                                      刷新模组列表(v)
                                  End If
                                  忙 = False
                                  刷新清单()
                              Catch
                              End Try
                          End Sub)
    End Sub

    Private Sub 显示进度(比例 As Double, 文本 As String)
        Try
            Dispatcher.Invoke(Sub()
                                  ' 解压阶段（文本带「解压」）没有百分比 → 进度条改成来回跑，别停在 98% 装死
                                  If 文本 IsNot Nothing AndAlso 文本.Contains("解压") Then
                                      BarProgress.IsIndeterminate = True
                                      LabProgress.Text = 文本
                                      Return
                                  End If
                                  BarProgress.IsIndeterminate = False
                                  BarProgress.Value = Math.Max(0, Math.Min(1, 比例))
                                  LabProgress.Text = 文本
                              End Sub)
        Catch
        End Try
    End Sub

    ' ==================== 启动 / 删除 ====================
    Private Sub 启动选中(sender As Object, e As RoutedEventArgs)
        Dim v = 选中版本项
        If v Is Nothing Then Return
        Try
            Dim a = 选中账号()
            ModQiangTu.启动版本(v, a)
            LabProgress.Text = "已启动 v" & v.版本 & If(a Is Nothing, "", "（账号：" & a.名字 & "）")
        Catch ex As Exception
            MessageBox.Show(ex.Message, "启动失败", MessageBoxButton.OK, MessageBoxImage.Warning)
        End Try
    End Sub

    Private Sub 删除选中(sender As Object, e As RoutedEventArgs)
        Dim v = 选中版本项
        If v Is Nothing Then Return
        Dim 问 As String = "确定删除本地版本 v" & v.版本 & " 吗？" & vbCrLf & "（这个版本里装的模组也会一起删掉）"
        If MessageBox.Show(问, "删除版本", MessageBoxButton.YesNo, MessageBoxImage.Question) <> MessageBoxResult.Yes Then Return
        Try
            ModQiangTu.删除版本(v)
            LabProgress.Text = "已删除 v" & v.版本
            刷新清单()
        Catch ex As Exception
            MessageBox.Show(ex.Message, "删除失败", MessageBoxButton.OK, MessageBoxImage.Warning)
        End Try
    End Sub

    ' ==================== 模组下载 ====================
    Private Sub 模组项点击(sender As Object, e As MouseButtonEventArgs)
        Dim 项 = TryCast(sender, MyListItem)
        If 项 Is Nothing Then Return
        Dim m = TryCast(项.Tag, ModQiangTu.模组信息)
        If m Is Nothing Then Return
        Dim v = 选中版本项
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
        BarProgress.IsIndeterminate = False
        BarProgress.Value = 0
        LabProgress.Text = "正在下载模组 " & m.名字 & "…"
        Task.Run(Sub() 模组任务(m, v))
    End Sub

    Private Sub 模组任务(m As ModQiangTu.模组信息, v As ModQiangTu.版本信息)
        Dim 出错 As String = ""
        Try
            ModQiangTu.下载模组(m, v, AddressOf 显示进度)
        Catch ex As Exception
            出错 = ex.Message
        End Try
        Dispatcher.Invoke(Sub()
                              Try
                                  If 出错 <> "" Then
                                      LabProgress.Text = "模组下载失败：" & 出错
                                      MessageBox.Show(出错, "下载失败", MessageBoxButton.OK, MessageBoxImage.Warning)
                                  Else
                                      LabProgress.Text = "模组 " & m.名字 & " 已装到 v" & v.版本
                                  End If
                                  忙 = False
                              Catch
                              End Try
                          End Sub)
    End Sub

    ' ==================== 输入框 ====================
    Private _输入框结果 As String = Nothing
    Private _输入框文本框 As TextBox = Nothing
    Private _输入框窗口 As Window = Nothing

    Private Sub 输入确定(sender As Object, e As RoutedEventArgs)
        _输入框结果 = _输入框文本框.Text
        _输入框窗口.Close()
    End Sub

    Private Sub 输入取消(sender As Object, e As RoutedEventArgs)
        _输入框窗口.Close()
    End Sub

    Private Function 输入框(标题 As String, 提示 As String, 默认值 As String) As String
        Dim 窗 As New Window With {
            .Title = 标题, .Width = 420, .Height = 190,
            .WindowStartupLocation = WindowStartupLocation.CenterOwner,
            .Owner = Me, .ResizeMode = ResizeMode.NoResize, .Background = Background
        }
        Dim 格 As New Grid With {.Margin = New Thickness(16)}
        格.RowDefinitions.Add(New RowDefinition With {.Height = GridLength.Auto})
        格.RowDefinitions.Add(New RowDefinition With {.Height = GridLength.Auto})
        格.RowDefinitions.Add(New RowDefinition With {.Height = GridLength.Auto})
        Dim 标 As New TextBlock With {.Text = 提示, .FontSize = 12, .Margin = New Thickness(0, 0, 0, 8)}
        标.Foreground = New SolidColorBrush(Color.FromRgb(&H8B, &H94, &HAD))
        格.Children.Add(标)
        Dim 框 As New TextBox With {.Text = 默认值, .Height = 30, .FontSize = 14, .Margin = New Thickness(0, 0, 0, 12)}
        框.VerticalContentAlignment = VerticalAlignment.Center
        Grid.SetRow(框, 1)
        格.Children.Add(框)
        Dim 钮格 As New StackPanel With {.Orientation = Orientation.Horizontal, .HorizontalAlignment = HorizontalAlignment.Right}
        Grid.SetRow(钮格, 2)
        Dim 确定 As New Button With {.Content = "确定", .Width = 84, .Height = 30, .Margin = New Thickness(0, 0, 8, 0)}
        Dim 取消 As New Button With {.Content = "取消", .Width = 84, .Height = 30}
        钮格.Children.Add(确定)
        钮格.Children.Add(取消)
        格.Children.Add(钮格)
        窗.Content = 格
        _输入框结果 = Nothing
        _输入框文本框 = 框
        _输入框窗口 = 窗
        AddHandler 确定.Click, AddressOf 输入确定
        AddHandler 取消.Click, AddressOf 输入取消
        窗.ShowDialog()
        Return _输入框结果
    End Function

    ' ==================== 关于 ====================
    Private Sub 关于_赞助链接(sender As Object, e As RequestNavigateEventArgs)
        Try
            Process.Start(New ProcessStartInfo(e.Uri.AbsoluteUri) With {.UseShellExecute = True})
        Catch
        End Try
        e.Handled = True
    End Sub

End Class
