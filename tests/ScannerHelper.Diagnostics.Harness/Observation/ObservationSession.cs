// =============================================================================
// ObservationSession.cs
//
// 中文：
//   一次 4a 观测的全部状态与逻辑。
//
//   界面只负责显示与按钮，所有"观测到了什么、意味着什么"的判断都在这里。
//   这样做的直接好处是：将来 4b 要换成拦截模式时，改的是这一个类，而不是
//   散落在窗口代码里的十几处。
//
//   ★ 本类不改变系统的任何行为。
//
//     钩子装了但每个事件都原样放行，Raw Input 只是登记接收。跑着它的机器
//     与没跑它的机器，在使用者看来完全一样——这正是规格 §4.3 说 4a"可以
//     安全地在生产机器上运行"的含义，也是它敢让工人在真实工位上跑一轮的
//     前提。
//
//   ★ 排空缓冲区的节奏是刻意选的。
//
//     钩子回调把事件写进无锁环形缓冲区就立刻返回；界面线程每隔一小段时间
//     来取一次。回调与界面之间没有任何同步点，因此界面再慢也不可能拖住
//     回调——而拖住回调超过 300 毫秒，Windows 就会悄悄摘掉钩子（规格 §19.1）。
//
//     取的间隔取 100 毫秒：足够密，看起来是实时的；又足够疏，不会让界面刷新
//     本身成为负担。
//
// English:
//   All the state and logic of one 4a observation run.
//
//   The window handles display and buttons; every judgment about what was observed and
//   what it means lives here. The immediate benefit is that switching to 4b's
//   intercepting mode later changes this one class rather than a dozen places scattered
//   through window code.
//
//   This class changes no system behavior. The hook is installed but passes every event
//   through unchanged, and Raw Input merely registers to receive. A machine running it
//   is indistinguishable from one that is not, which is what spec §4.3 means by 4a
//   being safe to run on a production machine — and the precondition for letting an
//   operator run it at a real station.
//
//   The drain cadence is deliberate. The hook callback writes into a lock-free ring
//   buffer and returns immediately; the UI thread collects periodically. There is no
//   synchronization point between them, so no amount of UI slowness can stall the
//   callback — and stalling it past 300 ms has Windows silently remove the hook
//   (spec §19.1). The interval is 100 ms: dense enough to look live, sparse enough that
//   refreshing is not itself a burden.
//
// 包含的成员 / Members in this file:
//   IsRunning / StartedAt / DroppedEventCount
//   Devices / Pairing
//   Start / Stop / HandleRawInput / Drain / Reset
//   ResolveDisplayName
// =============================================================================

using System.Diagnostics;
using ScannerHelper.Win32;
using ScannerHelper.Win32.Observation;

namespace ScannerHelper.Diagnostics.Harness.Observation;

/// <summary>
/// 中文：一次观测会话。只观测，不改变任何系统行为。
/// English: One observation run. Observes only; changes no system behavior.
/// </summary>
public sealed class ObservationSession : IDisposable
{
    /// <summary>
    /// 中文：界面来取事件的间隔。
    /// English: How often the UI drains events.
    /// </summary>
    public static readonly TimeSpan DrainInterval = TimeSpan.FromMilliseconds(100);

    private readonly ObservationBuffer _buffer = new();
    private readonly LowLevelKeyboardHook _hook;
    private readonly RawInputKeyboardListener _rawInput;
    private readonly RawInputDeviceResolver _resolver = new();
    private readonly Dictionary<nint, DeviceActivity> _devices = [];

    /// <summary>
    /// 中文：排空缓冲区时的临时接收数组。一次性分配、反复使用——每 100 毫秒
    ///       分配一个几千元素的数组，纯属给 GC 找事做，而 GC 暂停正是可能
    ///       顶穿钩子超时预算的东西之一（规格 §19.1）。
    /// English: The scratch array used when draining. Allocated once and reused —
    ///          allocating a several-thousand-element array every 100 ms is gratuitous
    ///          GC work, and a GC pause is among the things that can blow the hook's
    ///          timeout budget (spec §19.1).
    /// </summary>
    private readonly ObservedInputEvent[] _drainScratch = new ObservedInputEvent[4096];

    /// <summary>
    /// 中文：
    ///   构造观测会话。此时不安装任何东西，调用 <see cref="Start"/> 才开始观测。
    /// English:
    ///   Creates the session without installing anything; observation begins at
    ///   <see cref="Start"/>.
    /// </summary>
    public ObservationSession()
    {
        _hook = new LowLevelKeyboardHook(_buffer);
        _rawInput = new RawInputKeyboardListener(_buffer);
    }

