// =============================================================================
// CompactWindow.xaml.cs
//
// 中文：
//   Compact 窗口的代码。它是 Full 的一个缩影：同一份状态、同样的颜色与
//   不依赖颜色的标志，只是小得多。
//
//   ★ 它**不订阅会话**，由 Full 调用 Refresh 推给它。
//
//     两个窗口各自订阅同一个会话，就有两条各自切线程的路径，而且它们会在不同
//     时刻拿到不同的快照——同一时刻两个窗口显示不一致，是那种看一眼就知道有
//     bug、查起来却很费劲的问题。让状态只有一个入口，形状上就不可能不一致。
//
//   ★ 关掉它就是收起整个程序吗？不是。
//
//     Compact 的关闭按钮意味着"我不想看到这个小窗口了"，而不是"退出程序"。
//     但**绝不能**让它变成一个看不见却在工作的程序（规格 §11.3）——所以关掉
//     Compact 会展开回 Full，而不是双双消失。真正的退出只有 Full 上那条带
//     确认框的路。
//
// English:
//   The Compact window's code: a miniature of Full carrying the same state, colors and non-color
//   signatures.
//
//   It does not subscribe to the session; Full pushes state in through Refresh. Two windows
//   subscribing to one session would mean two thread-marshalling paths taking snapshots at
//   different moments — and two windows disagreeing at the same instant is the kind of bug that is
//   obvious on sight and painful to trace. One entrance for state makes disagreement structurally
//   impossible.
//
//   Closing it does not exit the program: the Compact close button means "I do not want this little
//   window", not "quit". But it must never leave an invisible program still working (spec §11.3),
//   so closing Compact expands back to Full rather than both disappearing. The only real exit is
//   Full's, behind its confirmation.
//
// 包含的成员 / Members in this file:
//   Refresh            由 Full 推进来的状态
//   ExpandRequested    请求展开回 Full
// =============================================================================

using System.Windows;
using System.Windows.Media;
using ScannerHelper.App.Localization;
using ScannerHelper.Core.Domain;

namespace ScannerHelper.App;

/// <summary>
/// 中文：Compact 窗口（规格 §11.3、§11.4）。
/// English: The Compact window (spec §11.3, §11.4).
/// </summary>
public partial class CompactWindow : Window
{
    private bool _isClosingForExpand;
    private bool _isClosingForExit;

    /// <summary>
    /// 中文：构造窗口。
    /// English: Creates the window.
    /// </summary>
    public CompactWindow() => InitializeComponent();

    /// <summary>
    /// 中文：工人要求展开回 Full。
    /// English: The operator asked to expand back to Full.
    /// </summary>
    public event EventHandler? ExpandRequested;

    /// <summary>
    /// 中文：
    ///   因为程序要退出而关闭。
    ///
    ///   ★ 必须和"工人点了小窗口的关闭按钮"分开。后者的含义是"我不想看到这个
    ///     小窗口"，处理方式是展开回 Full；退出时若走同一条路，就会变成：主窗口
    ///     正在关闭 → 关掉 Compact → Compact 取消关闭并请求展开 → 主窗口
    ///     Show() 出来 …… 一次退出点击换来一个又冒出来的窗口。
    /// English:
    ///   Closes because the application is exiting.
    ///
    ///   This must be distinct from the operator clicking the small window's close button, which
    ///   means "I do not want this little window" and is answered by expanding back to Full. Taking
    ///   that path during exit would give: the main window is closing, it closes Compact, Compact
    ///   cancels and asks to expand, the main window shows itself again — one exit click producing
    ///   a window that reappears.
    /// </summary>
    public void CloseForExit()
    {
        _isClosingForExit = true;
        Close();
    }

    private static App CurrentApp => (App)Application.Current;

    private static ScannerSession? Session => CurrentApp.Session;

