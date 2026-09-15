// =============================================================================
// EndToEndWindow.xaml.cs
//
// 中文：
//   新架构的第一条完整链路：串口 → ScanProcessor → SendInput。
//
//   ★ 这里终于可以用锁了，而这件事值得说明。
//
//     旧架构下，处理是在低层钩子的回调里发生的，那条路径上**绝不能有锁**：
//     一次争用就可能顶穿 LowLevelHooksTimeout，而 Windows 会因此悄悄摘掉钩子
//     （规格 §19）。于是所有跨线程的事都得靠"把动作投递到那条专属线程上"来
//     串行化，代价是一整套投递机制。
//
//     串口模式下没有钩子回调，也就没有那个必须在几十毫秒内返回的地方。扫描在
//     读取线程上进来，界面上的动作（暂停、切模式、强制发送）在界面线程上发生，
//     两者都要碰 ScanProcessor —— 一把锁就够了，而且是最直白的那种表达。
//
//     用锁而不是"全部投递到界面线程"，是因为后者会让输出的时机取决于界面忙不忙，
//     而输出是这个产品唯一的出口。4a 第一轮的教训正是"界面卡了，测量就跟着歪"。
//
//   ★ 输出默认关闭。
//
//     勾上之后它会真的往当前焦点所在的程序里打字。默认关闭意味着可以先看着
//     列表确认结果对不对，再决定要不要发出去——尤其是 SKU 规则刚改完的时候。
//
//   ★ 链路健康：距离上一枪多久了。
//
//     有人把扫码枪切回键盘模式之后，这条链路会安安静静地什么都不发生——端口
//     开着、不报错、也没有数据，而扫码枪正直接往业务软件里打字
//     （ARCHITECTURE_CHANGE_SERIAL.md §5.1）。"多久没动静了"是唯一能看见它的量，
//     所以它必须显示在界面上，而不是藏在日志里。
//
// English:
//   The first complete chain under the new architecture: serial to ScanProcessor to SendInput.
//
//   A lock is finally permissible here, which is worth explaining. Under the old architecture
//   processing happened inside a low-level hook callback, where a lock must never appear: one
//   contention can blow the LowLevelHooksTimeout budget and have Windows silently remove the hook
//   (spec §19). Everything cross-thread therefore had to be serialized by posting actions to that
//   one dedicated thread, at the cost of an entire posting mechanism. With no hook callback there
//   is no place that must return within milliseconds: scans arrive on the reader thread, UI
//   actions happen on the UI thread, both touch the ScanProcessor, and one lock says exactly that.
//
//   A lock rather than "marshal everything to the UI thread", because the latter would make output
//   timing depend on how busy the UI is — and output is this product's only exit. 4a's first run
//   taught precisely that lesson in the other direction.
//
//   Output is off by default: once enabled it really types into whatever has focus, and being off
//   allows checking the list first, especially just after changing a SKU rule.
//
//   Link health shows how long since the last scan. After someone switches the scanner back to
//   keyboard mode this link goes quietly inert — port open, no error, no data — while the scanner
//   types raw barcodes into the business application (ARCHITECTURE_CHANGE_SERIAL.md §5.1). How
//   long it has been silent is the only quantity that reveals it, so it belongs on screen rather
//   than in a log.
//
// 包含的成员 / Members in this file:
//   连接、断开 / connect, disconnect
//   模式、补回车、暂停、恢复 / mode, append enter, pause, resume
//   强制发送、取消 / force send, cancel
//   SKU 规则的现场修改 / editing the SKU rule on the spot
// =============================================================================

using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using ScannerHelper.Core.Domain;
using ScannerHelper.Core.Input;
using ScannerHelper.Core.Modes;
using ScannerHelper.Core.Parsing;
using ScannerHelper.Core.Settings;
using ScannerHelper.Core.Validation;
using ScannerHelper.Win32;
using ScannerHelper.Win32.Serial;

namespace ScannerHelper.Diagnostics.Harness;

