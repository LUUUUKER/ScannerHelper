// =============================================================================
// ScannerSession.cs
//
// 中文：
//   程序的核心：拥有串口来源与处理器，把它们接成一条链路，并把状态以界面能用的
//   形式暴露出去。窗口只负责显示和转达点击，不持有任何业务状态。
//
//   ★ 为什么状态集中在这里，而不是散在窗口里。
//
//     规格 §11.3 要求最小化就是切到 Compact，§11.7 要求 Compact 里出错时自动
//     展开成 Full。也就是说**同一份状态要在两个窗口里显示**，而且窗口会被反复
//     创建和销毁。状态若跟着窗口走，"切一下窗口，暂停状态没了"这种事迟早发生
//     ——而暂停是安全阀，它悄悄失效是最糟的一种。
//
//   ★ 一把锁，而不是"全部投递到界面线程"。
//
//     扫描从串口读取线程进来，点击从界面线程进来，两者都要碰处理器。旧架构里
//     处理跑在钩子回调上，那条路径绝不能有锁；现在没有钩子了，一把锁就够了。
//
//     不把处理投递到界面线程，是因为那会让**输出的时机取决于界面忙不忙**，而
//     输出是这个产品唯一的出口。反过来：事件是在读取线程上抛出的，窗口自己
//     负责切回界面线程——界面的事让界面自己管。
//
//   ★ 「多久没收到扫码」是新架构下的心跳（ARCHITECTURE_CHANGE_SERIAL.md §5.1）。
//
//     有人扫一张配置码把枪切回键盘模式之后，串口会好好地开着、不报任何错、
//     也永远收不到数据，而扫码枪开始直接往业务软件里打字。除了"多久没动静了"，
//     没有别的量能看见这件事，所以它必须是会话对外暴露的一等状态。
//
// English:
//   The application's core: it owns the serial source and the processor, wires them together, and
//   exposes state in a form the UI can use. Windows only display and relay clicks; they hold no
//   business state.
//
//   State lives here because spec §11.3 makes minimize mean Compact and §11.7 makes an error in
//   Compact expand to Full — the same state is shown by two windows that are repeatedly created
//   and destroyed. State that followed the window would eventually produce "switch view and the
//   pause is gone", and a safety valve failing quietly is the worst kind.
//
//   One lock rather than marshalling everything to the UI thread: scans arrive on the reader
//   thread, clicks on the UI thread, and both touch the processor. Under the old architecture
//   processing ran in a hook callback where a lock was forbidden; with no hook, a lock is exactly
//   right. Processing is not moved to the UI thread because that would make output timing depend on
//   how busy the UI is, and output is this product's only exit. Conversely, events are raised on
//   the reader thread and each window marshals for itself — the UI's business is the UI's.
//
//   Time since the last scan is the heartbeat under the new architecture
//   (ARCHITECTURE_CHANGE_SERIAL.md §5.1). Once someone switches the scanner back to keyboard mode
//   the port stays open, raises nothing and never receives data while the scanner types into the
//   business application; nothing but that silence reveals it, so it is first-class state here.
//
// 包含的成员 / Members in this file:
//   Connect / Disconnect        串口连接
//   ToggleMode / Pause / Resume 模式与安全阀
//   ForceSend / Discard         待决错误的两个出口
//   ApplySettings               设置变更
//   Snapshot                    给界面看的一份即时快照
// =============================================================================

using System.IO;
using ScannerHelper.Core.Domain;
using ScannerHelper.Core.Input;
using ScannerHelper.Core.Modes;
using ScannerHelper.Core.Output;
using ScannerHelper.Core.Parsing;
using ScannerHelper.Core.Settings;
using ScannerHelper.Core.Validation;
using ScannerHelper.Win32;
using ScannerHelper.Win32.Serial;

namespace ScannerHelper.App;