    /// <summary>
    /// 中文：是否正在观测。
    /// English: Whether observation is running.
    /// </summary>
    public bool IsRunning { get; private set; }

    /// <summary>
    /// 中文：本次观测开始的时刻，用于报告。
    /// English: When this run started, for the report.
    /// </summary>
    public DateTimeOffset? StartedAt { get; private set; }

    /// <summary>
    /// 中文：因缓冲区写满而丢弃的事件数。不为零时本次数据不完整，报告会如实标注。
    /// English: Events dropped because the buffer filled. Non-zero means the data is
    ///          incomplete, and the report says so.
    /// </summary>
    public long DroppedEventCount => _buffer.DroppedCount;

    /// <summary>
    /// 中文：本次观测中出现过的全部设备。
    /// English: Every device seen during this run.
    /// </summary>
    public IReadOnlyCollection<DeviceActivity> Devices => _devices.Values;

    /// <summary>
    /// 中文：两条通道的配对结果与统计。
    /// English: Cross-channel pairing results and statistics.
    /// </summary>
    public ChannelPairing Pairing { get; } = new();

    /// <summary>
    /// 中文：
    ///   开始观测。
    ///   输入：windowHandle 用于接收 WM_INPUT 的窗口句柄。
    ///   输出：无。
    ///   步骤：
    ///     1. 先注册 Raw Input，再安装钩子；
    ///     2. 记下开始时刻。
    ///
    ///   步骤 1 的顺序有意为之。若先装钩子，在 Raw Input 注册完成之前的那一小段
    ///   时间里，钩子已经在记录事件，而这些事件永远等不到对家——它们会全部
    ///   堆积成"未配对"，让一个本该为零、一旦不为零就该警觉的指标从一开始就
    ///   带着噪声。反过来则不会：Raw Input 先就位，最多是它先记几条等钩子，
    ///   而那几条会在钩子装好后正常配上。
    /// English:
    ///   Starts observing on the given window.
    ///   Steps: (1) register Raw Input first, then install the hook; (2) record the
    ///   start time.
    ///
    ///   The order in step 1 is deliberate. Installing the hook first leaves a window in
    ///   which it records events that can never find a counterpart, and those pile up as
    ///   "unpaired" — putting noise into a metric that should read zero and be alarming
    ///   when it does not. The reverse is harmless: Raw Input may record a few events
    ///   ahead of the hook, and those pair up normally once the hook is in place.
    /// </summary>
    public void Start(IntPtr windowHandle)
    {
        if (IsRunning)
        {
            return;
        }

        // 步骤 1 / Step 1
        _rawInput.Register(windowHandle);
        _hook.Install();

        // 步骤 2 / Step 2
        StartedAt = DateTimeOffset.Now;
        IsRunning = true;
    }

    /// <summary>
    /// 中文：
    ///   停止观测，摘掉钩子。
    ///
    ///   Raw Input 的注册不主动撤销：撤销要再调一次 RegisterRawInputDevices
    ///   并带上 RIDEV_REMOVE，而多收几条不再处理的消息毫无代价，撤销失败反而
    ///   要多一条错误路径。真正必须干净收尾的是钩子——它会拖慢**全系统**的
    ///   每一次按键，留着不摘是不负责任的（规格 §19）。
    /// English:
    ///   Stops observing and removes the hook.
    ///
    ///   The Raw Input registration is deliberately not torn down: doing so needs
    ///   another RegisterRawInputDevices call with RIDEV_REMOVE, while receiving a few
    ///   more messages nobody processes costs nothing and an unregister failure would
    ///   only add an error path. The hook is what genuinely must be cleaned up — it
    ///   slows every keystroke system-wide, and leaving it installed is irresponsible
    ///   (spec §19).
    /// </summary>
    public void Stop()
    {
        if (!IsRunning)
        {
            return;
        }

        _hook.Uninstall();
        IsRunning = false;
    }

    /// <summary>
    /// 中文：
    ///   处理一条 WM_INPUT。
    ///   输入：rawInputHandle 消息的 lParam；timestamp 窗口过程在**最开头**
    ///         取得的时间戳。
    ///   输出：无。
    ///   调用方必须在识别出 WM_INPUT 后立刻取时间戳再调用本方法，理由见
    ///   RawInputKeyboardListener 的说明。
    /// English:
    ///   Handles one WM_INPUT. The caller must take the timestamp the moment it
    ///   recognizes the message and before calling in; see RawInputKeyboardListener for
    ///   why.
    /// </summary>
    public void HandleRawInput(IntPtr rawInputHandle, long timestamp)
        => _rawInput.HandleRawInput(rawInputHandle, timestamp);

