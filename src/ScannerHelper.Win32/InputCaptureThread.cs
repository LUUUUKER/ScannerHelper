// =============================================================================
// InputCaptureThread.cs
//
// 中文：
//   Task 4a 的观测捕获：装上钩子与 Raw Input，把两条通道的事件原样写进观测
//   缓冲区，**不拦截任何东西**。
//
//   线程、仅消息窗口、消息循环这些机制不在本文件里，都在 MessageOnlyCaptureHost
//   ——4b 的拦截流水线用的是同一份（理由见那个文件与 ICaptureThreadWork）。
//   本文件只剩下 4a 独有的那一件事：观测。
//
//   ★ "只观测"是本类唯一的安全性质，而它由**结构**保证，不靠注释。
//
//     4a 的诊断工具敢在真实生产机上运行，全部理由就是它没有任何一条通向
//     "吞掉按键"的代码路径。所以这里走的是两个 CreateObserveOnly 工厂：
//     它们返回的回调恒为放行，且那个 lambda 不接受任何外部输入，调用方
//     想让它吞掉某个键也无从下手。
//
//     换成普通构造函数、在这里传一个"永远返回 PassThrough"的 handler，
//     读起来一样，但那时安全性就变成了"这个调用点恰好没写 Swallow"——
//     一次重构、一次手滑就没了，而代价是工人的笔记本键盘。
//
// English:
//   Task 4a's observing capture: install the hook and Raw Input, record both channels'
//   events into an observation buffer, and intercept nothing.
//
//   The thread, the message-only window and the message loop are not in this file but in
//   MessageOnlyCaptureHost, which 4b's interception pipeline shares (see that file and
//   ICaptureThreadWork). What remains here is the one thing specific to 4a: observing.
//
//   "Observes only" is this class's single safety property and it is structural rather than
//   commented. 4a's diagnostic tool is safe to run on a live production machine for exactly
//   one reason: it has no code path to swallowing a keystroke. Hence the two CreateObserveOnly
//   factories, whose callbacks always pass through and whose lambdas take no outside input,
//   leaving a caller no way to make one swallow a key.
//
//   Using the general constructors with a handler that always returns PassThrough would read
//   the same, but the safety would then rest on "this call site happens not to say Swallow" —
//   one refactor or one slip away from gone, at the cost of the operator's laptop keyboard.
//
// 包含的成员 / Members in this file:
//   IsRunning  是否正在捕获
//   Start      启动捕获并等待就绪
//   Stop       结束捕获
//   Dispose    同 Stop
// =============================================================================

using System.ComponentModel;
using ScannerHelper.Win32.Observation;

namespace ScannerHelper.Win32;

/// <summary>
/// 中文：Task 4a 的观测捕获。只记录，不拦截。
/// English: Task 4a's observing capture. Records, never intercepts.
/// </summary>
public sealed class InputCaptureThread : ICaptureThreadWork, IDisposable
{
    private readonly ObservationBuffer _buffer;
    private readonly MessageOnlyCaptureHost _host;

    private LowLevelKeyboardHook? _hook;
    private RawInputKeyboardListener? _rawInput;

    /// <summary>
    /// 中文：
    ///   构造捕获。此时不启动，调用 <see cref="Start"/> 才开始。
    ///   输入：buffer 观测事件的去处，不得为 null。
    /// English:
    ///   Creates the capture without starting it; <see cref="Start"/> does that.
    /// </summary>
    /// <exception cref="ArgumentNullException">
    /// 中文：buffer 为 null。 English: buffer is null.
    /// </exception>
    public InputCaptureThread(ObservationBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        _buffer = buffer;
        _host = new MessageOnlyCaptureHost(this, "ScannerHelper input capture");
    }

    /// <summary>
    /// 中文：是否正在捕获。
    /// English: Whether capture is running.
    /// </summary>
    public bool IsRunning => _host.IsRunning;

