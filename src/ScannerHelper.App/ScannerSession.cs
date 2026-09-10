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
    string? LastFaultMessage,
    string? SuggestedPortName,
    string? SuggestedPortDescription);

/// <summary>
/// 中文：串口来源 + 处理器组成的一次会话。
/// English: One session: the serial source plus the processor.
/// </summary>
public sealed class ScannerSession : IDisposable
{
    /// <summary>
    /// 中文：两次重连尝试之间至少隔这么久。枚举要走注册表，而断开状态下会一直重试。
    /// English: The minimum gap between reconnection attempts. Enumeration walks the registry and
    ///          retrying continues for as long as the port is closed.
    /// </summary>
    private static readonly TimeSpan ReconnectInterval = TimeSpan.FromSeconds(3);

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
    ///   串口报过错，等着在界面线程上收拾。
    ///
    ///   ★ 不在故障回调里直接断开，因为那个回调**跑在读取线程上**，而断开会
    ///     join 那条线程——自己 join 自己，要白等两秒才继续。设个标志，让重连
    ///     那一轮（跑在界面线程上）去做。
    /// English:
    ///   The port faulted and cleanup is owed on the UI thread.
    ///
    ///   Disconnecting inside the fault callback is not an option: that callback runs on the reader
    ///   thread and disconnecting joins that thread — joining itself, which simply burns the
    ///   two-second timeout. A flag lets the reconnect pass, which runs on the UI thread, do it.
    /// </summary>
    private volatile bool _isFaultPending;

    private DateTimeOffset _lastReconnectAttempt = DateTimeOffset.MinValue;
    private string? _suggestedPortName;
    private string? _suggestedPortDescription;

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
    /// 中文：
    ///   模式刚被切换。带上切换**之后**的模式。
    ///
    ///   ★ 单独一个事件，而不是让订阅方从 Changed 里自己比对。规格 §7 要求切换
    ///     模式时出声，而"出声"必须精确对应"这一次切换"——从状态变化里推断
    ///     会在每一次刷新时都要问一遍"是不是刚才变的"，而那种推断迟早会在某个
    ///     边界上多响一声或少响一声。
    /// English:
    ///   The mode was just toggled, carrying the mode after the change.
    ///
    ///   A separate event rather than leaving subscribers to diff Changed. Spec §7 requires a sound
    ///   on mode change, and the sound must correspond exactly to this toggle; inferring it from
    ///   state would ask "did it just change?" on every refresh, and such inference eventually
    ///   plays one sound too many or too few at some boundary.
    /// </summary>
    public event EventHandler<ScanMode>? ModeToggled;

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
        // ★ **绝不持着 _gate 去碰 _source。**
        //
        //   _source 的开与关都会 join 串口读取线程，而那条线程正是在
        //   OnScanReceived 里等 _gate 的那一个。持锁去关端口 = 界面等读取线程、
        //   读取线程等锁——一个典型的抱死。它不会永远卡住（join 有两秒上限），
        //   但界面会莫名其妙地僵两秒，而那两秒里工人多半会再点几下。
        //
        //   _gate 只保护处理器与模式；_source 自己有生命周期锁，不需要外面这一层。
        // Never touch _source while holding _gate. Opening and closing the source join the serial
        // reader thread, which is the very thread waiting on _gate inside OnScanReceived: the UI
        // waits for the reader and the reader waits for the lock. It does not hang forever (the
        // join has a two-second cap) but the UI freezes inexplicably for two seconds, and in those
        // two seconds the operator will click several more times. _gate guards the processor and
        // the mode; _source has a lifetime lock of its own and needs no second one.
        SerialPortSettings portSettings;

        lock (_gate)
        {
            _lastFaultMessage = null;
            portSettings = _settings.SerialPort;
        }

