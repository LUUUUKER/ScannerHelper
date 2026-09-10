// =============================================================================
// SerialScannerInputSource.cs
//
// 中文：
//   Core 的 IScannerInputSource 在 Windows 上的实现：把串口读到的帧变成一次扫描。
//
//   ★ 这个类薄得几乎没有内容，而这正是架构变更的全部收益。
//
//     它取代的 Win32ScannerInputSource 有：钩子、Raw Input 注册、专用捕获线程、
//     仅消息窗口、消息循环、按键解码器、扣留-补发、关联器接线、回调时间预算、
//     跨线程投递……三百多行，每一行都是被真实故障逼出来的。
//
//     那些东西存在的唯一理由是「扫码枪伪装成键盘，我们要从键盘流里把它认出来」。
//     串口模式下这个前提没有了，于是它们全都没有了。剩下的只是：把帧的两端
//     去掉终止符，交出去。
//
//   ★ 一件仍然必须做对的事：时间。
//
//     TimeSinceLastScan 是新架构下的心跳（ARCHITECTURE_CHANGE_SERIAL.md §5.1）。
//     有人把枪切回键盘模式之后，这条链路会**安安静静地什么都不发生**——端口开着、
//     不报错、也没有数据，而扫码枪正直接往业务软件里打字。除了「多久没动静了」，
//     没有别的量能看见这件事。
//
//     用 Stopwatch 而不是挂钟：NTP 校时会让挂钟跳变，跳一次就可能让「10 分钟没
//     数据」变成「刚刚才有数据」，警告于是在最需要的时候消失。
//
// English:
//   The Windows implementation of Core's IScannerInputSource: serial frames become scans.
//
//   The class is almost empty, and that is the whole benefit of the architecture change. What it
//   replaces — Win32ScannerInputSource — carried a hook, a Raw Input registration, a dedicated
//   capture thread, a message-only window, a message loop, a keystroke decoder, withhold-and-
//   replay, correlator plumbing, a callback time budget and cross-thread posting: three hundred
//   lines, every one of them forced by a real failure. All of it existed for one reason, that the
//   scanner impersonated a keyboard and had to be picked out of the keyboard stream. On a serial
//   port that premise is gone, and so is all of it. What remains is stripping a terminator and
//   handing the text on.
//
//   One thing still has to be right: time. TimeSinceLastScan is the heartbeat under the new
//   architecture (ARCHITECTURE_CHANGE_SERIAL.md §5.1). Once someone switches the scanner back to
//   keyboard mode this link goes quietly inert — port open, no error, no data — while the scanner
//   types raw barcodes into the business application, and nothing but "how long has it been
//   silent" can see that.
//
//   Stopwatch rather than the wall clock: an NTP correction makes the wall clock jump, and one
//   jump can turn "ten minutes without data" into "data just arrived", removing the warning
//   exactly when it is needed.
//
// 包含的类型 / Types in this file:
//   SerialScannerInputSource
// =============================================================================

using System.Diagnostics;
using ScannerHelper.Core.Input;
using ScannerHelper.Core.Settings;

namespace ScannerHelper.Win32.Serial;

/// <summary>
/// 中文：从虚拟串口读扫码枪，实现 Core 的输入来源契约。
/// English: Reads the scanner from a virtual COM port, implementing Core's input source contract.
/// </summary>
public sealed class SerialScannerInputSource : IScannerInputSource
{
    private readonly SerialScannerReader _reader = new();

    private long _lastScanTimestamp;
    private bool _hasEverReceived;
    private bool _isDisposed;

    /// <summary>
    /// 中文：构造来源。此时不连接，调用 <see cref="Connect"/> 才开始。
    /// English: Creates the source without connecting; <see cref="Connect"/> does that.
    /// </summary>
    public SerialScannerInputSource()
    {
        _reader.FrameReceived += OnFrameReceived;
        _reader.Faulted += (_, exception) => Faulted?.Invoke(this, exception);
    }

    /// <inheritdoc />
    public event EventHandler<ScanReceivedEventArgs>? ScanReceived;

    /// <inheritdoc />
    public event EventHandler<Exception>? Faulted;

    /// <inheritdoc />
    public bool IsConnected => _reader.IsOpen;

