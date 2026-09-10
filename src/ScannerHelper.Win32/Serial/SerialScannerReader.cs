// =============================================================================
// SerialScannerReader.cs
//
// 中文：
//   从虚拟串口读扫码枪。这是选项 A 的地基。
//
//   ★ 为什么要有这个类：Task 4b 的硬性关卡失败了。
//
//     2026-09-10 实测证明，按键一旦被 WH_KEYBOARD_LL 吞掉，Windows 就不再为它
//     产生 WM_INPUT（同一台机器：吞的时候来自扫码枪的 Raw Input 是 0 条，不吞
//     的时候是 487 条）。规格 §5.3 的「先扣留、等 Raw Input 揭示来源、再决定」
//     因此不可能实现——扣留这个动作本身销毁了做决定所需要的证据。
//     完整证据见 docs/TASK_4B_FINDING_20260910.md。
//
//     出路是不再让扫码枪扮演键盘：用配置条码把它切到 USB 虚拟串口模式。于是
//     它根本不打字，业务软件收不到任何原始字符——泄漏问题从源头消失，而不是
//     被我们拦下来。钩子、关联器、扣留补发、扫描码解码，连同它们带来的中文
//     输入法、CapsLock、按住重复、UIPI、静默摘钩等一整类问题，全部不再需要。
//
//   ★ 用专用读取线程，而不是 SerialPort.DataReceived。
//
//     DataReceived 由 SerialPort 内部的线程池触发，触发时机与缓冲区状态的关系
//     没有保证，而且在关闭端口时与回调之间的竞争很容易变成挂死。一条自己的
//     线程做阻塞读，行为是可预测的——这和捕获线程当初的理由是同一个：需要
//     确定性的时候，就自己拥有那条线程（见 MessageOnlyCaptureHost）。
//
//   ★ 默认置起 DTR 与 RTS。
//
//     不少 USB-CDC 设备在对方没有置起 DTR 之前不发数据。表现是"端口打开成功、
//     扫码却什么都收不到"——一个看起来像"串口方案行不通"的假象。这一行注释
//     写在这里，是为了让下一个遇到它的人不用重新查一遍。
//
// English:
//   Reads a scanner over a virtual COM port. This is Option A's foundation.
//
//   It exists because Task 4b's hard gate failed: measurement on 2026-09-10 proved that once a
//   keystroke is swallowed by a WH_KEYBOARD_LL callback, Windows produces no WM_INPUT for it
//   (same machine: zero Raw Input events from the scanner while swallowing, 487 while not). Spec
//   §5.3's withhold-correlate-decide is therefore impossible — withholding destroys the evidence
//   the decision needs. Full evidence in docs/TASK_4B_FINDING_20260910.md.
//
//   The way out is to stop having the scanner impersonate a keyboard: a configuration barcode
//   switches it to USB virtual COM. It then types nothing at all, the business application
//   receives no raw characters, and leakage disappears at the source rather than being
//   intercepted. The hook, the correlator, withhold-and-replay and scan-code decoding all become
//   unnecessary, and with them the whole class of IME, CapsLock, key-repeat, UIPI and
//   silent-unhook problems.
//
//   A dedicated reader thread is used rather than SerialPort.DataReceived, whose thread-pool
//   callbacks make no guarantee about buffer state and race with closing the port in ways that
//   readily hang. A thread of one's own doing blocking reads behaves predictably — the same
//   reasoning as the capture thread: when determinism is needed, own the thread (see
//   MessageOnlyCaptureHost).
//
//   DTR and RTS are asserted by default. Many USB-CDC devices send nothing until DTR is
//   asserted, presenting as "the port opened but scanning produces nothing" — an illusion that
//   looks exactly like "the serial approach does not work". This note is here so the next person
//   to hit it does not have to find out again.
//
// 包含的类型 / Types in this file:
//   SerialScannerOptions   打开端口所需的参数
//   SerialChunkEventArgs   一次读到的原始字节
//   SerialFrameEventArgs   按终止符切出来的一帧
//   SerialScannerReader    读取器
// =============================================================================

using System.IO.Ports;
using System.Text;

namespace ScannerHelper.Win32.Serial;

