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
using ScannerHelper.Win32;

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

    /// <summary>
    /// 中文：
    ///   全局热键。**它自己拥有一个仅消息窗口**，与本窗口的可见性无关——
    ///   最小化切到 Compact 时本窗口是隐藏的，热键必须照常管用。
    /// English:
    ///   The global hotkeys. They own a message-only window of their own and do not depend on this
    ///   window's visibility: minimizing to Compact hides this window while the hotkeys must keep
    ///   working.
    /// </summary>
    private readonly GlobalHotkeyListener? _hotkeys;

    private CompactWindow? _compactWindow;
    private bool _exitConfirmed;
    private bool _isToggleHotkeyAvailable = true;
    private bool _isRestoringPin;

    /// <summary>
    /// 中文：
    ///   托盘图标（规格 §11.9）。它是**第二块状态显示**，不是第二个隐藏处——
    ///   菜单里没有"最小化到托盘"，理由见 TrayIcon 的文件头。
    /// English:
    ///   The tray icon (spec §11.9): a second status display rather than a second place to hide.
    ///   Its menu has no "minimize to tray"; see TrayIcon's header.
    /// </summary>
    private readonly TrayIcon? _trayIcon;

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

        // ★ 置顶偏好在**订阅刷新之前**恢复，因为设置 IsChecked 会触发 Checked 事件，
        //   而那个处理器要写回设置——载入期间写回等于把工人的偏好覆盖成默认值。
        //   用 _isRestoringPin 挡住那一次。
        // The topmost preference is restored before anything else subscribes, because assigning
        // IsChecked raises Checked and that handler writes the setting back — writing during load
        // would overwrite the operator's preference with the default. _isRestoringPin blocks that
        // one call.
        _isRestoringPin = true;
        PinButton.IsChecked = CurrentApp.Settings.FullWindowAlwaysOnTop;
        Topmost = CurrentApp.Settings.FullWindowAlwaysOnTop;
        _isRestoringPin = false;

        Localizer.LanguageChanged += (_, _) => Refresh();

        // ★ 热键注册不上不该让程序起不来：注册失败是常态（别的程序占着），
        //   而没有热键的程序仍然完全可用——所有操作都有鼠标入口。
        // A failure to set up hotkeys must not stop the program: failing is ordinary (another
        // program holds the key) and a program without hotkeys is still fully usable, every action
        // having a mouse entrance.
        try
        {
            _hotkeys = new GlobalHotkeyListener();
            _hotkeys.Pressed += OnHotkeyPressed;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            _hotkeys = null;
        }

        // 托盘建不起来不该让程序起不来：那只是少了一块次要的状态显示，
        // 而主界面才是主通道（规格 §11.9 也明说 V1 可以没有它）。
        // A tray icon that cannot be created must not stop the program: it is only a secondary
        // status display, the main window being the primary channel — and spec §11.9 says V1 may
        // omit it entirely.
        try
        {
            _trayIcon = new TrayIcon();
            _trayIcon.ShowRequested += (_, _) => BringToFront();
            _trayIcon.PauseToggleRequested += (_, _) => TogglePause();
            _trayIcon.SettingsRequested += (_, _) =>
            {
                BringToFront();
                OpenSettings();
            };
            _trayIcon.ExitRequested += (_, _) =>
            {
                BringToFront();
                Close();
            };
        }
        catch (Exception)
        {
            _trayIcon = null;
        }

        if (Session is { } soundSession)
        {
            soundSession.ModeToggled += OnModeToggled;
            soundSession.ErrorRaised += OnErrorSound;

            // 认不出的命令条码只出声，不进待决状态——理由见 ScannerSession.CommandRejected。
            // An unrecognized command sheet only makes a sound; see ScannerSession.CommandRejected.
            soundSession.CommandRejected += OnErrorSound;
        }

        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = RefreshInterval,
        };
        _refreshTimer.Tick += (_, _) =>
        {
            // ★ 重连由界面的秒级心跳推动，而不是会话自己起一条线程。
            //   多一条线程就多一处要考虑"它和界面线程谁先谁后"，而这件事**本来
            //   就该按秒来**——工人拔插一次枪要好几秒，没有任何理由更快。
            // Reconnection is driven by the UI's one-second heartbeat rather than a thread of the
            // session's own. Another thread is another place to reason about ordering against the
            // UI thread, and this genuinely belongs on a one-second cadence: unplugging and
            // replugging takes the operator several seconds and nothing here needs to be faster.
            Session?.Tick();
            Refresh();
        };
        _refreshTimer.Start();

        Refresh();
    }

    /// <summary>
    /// 中文：
    ///   模式变了，出一声（规格 §7）。
    ///   工人低头搬货、双手都占着，声音是替代看屏幕的那条通道。
    /// English:
    ///   The mode changed; play a sound (spec §7). The operator is bent over goods with both hands
    ///   busy, and sound is the channel that replaces looking at the screen.
    /// </summary>
    private void OnModeToggled(object? sender, ScanMode mode)
        => Sounds.ModeChanged(mode, CurrentApp.Settings.ModeSwitchSoundEnabled);

    /// <summary>
    /// 中文：一枪没能发出去，出一声（规格 §13 的 ErrorSoundEnabled）。
    /// English: A scan did not go out; play a sound (spec §13's ErrorSoundEnabled).
    /// </summary>
    private void OnErrorSound(object? sender, EventArgs e)
        => Sounds.Error(CurrentApp.Settings.ErrorSoundEnabled);

    /// <summary>
    /// 中文：
    ///   热键被按下。**在界面线程上触发**（见 GlobalHotkeyListener），不需要切线程。
    ///
    ///   这里不必再判断"现在该不该响应"——规格 §7 的条件已经由**注册与否**表达了：
    ///   没有待决错误时 F10 与 Esc 根本没被注册，那两个键压根到不了这里。
    /// English:
    ///   A hotkey was pressed, raised on the UI thread (see GlobalHotkeyListener) with no
    ///   marshalling needed.
    ///
    ///   No "should this apply now" check is needed: spec §7's conditions are expressed by whether
    ///   the key is registered at all. With nothing pending, F10 and Escape are not registered and
    ///   never reach here.
    /// </summary>
    private void OnHotkeyPressed(object? sender, HotkeyPressedEventArgs e)
    {
        switch (e.Hotkey)
        {
            case ScannerHotkey.ToggleMode:
                Session?.ToggleMode();
                break;

            case ScannerHotkey.ForceSend:
                Session?.ForceSend();
                break;

            case ScannerHotkey.Discard:
                Session?.Discard();
                break;
        }
    }

    /// <summary>
    /// 中文：
    ///   按当前状态决定注册哪几个热键——规格 §7 的「放行表」就是这个方法。
    ///
    ///   ★ 暂停时全部注销。§7 最后一行写着"暂停时一律放行"，而注销之后那几个键
    ///     根本不经过我们，放行是结构上的结果而不是一个判断。
    ///
    ///   ★ F10 与 Esc 只在有待决错误时注册。§7 明说 Esc "在日常网页操作里太常用，
    ///     不能无条件吞掉"——没注册就等于没抢，工人在浏览器里按 Esc 照样管用。
    ///
    ///   ★ F8 注册不上时改口。裸键会被别的程序抢走，那时界面上"按 F8 切换模式"
    ///     就是一句假话（规格 §19.1）。
    /// English:
    ///   Decides which hotkeys are registered from the current state — this method *is* spec §7's
    ///   pass-through table.
    ///
    ///   Everything is unregistered while paused: §7's last row passes everything through, and once
    ///   unregistered those keys never reach us, so passing through is structural rather than a
    ///   judgment. F10 and Escape are registered only while an error is pending: §7 says Escape is
    ///   far too common in ordinary web use to swallow unconditionally, and not registering is not
    ///   taking it, so Escape keeps working in the browser. When F8 cannot be taken the hint
    ///   changes, because "press F8 to switch mode" would then be false (spec §19.1).
    /// </summary>
    private void SyncHotkeys(SessionSnapshot snapshot)
    {
        if (_hotkeys is not { } hotkeys)
        {
            _isToggleHotkeyAvailable = false;
            return;
        }

        // ★ 配置里的键换了，就得先把旧的还回去再注册新的。
        //
        //   不还回去的后果是双重的：旧键**继续被本程序占着**，在别的程序里一直
        //   失灵（而没有人会把"Insert 不好使了"和这个扫码程序联系起来）；同时
        //   Register 看见"已经注册过"直接返回 true，新键其实根本没生效，而界面
        //   会兴高采烈地显示"这台电脑上可用"。
        // A changed key must be handed back before the new one is taken. Otherwise the old key stays
        // held by this program and remains dead in every other one — and nobody connects "Insert
        // stopped working" with the scanner program — while Register sees "already registered",
        // returns true, and the UI cheerfully reports the new key as available when it was never
        // taken at all.
        var configuredChord = CurrentApp.Settings.Hotkeys.ToggleMode ?? "Insert";

        if (!string.Equals(hotkeys.ToggleModeChord, configuredChord, StringComparison.Ordinal))
        {
            hotkeys.Unregister(ScannerHotkey.ToggleMode);
            hotkeys.ToggleModeChord = configuredChord;
        }

        if (snapshot.IsPaused)
        {
            hotkeys.Unregister(ScannerHotkey.ToggleMode);
            hotkeys.Unregister(ScannerHotkey.ForceSend);
            hotkeys.Unregister(ScannerHotkey.Discard);
            return;
        }

        _isToggleHotkeyAvailable = hotkeys.Register(ScannerHotkey.ToggleMode);

        if (snapshot.PendingErrorRawCode is not null)
        {
            hotkeys.Register(ScannerHotkey.ForceSend);
            hotkeys.Register(ScannerHotkey.Discard);
        }
        else
        {
            hotkeys.Unregister(ScannerHotkey.ForceSend);
            hotkeys.Unregister(ScannerHotkey.Discard);
        }
    }

    /// <summary>
    /// 中文：
    ///   试一下某个键在**这台机器上**能不能被本程序拿到。设置界面用它，
    ///   在人还没离开设置窗口的时候就把答案说出来。
    ///   输出：拿到了返回 true。
    ///
    ///   ★ 必须先把当前那个热键**还回去**再试。热键是全局独占的，本程序自己
    ///     占着的时候去试同一个键，Windows 一样会拒绝——那会让设置界面把一个
    ///     好好的键报成"被占用"，而"占用"它的正是提问的人自己。
    ///
    ///   ★ 试完不必收拾现场：SyncHotkeys 每次刷新都会拿设置里的值和当前值比，
    ///     不一样就还回去重注册。所以取消设置之后，键会自己回到原来那个。
    /// English:
    ///   Tries whether a key can be taken by this program on this machine, so Settings can answer
    ///   while the person is still standing in front of it.
    ///
    ///   The current hotkey must be handed back first: hotkeys are globally exclusive, and testing
    ///   the same key while this program holds it is refused just the same — which would report a
    ///   perfectly good key as taken, by the very process asking the question.
    ///
    ///   Nothing needs tidying afterwards: every refresh SyncHotkeys compares the configured value
    ///   with the current one and re-registers when they differ, so cancelling Settings returns the
    ///   key to what it was.
    /// </summary>
    internal bool ProbeToggleHotkey(string chord)
    {
        if (_hotkeys is not { } hotkeys)
        {
            return false;
        }

        hotkeys.Unregister(ScannerHotkey.ToggleMode);
        hotkeys.ToggleModeChord = chord;

        _isToggleHotkeyAvailable = hotkeys.Register(ScannerHotkey.ToggleMode);
        return _isToggleHotkeyAvailable;
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

        SyncHotkeys(snapshot);

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
            SwitchHintText.Text = Localizer.Format(
                _isToggleHotkeyAvailable ? "SwitchModeHint" : "SwitchModeHintUnavailable",
                CurrentApp.Settings.Hotkeys.ToggleMode ?? string.Empty);
        }

        // ★ 暂停按钮**永远可点**。它是安全阀（规格 §5.7），而一个会在某些状态下
        //   变灰的安全阀，等于让工人先判断"现在能不能按"——需要它的那一刻恰恰是
        //   最不适合做判断的时候。
        // The Pause button is always clickable. It is the safety valve (spec §5.7), and a valve
        // that greys out in some states makes the operator first work out whether it can be
        // pressed — at precisely the moment least suited to working anything out.
        PinButton.Content = strings[PinButton.IsChecked == true ? "Unpin" : "Pin"];

        PauseButton.Content = strings[snapshot.IsPaused ? "Resume" : "Pause"];

        ConnectionText.Text = snapshot.IsConnected
            ? Localizer.Format("ConnectedOn", snapshot.PortName ?? string.Empty)
            : strings["Disconnected"];

        // 命令条码那一枪没有发出去，所以显示的不是"发了什么"而是"发生了什么"。
        // 见 SessionSnapshot.LastScanNoticeKey。
        // A command barcode is not sent, so what is shown is what happened rather than what went
        // out. See SessionSnapshot.LastScanNoticeKey.
        LastScanText.Text = snapshot.LastScanRawCode is { } raw
            ? snapshot.LastScanNoticeKey is { } noticeKey
                ? $"{raw}  →  {strings[noticeKey]}"
                : snapshot.LastScanEmitted is { } emitted && emitted != raw
                    ? $"{raw}  →  {emitted}"
                    : raw
            : strings["NoScanYet"];

        UpdateLinkWarning(snapshot);
        _trayIcon?.Update(snapshot);

        // 候选端口：置信度不够自动连，但工人点一下就能确认（见 ScannerSession.Tick）。
        // A suggested port: not confident enough to connect automatically, but one click from the
        // operator settles it (see ScannerSession.Tick).
        var hasSuggestion = !snapshot.IsConnected && snapshot.SuggestedPortName is not null;
        SuggestionPanel.Visibility = hasSuggestion ? Visibility.Visible : Visibility.Collapsed;

        if (hasSuggestion)
        {
            SuggestionText.Text = Localizer.Format(
                "ReconnectSuggestion",
                snapshot.SuggestedPortName ?? string.Empty,
                snapshot.SuggestedPortDescription ?? string.Empty);
        }

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

    /// <summary>
    /// 中文：
    ///   置顶开关被拨动（规格 §11.4）。
    ///   立即生效并存进设置——工人拨它是因为**此刻**窗口挡着或者看不见了，
    ///   要求他再去别处保存一次，那一下就白拨了。
    /// English:
    ///   The always-on-top switch moved (spec §11.4). It applies immediately and is persisted: the
    ///   operator flips it because the window is in the way or out of sight *right now*, and making
    ///   them save it somewhere else would waste the flip.
    /// </summary>
    private void OnPinChanged(object sender, RoutedEventArgs e)
    {
        if (_isRestoringPin)
        {
            return;
        }

        var isPinned = PinButton.IsChecked == true;

        Topmost = isPinned;
        CurrentApp.Settings.FullWindowAlwaysOnTop = isPinned;
        CurrentApp.SaveSettings();

        Refresh();
    }

    private void OnSwitchModeClicked(object sender, RoutedEventArgs e) => Session?.ToggleMode();

    private void OnPauseClicked(object sender, RoutedEventArgs e) => TogglePause();

    private void TogglePause()
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

    /// <summary>
    /// 中文：
    ///   把窗口叫到前面来。从 Compact 点托盘时要先展开。
    ///
    ///   ★ 这里**可以**抢焦点：工人是主动点的托盘，他要的就是看到这个窗口。
    ///     与出错自动展开那条路正好相反——那时他没点任何东西，抢焦点会把他
    ///     接下来敲的字吃掉（规格 §11.8）。
    /// English:
    ///   Brings the window forward, expanding from Compact first.
    ///
    ///   Taking focus is right here: the operator clicked the tray and wants to see this window.
    ///   The opposite of the automatic expansion on an error, where they clicked nothing and taking
    ///   focus would swallow what they type next (spec §11.8).
    /// </summary>
    private void BringToFront()
    {
        if (_compactWindow is not null)
        {
            ExpandFromCompact(activate: true);
            return;
        }

        if (!IsVisible)
        {
            Show();
        }

        WindowState = WindowState.Normal;
        Activate();
    }

    private void OnForceSendClicked(object sender, RoutedEventArgs e) => Session?.ForceSend();

    private void OnDiscardClicked(object sender, RoutedEventArgs e) => Session?.Discard();

    /// <summary>
    /// 中文：
    ///   工人确认那个候选端口就是他的枪。
    ///
    ///   ★ 这一下点击的含义是"我知道我把枪插到哪儿了"——那个判断本来就该他来做。
    ///     程序只能看到 VID/PID 一样，看不到桌上有几把枪。
    /// English:
    ///   The operator confirms the suggested port is their scanner. The click means "I know where I
    ///   plugged it in", which was always their judgment to make: the program sees matching VID and
    ///   PID and cannot see how many scanners are on the desk.
    /// </summary>
    private void OnConnectSuggestedClicked(object sender, RoutedEventArgs e)
    {
        if (Session?.ConnectToSuggested() is { } failure)
        {
            MessageBox.Show(
                this,
                failure.Failure.Message,
                Localizer.Get(failure.TitleKey),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        CurrentApp.SaveSettings();
        Refresh();
    }

    private void OnSettingsClicked(object sender, RoutedEventArgs e) => OpenSettings();

    private void OpenSettings()
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

        // ★ 恢复**工人在 Full 上的**置顶偏好，而不是把 Compact 的强制置顶带回来
        //   （规格 §11.4）。不这么做的话，收起再展开一次，窗口就永久置顶了，
        //   而工人根本没有做过那个选择。
        // Restore the operator's preference for Full rather than carrying Compact's forced topmost
        // back (spec §11.4). Otherwise one round trip through Compact leaves the window pinned
        // forever, a choice the operator never made.
        Topmost = CurrentApp.Settings.FullWindowAlwaysOnTop;

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

        // ★ 必须真的注销热键。它们是系统范围的：注册挂着不放，那几个键在别的
        //   程序里就一直失灵，而工人不会把"F8 不好使了"和这个扫码程序联系起来。
        // Hotkeys must actually be released. They are system-wide, and a registration left behind
        // leaves those keys dead in every other program — and nobody connects "F8 stopped working"
        // with this scanner program.
        _hotkeys?.Dispose();

        // ★ 托盘图标必须显式释放。不释放的话它会**留在托盘里**直到鼠标碰它一下
        //   才消失——工人看到的是"关了程序但图标还在"，与规格 §19 的干净收尾
        //   正好相反。
        // The tray icon must be disposed explicitly, or it sits in the tray until the mouse happens
        // to pass over it — "I closed it but the icon is still there", the opposite of spec §19's
        // clean shutdown.
        _trayIcon?.Dispose();

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