        try
        {
            _source.Connect(portSettings);

            // ★ 连上了却还没有绑定身份，就顺手记一次（规格 §6）。
            //
            //   "工人选了这个端口并且真的连上了"，本身就是绑定这个动作——再要求
            //   他去别处点一下"绑定"，只是多一步他不会明白为什么要做的操作。
            //
            //   这也让**已有的配置自动补上身份**：早先的版本只存了端口号，
            //   没有身份就没有自动重连，而工人不会知道要重新保存一次设置。
            //
            //   只在没有绑定时记。已经绑过就不动——那时端口变了可能意味着
            //   "换了一台设备"，那是需要工人确认的事，不该我们替他决定。
            // Record the identity when connected without one (spec §6). "The operator chose this
            // port and it genuinely opened" is the act of binding; asking them to click Bind
            // somewhere else only adds a step whose purpose they will not see. It also upgrades
            // existing configurations, which stored a port number and no identity: without one
            // there is no reconnection, and nobody would know to re-save their settings.
            //
            // Only when absent. An existing binding is left alone: a changed port may then mean a
            // different device, which is the operator's call rather than ours.
            var needsBinding = false;

            lock (_gate)
            {
                needsBinding = _settings.ScannerBinding is null;
            }

            if (needsBinding && portSettings.PortName is { } connectedPort)
            {
                RecordBinding(connectedPort);
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
        // 见 Connect：绝不持着 _gate 去碰 _source。
        // See Connect: never touch _source while holding _gate.
        _source.Disconnect();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 中文：切换 SN / SKU（规格 §5.2）。
    /// English: Toggles SN/SKU (spec §5.2).
    /// </summary>
    public void ToggleMode()
    {
        ScanMode mode;

        lock (_gate)
        {
            _modeManager.Toggle();
            mode = _modeManager.CurrentMode;
        }

        ModeToggled?.Invoke(this, mode);
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

        var wasConnected = _source.IsConnected;
        bool parametersChanged;

        lock (_gate)
        {
            var previous = _settings.SerialPort;
            parametersChanged =
                previous.PortName != settings.SerialPort.PortName
                || previous.BaudRate != settings.SerialPort.BaudRate
                || previous.DataBits != settings.SerialPort.DataBits
                || previous.Parity != settings.SerialPort.Parity
                || previous.StopBits != settings.SerialPort.StopBits;

            _settings = settings;
            _processor.AppendEnterAfterScan = settings.AppendEnterAfterScan;
            _processor.UpdateSkuRules(
                SkuParserFactory.Create(settings.SkuParsing),
                SkuValidatorFactory.Create(settings.SkuValidation));
        }

        var hasPort = !string.IsNullOrWhiteSpace(settings.SerialPort.PortName);

        // ★ 还没连上、而现在有端口了 —— 必须连。
        //
        //   这是第一次使用时最常见的一步：程序开起来是"未连接"，工人进设置选了
        //   COM3 再保存。若只在"本来就连着"的前提下才重连，那一步就什么都不会发生
        //   ——他刚做完唯一该做的事，界面却还写着未连接。
        // Not connected and a port is now configured — connect. This is the commonest first-run
        // step: the program opens disconnected, the operator picks COM3 and saves. Reconnecting
        // only when already connected would make that step do nothing, leaving the UI still saying
        // "not connected" right after they did the one thing they were supposed to do.
        if (!wasConnected)
        {
            return hasPort ? Connect() : null;
        }

        if (!parametersChanged)
        {
            Changed?.Invoke(this, EventArgs.Empty);
            return null;
        }

        Disconnect();
        return hasPort ? Connect() : null;
    }

    /// <summary>
    /// 中文：取一份即时快照。
    /// English: Takes an instantaneous snapshot.
    /// </summary>
    public SessionSnapshot Snapshot()
    {
        // 串口那几项在锁外读：它们由 _source 自己保护，而把它们放进临界区只会
        // 让这把锁的作用域悄悄变大——最终就会有人在持锁时调用 Connect。
        // The port properties are read outside the lock: _source guards them itself, and pulling
        // them into the critical section only widens this lock's scope until somebody eventually
        // calls Connect while holding it.
        var isConnected = _source.IsConnected;
        var portName = _source.PortName;
        var timeSinceLastScan = _source.TimeSinceLastScan;

        lock (_gate)
        {
            return new SessionSnapshot(
                IsConnected: isConnected,
                PortName: portName,
                Mode: _modeManager.CurrentMode,
                IsPaused: _processor.IsPaused,
                PendingErrorRawCode: _processor.PendingError?.RawCode,
                LastScanFailureKey: _pendingFailureKey,
                LastScanRawCode: _lastScanRawCode,
                LastScanEmitted: _lastScanEmitted,
                TimeSinceLastScan: timeSinceLastScan,
                LastFaultMessage: _lastFaultMessage,
                SuggestedPortName: _suggestedPortName,
                SuggestedPortDescription: _suggestedPortDescription);
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

        // 见 _isFaultPending 的说明：断开必须换一条线程去做。
        // See _isFaultPending: disconnecting has to happen on another thread.
        _isFaultPending = true;

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 中文：
    ///   周期性地做两件事：收拾故障、尝试自动重连。由界面的秒级定时器调用。
    ///
    ///   ★ 为什么必须有自动重连。
    ///
    ///     工人把枪拔下来扫远处的货、笔记本睡一觉醒来、USB 重新枚举——每一种都会
    ///     让串口断掉，而端口号还可能从 COM3 变成 COM7。没有自动重连，这些都变成
    ///     "扫码没反应了，去设置里重选端口"，也就是一通电话。
    ///
    ///   ★ 但**不是什么都自动连**。
    ///
    ///     置信度是 Exact 或 High（设备实例路径或序列号对上）才自动连——那时
    ///     我们确知是同一台设备。只靠 VID/PID 对上是 Low：那多半是个通用的
    ///     USB 转串口芯片，成千上万种设备共用同一个 VID/PID。自动连上去，很可能
    ///     连到的是另一个转接头，而界面会显示"已连接"、工人扫码却永远没反应——
    ///     那正是规格 §19.1 最忌讳的"看起来在工作"。
    ///
    ///     所以 Low 的时候不连，只把它作为**候选**报给界面，由工人点一下确认。
    ///     他知道自己刚把枪插到了哪个口，这个判断本来就该他来做。
    ///
    ///   ★ 节流到几秒一次：枚举要走注册表，而重连是会一直重试的。
    /// English:
    ///   Does two things periodically, called by the UI's one-second timer: clean up after a fault,
    ///   and try to reconnect.
    ///
    ///   Automatic reconnection is necessary because the operator unplugs the scanner to reach
    ///   distant goods, the laptop sleeps and wakes, USB re-enumerates — each drops the port, and
    ///   the number may move from COM3 to COM7. Without it, every one of those becomes "scanning
    ///   does nothing, go into Settings and pick the port again", which is a phone call.
    ///
    ///   But not everything is connected automatically. Exact or High confidence — the device
    ///   instance path or a serial number matched — means we know it is the same device. A VID/PID
    ///   match alone is Low: that is usually a generic USB-to-serial chip shared by thousands of
    ///   products, and connecting automatically would likely reach a different adapter while the UI
    ///   says "connected" and scanning never produces anything — spec §19.1's "looks like it is
    ///   working" in its purest form. So a Low match is offered to the UI as a suggestion for the
    ///   operator to confirm with one click: they know which socket they just used, and that
    ///   judgment was always theirs to make.
    ///
    ///   Throttled to a few seconds because enumeration walks the registry and reconnection retries
    ///   indefinitely.
    /// </summary>
    public void Tick()
    {
        if (_isDisposed)
        {
            return;
        }

        if (_isFaultPending)
        {
            _isFaultPending = false;
            Disconnect();
        }

        if (_source.IsConnected)
        {
            if (_suggestedPortName is not null)
            {
                _suggestedPortName = null;
                _suggestedPortDescription = null;
                Changed?.Invoke(this, EventArgs.Empty);
            }

            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (now - _lastReconnectAttempt < ReconnectInterval)
        {
            return;
        }

        _lastReconnectAttempt = now;
        TryReconnect();
    }

    /// <summary>
    /// 中文：连到界面上提示的那个候选端口，并把它记成新的绑定。
    /// English: Connects to the suggested port and records it as the new binding.
    /// </summary>
    public (string TitleKey, Exception Failure)? ConnectToSuggested()
    {
        if (_suggestedPortName is not { } portName)
        {
            return null;
        }

        lock (_gate)
        {
            _settings.SerialPort.PortName = portName;
        }

        var failure = Connect();

        if (failure is null)
        {
            RecordBinding(portName);
            _suggestedPortName = null;
            _suggestedPortDescription = null;
        }

        return failure;
    }

    /// <summary>
    /// 中文：
    ///   把当前端口背后的设备身份记进绑定设置（规格 §6）。
    ///   工人在设置里选定端口、或确认了候选之后调用。
    ///
    ///   ★ 记的是身份，不是端口号。端口号会随插在哪个 USB 口而变，
    ///     而身份不会——重连时是拿身份去找端口，不是反过来。
    /// English:
    ///   Records the identity behind the current port into the binding settings (spec §6), after
    ///   the operator picks a port or confirms a suggestion.
    ///
    ///   The identity is recorded rather than the port number: the number moves with the USB
    ///   socket while the identity does not, and reconnection resolves the port from the identity
    ///   rather than the other way round.
    /// </summary>
    public void RecordBinding(string portName)
    {
        if (SerialPortResolver.IdentifyPort(portName) is not { } identity)
        {
            return;
        }

        lock (_gate)
        {
            _settings.ScannerBinding = new ScannerBindingSettings
            {
                DevicePath = identity.DevicePath,
                VendorId = identity.VendorId,
                ProductId = identity.ProductId,
                FriendlyName = identity.FriendlyName,
                SerialNumber = identity.SerialNumber,
            };
        }
    }

    /// <summary>
    /// 中文：尝试一次重连。见 <see cref="Tick"/> 的说明。
    /// English: One reconnection attempt; see <see cref="Tick"/>.
    /// </summary>
    private void TryReconnect()
    {
        ScannerBindingSettings? binding;

        lock (_gate)
        {
            binding = _settings.ScannerBinding;
        }

        if (binding is null)
        {
            return;
        }

        var (portName, match) = SerialPortResolver.FindBoundPort(binding);

        if (portName is null)
        {
            SetSuggestion(null, null);
            return;
        }

        if (match.CanReconnectAutomatically)
        {
            lock (_gate)
            {
                _settings.SerialPort.PortName = portName;
            }

            SetSuggestion(null, null);
            Connect();
            return;
        }

        // 置信度不够，只提示不自动连——理由见 Tick。
        // Not confident enough to connect; suggest only. See Tick for why.
        SetSuggestion(portName, binding.FriendlyName);
    }

    private void SetSuggestion(string? portName, string? description)
    {
        if (_suggestedPortName == portName)
        {
            return;
        }

        _suggestedPortName = portName;
        _suggestedPortDescription = description;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
