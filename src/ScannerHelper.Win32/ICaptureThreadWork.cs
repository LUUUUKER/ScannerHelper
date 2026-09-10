// =============================================================================
// ICaptureThreadWork.cs
//
// 中文：
//   捕获线程要替谁干活——由 MessageOnlyCaptureHost 回调。
//
//   ★ 为什么要有这层接口，而不是让 4a 的观测和 4b 的拦截各写一条线程。
//
//     那条线程本身很短，但每一处细节都是踩出来的，且踩错了都不报错：
//       - 钩子必须由**安装它的那条线程**卸载，窗口必须由创建它的线程销毁；
//       - Raw Input 要先注册、钩子后安装，否则开头那批钩子事件永远配不上对家；
//       - 启动握手必须在失败路径上也置位，否则 Start 永远阻塞；
//       - 线程必须是后台线程，否则忘了 Stop 进程就退不出去，钩子还挂着。
//
//     抄一份就等于把这四条各留一次抄错的机会，而抄错的表现全都是"看起来在
//     工作"（规格 §19.1 专门点名的那类失效）。所以线程只有一份，两种用途
//     通过本接口接进去。
//
//   ★ 所有方法都在捕获线程上被调用，且**串行**。
//
//     这是本接口最重要的性质：实现方因此不需要任何锁。而锁恰恰是钩子回调
//     路径上最不能有的东西——一次争用就可能顶穿 LowLevelHooksTimeout，
//     Windows 随即悄悄摘掉钩子（规格 §19、§19.1）。
//
//     串行性来自消息循环：WM_INPUT、WM_TIMER、投递进来的动作，全都由同一个
//     GetMessage 依次取出。唯一的例外是钩子回调——它由系统在这条线程上直接
//     调用，会打断消息循环，但仍然在这条线程上，所以与消息处理之间不会并发。
//
// English:
//   The work a capture thread performs, called back by MessageOnlyCaptureHost.
//
//   The interface exists so that 4a's observation and 4b's interception share one thread
//   implementation. The thread is short but every detail in it was learned the hard way and
//   each one fails silently: a hook must be removed by the thread that installed it and a
//   window destroyed by its creator; Raw Input must be registered before the hook is
//   installed or the first hook events can never find counterparts; the startup handshake
//   must be signalled on the failure path too or Start blocks forever; the thread must be a
//   background thread or a missed Stop keeps the process alive with the hook still installed.
//   A second copy is four fresh chances to get those wrong, and every one of them presents as
//   "it looks like it is working" — the failure mode spec §19.1 names.
//
//   Every method here is called on the capture thread, serially. That is this interface's most
//   important property: implementations need no locks, and a lock is the one thing that must
//   never appear on the hook callback path, where a single contention can blow the
//   LowLevelHooksTimeout budget and have Windows silently remove the hook (spec §19, §19.1).
//
//   The serialization comes from the message loop: WM_INPUT, WM_TIMER and posted actions are
//   all retrieved by the same GetMessage in turn. The one exception is the hook callback,
//   which the system invokes directly on this thread, interrupting the loop — still on this
//   thread, so never concurrent with message handling.
//
// 包含的类型 / Types in this file:
//   ICaptureThreadWork
// =============================================================================

namespace ScannerHelper.Win32;

