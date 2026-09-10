// =============================================================================
// MainWindow.xaml.cs
//
// 中文：
//   Full 窗口的代码。它只做两件事：把会话的状态画出来，把点击转达给会话。
//   没有任何业务状态住在这里——理由见 ScannerSession 的文件头。
//
//   ★ 会话的事件是在**串口读取线程**上抛出的，所以每一处订阅都要切回界面线程。
//
//     不切的后果不是偶尔闪一下，而是 WPF 直接抛异常（跨线程访问控件）。而那个
//     异常发生在读取线程上——没人接住，进程就没了，工人正扫着货程序突然消失。
//
//   ★ 最小化即 Compact（规格 §11.3），而且**绝不最小化到看不见的状态**。
//
//     这条规则的分量在于：本程序会拦下扫码枪的内容再发出去，一个看不见却在
//     工作的程序，工人无法判断"现在到底是不是它在起作用"。所以最小化被拦下来，
//     换成一个小窗口。
//
//   ★ 关闭要确认（规格 §11.6）。
//
//     关掉之后扫码就不再被处理，而工人很可能只是想让窗口别挡着。确认框里同时
//     告诉他"想藏起来请用收起"，把误操作变成一次学习。
//
// English:
//   The Full window's code. It draws the session's state and relays clicks; no business state
//   lives here (see ScannerSession's header for why).
//
//   The session raises its events on the serial reader thread, so every subscription marshals back
//   to the UI thread. Not doing so does not cause an occasional flicker: WPF throws on cross-thread
//   control access, and that exception happens on the reader thread where nobody catches it — the
//   process disappears while the operator is scanning goods.
//
//   Minimize means Compact (spec §11.3) and the program never minimizes into invisibility. The rule
//   carries weight because this program intercepts scanner content and re-emits it: an invisible
//   but working program leaves the operator unable to tell whether it is the thing acting. So
//   minimizing is intercepted and turned into a small window.
//
//   Closing asks for confirmation (spec §11.6): afterwards scans stop being processed, while the
//   operator most likely just wanted the window out of the way. The dialog also names Compact,
//   turning a misclick into something learned.
//
// 包含的成员 / Members in this file:
//   Refresh                     把会话状态画出来
//   OnStateChanged              最小化 → Compact
//   OnClosing                   退出确认
//   各个按钮的处理 / button handlers
// =============================================================================

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ScannerHelper.App.Localization;
using ScannerHelper.Core.Domain;

namespace ScannerHelper.App;