/// <summary>
/// 中文：给界面看的一份即时快照。一次取齐，避免界面逐个属性去读而读到彼此
///       不一致的数字。
/// English: One instantaneous snapshot for the UI, taken all at once so that properties read one
///          by one cannot disagree.
/// </summary>
/// <param name="LastScanFailureKey">
/// 中文：最近一次失败的原因，已经是本地化的键；null 表示没有待决错误。
/// English: The most recent failure's reason as a localization key, or null when nothing is
///          pending.
/// </param>
public readonly record struct SessionSnapshot(
    bool IsConnected,
    string? PortName,
    ScanMode Mode,
    bool IsPaused,
    string? PendingErrorRawCode,
    string? LastScanFailureKey,
    string? LastScanRawCode,
    string? LastScanEmitted,
    TimeSpan? TimeSinceLastScan,
    string? LastFaultMessage);

/// <summary>
/// 中文：串口来源 + 处理器组成的一次会话。
/// English: One session: the serial source plus the processor.
/// </summary>
public sealed class ScannerSession : IDisposable
{
    private readonly object _gate = new();
    private readonly ModeManager _modeManager = new();
    private readonly SerialScannerInputSource _source = new();
    private readonly IKeyboardOutputService _output = new SendInputKeyboardOutputService();
    private readonly ScanProcessor _processor;

    private AppSettings _settings;
    private string? _pendingFailureKey;
    private string? _lastScanRawCode;
    private string? _lastScanEmitted;
    private string? _lastFaultMessage;
    private bool _isDisposed;

    /// <summary>
    /// 中文：
    ///   组装会话。
    ///   输入：settings 当前配置，不得为 null。此时不连接串口。
    /// English:
    ///   Assembles the session from the current settings; the port is not opened yet.
    /// </summary>
    /// <exception cref="ArgumentNullException">
    /// 中文：settings 为 null。 English: settings is null.
    /// </exception>
    public ScannerSession(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;

        _processor = new ScanProcessor(
            _modeManager,
            SkuParserFactory.Create(settings.SkuParsing),
            SkuValidatorFactory.Create(settings.SkuValidation),
            _output)
        {
            AppendEnterAfterScan = settings.AppendEnterAfterScan,
        };

        _processor.ScanProcessed += OnScanProcessed;
        _source.ScanReceived += OnScanReceived;
        _source.Faulted += OnSourceFaulted;
    }

    /// <summary>
    /// 中文：
    ///   有任何状态变化。**可能在串口读取线程上抛出**，订阅方自己负责切回界面线程。
    /// English:
    ///   Something changed. May be raised on the serial reader thread; subscribers marshal for
    ///   themselves.
    /// </summary>
    public event EventHandler? Changed;

    /// <summary>
    /// 中文：
    ///   出现了一个待决错误，需要工人决定（规格 §11.7：Compact 时要自动展开）。
    ///   **可能在读取线程上抛出。**
    /// English:
    ///   An error now awaits the operator (spec §11.7: Compact must expand for it). May be raised
    ///   on the reader thread.
    /// </summary>
    public event EventHandler? ErrorRaised;

    /// <summary>
    /// 中文：本机可用的串口。
    /// English: The COM ports available on this machine.
    /// </summary>
    public static IReadOnlyList<string> ListPorts() => SerialScannerInputSource.ListPorts();

    /// <summary>
    /// 中文：
    ///   按当前设置连接串口。
    ///   输出：连上了返回 null；失败返回本地化的标题键与原始异常。
    ///
    ///   ★ 失败不抛异常，而是把结果交回去。连不上是**正常情形**而不是缺陷：
    ///     枪没插、被别的程序占着、端口号变了，每一种在现场都会发生。让调用方
    ///     拿着结果去显示一条工人看得懂的提示，比让它接住异常再翻译一遍要直接。
    ///
    ///   ★ 原始异常一并带回。「端口被占用」和「端口不存在」这两句话本身就是最
    ///     有用的线索，只给一句"连接失败"等于把它盖掉。
    /// English:
    ///   Connects with the current settings, returning null on success or a localization key plus
    ///   the original exception on failure.
    ///
    ///   Failure is returned rather than thrown because failing to connect is a normal situation
    ///   rather than a defect: the scanner is unplugged, another program holds the port, the number
    ///   changed — each happens on site. Handing the caller a result to show beats making it catch
    ///   and translate an exception.
    ///
    ///   The original exception comes along: "the port is in use" and "no such port" are themselves
    ///   the most useful clue, and a bare "could not connect" would hide it.
    /// </summary>
    public (string TitleKey, Exception Failure)? Connect()
    {
        try
        {
            lock (_gate)
            {
                _lastFaultMessage = null;
                _source.Connect(_settings.SerialPort);
            }

            Changed?.Invoke(this, EventArgs.Empty);
            return null;
        }
        catch (UnauthorizedAccessException busy)
        {
            return ("PortBusyTitle", busy);
        }
        catch (FileNotFoundException missing)
        {
            return ("PortMissingTitle", missing);
        }
        catch (IOException notFound)
        {
            return ("PortMissingTitle", notFound);
        }
        catch (ArgumentException invalid)
        {
            return ("PortMissingTitle", invalid);
        }
    }