/// <summary>
/// 中文：打开串口所需的参数。默认值是扫码枪出厂最常见的 9600 8N1。
/// English: What opening a port needs. The defaults are 9600 8N1, the most common factory
///          setting on scanners.
/// </summary>
/// <param name="PortName">中文：端口名，例如 COM5。 English: The port name, such as COM5.</param>
/// <param name="BaudRate">
/// 中文：波特率。**填错不会报错，只会收到乱码**——这是串口最经典的坑，所以
///       诊断工具必须把原始字节显示出来，让人一眼看出是乱码而不是设备坏了。
/// English: The baud rate. A wrong value raises no error and simply yields garbage — the classic
///          serial pitfall, which is why the diagnostic tool must show the raw bytes so a person
///          can see garbage rather than conclude the device is broken.
/// </param>
public readonly record struct SerialScannerOptions(
    string PortName,
    int BaudRate = 9600,
    int DataBits = 8,
    Parity Parity = Parity.None,
    StopBits StopBits = StopBits.One);

/// <summary>
/// 中文：一次读到的原始字节。诊断用——判断波特率对不对、终止符是什么，
///       全靠看这个。
/// English: One chunk of raw bytes as read. Diagnostic: whether the baud rate is right and what
///          the terminator is can only be seen here.
/// </summary>
public sealed class SerialChunkEventArgs(byte[] bytes) : EventArgs
{
    /// <summary>中文：读到的字节。 English: The bytes read.</summary>
    public byte[] Bytes { get; } = bytes;
}

/// <summary>
/// 中文：按终止符切出来的一帧，也就是一枪。
/// English: One frame delimited by the terminator — one scan.
/// </summary>
public sealed class SerialFrameEventArgs(string text, string terminator) : EventArgs
{
    /// <summary>中文：帧内容，不含终止符。 English: The frame's content, terminator excluded.</summary>
    public string Text { get; } = text;

    /// <summary>
    /// 中文：这一帧是被什么结束的：CR、LF、CRLF，或"超长"。
    /// English: What ended this frame: CR, LF, CRLF, or an overlong frame.
    /// </summary>
    public string Terminator { get; } = terminator;
}

/// <summary>
/// 中文：从虚拟串口读扫码枪。
/// English: Reads a scanner over a virtual COM port.
/// </summary>
public sealed class SerialScannerReader : IDisposable
{
    /// <summary>
    /// 中文：
    ///   一帧的字符数上限。超过就强制切帧并标记。
    ///
    ///   ★ 没有上限的话，一个从不发终止符的设备会让缓冲区一直涨到内存耗尽。
    ///     而"配置成不发终止符"是扫码枪一个很常见的出厂设置。
    /// English:
    ///   The maximum characters in one frame; beyond it the frame is cut and marked.
    ///
    ///   Without a limit a device that never sends a terminator grows the buffer until memory
    ///   runs out — and "no terminator" is a common factory setting on scanners.
    /// </summary>
    private const int MaximumFrameLength = 4096;

    /// <summary>
    /// 中文：一次阻塞读的超时。超时不是错误，只是"这段时间没人扫码"，
    ///       读线程借此得到一个检查退出标志的机会。
    /// English: One blocking read's timeout. A timeout is not an error but "nobody scanned just
    ///          now", and it is what gives the reader thread a chance to check the stop flag.
    /// </summary>
    private const int ReadTimeoutMilliseconds = 200;

    /// <summary>
    /// 中文：
    ///   等读线程自己走的上限。读超时 200ms，正常情况下它最多 200ms 就看见停止
    ///   标志退出了，给到 1 秒是宽裕的余量。
    /// English:
    ///   How long to wait for the reader thread to leave on its own. With a 200 ms read timeout it
    ///   normally sees the stop flag within 200 ms; one second is generous headroom.
    /// </summary>
    private static readonly TimeSpan ReaderExitTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// 中文：
    ///   关端口这一步本身的上限。见 Close 里的说明——超时之后宁可漏掉一个端口
    ///   对象，也不能让工人点了退出而程序不退。
    /// English:
    ///   The deadline on closing the port itself. See Close: past it, leaking a port object is
    ///   preferable to a program that will not exit after the operator asked it to.
    /// </summary>
    private static readonly TimeSpan PortCloseDeadline = TimeSpan.FromSeconds(2);

    private readonly byte[] _readBuffer = new byte[1024];
    private readonly StringBuilder _frame = new();
    private readonly object _lifetimeLock = new();

    private SerialPort? _port;
    private Thread? _readerThread;
    private volatile bool _stopRequested;
    private bool _isDisposed;

    private long _byteCount;
    private long _frameCount;
    private long _errorCount;

    /// <summary>
    /// 中文：读到一段原始字节。**在读取线程上触发**，订阅方必须自行切回界面线程。
    /// English: Raw bytes were read. Raised on the reader thread; subscribers must marshal.
    /// </summary>
    public event EventHandler<SerialChunkEventArgs>? ChunkReceived;

