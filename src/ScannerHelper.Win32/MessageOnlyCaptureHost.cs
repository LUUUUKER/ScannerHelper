// =============================================================================
// MessageOnlyCaptureHost.cs
//
// 中文：
//   一条专用后台线程，独占一个仅消息窗口和它的消息循环，替 ICaptureThreadWork
//   干活。Task 4a 的观测和 4b 的拦截共用这一份。
//
//   ★ 这个类是第一轮实测的直接产物。
//
//     第一轮把捕获放在 WPF 界面线程上，结果扫码枪的段内间隔 p99 到了 48 毫秒
//     ——扫码枪根本不可能有这种停顿。原因是 `WM_INPUT` 是投递到消息队列里的，
//     它的时间戳只能是"消息循环处理到它的时刻"；界面线程每 100 毫秒重建一次
//     列表控件，消息就排在那些工作后面。低层钩子的回调同样由安装线程的消息
//     队列驱动，于是两条通道都被拖慢、而且拖慢的程度不同——测出来的时差既不
//     反映硬件也不反映 Windows，只反映"界面有多卡"。
//
//     所以捕获必须待在一条什么别的事都不干的线程上。这对产品同样成立：4b 的
//     扣留窗口必须覆盖"身份到手"的延迟，界面越卡，普通打字被扣留得越久。
//
//   ★ 钩子和 Raw Input 必须装在**同一条**线程上，理由同上：两条通道要处在
//     同一个调度环境里，否则各自排队、各自被打断，测出来（以及运行时依赖的）
//     时差里混进的是第二个调度器的噪声。
//
//   ★ 线程必须是后台线程。消息循环无限阻塞，前台线程会让忘记 Stop 的进程
//     退不出去——而"程序关掉了但进程还在，钩子还挂着"恰好是规格 §19 要求
//     干净收尾所要避免的情形。
//
//   ★ Post 存在的理由：暂停按钮在界面线程上。
//
//     规格 §5.7 要求 PAUSED 用鼠标就能点到，于是那一下必然发生在界面线程。
//     但 Pause() 会释放被扣留的按键（要调 SendInput）、会动关联器的队列——
//     那些状态属于捕获线程。从界面线程直接调，就是在钩子回调正在读同一批
//     队列的时候改它，一个没有任何异常、只在现场偶发的数据竞争。
//
//     Post 把动作投递到捕获线程的消息队列，由消息循环取出来执行，于是"点一下
//     暂停"和"钩子回调"之间自然串行，一把锁都不需要。
//
// English:
//   A dedicated background thread owning a message-only window and its message loop, doing
//   work on behalf of an ICaptureThreadWork. Task 4a's observation and 4b's interception
//   share this one implementation.
//
//   It is a direct product of the first measurement run. With capture on the WPF UI thread the
//   scanner's within-burst interval reached a p99 of 48 ms, which no scanner can produce:
//   WM_INPUT is posted to a message queue and its timestamp can only be "when the loop reached
//   it", and that thread was rebuilding list controls every 100 ms. A low-level hook's callback
//   is driven by the same queue, so both channels slowed by differing amounts and the resulting
//   delta described neither the hardware nor Windows, only how sluggish the UI was. The same
//   holds for the product: 4b's withhold window must cover the delay before identity arrives,
//   so a laggier UI withholds ordinary typing for longer.
//
//   The hook and Raw Input must live on the same thread, for the same reason — split, they get
//   different scheduling environments, each queuing and being interrupted on its own, and the
//   delta between them is what everything here depends on.
//
//   The thread must be a background thread. Its loop blocks forever, and a foreground thread
//   would keep the process alive if Stop were ever missed — "the program was closed but the
//   process is still there with the hook installed" being exactly what spec §19's
//   clean-shutdown requirement exists to prevent.
//
//   Post exists because the pause control is on the UI thread. Spec §5.7 requires PAUSED to be
//   reachable with the mouse, so that click necessarily happens on the UI thread — yet Pause()
//   releases withheld keystrokes (calling SendInput) and mutates the correlator's queues, state
//   that belongs to the capture thread. Calling it directly means modifying those queues while
//   the hook callback is reading them: a data race that raises nothing and shows up only
//   occasionally, on site. Post puts the action in the capture thread's message queue for the
//   loop to run, serializing "click pause" against the hook callback without a single lock.
//
// 包含的成员 / Members in this file:
//   IsRunning  是否正在运行
//   Start      启动线程并等待窗口与工作就绪
//   Stop       结束消息循环并等待线程退出
//   Post       把一个动作投递到捕获线程上执行
//   Dispose    同 Stop
// =============================================================================