    /// <summary>
    /// 中文：
    ///   取走缓冲区里累积的事件并更新统计。
    ///   输入：无。
    ///   输出：本次取到的事件，按时间顺序，供界面追加到日志。
    ///   步骤：
    ///     1. 从环形缓冲区排空到临时数组；
    ///     2. 逐个事件：Raw Input 通道的记入对应设备的活动；
    ///     3. 逐个事件：交给配对器；
    ///     4. 返回取到的事件列表。
    ///
    ///   步骤 2 只对 Raw Input 通道做，因为只有那条通道知道是哪台设备——
    ///   这正是整个 4a 要研究的不对称性本身。
    /// English:
    ///   Drains accumulated events and updates statistics, returning what was drained in
    ///   order for the UI log.
    ///   Steps: (1) drain the ring buffer; (2) attribute Raw Input events to their
    ///   device; (3) feed every event to the pairer; (4) return the list.
    ///
    ///   Step 2 applies to the Raw Input channel alone, because only that channel knows
    ///   the device — the very asymmetry 4a exists to study.
    /// </summary>
    public IReadOnlyList<ObservedInputEvent> Drain()
    {
        // 步骤 1 / Step 1
        var count = _buffer.Drain(_drainScratch);
        if (count == 0)
        {
            return [];
        }

        var drained = new List<ObservedInputEvent>(count);

        for (var index = 0; index < count; index++)
        {
            var observedEvent = _drainScratch[index];
            drained.Add(observedEvent);

            // 步骤 2 / Step 2
            if (observedEvent.Channel == InputChannel.RawInput)
            {
                if (!_devices.TryGetValue(observedEvent.DeviceHandle, out var activity))
                {
                    activity = new DeviceActivity(
                        observedEvent.DeviceHandle, _resolver.Resolve(observedEvent.DeviceHandle));
                    _devices[observedEvent.DeviceHandle] = activity;
                }

                activity.Record(observedEvent.Timestamp, observedEvent.IsKeyUp);
            }

            // 步骤 3 / Step 3
            Pairing.Accept(observedEvent);
        }

        // 步骤 4 / Step 4
        return drained;
    }

    /// <summary>
    /// 中文：清空全部已收集的数据，钩子与注册状态不变。
    ///       用于"换一把扫码枪重新测一轮"这种场景。
    /// English: Clears everything collected so far without touching the hook or the
    ///          registration — for "swap the scanner and measure again".
    /// </summary>
    public void Reset()
    {
        _devices.Clear();
        Pairing.Reset();
    }

    /// <summary>
    /// 中文：
    ///   枚举当前系统里连接着的全部键盘类设备。
    ///
    ///   与 <see cref="Devices"/> 不同：那个只包含"本次观测中真的按过键"的设备，
    ///   这个是"系统里现在接着的全部键盘"。回答 4a 第 4、6 个问题要用后者——
    ///   重新插拔或重启之后再枚举一次，对比设备路径是否保持不变；以及笔记本
    ///   内置键盘究竟有没有被 Raw Input 枚举出来。
    /// English:
    ///   Enumerates every keyboard-class device currently attached.
    ///
    ///   Distinct from <see cref="Devices"/>, which holds only devices that actually
    ///   produced a keystroke during this run. Answering 4a's fourth and sixth questions
    ///   needs this one: enumerate again after a replug or reboot and compare device
    ///   paths, and confirm whether the laptop's built-in keyboard is enumerated at all.
    /// </summary>
    public IReadOnlyList<(nint Handle, Core.Domain.ScannerDeviceIdentity Identity)>
        EnumerateAttachedKeyboards() => _resolver.EnumerateKeyboards();

    /// <summary>
    /// 中文：把设备句柄解析成显示用的名字，供事件日志使用。
    /// English: Resolves a device handle to a display name for the event log.
    /// </summary>
    public string ResolveDisplayName(nint deviceHandle)
        => _devices.TryGetValue(deviceHandle, out var activity)
            ? activity.DisplayName
            : $"0x{deviceHandle:X}";

    /// <summary>
    /// 中文：把 Stopwatch 时间戳差换算成毫秒，供界面显示相对时间。
    /// English: Converts a Stopwatch timestamp delta to milliseconds for display.
    /// </summary>
    public static double TicksToMilliseconds(long ticks)
        => ticks * 1000.0 / Stopwatch.Frequency;

    /// <inheritdoc />
    public void Dispose()
    {
        Stop();
        _hook.Dispose();
        _rawInput.Dispose();
    }
}