/// <summary>
/// 中文：串口 → 处理 → 输出的完整链路。
/// English: The complete chain: serial, processed, typed out.
/// </summary>
public partial class EndToEndWindow : Window
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(200);
    private const int MaximumRows = 500;

    /// <summary>
    /// 中文：
    ///   保护 ScanProcessor 的锁。扫描从读取线程进来，界面上的动作从界面线程进来，
    ///   两者都要碰它。
    ///
    ///   ★ 一把锁在这里是**允许**的，而在旧架构里是禁止的——理由见文件头。
    ///     临界区里只有纯计算和一次 SendInput，都是微秒级，不存在长时间持锁。
    /// English:
    ///   Guards the ScanProcessor, which both the reader thread and the UI thread touch. A lock is
    ///   permissible here and was forbidden under the old architecture; see the file header. The
    ///   critical section holds only pure computation and one SendInput, both measured in
    ///   microseconds.
    /// </summary>
    private readonly object _gate = new();

    private readonly ModeManager _modeManager = new();
    private readonly RecordingThenForwardingOutput _output = new();
    private readonly SerialScannerInputSource _source = new();
    private readonly ObservableCollection<ScanRow> _rows = [];
    private readonly ConcurrentQueue<ScanRow> _pendingRows = new();
    private readonly DispatcherTimer _refreshTimer;
    private readonly ScanProcessor _processor;

    private AppSettings _settings = new();
    private Exception? _lastFault;
    private int _index;

    /// <summary>
    /// 中文：构造窗口并组装整条链路。
    /// English: Creates the window and assembles the chain.
    /// </summary>
    public EndToEndWindow()
    {
        InitializeComponent();

        _processor = new ScanProcessor(
            _modeManager,
            SkuParserFactory.Create(_settings.SkuParsing),
            SkuValidatorFactory.Create(_settings.SkuValidation),
            _output);

        _processor.ScanProcessed += OnScanProcessed;
        _source.ScanReceived += OnScanReceived;
        _source.Faulted += (_, exception) => _lastFault = exception;

        ScanListView.ItemsSource = _rows;

        // 默认规则取前 7 位，和界面上的初值一致。
        // The default rule takes the first seven characters, matching the initial UI values.
        ApplyRule();

        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = RefreshInterval,
        };
        _refreshTimer.Tick += (_, _) => Refresh();
        _refreshTimer.Start();

        RefreshPorts();
        Refresh();
    }

    private void OnWindowClosing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        _refreshTimer.Stop();
        _source.Dispose();
    }

    /// <summary>
    /// 中文：
    ///   一枪到手。**这在读取线程上执行**，所以只做两件事：处理，然后把结果排队
    ///   给界面。界面刷新由界面自己的定时器来做。
    /// English:
    ///   A scan arrived. This runs on the reader thread and does two things only: process it, then
    ///   queue the result for the UI, whose own timer does the rendering.
    /// </summary>
    private void OnScanReceived(object? sender, ScanReceivedEventArgs args)
    {
        lock (_gate)
        {
            _output.BeginScan(args.RawCode);
            _processor.OnScanReceived(args.RawCode);
        }
    }

    /// <summary>
    /// 中文：
    ///   一枪处理完毕，记一行。
    ///   本方法可能在读取线程上（正常扫描），也可能在界面线程上（强制发送），
    ///   两者都只往并发队列里放东西，因此不需要区分。
    /// English:
    ///   A scan finished; record a row. This runs on the reader thread for a normal scan and on
    ///   the UI thread for Force Send, and both only enqueue, so the distinction does not matter.
    /// </summary>
    private void OnScanProcessed(object? sender, ScanProcessedEventArgs args)
    {
        var (rawCode, result) = Describe(args.Outcome);

        _pendingRows.Enqueue(new ScanRow(
            Index: Interlocked.Increment(ref _index),
            Time: DateTimeOffset.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
            Mode: _modeManager.CurrentMode == ScanMode.Sn ? "SN" : "SKU",
            RawCode: rawCode,
            Emitted: _output.LastEmittedText ?? "—",
            Result: result));
    }

    /// <summary>
    /// 中文：把结局转成两列文字。
    /// English: Turns an outcome into two columns of text.
    /// </summary>
    private static (string RawCode, string Result) Describe(ScanOutcome outcome)
        => outcome switch
        {
            ScanOutcome.Emit emit => (emit.RawCode, "已发出 / emitted"),

            ScanOutcome.EmitRawWhilePaused paused => (
                paused.RawCode, "暂停中，原样发出 / raw (paused)"),

            ScanOutcome.ParseFailed parseFailed => (
                parseFailed.Failure.RawCode,
                $"解析失败 / parse failed：{parseFailed.Failure.Reason}"),

            ScanOutcome.ValidationFailed validationFailed => (
                validationFailed.RawCode,
                $"校验失败 / validation failed：{validationFailed.Sku}"),

            _ => (string.Empty, outcome.ToString() ?? string.Empty),
        };

    private void RefreshPorts()
    {
        var previous = PortCombo.SelectedItem as string;
        PortCombo.ItemsSource = SerialScannerInputSource.ListPorts();

        if (previous is not null && PortCombo.Items.Contains(previous))
        {
            PortCombo.SelectedItem = previous;
        }
        else if (PortCombo.Items.Count > 0)
        {
            PortCombo.SelectedIndex = 0;
        }
    }

    private void OnRefreshPortsClicked(object sender, RoutedEventArgs e) => RefreshPorts();

    /// <summary>
    /// 中文：
    ///   连接串口。失败时原样显示异常——「端口被别的程序占用」和「端口不存在」
    ///   这两句话本身就是最有用的线索，包装成「连接失败」只会把它盖掉。
    /// English:
    ///   Connects, showing the original exception on failure: "the port is held by another
    ///   program" and "no such port" are themselves the most useful clue.
    /// </summary>
    private void OnConnectClicked(object sender, RoutedEventArgs e)
    {
        if (PortCombo.SelectedItem is not string portName)
        {
            MessageBox.Show(
                this,
                "先选一个端口 / Select a port first.",
                "Scanner Helper",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        _settings.SerialPort.PortName = portName;
        _settings.SerialPort.BaudRate = int.Parse(
            ((System.Windows.Controls.ComboBoxItem)BaudCombo.SelectedItem).Content.ToString()!,
            CultureInfo.InvariantCulture);

        try
        {
            _lastFault = null;
            _source.Connect(_settings.SerialPort);
        }
        catch (Exception connectException)
        {
            MessageBox.Show(
                this,
                "连接失败 / Could not connect:\n\n" + connectException.Message,
                "Scanner Helper",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }

        Refresh();
    }

    private void OnDisconnectClicked(object sender, RoutedEventArgs e)
    {
        _source.Disconnect();
        Refresh();
    }

    private void OnOutputEnabledChanged(object sender, RoutedEventArgs e)
        => _output.IsEnabled = OutputEnabledCheck.IsChecked == true;

    private void OnAppendEnterChanged(object sender, RoutedEventArgs e)
    {
        lock (_gate)
        {
            _processor.AppendEnterAfterScan = AppendEnterCheck.IsChecked == true;
        }
    }

    private void OnToggleModeClicked(object sender, RoutedEventArgs e)
    {
        lock (_gate)
        {
            _modeManager.Toggle();
        }

        Refresh();
    }

    private void OnPauseClicked(object sender, RoutedEventArgs e)
    {
        lock (_gate)
        {
            _processor.Pause();
        }

        Refresh();
    }

    private void OnResumeClicked(object sender, RoutedEventArgs e)
    {
        lock (_gate)
        {
            _processor.Resume();
        }

        Refresh();
    }

    /// <summary>
    /// 中文：
    ///   把界面上填的固定位置规则应用下去。
    ///   规格 §13.4 允许运行中改规则；正在等待决定的那一枪不受影响
    ///   （见 ScanProcessor.UpdateSkuRules）。
    /// English:
    ///   Applies the fixed-position rule from the UI. Spec §13.4 allows changing rules while
    ///   running, and a pending decision is unaffected (see ScanProcessor.UpdateSkuRules).
    /// </summary>
    private void OnApplyRuleClicked(object sender, RoutedEventArgs e)
    {
        ApplyRule();
        Refresh();
    }

    private void ApplyRule()
    {
        if (!int.TryParse(StartPositionText.Text, out var startPosition)
            || !int.TryParse(LengthText.Text, out var length))
        {
            MessageBox.Show(
                this,
                "起始位与长度必须是数字 / Start position and length must be numbers.",
                "Scanner Helper",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        _settings.SkuParsing = new SkuParsingSettings
        {
            RuleType = SkuParsingRuleType.FixedPosition,
            StartPosition = startPosition,
            Length = length,
        };

        lock (_gate)
        {
            _processor.UpdateSkuRules(
                SkuParserFactory.Create(_settings.SkuParsing),
                SkuValidatorFactory.Create(_settings.SkuValidation));
        }
    }

    /// <summary>
    /// 中文：把状态搬到界面上。
    /// English: Moves the state onto the UI.
    /// </summary>
    private void Refresh()
    {
        while (_pendingRows.TryDequeue(out var row))
        {
            _rows.Insert(0, row);
        }

        while (_rows.Count > MaximumRows)
        {
            _rows.RemoveAt(_rows.Count - 1);
        }

        ScanPipelineState state;
        bool isPaused;
        string? pendingRawCode;

        lock (_gate)
        {
            state = _processor.State;
            isPaused = _processor.IsPaused;
            pendingRawCode = _processor.PendingError?.RawCode;
        }

        UpdateState(state, isPaused);
        UpdateHealth();

        ModeText.Text = _modeManager.CurrentMode == ScanMode.Sn ? "SN" : "SKU";
        PendingErrorText.Text = pendingRawCode ?? "无 / none";

        ConnectButton.IsEnabled = !_source.IsConnected;
        DisconnectButton.IsEnabled = _source.IsConnected;
        PortCombo.IsEnabled = !_source.IsConnected;
        BaudCombo.IsEnabled = !_source.IsConnected;

        PauseButton.IsEnabled = !isPaused;
        ResumeButton.IsEnabled = isPaused;

    }

    /// <summary>
    /// 中文：
    ///   刷新状态区。
    ///
    ///   ★ 暂停用醒目的黄色，而且一直显示着。暂停是非常态——它绕过了解析与校验
    ///     （决策 D-26），看不见它就会有人在暂停下干一整天，把原始码当成 SKU
    ///     录进了仓库系统。
    /// English:
    ///   Refreshes the state panel. PAUSED is shown in a conspicuous amber and stays visible:
    ///   it bypasses parsing and validation (decision D-26), and unseen it lets somebody work a
    ///   whole shift in it, filing raw codes into the warehouse system as if they were SKUs.
    /// </summary>
    private void UpdateState(ScanPipelineState state, bool isPaused)
    {
        var text = new StringBuilder();

        text.AppendLine(isPaused
            ? "★ 已暂停 —— 原样发出，不解析不校验"
              + " / PAUSED — raw output, no parsing"
            : _source.IsConnected
                ? "已连接 / Connected"
                : "未连接 / Not connected");

        text.AppendLine(CultureInfo.InvariantCulture, $"流水线 / Pipeline: {state}");
        text.AppendLine(CultureInfo.InvariantCulture, $"端口 / Port: {_source.PortName ?? "—"}");
        text.Append(CultureInfo.InvariantCulture,
            $"输出 / Output: {(_output.IsEnabled ? "已启用 / enabled" : "未启用 / disabled")}");

        StateText.Text = text.ToString();

        StateBorder.Background = isPaused
            ? new SolidColorBrush(Color.FromRgb(0xFF, 0xF4, 0xE5))
            : _source.IsConnected
                ? new SolidColorBrush(Color.FromRgb(0xE8, 0xF5, 0xE9))
                : new SolidColorBrush(Color.FromRgb(0xF6, 0xF6, 0xF6));
    }

    /// <summary>
    /// 中文：
    ///   刷新链路健康。
    ///
    ///   ★ 「多久没动静了」是唯一能看见「有人把枪切回键盘模式」的量。那之后端口
    ///     照样开着、不报任何错，而扫码枪正直接往业务软件里打字。超过阈值就变色，
    ///     并且明确写出这个可能——规格 §19.1 要求界面绝不显示一个它没有验证过的
    ///     运行状态，而「连接正常」在那种情形下正是一句没有依据的话。
    /// English:
    ///   Refreshes link health. Time since the last scan is the only quantity that reveals someone
    ///   having switched the scanner back to keyboard mode, after which the port stays open and
    ///   silent while the scanner types into the business application. Past the threshold the
    ///   panel changes color and names that possibility: spec §19.1 forbids presenting an
    ///   operational state that has not been verified, and "connected" is exactly such an
    ///   unfounded claim in that situation.
    /// </summary>
    private void UpdateHealth()
    {
        var text = new StringBuilder();
        var silence = _source.TimeSinceLastScan;
        var threshold = TimeSpan.FromMinutes(_settings.SerialPort.SilenceWarningMinutes);
        var isStale = _source.IsConnected && silence is { } elapsed && elapsed > threshold;

        text.AppendLine(CultureInfo.InvariantCulture, $"收到的枪数 / Scans   {_source.ScanCount}");
        text.AppendLine(silence is { } since
            ? string.Format(
                CultureInfo.InvariantCulture,
                "距上一枪 / Silent   {0:0} 秒 / s",
                since.TotalSeconds)
            : "距上一枪 / Silent   —（还没收到过 / none yet）");

        if (_lastFault is { } fault)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"故障 / Fault: {fault.Message}");
        }

        if (isStale)
        {
            text.Append(
                "★ 已经很久没有数据了。请确认扫码枪还在虚拟串口模式——"
                + "若有人扫配置码把它切回了键盘模式，它会直接往业务软件里打字，"
                + "而这里什么都不会报错。"
                + " / Silent for a long time. Confirm the scanner is still in virtual COM mode: if"
                + " someone switched it back to keyboard mode it types straight into the business"
                + " application while nothing here reports an error.");
        }

        HealthText.Text = text.ToString().TrimEnd();

        HealthBorder.Background = isStale
            ? new SolidColorBrush(Color.FromRgb(0xFF, 0xF4, 0xE5))
            : new SolidColorBrush(Color.FromRgb(0xF6, 0xF6, 0xF6));
    }

    /// <summary>
    /// 中文：列表里的一行。
    /// English: One row in the list.
    /// </summary>
    private sealed record ScanRow(
        int Index, string Time, string Mode, string RawCode, string Emitted, string Result);

    /// <summary>
    /// 中文：
    ///   输出服务：记录下来，并在启用时真的发出去。
    ///
    ///   ★ 「启用」这个开关放在输出这一层，而不是放在处理器里。处理器该不该发，
    ///     是业务规则（模式、解析、校验、暂停）；发出去的东西**去不去真实世界**，
    ///     是这一层的事。混在一起，就会出现「因为没启用输出所以走了不同的业务
    ///     分支」这种最难查的差异——诊断时看到的行为将不再是生产时的行为。
    /// English:
    ///   The output service: records everything, and actually emits when enabled.
    ///
    ///   The enable switch lives in the output layer rather than in the processor. Whether the
    ///   processor should emit is a business rule — mode, parsing, validation, paused; whether what
    ///   it emits reaches the real world belongs here. Mixing them produces the hardest kind of
    ///   discrepancy, where disabling output takes a different business branch and what is observed
    ///   while diagnosing is no longer what happens in production.
    /// </summary>
    private sealed class RecordingThenForwardingOutput : ScannerHelper.Core.Output.IKeyboardOutputService
    {
        private readonly SendInputKeyboardOutputService _real = new();

        /// <summary>中文：是否真的发出去。 English: Whether to actually emit.</summary>
        public bool IsEnabled { get; set; }

        /// <summary>中文：本枪发出的文本，null 表示没发。 English: This scan's emitted text, or null.</summary>
        public string? LastEmittedText { get; private set; }

        /// <summary>
        /// 中文：一枪开始，清掉上一枪的记录。
        /// English: A scan begins; clear the previous one's record.
        /// </summary>
        public void BeginScan(string rawCode)
        {
            _ = rawCode;
            LastEmittedText = null;
        }

        /// <inheritdoc />
        public void EmitText(string text)
        {
            LastEmittedText = text;

            if (IsEnabled)
            {
                _real.EmitText(text);
            }
        }

        /// <inheritdoc />
        public void EmitEnter()
        {
            LastEmittedText = (LastEmittedText ?? string.Empty) + "⏎";

            if (IsEnabled)
            {
                _real.EmitEnter();
            }
        }
    }
}