using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ScannerHelper.Win32.Native;

namespace ScannerHelper.Win32;

/// <summary>
/// 中文：独占仅消息窗口与消息循环的专用捕获线程。
/// English: The dedicated capture thread owning a message-only window and its message loop.
/// </summary>
public sealed class MessageOnlyCaptureHost : IDisposable
{
    /// <summary>
    /// 中文：等待线程退出的上限。超时就不再等——卡在关闭流程里比残留一条后台
    ///       线程糟得多；而线程是后台线程，进程退出时会被回收。
    /// English: How long to wait for the thread to exit. Past that, stop waiting: hanging on
    ///          shutdown is worse than a lingering background thread, and a background thread
    ///          is reclaimed when the process exits.
    /// </summary>
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// 中文：投递动作用的自定义消息号。WM_APP 之上的号段由应用程序自由使用。
    /// English: The custom message used for posted actions. The range above WM_APP is the
    ///          application's to use freely.
    /// </summary>
    private const uint WM_RUN_POSTED_ACTION = 0x8000 + 1;

    /// <summary>
    /// 中文：定时器 id。本窗口只有一个定时器，取什么值都行，取 1 便于调试时辨认。
    /// English: The timer id. This window has exactly one timer, so any value works; 1 is
    ///          simply easy to recognize while debugging.
    /// </summary>
    private const nuint TickTimerId = 1;

    private readonly ICaptureThreadWork _work;

    /// <summary>
    /// 中文：
    ///   等着在捕获线程上执行的动作。
    ///   用并发队列是因为入队来自任意线程（多半是界面线程），出队只在捕获线程。
    /// English:
    ///   Actions waiting to run on the capture thread. A concurrent queue because enqueuing
    ///   happens on any thread — usually the UI thread — while dequeuing happens only on the
    ///   capture thread.
    /// </summary>
    private readonly ConcurrentQueue<Action> _postedActions = new();

    /// <summary>
    /// 中文：窗口类名带一个唯一后缀。同一进程内注册两次同名窗口类会失败，
    ///       而"停止后再开始"是常规操作。
    /// English: The window class name carries a unique suffix. Registering the same class name
    ///          twice in one process fails, and stop-then-start again is ordinary use.
    /// </summary>
    private readonly string _windowClassName =
        $"ScannerHelperCaptureHost_{Guid.NewGuid():N}";

    /// <summary>
    /// 中文：线程启动完成（成功或失败）的信号。Start 必须等它——否则调用方
    ///       会在钩子还没装上时就以为捕获已经开始，而最初那几次按键会静静丢掉。
    /// English: Signals that startup finished, successfully or not. Start must wait on it, or
    ///          the caller believes capture began while the hook is not yet installed and the
    ///          first keystrokes vanish silently.
    /// </summary>
    private readonly ManualResetEventSlim _startupCompleted = new(initialState: false);

    private readonly string _threadName;

    private Thread? _captureThread;
    private uint _captureThreadId;
    private IntPtr _windowHandle;
    private ushort _windowClassAtom;
    private Exception? _startupFailure;
    private bool _isDisposed;

    /// <summary>
    /// 中文：窗口过程委托。必须持有，理由同钩子回调——窗口类里存的是函数指针，
    ///       委托一旦被 GC 回收，下一条消息就跳进已释放的内存。
    /// English: The window procedure delegate, held for the same reason as the hook callback:
    ///          the window class stores a function pointer, and once the delegate is collected
    ///          the next message jumps into freed memory.
    /// </summary>
    private WindowNative.WindowProcedure? _windowProcedure;

    /// <summary>
    /// 中文：
    ///   构造宿主。此时不启动，调用 <see cref="Start"/> 才开始。
    ///   输入：work 要在捕获线程上执行的工作，不得为 null；
    ///         threadName 线程名，只影响调试器与性能分析里的显示。
    /// English:
    ///   Creates the host without starting it; <see cref="Start"/> does that. threadName only
    ///   affects how the thread appears in a debugger or profiler.
    /// </summary>
    /// <exception cref="ArgumentNullException">
    /// 中文：work 为 null。 English: work is null.
    /// </exception>
    public MessageOnlyCaptureHost(ICaptureThreadWork work, string threadName = "ScannerHelper input capture")
    {
        ArgumentNullException.ThrowIfNull(work);

        _work = work;
        _threadName = threadName;
    }

