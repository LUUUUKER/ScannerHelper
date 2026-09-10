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

    /// <summary>
    /// 中文：捕获跑在一条专用线程上，那条线程只有一个仅消息窗口、只泵消息、
    ///       不碰任何界面。第一轮把捕获放在界面线程上，扫码枪的段内间隔 p99
    ///       量到了 48 毫秒——扫码枪不可能有这种停顿，那是界面刷新把消息挤到
    ///       后面去的结果。详见 InputCaptureThread 的说明。
    /// English: Capture runs on a dedicated thread owning one message-only window that
    ///          pumps messages and touches no UI. With capture on the UI thread the
    ///          first run measured a 48 ms p99 for the scanner's within-burst interval —
    ///          impossible for a scanner, and caused by UI refresh pushing messages
    ///          behind it. See InputCaptureThread.
    /// </summary>
    private readonly InputCaptureThread _capture;

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
        _capture = new InputCaptureThread(_buffer);
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
    ///   输入：无。输出：无。
    ///
    ///   捕获线程会阻塞等待窗口与钩子就绪之后才返回，因此本方法返回时捕获
    ///   确实已经开始——不会出现"以为在录、其实前几个按键丢了"的情况。
    ///   安装失败会原样抛出 Win32 异常（最常见的原因是权限不匹配，
    ///   规格 §2.1 假设 A2）。
    /// English:
    ///   Starts observing. The capture thread blocks until its window and hook are
    ///   ready before returning, so capture really has begun by the time this returns —
    ///   there is no "thought it was recording while the first keystrokes were lost".
    ///   Installation failures surface as the original Win32 exception; the usual cause
    ///   is a privilege mismatch (spec §2.1, assumption A2).
    /// </summary>
    public void Start()
    {
        if (IsRunning)
        {
            return;
        }

        _capture.Start();

        StartedAt = DateTimeOffset.Now;
        IsRunning = true;
    }

    /// <summary>
    /// 中文：
    ///   停止观测。捕获线程会在自己那一侧摘钩子、销毁窗口、注销窗口类，
    ///   然后退出——钩子必须由安装它的线程卸载，窗口也必须由创建它的线程销毁。
    ///
    ///   钩子会拖慢**全系统**的每一次按键，因此收尾必须干净（规格 §19）。
    /// English:
    ///   Stops observing. The capture thread unhooks, destroys its window and
    ///   unregisters its class on its own side before exiting — a hook must be removed
    ///   by the thread that installed it and a window destroyed by its creator.
    ///
    ///   The hook slows every keystroke system-wide, so shutdown must be clean
    ///   (spec §19).
    /// </summary>
    public void Stop()
    {
        if (!IsRunning)
        {
            return;
        }

        _capture.Stop();
        IsRunning = false;
    }

    /// <summary>
    /// 中文：
    ///   取走缓冲区里累积的事件并更新统计。
    ///   输入：无。
    ///   输出：本次取到的事件，按时间顺序，供界面追加到日志。
    ///   步骤：
    ///     1. 从环形缓冲区排空到临时数组；
    ///     2. 逐个事件交给配对器；
    ///     3. 配成一对时，用**钩子那一侧的时间戳**记入对应设备的活动；
    ///     4. 返回取到的事件列表。
    ///
    ///   ★ 步骤 3 必须等到配对之后，而且必须用钩子的时间戳。
    ///
    ///     设备身份只有 Raw Input 那条通道知道，"这次按键什么时候发生"却只有
    ///     钩子那一侧问得准——钩子回调是在输入派发路径上被同步调用的，
    ///     而 `WM_INPUT` 要先排队再被消息循环取出。配对正好把两半凑齐：
    ///     设备取自 Raw Input，时刻取自钩子。
    ///
    ///     第一轮直接用 Raw Input 的时间戳算字符间隔，量到扫码枪段内 p99
    ///     48 毫秒——扫码枪不可能有这种停顿，那是消息排队的时间。
    /// English:
    ///   Drains accumulated events and updates statistics, returning what was drained in
    ///   order for the UI log.
    ///   Steps: (1) drain the ring buffer; (2) feed every event to the pairer; (3) on a
    ///   completed pair, attribute it to its device using the *hook-side* timestamp;
    ///   (4) return the list.
    ///
    ///   Step 3 must wait for pairing and must use the hook timestamp. Only Raw Input
    ///   knows the device, and only the hook side answers "when did this keystroke
    ///   happen" accurately — the callback is invoked synchronously on the dispatch
    ///   path while WM_INPUT queues first. Pairing supplies both halves: the device from
    ///   Raw Input, the moment from the hook.
    ///
    ///   The first run computed intervals straight from Raw Input timestamps and
    ///   measured a 48 ms within-burst p99 for the scanner — impossible for a scanner,
    ///   and in fact message-queue latency.
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
            var pair = Pairing.Accept(observedEvent);
            if (pair is not { } completedPair)
            {
                continue;
            }

            // 步骤 3 / Step 3 —— 设备取自 Raw Input，时刻取自钩子
            // Device from Raw Input, moment from the hook
            if (!_devices.TryGetValue(completedPair.DeviceHandle, out var activity))
            {
                activity = new DeviceActivity(
                    completedPair.DeviceHandle, _resolver.Resolve(completedPair.DeviceHandle));
                _devices[completedPair.DeviceHandle] = activity;
            }

            activity.Record(
                completedPair.HookTimestamp, completedPair.HookVirtualKey, completedPair.IsKeyUp);
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
        _capture.Dispose();
    }
}