    /// <summary>
    /// 中文：
    ///   按快照重画。五种状态的优先级与 Full 完全一致——两个视图对"现在最要紧的
    ///   是什么"必须给出同一个答案，否则工人在两处看到两件事。
    /// English:
    ///   Redraws from a snapshot. The five states take the same priority as in Full: the two views
    ///   must agree on what matters most right now, or the operator sees two different things in
    ///   two places.
    /// </summary>
    public void Refresh(SessionSnapshot snapshot)
    {
        var strings = Localizer.Strings;

        HazardStripe.Visibility = Visibility.Collapsed;
        DisconnectedBar.Visibility = Visibility.Collapsed;

        if (!snapshot.IsConnected)
        {
            StatePanel.Background = (Brush)FindResource("DisconnectedBrush");
            DisconnectedBar.Visibility = Visibility.Visible;
            StateTitleText.Text = "⛔";
            StateSubtitleText.Text = strings["Disconnected"];
        }
        else if (snapshot.PendingErrorRawCode is { } pendingCode)
        {
            StatePanel.Background = (Brush)FindResource("ErrorBrush");
            HazardStripe.Visibility = Visibility.Visible;
            StateTitleText.Text = "⚠";
            StateSubtitleText.Text = pendingCode;
        }
        else if (snapshot.IsPaused)
        {
            StatePanel.Background = (Brush)FindResource("PausedBrush");
            StateTitleText.Text = "⏸  " + strings["PausedHeading"];
            StateSubtitleText.Text = strings["PausedSubtitle"];
        }
        else
        {
            var isSn = snapshot.Mode == ScanMode.Sn;
            StatePanel.Background = (Brush)FindResource(isSn ? "SnBrush" : "SkuBrush");
            StateTitleText.Text = strings[isSn ? "ModeSn" : "ModeSku"];
            // 小窗里空间只够一行，所以命令条码那一枪显示的是"发生了什么"而不是
            // 原始码——"（已切到 SKU 模式）"比 "#SH:SKU#" 对工人有用得多，
            // 而后者他刚刚才扫过，不需要再看一遍。
            // One line is all the compact window has, so a command barcode shows what happened
            // rather than the raw code: "(switched to SKU)" is far more use to the operator than
            // "#SH:SKU#", which they just scanned and do not need read back.
            StateSubtitleText.Text = snapshot.LastScanNoticeKey is { } noticeKey
                ? strings[noticeKey]
                : snapshot.LastScanRawCode ?? strings["NoScanYet"];
        }

        PauseButton.Content = strings[snapshot.IsPaused ? "Resume" : "Pause"];
    }

    /// <summary>
    /// 中文：
    ///   显示出来之后放到工作区右下角。
    ///
    ///   ★ 靠右下是因为业务软件的输入框通常在上半屏，而工人的视线在货与屏幕之间
    ///     来回——把小窗放在角落，抬眼能看到，又不压住他正在填的那一格。
    ///     用 WorkArea 而不是整块屏幕，是为了不被任务栏盖住。
    /// English:
    ///   Places itself in the bottom-right of the work area once shown. The business
    ///   application's input field is usually in the upper half of the screen and the operator's
    ///   eyes move between goods and display, so a corner keeps it glanceable without covering the
    ///   field being filled. The work area rather than the full screen keeps it clear of the
    ///   taskbar.
    /// </summary>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var settings = CurrentApp.Settings;

        if (settings.RememberWindowPosition && settings.CompactWindowBounds is { } bounds)
        {
            Left = bounds.Left;
            Top = bounds.Top;
            return;
        }

        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - 16;
        Top = workArea.Bottom - Height - 16;
    }

    private void OnPauseClicked(object sender, RoutedEventArgs e)
    {
        if (Session is not { } session)
        {
            return;
        }

        if (session.Snapshot().IsPaused)
        {
            session.Resume();
        }
        else
        {
            session.Pause();
        }

        Refresh(session.Snapshot());
    }

    private void OnExpandClicked(object sender, RoutedEventArgs e)
    {
        _isClosingForExpand = true;
        ExpandRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 中文：
    ///   关掉 Compact 不等于退出程序——它会展开回 Full。
    ///   规格 §11.3 禁止"程序还在跑但看不见"，而那正是关掉之后什么都不做的结果。
    /// English:
    ///   Closing Compact expands back to Full rather than exiting. Spec §11.3 forbids a running but
    ///   invisible program, which is exactly what doing nothing here would produce.
    /// </summary>
    private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        SaveBounds();

        if (_isClosingForExpand || _isClosingForExit)
        {
            return;
        }

        e.Cancel = true;
        _isClosingForExpand = true;
        ExpandRequested?.Invoke(this, EventArgs.Empty);
    }

    private void SaveBounds()
    {
        if (!CurrentApp.Settings.RememberWindowPosition)
        {
            return;
        }

        CurrentApp.Settings.CompactWindowBounds = new Core.Settings.WindowBounds
        {
            Left = Left,
            Top = Top,
            Width = Width,
            Height = Height,
        };
    }
}