    /// <summary>
    /// 中文：是否正在运行。
    /// English: Whether the host is running.
    /// </summary>
    public bool IsRunning { get; private set; }

    /// <summary>
    /// 中文：
    ///   启动捕获线程，并**阻塞等待**窗口与工作就绪。
    ///   步骤：
    ///     1. 起一条后台线程跑 <see cref="RunCaptureLoop"/>；
    ///     2. 等待启动完成信号；
    ///     3. 启动过程中出过错就把原始异常重新抛出。
    ///
    ///   步骤 2 的等待是必需的。不等的话，调用方会在钩子尚未装上时就认为捕获
    ///   已经开始，而这段空窗期里的按键会静静丢失——丢得毫无征兆，排查时只
    ///   看到"最开始几个字符没记上"。
    ///
    ///   步骤 3 保留原始异常而不是包一层：安装失败最常见的原因是权限不匹配
    ///   （规格 §2.1 假设 A2），那条 Win32 错误信息本身就是最有用的线索，
    ///   包装成"启动失败"只会把它盖掉。
    /// English:
    ///   Starts the capture thread and blocks until the window and the work are ready.
    ///   Steps: (1) start a background thread running the loop; (2) wait for the startup
    ///   signal; (3) rethrow the original exception if startup failed.
    ///
    ///   The wait in step 2 is required. Without it the caller believes capture began while the
    ///   hook is not yet installed, and keystrokes in that gap vanish without a trace,
    ///   presenting later as "the first few characters were not recorded".
    ///
    ///   Step 3 preserves the original exception rather than wrapping it: the usual cause is a
    ///   privilege mismatch (spec §2.1, assumption A2), and that Win32 message is the most
    ///   useful clue there is.
    /// </summary>
    /// <exception cref="Win32Exception">
    /// 中文：窗口创建失败，或工作在启动阶段失败。
    /// English: Creating the window failed, or the work failed during startup.
    /// </exception>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        if (IsRunning)
        {
            return;
        }

        _startupFailure = null;
        _startupCompleted.Reset();

        // 步骤 1 / Step 1
        _captureThread = new Thread(RunCaptureLoop)
        {
            IsBackground = true,
            Name = _threadName,
        };
        _captureThread.Start();

        // 步骤 2 / Step 2
        _startupCompleted.Wait();

        // 步骤 3 / Step 3
        if (_startupFailure is not null)
        {
            _captureThread = null;
            throw _startupFailure;
        }