    /// <summary>
    /// 中文：断开串口。
    /// English: Closes the port.
    /// </summary>
    public void Disconnect()
    {
        lock (_gate)
        {
            _source.Disconnect();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 中文：切换 SN / SKU（规格 §5.2）。
    /// English: Toggles SN/SKU (spec §5.2).
    /// </summary>
    public void ToggleMode()
    {
        lock (_gate)
        {
            _modeManager.Toggle();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 中文：
    ///   进入暂停（决策 D-26）：跳过解析与校验，扫到的码原样发出。
    ///   规格 §5.7 要求这个动作**鼠标可达**，所以它由界面上的按钮调用，
    ///   而不依赖任何热键。
    /// English:
    ///   Enters PAUSED (decision D-26): parsing and validation are skipped and scans go out
    ///   unchanged. Spec §5.7 requires this to be mouse-reachable, so a button calls it and it
    ///   depends on no hotkey.
    /// </summary>
    public void Pause()
    {
        lock (_gate)
        {
            _processor.Pause();
            _pendingFailureKey = null;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 中文：离开暂停。
    /// English: Leaves PAUSED.
    /// </summary>
    public void Resume()
    {
        lock (_gate)
        {
            _processor.Resume();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 中文：强制发送待决错误里的原始码（规格 §10）。
    /// English: Force-sends the pending error's raw code (spec §10).
    /// </summary>
    public void ForceSend()
    {
        lock (_gate)
        {
            _processor.ForceSend();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 中文：丢弃待决错误，什么都不发。
    /// English: Discards the pending error, emitting nothing.
    /// </summary>
    public void Discard()
    {
        lock (_gate)
        {
            _processor.Cancel();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 中文：
    ///   应用新的设置。
    ///   串口参数变了就重连——端口号或波特率改了却还连着旧的，界面会显示"已连接"
    ///   而工人改的东西没有生效，那正是规格 §19.1 禁止的"显示一个没有验证过的
    ///   状态"。
    /// English:
    ///   Applies new settings, reconnecting when the port parameters changed. Leaving the old
    ///   connection open would show "connected" while the operator's change had no effect —
    ///   exactly the unverified state spec §19.1 forbids.
    /// </summary>
    /// <exception cref="ArgumentNullException">
    /// 中文：settings 为 null。 English: settings is null.
    /// </exception>
    public (string TitleKey, Exception Failure)? ApplySettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        bool needsReconnect;

        lock (_gate)
        {
            var previous = _settings.SerialPort;
            needsReconnect = _source.IsConnected
                && (previous.PortName != settings.SerialPort.PortName
                    || previous.BaudRate != settings.SerialPort.BaudRate
                    || previous.DataBits != settings.SerialPort.DataBits
                    || previous.Parity != settings.SerialPort.Parity
                    || previous.StopBits != settings.SerialPort.StopBits);

            _settings = settings;
            _processor.AppendEnterAfterScan = settings.AppendEnterAfterScan;
            _processor.UpdateSkuRules(
                SkuParserFactory.Create(settings.SkuParsing),
                SkuValidatorFactory.Create(settings.SkuValidation));
        }

        if (!needsReconnect)
        {
            Changed?.Invoke(this, EventArgs.Empty);
            return null;
        }

        Disconnect();
        return Connect();
    }

    /// <summary>
    /// 中文：取一份即时快照。
    /// English: Takes an instantaneous snapshot.
    /// </summary>
    public SessionSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new SessionSnapshot(
                IsConnected: _source.IsConnected,
                PortName: _source.PortName,
                Mode: _modeManager.CurrentMode,
                IsPaused: _processor.IsPaused,
                PendingErrorRawCode: _processor.PendingError?.RawCode,
                LastScanFailureKey: _pendingFailureKey,
                LastScanRawCode: _lastScanRawCode,
                LastScanEmitted: _lastScanEmitted,
                TimeSinceLastScan: _source.TimeSinceLastScan,
                LastFaultMessage: _lastFaultMessage);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _processor.ScanProcessed -= OnScanProcessed;
        _source.ScanReceived -= OnScanReceived;
        _source.Faulted -= OnSourceFaulted;
        _source.Dispose();
    }

    /// <summary>
    /// 中文：一枪到手。**在串口读取线程上执行。**
    /// English: A scan arrived; runs on the serial reader thread.
    /// </summary>
    private void OnScanReceived(object? sender, ScanReceivedEventArgs args)
    {
        lock (_gate)
        {
            _processor.OnScanReceived(args.RawCode);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 中文：
    ///   一枪处理完毕，记下界面要显示的东西。
    ///
    ///   ★ 失败原因存成**本地化的键**而不是成品文字。会话是业务层，它不该知道
    ///     当前是中文还是英文；更要紧的是，切换语言时那条错误还挂在界面上等工人
    ///     决定——存成文字的话，切了语言它就变成另一种语言的化石，而它旁边的
    ///     按钮已经跟着换了。
    /// English:
    ///   A scan finished; record what the UI needs.
    ///
    ///   The failure reason is stored as a localization key rather than finished text. The session
    ///   is business logic and should not know which language is in force — and more to the point,
    ///   the error is still on screen awaiting a decision when the language is switched: stored as
    ///   text it would become a fossil in the previous language beside buttons that had already
    ///   changed.
    /// </summary>
    private void OnScanProcessed(object? sender, ScanProcessedEventArgs args)
    {
        var raised = false;

        lock (_gate)
        {
            switch (args.Outcome)
            {
                case ScanOutcome.Emit emit:
                    _lastScanRawCode = emit.RawCode;
                    _lastScanEmitted = emit.Text;
                    _pendingFailureKey = null;
                    break;

                case ScanOutcome.EmitRawWhilePaused paused:
                    _lastScanRawCode = paused.RawCode;
                    _lastScanEmitted = paused.RawCode;
                    _pendingFailureKey = null;
                    break;

                case ScanOutcome.ParseFailed parseFailed:
                    _lastScanRawCode = parseFailed.Failure.RawCode;
                    _lastScanEmitted = null;
                    _pendingFailureKey = "ErrorParse";
                    raised = true;
                    break;

                case ScanOutcome.ValidationFailed validationFailed:
                    _lastScanRawCode = validationFailed.RawCode;
                    _lastScanEmitted = null;
                    _pendingFailureKey = "ErrorValidation";
                    raised = true;
                    break;

                case ScanOutcome.ForceSent forceSent:
                    _lastScanRawCode = forceSent.RawCode;
                    _lastScanEmitted = forceSent.RawCode;
                    _pendingFailureKey = null;
                    break;

                case ScanOutcome.Cancelled cancelled:
                    _lastScanRawCode = cancelled.RawCode;
                    _lastScanEmitted = null;
                    _pendingFailureKey = null;
                    break;
            }
        }

        if (raised)
        {
            ErrorRaised?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// 中文：
    ///   串口出错，多半是设备被拔掉。
    ///   记下来并通知界面——设备没了却不告诉工人，界面会继续显示"已连接"，
    ///   而那是一句没有依据的话（规格 §19.1）。
    /// English:
    ///   The port faulted, usually because the device was unplugged. Recorded and announced: a
    ///   vanished device nobody is told about leaves the UI claiming "connected", which is an
    ///   unfounded statement (spec §19.1).
    /// </summary>
    private void OnSourceFaulted(object? sender, Exception exception)
    {
        lock (_gate)
        {
            _lastFaultMessage = exception.Message;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