    /// <summary>
    /// 中文：
    ///   观测不需要周期性推进，因此不装定时器。
    ///
    ///   4a 只把事件记下来，配对与统计都在事后离线做（见 ChannelPairing）。
    ///   需要定时器的是 4b：被扣留的按键必须有人在"什么都不再发生"的时候
    ///   把它放出来，而那一刻没有任何事件可以触发检查。
    /// English:
    ///   Observation needs no periodic advance, so no timer is started.
    ///
    ///   4a only records; pairing and statistics happen offline afterwards (see
    ///   ChannelPairing). The timer is 4b's need: a withheld keystroke must be released by
    ///   someone at the moment nothing is happening any more, and then no event exists to
    ///   trigger the check.
    /// </summary>
    TimeSpan ICaptureThreadWork.TickInterval => TimeSpan.Zero;

    /// <summary>
    /// 中文：
    ///   启动捕获，阻塞等待窗口与钩子就绪。
    ///   启动失败时抛出的是原始异常——最常见的原因是权限不匹配
    ///   （规格 §2.1 假设 A2），那条 Win32 错误信息本身就是最有用的线索。
    /// English:
    ///   Starts capture, blocking until the window and hook are ready. A startup failure
    ///   throws the original exception; the usual cause is a privilege mismatch (spec §2.1,
    ///   assumption A2) and that Win32 message is the most useful clue there is.
    /// </summary>
    /// <exception cref="Win32Exception">
    /// 中文：窗口创建、Raw Input 注册或钩子安装失败。
    /// English: Creating the window, registering Raw Input, or installing the hook failed.
    /// </exception>
    public void Start() => _host.Start();

    /// <summary>
    /// 中文：结束捕获。
    /// English: Ends capture.
    /// </summary>
    public void Stop() => _host.Stop();

    /// <inheritdoc />
    public void Dispose() => _host.Dispose();

    /// <summary>
    /// 中文：
    ///   在捕获线程上装好两条通道。
    ///   顺序有意为之：**先注册 Raw Input，再安装钩子**。
    ///
    ///   反过来的话，在 Raw Input 注册完成之前那一小段时间里钩子已经在记事件，
    ///   而这些事件永远等不到对家，会全部堆成"未配对"——把一个本该为零、
    ///   一旦不为零就该警觉的指标从一开始就掺进噪声。
    /// English:
    ///   Installs both channels on the capture thread, Raw Input first and the hook second.
    ///
    ///   The other order leaves a window in which the hook records events that can never find
    ///   a counterpart, and those pile up as "unpaired" — seeding noise into a metric that
    ///   should read zero and be alarming when it does not.
    /// </summary>
    void ICaptureThreadWork.OnCaptureStarted(IntPtr windowHandle)
    {
        _rawInput = RawInputKeyboardListener.CreateObserveOnly(_buffer);
        _rawInput.Register(windowHandle);

        _hook = LowLevelKeyboardHook.CreateObserveOnly(_buffer);
        _hook.Install();
    }

    /// <inheritdoc />
    void ICaptureThreadWork.OnRawInputMessage(IntPtr rawInputHandle, long timestamp)
        => _rawInput?.HandleRawInput(rawInputHandle, timestamp);

    /// <summary>
    /// 中文：观测不装定时器，本方法不会被调用。
    /// English: Observation starts no timer, so this is never called.
    /// </summary>
    void ICaptureThreadWork.OnTick()
    {
    }

    /// <summary>
    /// 中文：
    ///   在捕获线程上摘掉两条通道。先摘钩子：它影响全系统的每一次按键，
    ///   最该先停。
    /// English:
    ///   Removes both channels on the capture thread, the hook first: it affects every
    ///   keystroke on the machine and should stop soonest.
    /// </summary>
    void ICaptureThreadWork.OnCaptureStopping()
    {
        _hook?.Dispose();
        _hook = null;

        _rawInput?.Dispose();
        _rawInput = null;
    }
}