    /// <summary>
    /// 中文：切出了一帧。**在读取线程上触发**。
    /// English: A frame was completed. Raised on the reader thread.
    /// </summary>
    public event EventHandler<SerialFrameEventArgs>? FrameReceived;

    /// <summary>
    /// 中文：读取出错。端口被拔掉时也会走这里。**在读取线程上触发**。
    /// English: A read failed, including when the device is unplugged. Raised on the reader
    ///          thread.
    /// </summary>
    public event EventHandler<Exception>? Faulted;

    /// <summary>中文：端口是否打开着。 English: Whether the port is open.</summary>
    public bool IsOpen => _port?.IsOpen == true;

    /// <summary>中文：累计读到的字节数。 English: Bytes read so far.</summary>
    public long ByteCount => Interlocked.Read(ref _byteCount);

    /// <summary>中文：累计切出的帧数。 English: Frames completed so far.</summary>
    public long FrameCount => Interlocked.Read(ref _frameCount);

    /// <summary>中文：累计读取错误数。 English: Read errors so far.</summary>
    public long ErrorCount => Interlocked.Read(ref _errorCount);

    /// <summary>
    /// 中文：
    ///   列出本机的串口。
    ///   把扫码枪切到虚拟串口模式之后，它会作为一个新端口出现在这里；插拔一次
    ///   前后各列一遍，多出来的那个就是它——这比猜端口号可靠。
    /// English:
    ///   Lists the machine's COM ports. A scanner switched to virtual COM mode appears here as a
    ///   new port; listing once before and once after replugging identifies it by what appeared,
    ///   which beats guessing the number.
    /// </summary>
    public static IReadOnlyList<string> ListPorts()
    {
        var ports = SerialPort.GetPortNames();
        Array.Sort(ports, StringComparer.OrdinalIgnoreCase);
        return ports;
    }

    /// <summary>
    /// 中文：
    ///   打开端口并开始读。
    ///   步骤：
    ///     1. 建立并配置端口；
    ///     2. 置起 DTR / RTS（见文件头：不置起的话很多设备根本不发数据）；
    ///     3. 打开；
    ///     4. 起一条后台读取线程。
    ///
    ///   线程是后台线程：忘了 Close 也不会让进程退不出去。
    /// English:
    ///   Opens the port and starts reading: configure, assert DTR/RTS (see the file header —
    ///   many devices send nothing otherwise), open, then start a background reader thread. The
    ///   thread is a background thread so a missed Close cannot keep the process alive.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// 中文：端口名为空。 English: The port name is empty.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">
    /// 中文：端口已被别的程序占用。 English: The port is already held by another program.
    /// </exception>
    /// <exception cref="IOException">
    /// 中文：端口不存在或打开失败。 English: The port does not exist, or opening failed.
    /// </exception>
    public void Open(SerialScannerOptions options)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        if (string.IsNullOrWhiteSpace(options.PortName))
        {
            throw new ArgumentException(
                "必须指定端口名。 A port name is required.", nameof(options));
        }