/// <summary>
/// 中文：捕获线程上要执行的工作。全部方法都在捕获线程上串行调用。
/// English: The work performed on the capture thread. Every method is called on that thread,
///          serially.
/// </summary>
public interface ICaptureThreadWork
{
    /// <summary>
    /// 中文：
    ///   周期性推进的间隔。<see cref="TimeSpan.Zero"/> 表示不需要定时器。
    ///
    ///   ★ WM_TIMER 是**低优先级**消息：只有在消息队列里没有别的东西时
    ///     Windows 才生成它，而且分辨率受系统时钟节拍限制（约 15.6 毫秒），
    ///     填一个比它更小的值不会更快。
    ///
    ///     队列忙时被饿死这一点不是缺陷，反而正合适：队列忙意味着事件正在
    ///     源源不断地来，而关联器每接纳一个事件都会顺手清一次过期
    ///     （见 InputEventCorrelator 的决策 D-12）。真正需要定时器的恰恰是
    ///     "什么都不再发生"的时刻——那时队列必然是空的。
    /// English:
    ///   The periodic advance interval; <see cref="TimeSpan.Zero"/> means no timer is needed.
    ///
    ///   WM_TIMER is a low-priority message: Windows generates it only when the queue holds
    ///   nothing else, and its resolution is bounded by the system tick (about 15.6 ms), so a
    ///   smaller value does not fire sooner.
    ///
    ///   Being starved while the queue is busy is not a defect but the right behavior: a busy
    ///   queue means events are arriving, and the correlator sweeps expiries on every event it
    ///   accepts (decision D-12 in InputEventCorrelator). What actually needs a timer is the
    ///   moment when nothing is happening any more — and then the queue is empty by
    ///   definition.
    /// </summary>
    /// <remarks>
    /// 中文：
    ///   ★ 目前**没有任何实现需要它**：唯一的实现是 Task 4a 的观测捕获，它返回零。
    ///     需要周期推进的是 4b 的关联超时，而 4b 已随架构变更退役
    ///     （ARCHITECTURE_CHANGE_SERIAL.md）。留着它是因为热插拔检测很可能会用上
    ///     同一条线程——但那一天到来之前，这里如实写明它现在是空转的，好过让人
    ///     以为有东西在依赖它。
    /// English:
    ///   No implementation currently needs this: the only one is Task 4a's observing capture,
    ///   which returns zero. Periodic advance was 4b's need for correlation timeouts, and 4b
    ///   retired with the architecture change (ARCHITECTURE_CHANGE_SERIAL.md). It is kept
    ///   because hot-plug detection will likely want the same thread — but until then, saying
    ///   plainly that it idles beats letting someone assume something depends on it.
    /// </remarks>
    TimeSpan TickInterval { get; }

    /// <summary>
    /// 中文：
    ///   仅消息窗口已创建，可以安装钩子、注册 Raw Input 了。
    ///   输入：windowHandle 仅消息窗口的句柄，用于 Raw Input 注册。
    ///
    ///   在这里抛异常是允许的：宿主会把它原样转交给 <c>Start</c> 的调用方，
    ///   并清理掉已经建立的一切。安装失败最常见的原因是权限不匹配
    ///   （规格 §2.1 假设 A2），那条 Win32 错误信息本身就是最有用的线索。
    /// English:
    ///   The message-only window exists; install hooks and register Raw Input now.
    ///
    ///   Throwing here is allowed: the host rethrows to <c>Start</c>'s caller unchanged and
    ///   cleans up whatever was established. The usual cause of a failed install is a
    ///   privilege mismatch (spec §2.1, assumption A2), and that Win32 message is the most
    ///   useful clue there is.
    /// </summary>
    void OnCaptureStarted(IntPtr windowHandle);

    /// <summary>
    /// 中文：
    ///   收到一条 WM_INPUT。
    ///   输入：rawInputHandle 消息的 lParam；timestamp 窗口过程**最开头**取得的
    ///         <c>Stopwatch.GetTimestamp()</c> 读数。
    ///
    ///   时间戳由宿主取并传进来，而不是在这里取：它必须是处理这条消息时做的
    ///   第一件事，晚一步就多掺进一分我们自己代码的耗时，而 Task 4a 实测两条
    ///   通道的时差最小只有 110 微秒。
    /// English:
    ///   One WM_INPUT arrived. timestamp is the <c>Stopwatch.GetTimestamp()</c> reading taken
    ///   at the very top of the window procedure.
    ///
    ///   The host takes it and passes it in rather than leaving it to this method: it must be
    ///   the first thing done for the message, and every step before it folds more of our own
    ///   cost into a delta Task 4a measured as low as 110 µs.
    /// </summary>
    void OnRawInputMessage(IntPtr rawInputHandle, long timestamp);

    /// <summary>
    /// 中文：定时器到点。<see cref="TickInterval"/> 为零时永不调用。
    /// English: The tick timer elapsed. Never called when <see cref="TickInterval"/> is zero.
    /// </summary>
    void OnTick();

    /// <summary>
    /// 中文：
    ///   消息循环已结束，窗口即将销毁。在这里摘钩子、注销 Raw Input。
    ///
    ///   本方法在捕获线程上调用，这不是风格问题：钩子必须由安装它的那条线程
    ///   卸载，从别的线程调用会失败——而失败之后钩子还挂在系统上，继续拦截
    ///   全机器的每一次按键。
    /// English:
    ///   The message loop has ended and the window is about to be destroyed; unhook and
    ///   unregister here.
    ///
    ///   Called on the capture thread, and not as a matter of style: a hook must be removed by
    ///   the thread that installed it, and a call from elsewhere fails — leaving the hook
    ///   installed and still intercepting every keystroke on the machine.
    /// </summary>
    void OnCaptureStopping();
}