    /// <inheritdoc />
    public TimeSpan? TimeSinceLastScan
        => _hasEverReceived
            ? Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastScanTimestamp))
            : null;

    /// <summary>
    /// 中文：累计收到的扫描次数。
    /// English: How many scans have arrived.
    /// </summary>
    public long ScanCount => _reader.FrameCount;

    /// <summary>
    /// 中文：当前连接的端口名，未连接时为 null。
    /// English: The connected port's name, or null.
    /// </summary>
    public string? PortName { get; private set; }

    /// <summary>
    /// 中文：
    ///   列出本机的串口。
    ///   把扫码枪切到虚拟串口模式之后，它会作为一个新端口出现；插拔前后各列一遍，
    ///   多出来的那个就是它——这比猜端口号可靠。
    /// English:
    ///   Lists the machine's COM ports. A scanner switched to virtual COM mode appears as a new
    ///   one; listing before and after replugging identifies it by what appeared, which beats
    ///   guessing the number.
    /// </summary>
    public static IReadOnlyList<string> ListPorts() => SerialScannerReader.ListPorts();

    /// <summary>
    /// 中文：
    ///   按给定参数连接。
    ///   输入：settings 串口参数；<see cref="SerialPortSettings.PortName"/> 不得为空。
    ///
    ///   ★ 打开失败时抛出的是原始异常。最常见的两种是「端口被别的程序占用」
    ///     和「端口不存在」，而这两句话本身就是最有用的线索，包装成「连接失败」
    ///     只会把它盖掉。串口是独占的，被占用是真实且常见的情形
    ///     （ARCHITECTURE_CHANGE_SERIAL.md §5.3）。
    /// English:
    ///   Connects with the given settings; PortName must be present.
    ///
    ///   A failure throws the original exception. The two usual causes — the port is held by
    ///   another program, or it does not exist — are themselves the most useful clue, and wrapping
    ///   them into "could not connect" would hide it. Serial ports are exclusive and contention is
    ///   real and common (ARCHITECTURE_CHANGE_SERIAL.md §5.3).
    /// </summary>
    /// <exception cref="ArgumentNullException">
    /// 中文：settings 为 null。 English: settings is null.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// 中文：端口名为空。 English: The port name is empty.
    /// </exception>
    public void Connect(SerialPortSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        _reader.Open(new SerialScannerOptions(
            PortName: settings.PortName ?? string.Empty,
            BaudRate: settings.BaudRate,
            DataBits: settings.DataBits,
            Parity: ToNativeParity(settings.Parity),
            StopBits: ToNativeStopBits(settings.StopBits)));

        PortName = settings.PortName;
    }

    /// <summary>
    /// 中文：断开连接。
    /// English: Disconnects.
    /// </summary>
    public void Disconnect()
    {
        _reader.Close();
        PortName = null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _reader.Dispose();
    }

    /// <summary>
    /// 中文：
    ///   一帧到手，变成一次扫描。
    ///
    ///   终止符已经在读取器那一层去掉了：它是传输层的东西，不是条码的一部分。
    ///   让它流进业务逻辑，会在「长度校验」这类地方莫名其妙地多出一个字符，
    ///   而那种错查起来极其费劲，因为它看不见。
    ///
    ///   ★ 事件在**读取线程**上触发，与接口的约定一致。订阅方自己负责切线程。
    /// English:
    ///   A frame arrived; it becomes a scan. The terminator was already stripped by the reader: it
    ///   belongs to the transport rather than the barcode, and letting it through would add an
    ///   inexplicable extra character to things like length validation — a bug that is painful to
    ///   find precisely because it is invisible.
    ///
    ///   Raised on the reader thread, as the interface states; subscribers marshal for themselves.
    /// </summary>
    private void OnFrameReceived(object? sender, SerialFrameEventArgs args)
    {
        Interlocked.Exchange(ref _lastScanTimestamp, Stopwatch.GetTimestamp());
        _hasEverReceived = true;

        ScanReceived?.Invoke(this, new ScanReceivedEventArgs(args.Text));
    }

    /// <summary>
    /// 中文：把 Core 的校验位枚举翻译成 System.IO.Ports 的。
    ///       翻译在这一层做，是因为 Core 不引用任何东西、必须能在 macOS 上构建
    ///       （规格 §16）——见 SerialPortSettings 的文件头。
    /// English: Translates Core's parity enum into System.IO.Ports'. The translation lives here
    ///          because Core references nothing and must build on macOS (spec §16); see
    ///          SerialPortSettings' header.
    /// </summary>
    private static System.IO.Ports.Parity ToNativeParity(SerialParity parity)
        => parity switch
        {
            SerialParity.Odd => System.IO.Ports.Parity.Odd,
            SerialParity.Even => System.IO.Ports.Parity.Even,
            SerialParity.Mark => System.IO.Ports.Parity.Mark,
            SerialParity.Space => System.IO.Ports.Parity.Space,
            _ => System.IO.Ports.Parity.None,
        };

    /// <summary>
    /// 中文：把 Core 的停止位枚举翻译成 System.IO.Ports 的。
    /// English: Translates Core's stop-bit enum into System.IO.Ports'.
    /// </summary>
    private static System.IO.Ports.StopBits ToNativeStopBits(SerialStopBits stopBits)
        => stopBits switch
        {
            SerialStopBits.OnePointFive => System.IO.Ports.StopBits.OnePointFive,
            SerialStopBits.Two => System.IO.Ports.StopBits.Two,
            _ => System.IO.Ports.StopBits.One,
        };
}