        IsRunning = true;
    }

    /// <summary>
    /// 中文：
    ///   结束捕获。
    ///   步骤：
    ///     1. 向捕获线程投递 WM_QUIT，让消息循环自然退出；
    ///     2. 等待线程结束，最多等 <see cref="ShutdownTimeout"/>。
    ///
    ///   摘钩子、销毁窗口、注销窗口类都在捕获线程自己那一侧做。这不是风格问题：
    ///   钩子必须由**安装它的那条线程**卸载，窗口也必须由创建它的线程销毁，
    ///   从别的线程调用会失败。
    /// English:
    ///   Ends capture.
    ///   Steps: (1) post WM_QUIT so the message loop exits on its own; (2) join, up to
    ///   <see cref="ShutdownTimeout"/>.
    ///
    ///   Unhooking, destroying the window and unregistering the class all happen on the capture
    ///   thread. Not a stylistic choice: a hook must be removed by the thread that installed it
    ///   and a window destroyed by the thread that created it; calling from elsewhere fails.
    /// </summary>
    public void Stop()
    {
        if (!IsRunning)
        {
            return;
        }

        // 步骤 1 / Step 1
        WindowNative.PostThreadMessageW(
            _captureThreadId, WindowNative.WM_QUIT, IntPtr.Zero, IntPtr.Zero);

        // 步骤 2 / Step 2
        _captureThread?.Join(ShutdownTimeout);
        _captureThread = null;
        IsRunning = false;
    }

    /// <summary>
    /// 中文：
    ///   把一个动作投递到捕获线程上执行。
    ///   输入：action 要执行的动作，不得为 null。
    ///   输出：投递成功返回 true；宿主没在运行返回 false。
    ///
    ///   ★ 本方法**不等待**动作执行完。等待会引出一类很典型的死锁：界面线程
    ///     等捕获线程，而捕获线程此刻正卡在钩子回调里等某个界面上的东西。
    ///     何况钩子回调等谁都不行——超时一次 Windows 就摘钩子（规格 §19.1）。
    ///
    ///   返回 false 表示动作**不会**被执行，调用方必须当回事：如果这个动作是
    ///   "进入暂停"，false 的意思是没暂停成。不过宿主没在运行时钩子也没装，
    ///   本来就什么都没拦，所以那一刻的"没暂停成"并不危险。
    /// English:
    ///   Posts an action to run on the capture thread. Returns false when the host is not
    ///   running, in which case the action will never run.
    ///
    ///   It does not wait for the action to complete. Waiting invites a classic deadlock: the
    ///   UI thread waits on the capture thread while the capture thread sits inside a hook
    ///   callback waiting on something owned by the UI. And a hook callback may not wait for
    ///   anything at all — exceed the timeout once and Windows removes the hook (spec §19.1).
    ///
    ///   A false return matters to the caller: if the action was "enter PAUSED", false means it
    ///   did not pause. It is not dangerous at that moment, though — with the host stopped the
    ///   hook is not installed and nothing is being intercepted in the first place.
    /// </summary>
    /// <exception cref="ArgumentNullException">
    /// 中文：action 为 null。 English: action is null.
    /// </exception>
    public bool Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (!IsRunning)
        {
            return false;
        }

        _postedActions.Enqueue(action);

        return WindowNative.PostThreadMessageW(
            _captureThreadId, WM_RUN_POSTED_ACTION, IntPtr.Zero, IntPtr.Zero);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        Stop();
        _startupCompleted.Dispose();
    }

    /// <summary>
    /// 中文：
    ///   捕获线程的主体。
    ///   步骤：
    ///     1. 记下线程 id，供 Stop 与 Post 投递消息；
    ///     2. 注册窗口类并创建仅消息窗口；
    ///     3. 交给工作去安装钩子、注册 Raw Input；
    ///     4. 需要的话装上定时器；
    ///     5. 无论成败都置起启动完成信号，放 Start 继续；
    ///     6. 跑消息循环，直到收到 WM_QUIT；
    ///     7. 通知工作收尾，再销毁窗口、注销窗口类。
    ///
    ///   步骤 5 必须在失败路径上也执行，否则一旦启动失败，Start 会永远阻塞在
    ///   等待上——程序变成一个卡死的窗口，比报错糟得多。
    /// English:
    ///   The capture thread's body.
    ///   Steps: (1) record the thread id for Stop and Post; (2) register the class and create
    ///   the message-only window; (3) let the work install its hook and registration; (4) start
    ///   the timer if one is wanted; (5) signal startup complete either way, releasing Start;
    ///   (6) pump until WM_QUIT; (7) let the work clean up, then destroy the window and
    ///   unregister the class.
    ///
    ///   Step 5 must also run on the failure path, or a failed startup leaves Start blocked
    ///   forever — turning the program into a frozen window, which is considerably worse than
    ///   an error message.
    /// </summary>
    private void RunCaptureLoop()
    {
        // 步骤 1 / Step 1
        _captureThreadId = WindowNative.GetCurrentThreadId();

        try
        {
            // 步骤 2 / Step 2
            CreateMessageOnlyWindow();

            // 步骤 3 / Step 3
            _work.OnCaptureStarted(_windowHandle);

            // 步骤 4 / Step 4
            StartTickTimer();
        }
        catch (Exception startupException)
        {
            _startupFailure = startupException;
            StopWork();
            CleanUp();

            // 步骤 5 / Step 5（失败路径也必须放行 Start）
            // Step 5 (the failure path must release Start too)
            _startupCompleted.Set();
            return;
        }

        // 步骤 5 / Step 5
        _startupCompleted.Set();

        // 步骤 6 / Step 6
        //
        // 投递进来的动作用 PostThreadMessage 送达，这类消息的 hwnd 为 0，
        // DispatchMessage 不会把它交给任何窗口过程——必须在循环里自己认出来。
        //
        // Posted actions arrive via PostThreadMessage, whose messages carry a zero hwnd;
        // DispatchMessage delivers them to no window procedure, so the loop must recognize
        // them itself.
        while (WindowNative.GetMessageW(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            if (message.hwnd == IntPtr.Zero && message.message == WM_RUN_POSTED_ACTION)
            {
                DrainPostedActions();
                continue;
            }

            WindowNative.DispatchMessageW(ref message);
        }

        // 步骤 7 / Step 7
        StopWork();
        CleanUp();
    }

    /// <summary>
    /// 中文：
    ///   执行所有排队的动作。
    ///
    ///   一次投递对应一条消息，但这里一次把队列排空，因此后面那几条消息可能
    ///   发现队列已经空了——那是正常的，不是错误。反过来（每条消息只取一个）
    ///   在消息丢失时会让动作永远留在队列里，那才危险。
    ///
    ///   ★ 单个动作抛出异常不能掀翻消息循环：循环一停，钩子还挂着但再也没人
    ///     处理 WM_INPUT，被扣留的按键永远等不到结论——键盘当场失灵。所以
    ///     每个动作各自兜住，异常交给工作那边去记录。
    /// English:
    ///   Runs every queued action.
    ///
    ///   Each Post sends one message but this drains the whole queue, so later messages may
    ///   find it already empty — normal, not an error. The reverse (one action per message)
    ///   would leave actions queued forever if a message were ever lost, which is the dangerous
    ///   direction.
    ///
    ///   An exception from one action must not tear down the message loop: with the loop
    ///   stopped the hook is still installed but nothing processes WM_INPUT any more, so
    ///   withheld keystrokes never reach a verdict and the keyboard stops working. Each action
    ///   is therefore contained on its own.
    /// </summary>
    private void DrainPostedActions()
    {
        while (_postedActions.TryDequeue(out var action))
        {
            try
            {
                action();
            }
            catch (Exception actionException)
            {
                Debug.WriteLine($"投递到捕获线程的动作抛出异常。 A posted action threw: {actionException}");
            }
        }
    }

    /// <summary>
    /// 中文：按工作声明的间隔装上定时器；间隔为零则不装。
    ///       装不上不算致命——只是超时的推进要慢一些，事件到达时的顺带清理
    ///       仍然有效（决策 D-12）——但值得记下来。
    /// English: Starts the tick timer at the work's declared interval, or none if it is zero.
    ///          Failing to start it is not fatal — timeouts merely advance more slowly, the
    ///          sweep that rides along with each accepted event still working (decision D-12) —
    ///          but it is worth recording.
    /// </summary>
    private void StartTickTimer()
    {
        var interval = _work.TickInterval;
        if (interval <= TimeSpan.Zero)
        {
            return;
        }

        var milliseconds = (uint)Math.Max(1, Math.Round(interval.TotalMilliseconds));

        if (WindowNative.SetTimer(_windowHandle, TickTimerId, milliseconds, IntPtr.Zero) == 0)
        {
            Debug.WriteLine(
                "装定时器失败，超时推进将只依赖事件到达。"
                + " Failed to start the tick timer; timeouts will advance only on arriving events.");
        }
    }

    /// <summary>
    /// 中文：
    ///   注册窗口类并创建仅消息窗口。
    ///
    ///   父窗口传 HWND_MESSAGE 就得到一个仅消息窗口：不显示、不被枚举、
    ///   收不到广播消息，只接收发给它的消息。这正是接收 `WM_INPUT` 所需要的
    ///   全部功能，也避免了一个不该出现在任务栏里的窗口。
    /// English:
    ///   Registers the class and creates the message-only window. Passing HWND_MESSAGE as the
    ///   parent yields a window that is invisible, not enumerated and receives no broadcasts —
    ///   everything receiving WM_INPUT needs, and nothing that would put a stray window in the
    ///   taskbar.
    /// </summary>
    private void CreateMessageOnlyWindow()
    {
        _windowProcedure = OnWindowMessage;

        var windowClass = new WindowNative.WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WindowNative.WNDCLASSEXW>(),
            lpfnWndProc = _windowProcedure,
            hInstance = NativeMethods.GetCurrentModuleHandle(),
            lpszClassName = _windowClassName,
        };

        _windowClassAtom = WindowNative.RegisterClassExW(ref windowClass);
        if (_windowClassAtom == 0)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "注册仅消息窗口的窗口类失败。 Failed to register the message-only window class.");
        }

        _windowHandle = WindowNative.CreateWindowExW(
            0, _windowClassName, null, 0, 0, 0, 0, 0,
            WindowNative.HWND_MESSAGE, IntPtr.Zero, windowClass.hInstance, IntPtr.Zero);

        if (_windowHandle == IntPtr.Zero)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "创建仅消息窗口失败。 Failed to create the message-only window.");
        }
    }

    /// <summary>
    /// 中文：
    ///   仅消息窗口的窗口过程。
    ///
    ///   ★ 对 `WM_INPUT` 只做一件事：**立刻**取时间戳，然后交出去。
    ///
    ///     时间戳必须是认出消息之后的第一条语句。两条通道的时差可能只有
    ///     一百多微秒；时间戳每晚一步，就多混进一分我们自己代码的耗时。
    ///     这条线程上没有任何别的工作，正是为了让"消息何时被取到"尽可能贴近
    ///     "消息何时到达"。
    /// English:
    ///   The message-only window's procedure. For WM_INPUT it does one thing: take a timestamp
    ///   immediately, then hand the payload on.
    ///
    ///   The timestamp must be the first statement after recognition. The inter-channel delta
    ///   may be only a hundred-odd microseconds, and every step taken before the timestamp
    ///   folds more of our own cost into it. This thread does nothing else precisely so that
    ///   "when the message was retrieved" sits as close as possible to "when it arrived".
    /// </summary>
    private IntPtr OnWindowMessage(IntPtr windowHandle, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == NativeMethods.WM_INPUT)
        {
            var timestamp = Stopwatch.GetTimestamp();
            _work.OnRawInputMessage(lParam, timestamp);
            return IntPtr.Zero;
        }

        if (message == WindowNative.WM_TIMER && (nuint)wParam == TickTimerId)
        {
            _work.OnTick();
            return IntPtr.Zero;
        }

        return WindowNative.DefWindowProcW(windowHandle, message, wParam, lParam);
    }

    /// <summary>
    /// 中文：通知工作收尾。它失败也不能挡住后面销毁窗口的步骤，否则窗口和
    ///       窗口类一起泄漏，而"停止后再开始"是常规操作。
    /// English: Tells the work to clean up. Its failure must not block destroying the window,
    ///          or the window and its class leak together — and stop-then-start again is
    ///          ordinary use.
    /// </summary>
    private void StopWork()
    {
        try
        {
            _work.OnCaptureStopping();
        }
        catch (Exception stoppingException)
        {
            Debug.WriteLine($"捕获收尾时抛出异常。 Capture teardown threw: {stoppingException}");
        }
    }

    /// <summary>
    /// 中文：
    ///   在捕获线程上释放窗口相关的原生资源：先停定时器，再销毁窗口，
    ///   最后注销窗口类。
    ///
    ///   每一步都独立地容错：其中一步失败不该阻止后面几步——残留一个窗口类
    ///   只是浪费一点资源。
    /// English:
    ///   Releases the window's native resources on the capture thread: stop the timer, destroy
    ///   the window, unregister the class.
    ///
    ///   Each step tolerates its own failure so one cannot block the rest; a leaked window
    ///   class merely wastes a little memory.
    /// </summary>
    private void CleanUp()
    {
        if (_windowHandle != IntPtr.Zero)
        {
            WindowNative.KillTimer(_windowHandle, TickTimerId);
            WindowNative.DestroyWindow(_windowHandle);
            _windowHandle = IntPtr.Zero;
        }

        if (_windowClassAtom != 0)
        {
            WindowNative.UnregisterClassW(_windowClassName, NativeMethods.GetCurrentModuleHandle());
            _windowClassAtom = 0;
        }

        _windowProcedure = null;

        // 队列里可能还留着来不及执行的动作。清掉，免得下一次 Start 时
        // 突然执行一批属于上一轮的动作——比如一个早已过时的"暂停"。
        // Actions may remain that never ran. Clear them, so a later Start does not suddenly
        // execute a batch belonging to the previous run — a long-stale "pause", for instance.
        _postedActions.Clear();
    }
}