/// <summary>
/// 中文：Full 窗口（规格 §11.1）。
/// English: The Full window (spec §11.1).
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>
    /// 中文：界面刷新间隔。「多久没收到扫码」是随时间自己走的，事件推不动它，
    ///       所以需要一个心跳来重画。一秒足够——阈值是以分钟计的。
    /// English: The refresh interval. Time since the last scan advances on its own and no event
    ///          moves it, so a heartbeat redraws it. One second suffices; the threshold is in
    ///          minutes.
    /// </summary>
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(1);

    private readonly DispatcherTimer _refreshTimer;
    private CompactWindow? _compactWindow;
    private bool _exitConfirmed;

    /// <summary>
    /// 中文：构造窗口并接上会话。
    /// English: Creates the window and attaches to the session.
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();

        RestorePersistedBounds();

        if (Session is { } session)
        {
            session.Changed += OnSessionChanged;
            session.ErrorRaised += OnSessionErrorRaised;
        }

        Localizer.LanguageChanged += (_, _) => Refresh();

        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = RefreshInterval,
        };
        _refreshTimer.Tick += (_, _) => Refresh();
        _refreshTimer.Start();

        Refresh();
    }

    private static App CurrentApp => (App)Application.Current;

    private static ScannerSession? Session => CurrentApp.Session;

    /// <summary>
    /// 中文：
    ///   会话状态变了。**可能在读取线程上被调用**，所以切回界面线程再重画。
    /// English:
    ///   The session changed. May be called on the reader thread, so it hops to the UI thread
    ///   before redrawing.
    /// </summary>
    private void OnSessionChanged(object? sender, EventArgs e)
        => Dispatcher.BeginInvoke(Refresh);

    /// <summary>
    /// 中文：
    ///   出现了待决错误。规格 §11.7：Compact 状态下必须自动展开成 Full，
    ///   **但不得抢走业务软件的焦点**——工人正在网页里操作，把焦点抢过来会让
    ///   他接下来敲的东西落到我们窗口上。
    /// English:
    ///   An error is pending. Spec §11.7: from Compact this must expand to Full — without stealing
    ///   focus from the business application, where the operator is working; taking focus would
    ///   send whatever they type next into our window.
    /// </summary>
    private void OnSessionErrorRaised(object? sender, EventArgs e)
        => Dispatcher.BeginInvoke(() =>
        {
            if (_compactWindow is not null)
            {
                ExpandFromCompact(activate: false);
            }

            Refresh();
        });

    /// <summary>
    /// 中文：
    ///   把会话的状态画出来。
    ///   五种状态各有一套颜色与不依赖颜色的标志（规格 §11.2），优先级从高到低是：
    ///     未连接 → 待决错误 → 暂停 → SKU → SN
    ///
    ///   ★ 这个优先级不是随手排的。
    ///     未连接排第一，是因为那时**别的一切都无从谈起**：模式显示得再准确，
    ///     也没有码会进来。待决错误排第二，因为它在等一个人的决定，而人不看
    ///     就不会决定。暂停排第三，因为它绕过了规则，必须盖住模式（规格 §11.1）。
    /// English:
    ///   Draws the session's state. Five states each carry colors and a non-color signature
    ///   (spec §11.2), in priority: disconnected, pending error, paused, SKU, SN.
    ///
    ///   The order is not arbitrary. Disconnected comes first because nothing else matters then:
    ///   however accurately the mode is shown, no code is arriving. A pending error comes next
    ///   because it waits on a person's decision and an unseen decision is never made. Paused comes
    ///   third because it bypasses the rules and must cover the mode (spec §11.1).
    /// </summary>
    private void Refresh()
    {
        if (Session is not { } session)
        {
            return;
        }

        var snapshot = session.Snapshot();
        var strings = Localizer.Strings;

        HazardStripe.Visibility = Visibility.Collapsed;
        DisconnectedBar.Visibility = Visibility.Collapsed;
        ModeOnResumeText.Visibility = Visibility.Collapsed;
        ErrorCodeText.Visibility = Visibility.Collapsed;
        ErrorActions.Visibility = Visibility.Collapsed;
        SwitchHintText.Visibility = Visibility.Visible;
        SwitchModeButton.Visibility = Visibility.Visible;

        if (!snapshot.IsConnected)
        {
            StatePanel.Background = (Brush)FindResource("DisconnectedBrush");
            DisconnectedBar.Visibility = Visibility.Visible;
            StateHeadingText.Text = string.Empty;
            StateTitleText.Text = "⛔";
            StateSubtitleText.Text = strings["Disconnected"];
            SwitchHintText.Visibility = Visibility.Collapsed;
            SwitchModeButton.Visibility = Visibility.Collapsed;
        }
        else if (snapshot.PendingErrorRawCode is { } pendingCode)
        {
            StatePanel.Background = (Brush)FindResource("ErrorBrush");
            HazardStripe.Visibility = Visibility.Visible;
            StateHeadingText.Text = string.Empty;
            StateTitleText.Text = strings["ErrorHeading"];
            StateSubtitleText.Text = snapshot.LastScanFailureKey is { } key
                ? strings[key]
                : strings["ErrorPrompt"];
            ErrorCodeText.Text = pendingCode;
            ErrorCodeText.Visibility = Visibility.Visible;
            ErrorActions.Visibility = Visibility.Visible;
            SwitchHintText.Visibility = Visibility.Collapsed;
            SwitchModeButton.Visibility = Visibility.Collapsed;
        }
        else if (snapshot.IsPaused)
        {
            StatePanel.Background = (Brush)FindResource("PausedBrush");
            StateHeadingText.Text = string.Empty;
            StateTitleText.Text = "⏸  " + strings["PausedHeading"];
            StateSubtitleText.Text = strings["PausedSubtitle"];
            ModeOnResumeText.Text =
                strings["ModeOnResume"] + "：" + ModeLabel(snapshot.Mode, strings);
            ModeOnResumeText.Visibility = Visibility.Visible;
            SwitchHintText.Visibility = Visibility.Collapsed;
            SwitchModeButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            var isSn = snapshot.Mode == ScanMode.Sn;
            StatePanel.Background = (Brush)FindResource(isSn ? "SnBrush" : "SkuBrush");
            StateHeadingText.Text = strings["ModeHeading"];
            StateTitleText.Text = ModeLabel(snapshot.Mode, strings);
            StateSubtitleText.Text = strings[isSn ? "ModeSnSubtitle" : "ModeSkuSubtitle"];
            SwitchHintText.Text = strings["SwitchModeHint"];
        }

        // ★ 暂停按钮**永远可点**。它是安全阀（规格 §5.7），而一个会在某些状态下
        //   变灰的安全阀，等于让工人先判断"现在能不能按"——需要它的那一刻恰恰是
        //   最不适合做判断的时候。
        // The Pause button is always clickable. It is the safety valve (spec §5.7), and a valve
        // that greys out in some states makes the operator first work out whether it can be
        // pressed — at precisely the moment least suited to working anything out.
        PauseButton.Content = strings[snapshot.IsPaused ? "Resume" : "Pause"];

        ConnectionText.Text = snapshot.IsConnected
            ? Localizer.Format("ConnectedOn", snapshot.PortName ?? string.Empty)
            : strings["Disconnected"];

        LastScanText.Text = snapshot.LastScanRawCode is { } raw
            ? snapshot.LastScanEmitted is { } emitted && emitted != raw
                ? $"{raw}  →  {emitted}"
                : raw
            : strings["NoScanYet"];

        UpdateLinkWarning(snapshot);
        _compactWindow?.Refresh(snapshot);
    }

    /// <summary>
    /// 中文：
    ///   链路沉默的警告。
    ///
    ///   ★ 这是新架构下 §19.1 的心跳。有人扫一张配置码把枪切回键盘模式之后，
    ///     串口会好好地开着、不报任何错、也永远收不到数据，而扫码枪开始直接往
    ///     业务软件里打字。除了"多久没动静了"，没有别的量能看见这件事——所以
    ///     它必须出现在工人看得见的地方，而不是躺在日志里。
    /// English:
    ///   The silence warning: §19.1's heartbeat under the new architecture. Once someone switches
    ///   the scanner back to keyboard mode the port stays open, raises nothing and never receives
    ///   data while the scanner types into the business application. Nothing but this silence
    ///   reveals it, so it belongs where the operator can see it rather than in a log.
    /// </summary>
    private void UpdateLinkWarning(SessionSnapshot snapshot)
    {
        var minutes = CurrentApp.Settings.SerialPort.SilenceWarningMinutes;
        var threshold = TimeSpan.FromMinutes(minutes);

        var isStale = snapshot.IsConnected
            && snapshot.TimeSinceLastScan is { } silence
            && silence > threshold;

        LinkWarningText.Visibility = isStale ? Visibility.Visible : Visibility.Collapsed;

        if (isStale)
        {
            LinkWarningText.Text = Localizer.Format(
                "LinkSilent",
                ((int)(snapshot.TimeSinceLastScan ?? threshold).TotalMinutes)
                    .ToString(CultureInfo.InvariantCulture));
        }
    }

    /// <summary>
    /// 中文：模式的显示名。SN / SKU 两种语言下都不翻译（规格 §12「术语」）。
    /// English: The mode's label. SN and SKU stay untranslated in both languages (spec §12).
    /// </summary>
    private static string ModeLabel(ScanMode mode, LocalizedStrings strings)
        => strings[mode == ScanMode.Sn ? "ModeSn" : "ModeSku"];

    private void OnSwitchModeClicked(object sender, RoutedEventArgs e) => Session?.ToggleMode();

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
    }

    private void OnForceSendClicked(object sender, RoutedEventArgs e) => Session?.ForceSend();

    private void OnDiscardClicked(object sender, RoutedEventArgs e) => Session?.Discard();

    private void OnSettingsClicked(object sender, RoutedEventArgs e)
    {
        var settingsWindow = new SettingsWindow { Owner = this };
        settingsWindow.ShowDialog();
        Refresh();
    }

    /// <summary>
    /// 中文：
    ///   最小化被拦下来，换成 Compact（规格 §11.3）。
    ///
    ///   把窗口状态改回 Normal 再隐藏，是为了下次展开时不会先闪一下最小化动画；
    ///   也保证 §11.5 存的窗口坐标是正常状态下的坐标，而不是最小化时的那组假值。
    /// English:
    ///   Minimizing is intercepted and becomes Compact (spec §11.3). The state is set back to
    ///   Normal before hiding so that expanding later does not flash a minimize animation, and so
    ///   that the bounds persisted per §11.5 are the normal ones rather than a minimized window's
    ///   placeholder values.
    /// </summary>
    private void OnStateChanged(object sender, EventArgs e)
    {
        if (WindowState != WindowState.Minimized)
        {
            return;
        }

        WindowState = WindowState.Normal;
        ShowCompact();
    }

    /// <summary>
    /// 中文：切到 Compact：藏起 Full，开一个小窗口。
    /// English: Switches to Compact: hide Full and open the small window.
    /// </summary>
    private void ShowCompact()
    {
        SaveBounds();
        Hide();

        _compactWindow = new CompactWindow();
        _compactWindow.ExpandRequested += (_, _) => ExpandFromCompact(activate: true);
        _compactWindow.Show();

        if (Session is { } session)
        {
            _compactWindow.Refresh(session.Snapshot());
        }
    }

    /// <summary>
    /// 中文：
    ///   从 Compact 展开回 Full。
    ///   输入：activate 是否把焦点拿过来。
    ///
    ///   ★ 出错自动展开时必须传 false（规格 §11.8）。工人正在业务软件里操作，
    ///     抢走焦点会让他接下来敲的字落到我们窗口上——本程序存在的意义是让他
    ///     少操心，不是给他添一个新的意外。
    /// English:
    ///   Expands back to Full. An automatic expansion caused by an error must pass false
    ///   (spec §11.8): the operator is working in the business application, and taking focus would
    ///   send what they type next into our window. This program exists to spare them attention,
    ///   not to add a surprise.
    /// </summary>
    private void ExpandFromCompact(bool activate)
    {
        if (_compactWindow is { } compact)
        {
            compact.Close();
            _compactWindow = null;
        }

        Show();

        if (activate)
        {
            Activate();
        }

        Refresh();
    }

    /// <summary>
    /// 中文：
    ///   关闭要确认（规格 §11.6）。
    ///   关掉之后扫码不再被处理，而工人很可能只是想让窗口别挡着——所以确认框里
    ///   顺便告诉他「收起」这条路。
    /// English:
    ///   Closing asks first (spec §11.6). Afterwards scans stop being processed, while the operator
    ///   most likely just wanted the window out of the way — so the dialog names Compact.
    /// </summary>
    private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_exitConfirmed)
        {
            return;
        }

        var answer = MessageBox.Show(
            this,
            Localizer.Get("ExitMessage"),
            Localizer.Get("ExitTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);

        if (answer != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }

        _exitConfirmed = true;
        _refreshTimer.Stop();
        SaveBounds();

        _compactWindow?.CloseForExit();
        Application.Current.Shutdown();
    }

    /// <summary>
    /// 中文：
    ///   恢复上次的窗口位置（规格 §11.5）。
    ///
    ///   ★ 必须校验坐标还落在某块屏幕上。工人在工位上接了外接显示器、回家拔掉，
    ///     存下来的坐标就指向一块不存在的屏幕——窗口会开在看不见的地方，而程序
    ///     看起来"启动了但没出来"。落在外面就退回居中。
    /// English:
    ///   Restores the previous window position (spec §11.5), after checking the coordinates still
    ///   land on a screen. An operator docks a second monitor at the workstation and undocks it
    ///   later, and the stored position then names a screen that no longer exists: the window opens
    ///   where nobody can see it and the program appears to start without appearing. Off-screen
    ///   falls back to centred.
    /// </summary>
    private void RestorePersistedBounds()
    {
        var settings = CurrentApp.Settings;

        if (!settings.RememberWindowPosition || settings.FullWindowBounds is not { } bounds)
        {
            return;
        }

        var virtualLeft = SystemParameters.VirtualScreenLeft;
        var virtualTop = SystemParameters.VirtualScreenTop;
        var virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
        var virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;

        // 至少要有一块可见区域落在虚拟屏幕内，才认为这组坐标还可用。
        // The position is only usable if some visible part of the window lands on the desktop.
        var isVisible = bounds.Left + 80 < virtualRight
            && bounds.Left + bounds.Width - 80 > virtualLeft
            && bounds.Top + 40 < virtualBottom
            && bounds.Top + bounds.Height - 40 > virtualTop;

        if (!isVisible)
        {
            return;
        }

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = bounds.Left;
        Top = bounds.Top;
        Width = bounds.Width;
        Height = bounds.Height;
    }

    private void SaveBounds()
    {
        if (!CurrentApp.Settings.RememberWindowPosition || WindowState != WindowState.Normal)
        {
            return;
        }

        CurrentApp.Settings.FullWindowBounds = new Core.Settings.WindowBounds
        {
            Left = Left,
            Top = Top,
            Width = Width,
            Height = Height,
        };
    }
}