        lock (_lifetimeLock)
        {
            if (IsOpen)
            {
                return;
            }

            _stopRequested = false;
            _frame.Clear();

            // 步骤 1 / Step 1
            var port = new SerialPort(
                options.PortName,
                options.BaudRate,
                options.Parity,
                options.DataBits,
                options.StopBits)
            {
                ReadTimeout = ReadTimeoutMilliseconds,

                // 步骤 2 / Step 2 —— 见文件头 / see the file header
                DtrEnable = true,
                RtsEnable = true,
            };

            // 步骤 3 / Step 3
            port.Open();
            _port = port;

            // 步骤 4 / Step 4
            _readerThread = new Thread(ReadLoop)
            {
                IsBackground = true,
                Name = "ScannerHelper serial reader",
            };
            _readerThread.Start();
        }
    }

    /// <summary>
    /// 中文：
    ///   停止读取并关闭端口。
    ///   先置停止标志再关端口：读线程正阻塞在读上，关端口会让那次读抛异常，
    ///   而有了标志，读线程就知道那个异常是我们自己造成的，不该报成故障。
    /// English:
    ///   Stops reading and closes the port. The stop flag is set before closing: the reader
    ///   thread is blocked in a read, closing makes that read throw, and the flag tells the
    ///   thread the exception was ours rather than a fault worth reporting.
    /// </summary>
    public void Close()
    {
        lock (_lifetimeLock)
        {
            _stopRequested = true;

            // ★ 顺序是**先等读线程走干净，再关端口**——反过来会挂死。
            //
            //   这一条是现场用"点了退出，程序卡死"换来的。
            //
            //   SerialPort.Close 会等自己内部的读操作结束。而我们的读线程此刻正
            //   阻塞在 port.Read 里，于是关端口的那条线程（界面线程）就开始等一次
            //   它无权结束的读。在 USB 转串口设备上，尤其是**设备已经被拔掉**之后，
            //   那一等可能永远不结束——.NET 的 System.IO.Ports 在这一点上有长期
            //   已知的问题。表现就是工人点了 × 之后整个窗口白掉。
            //
            //   而反过来就没有这个问题：停止标志已经置上，读最多 200ms 就超时一次，
            //   读线程借那次超时看见标志自己退出。等它走了再关，端口上没有任何
            //   在飞的操作，Close 是瞬间的。
            //
            // The order is: let the reader leave first, then close the port. The other way round
            // hangs, and this was paid for on site with "the program freezes when you close it".
            //
            // SerialPort.Close waits for its own read to finish, and our reader thread is at that
            // moment blocked in port.Read — so the closing thread (the UI thread) starts waiting on
            // a read it has no power to end. On a USB-serial adapter, and especially once the device
            // has been unplugged, that wait may never end; .NET's System.IO.Ports has a long-known
            // problem here. What the operator sees is the window going white after clicking X.
            //
            // The other order has none of that: the stop flag is already set, a read times out
            // within 200 ms, and the reader sees the flag and leaves. Once it has, nothing is in
            // flight on the port and Close returns immediately.
            // ★ 不能 Join 自己。读线程走 Faulted 那条路时，事件处理方原则上不该在
            //   那条线程上关端口（会话是把故障挂起来交给 Tick 处理的，正是为此），
            //   但这里多一行判断就能把"一旦有人这么做就永久冻住"整类问题挡掉——
            //   Thread.Join 在自己这条线程上会一直等一个永远不会发生的结束。
            // A thread must not join itself. The reader reaches Faulted on its own thread and its
            // handler is not supposed to close the port from there — the session defers faults to
            // Tick precisely for this — but one line here rules out an entire class of permanent
            // freeze, since Thread.Join on the current thread waits for an end that cannot come.
            if (_readerThread is { } readerThread && readerThread != Thread.CurrentThread)
            {
                readerThread.Join(ReaderExitTimeout);
            }

            _readerThread = null;

            var port = _port;
            _port = null;

            if (port is not null)
            {
                CloseWithDeadline(port);
            }
        }
    }

    /// <summary>
    /// 中文：
    ///   关端口，但**有上限**。
    ///
    ///   ★ 读线程已经走了，正常情况下这一步是瞬间的，这条兜底路径根本不会走到。
    ///     它存在是因为设备被拔掉时 SerialPort 的收尾仍可能挂住，而那时的取舍
    ///     很清楚：漏掉一个端口对象没有任何实际后果——进程结束时操作系统会收回
    ///     句柄——而一个关不掉的程序会让工人以为整台机器死了。
    ///
    ///   ★ 用一条一次性的后台线程去关，而不是 Task。这条线程有可能永远不返回，
    ///     那样会占住一个线程池线程不放；后台线程则不会拦住进程退出。
    /// English:
    ///   Closes the port under a deadline.
    ///
    ///   With the reader already gone this is instantaneous and the fallback never runs. It exists
    ///   because SerialPort's teardown can still hang for an unplugged device, and the trade there
    ///   is clear: a leaked port object has no practical consequence — the OS reclaims the handle
    ///   when the process ends — while a program that will not close reads to the operator as a
    ///   dead machine.
    ///
    ///   A one-shot background thread rather than a Task: this work may never return, which would
    ///   hold a thread-pool thread forever, whereas a background thread cannot keep the process
    ///   alive.
    /// </summary>
    private static void CloseWithDeadline(SerialPort port)
    {
        var finished = new ManualResetEventSlim(false);

        var closer = new Thread(() =>
        {
            try
            {
                port.Close();
                port.Dispose();
            }
            catch (Exception)
            {
                // 关一个已经被拔掉的端口会抛各种异常。这里没有任何可做的事，
                // 也没有任何值得报告的事——目标就是它现在关着。
                // Closing an already-unplugged port throws in several ways. There is nothing to do
                // and nothing worth reporting: the goal was for it to be closed, and it is.
            }
            finally
            {
                finished.Set();
            }
        })
        {
            IsBackground = true,
            Name = "ScannerHelper serial close",
        };

        closer.Start();
        finished.Wait(PortCloseDeadline);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        Close();
    }

    /// <summary>
    /// 中文：
    ///   读取线程主体：阻塞读 → 报告原始字节 → 切帧。
    ///
    ///   超时不算错误，只是这段时间没人扫码，它同时是检查退出标志的机会。
    ///   其它异常先看退出标志：是我们自己关的端口就安静退出，否则报告出去
    ///   ——设备被拔掉走的正是这条路，而"扫码枪被拔了"必须让界面知道，
    ///   不能装作还在正常工作（规格 §19.1 的同一条原则）。
    /// English:
    ///   The reader thread: blocking read, report the raw bytes, then frame them.
    ///
    ///   A timeout is not an error but simply nobody scanning, and it doubles as the chance to
    ///   check the stop flag. Any other exception consults that flag first: our own Close exits
    ///   quietly, anything else is reported — an unplugged device arrives this way, and "the
    ///   scanner was unplugged" must reach the UI rather than being passed off as normal
    ///   operation (spec §19.1's principle, applied here).
    /// </summary>
    private void ReadLoop()
    {
        while (!_stopRequested)
        {
            var port = _port;
            if (port is null || !port.IsOpen)
            {
                return;
            }

            int bytesRead;

            try
            {
                bytesRead = port.Read(_readBuffer, 0, _readBuffer.Length);
            }
            catch (TimeoutException)
            {
                continue;
            }
            catch (Exception readException)
            {
                if (_stopRequested)
                {
                    return;
                }

                Interlocked.Increment(ref _errorCount);
                Faulted?.Invoke(this, readException);
                return;
            }

            if (bytesRead <= 0)
            {
                continue;
            }

            Interlocked.Add(ref _byteCount, bytesRead);

            var chunk = new byte[bytesRead];
            Array.Copy(_readBuffer, chunk, bytesRead);
            ChunkReceived?.Invoke(this, new SerialChunkEventArgs(chunk));

            AccumulateFrames(chunk);
        }
    }

    /// <summary>
    /// 中文：
    ///   把字节喂进帧缓冲，遇到终止符就切一帧。
    ///
    ///   ★ CR、LF、CRLF 三种都当作终止符，且 CRLF 只算一次。
    ///     扫码枪的终止符是可配置的，出厂设置各家不同，而"到底是哪一种"这件事
    ///     不该由本类替使用者决定——三种都接受，同时把实际收到的那一种报出去，
    ///     让人看得见自己的枪到底在发什么。
    ///
    ///   ★ 按字节直接转字符（Latin-1 式），不做任何编码猜测。
    ///     条码内容在实践中是 ASCII，而这里的首要任务是**如实呈现设备发了什么**。
    ///     猜编码会把一个"波特率填错了"的乱码悄悄变成另一种乱码，掩盖真正的原因。
    /// English:
    ///   Feeds bytes into the frame buffer, cutting a frame at each terminator.
    ///
    ///   CR, LF and CRLF are all accepted as terminators, with CRLF counting once. A scanner's
    ///   terminator is configurable and factory settings differ by vendor, and which one it is
    ///   should not be this class's decision: accept all three and report which actually arrived,
    ///   so a person can see what their scanner really sends.
    ///
    ///   Bytes become characters directly, Latin-1 style, with no encoding guesswork. Barcode
    ///   content is ASCII in practice, and the first duty here is to show faithfully what the
    ///   device sent; guessing an encoding would quietly turn "the baud rate is wrong" garbage
    ///   into different garbage and hide the real cause.
    /// </summary>
    private void AccumulateFrames(byte[] chunk)
    {
        for (var index = 0; index < chunk.Length; index++)
        {
            var value = chunk[index];

            if (value is (byte)'\r' or (byte)'\n')
            {
                var terminator = value == '\r' ? "CR" : "LF";

                // CRLF 只算一次终止符 / CRLF counts as one terminator
                if (value == '\r'
                    && index + 1 < chunk.Length
                    && chunk[index + 1] == (byte)'\n')
                {
                    terminator = "CRLF";
                    index++;
                }

                CompleteFrame(terminator);
                continue;
            }

            _frame.Append((char)value);

            if (_frame.Length >= MaximumFrameLength)
            {
                CompleteFrame("超长 / overlong");
            }
        }
    }

    /// <summary>
    /// 中文：切出一帧。空帧会被丢掉——CRLF 拆在两次读取里时会产生空帧，
    ///       那不是一枪扫描。
    /// English: Cuts a frame. Empty frames are dropped: a CRLF split across two reads produces
    ///          one, and it is not a scan.
    /// </summary>
    private void CompleteFrame(string terminator)
    {
        if (_frame.Length == 0)
        {
            return;
        }

        var text = _frame.ToString();
        _frame.Clear();

        Interlocked.Increment(ref _frameCount);
        FrameReceived?.Invoke(this, new SerialFrameEventArgs(text, terminator));
    }
}
