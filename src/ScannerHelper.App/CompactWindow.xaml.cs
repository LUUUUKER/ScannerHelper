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
//   ExpandRequested    请求展开回 Full（「展开」按钮）
//   CloseRequested     请求退出程序（右上角 ×）
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
    ///   工人点了小窗右上角的 ×，意思是**退出程序**。
    ///
    ///   ★ 这里曾经把 × 当成"展开"。那是错的：× 在任何窗口上都意味着关闭，
    ///     而小窗旁边本来就有一个「展开」按钮——两个控件做同一件事，其中一个
    ///     还骗了用户一次。工人点 × 是想关掉程序，就该按他想的来。
    ///
    ///   ★ 接这个事件的一方要先展开成 Full 再走关闭流程，而不是直接在小窗上
    ///     弹确认框。确认框需要一个可见的属主窗口：挂在一个即将消失的小窗上，
    ///     框可能跑到别的窗口后面去，而工人看到的是"点了没反应"。
    /// English:
    ///   The operator clicked the compact window's close button, meaning they want to exit.
    ///
    ///   This used to be treated as "expand". That was wrong: an X means close on every window, and
    ///   the compact window already has an Expand button beside it — two controls doing one thing,
    ///   one of them lying. Someone clicking X wants the program closed, and that is what should
    ///   happen.
    ///
    ///   The handler expands to Full first and then runs the close, rather than raising the
    ///   confirmation on the compact window: a modal dialog needs a visible owner, and one owned by a
    ///   window that is about to disappear can end up behind something else, which the operator reads
    ///   as "clicking did nothing".
    /// </summary>
    public event EventHandler? CloseRequested;

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

    /// <summary>
    /// 中文：模式的显示名。SN / SKU 两种语言下都不翻译（规格 §12「术语」）。
    /// English: The mode's label. SN and SKU stay untranslated in both languages (spec §12).
    /// </summary>
    private static string ModeLabel(ScanMode mode, LocalizedStrings strings)
        => strings[mode == ScanMode.Sn ? "ModeSn" : "ModeSku"];

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

        // ★ 切换按钮默认可见，由下面三个分支各自收回去——和 Full 窗口逐条一致。
        //
        //   未连接时藏起来：没有码会进来，切模式是个无意义的动作，而一个按下去
        //   什么都不发生的按钮会让工人怀疑是不是程序卡了。
        //   待决错误时藏起来：那时在等一个决定（F10 / Esc），中途改模式只会让
        //   那一枪的结局更难说清。
        //   暂停时藏起来：暂停绕过了全部规则，此刻的"模式"根本不参与任何事。
        //
        // Visible by default and withdrawn by each of the three branches below, exactly as the Full
        // window does. Hidden while disconnected, because no code is arriving and a button that does
        // nothing makes the operator suspect the program has hung; hidden while an error is pending,
        // because a decision is being waited on (F10 / Esc) and changing mode midway only muddies
        // that scan's outcome; hidden while paused, because pausing bypasses every rule and the mode
        // takes part in nothing.
        SwitchModeButton.Visibility = Visibility.Visible;

        if (!snapshot.IsConnected)
        {
            StatePanel.Background = (Brush)FindResource("DisconnectedBrush");
            DisconnectedBar.Visibility = Visibility.Visible;
            StateTitleText.Text = "⛔";
            StateSubtitleText.Text = strings["Disconnected"];
            SwitchModeButton.Visibility = Visibility.Collapsed;
        }
        else if (snapshot.PendingErrorRawCode is { } pendingCode)
        {
            StatePanel.Background = (Brush)FindResource("ErrorBrush");
            HazardStripe.Visibility = Visibility.Visible;
            StateTitleText.Text = "⚠";

            // ★ 小窗里也要说清是在哪个模式下出的错。
            //
            //   出错会自动展开成大窗（规格 §11.7），所以这一屏往往只闪一下——
            //   但"只闪一下"不是可以少说一句的理由：那一下正是工人抬眼看到的
            //   第一眼，而模式不对恰恰是最常见的病因。
            //
            //   这里用文字而不是色块：小窗只有一行的地方，再塞一块颜色会把
            //   本来就短的那行码挤掉，而码是他要认的东西。
            // The compact window says which mode too. An error auto-expands to Full (spec §11.7), so
            // this screen often only flashes — but a flash is no reason to say less: it is the first
            // thing the operator's eye lands on, and the wrong mode is the commonest cause.
            //
            // Text rather than a colour block: there is room for one line here, and another block
            // would squeeze out the code itself, which is what they need to read.
            StateSubtitleText.Text = Localizer.Format(
                "ErrorInMode", ModeLabel(snapshot.Mode, strings)) + "  " + pendingCode;
            SwitchModeButton.Visibility = Visibility.Collapsed;
        }
        else if (snapshot.IsPaused)
        {
            StatePanel.Background = (Brush)FindResource("PausedBrush");
            StateTitleText.Text = "⏸  " + strings["PausedHeading"];
            StateSubtitleText.Text = strings["PausedSubtitle"];
            SwitchModeButton.Visibility = Visibility.Collapsed;
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

    /// <summary>
    /// 中文：
    ///   切换 SN / SKU。
    ///
    ///   ★ 切完立刻按新快照重画。小窗是靠 Full 窗口定时推快照刷新的，中间隔着
    ///     一个刷新周期——工人按下去之后那几百毫秒里看到的还是旧模式，而他会
    ///     以为没按上，于是再按一次，正好又切回去了。
    /// English:
    ///   Toggles SN/SKU.
    ///
    ///   The view is redrawn from the new snapshot immediately. Compact is refreshed by snapshots
    ///   pushed from the Full window on a timer, leaving a gap in which the operator still sees the
    ///   old mode, concludes the press did not register, presses again — and lands back where they
    ///   started.
    /// </summary>
    private void OnSwitchModeClicked(object sender, RoutedEventArgs e)
    {
        if (Session is not { } session)
        {
            return;
        }

        session.ToggleMode();
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

        // 工人点了小窗的 ×，意思是退出程序（见 CloseRequested）。先把这次关闭
        // 挡下来——真正的退出要在展开成 Full 之后走，那里有确认框（规格 §11.6）。
        e.Cancel = true;
        _isClosingForExpand = true;

        // ★ 必须**异步**发这个通知，不能在这儿直接调。
        //
        //   接这个事件的是 MainWindow.ExpandFromCompact，而它头一件事就是
        //   compact.Close()——也就是在本窗口自己的 Closing 事件里再关它一次。
        //   WPF 对此直接抛 InvalidOperationException（"Cannot ... call Close
        //   while a Window is closing"），而界面线程上没人接，进程当场死掉。
        //
        //   这个崩溃的代价比"关不掉"大得多：退出确认没弹过、设置没存、诊断日志
        //   里连 Stopped 都没有——事后看日志的人只看到记录在 Connected 那一行
        //   戛然而止，而那正是规格 §15 要回答却答不上来的情形。工人看到的则是
        //   窗口僵住几十秒（Windows 在收集崩溃转储）。
        //
        //   BeginInvoke 把展开推到这次关闭处理完之后，那时本窗口已经不在
        //   "正在关闭"状态里，ExpandFromCompact 关它就是合法的。
        //
        // The notification must be raised asynchronously rather than called here.
        //
        // MainWindow.ExpandFromCompact receives it and immediately calls compact.Close() — closing
        // this window from inside its own Closing event. WPF throws InvalidOperationException
        // ("Cannot ... call Close while a Window is closing"), nothing on the UI thread catches it,
        // and the process dies on the spot.
        //
        // That crash costs far more than a window that will not close: the exit confirmation never
        // appears, settings are not saved, and the diagnostic log has no Stopped line — whoever
        // reads it later sees the record simply stop after Connected, exactly the situation spec §15
        // exists to explain. What the operator sees is a window frozen for tens of seconds while
        // Windows collects a crash dump.
        //
        // BeginInvoke defers the expansion until after this close is handled, by which point the
        // window is no longer closing and ExpandFromCompact may legitimately close it.
        Dispatcher.BeginInvoke(() => CloseRequested?.Invoke(this, EventArgs.Empty));
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
