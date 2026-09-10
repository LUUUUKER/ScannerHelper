// =============================================================================
// InputCaptureThread.cs
//
// 中文：
//   一条专用线程，独占钩子、Raw Input 注册和它们共用的消息循环。
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
//     所以捕获必须待在一条什么别的事都不干的线程上。
//
//   ★ 钩子和 Raw Input 必须装在**同一条**线程上。
//
//     低层键盘钩子的回调由安装它的线程的消息队列驱动，`WM_INPUT` 也投递到
//     这条线程的窗口。分在两条线程上就等于给两条通道各配一个不同的调度环境，
//     它们各自排队、各自被打断——而这里唯一要测的就是两者的时差，混进第二个
//     调度环境等于在测量对象里掺进噪声。
//
//   ★ 线程必须是后台线程。
//
//     消息循环是无限阻塞的。若是前台线程，忘记调用 Stop 就会让整个进程退不出去
//     ——而"程序关掉了但进程还在，钩子还挂着"恰好是规格 §19 要求干净收尾所要
//     避免的情形。
//
// English:
//   A dedicated thread owning the hook, the Raw Input registration, and the message
//   loop they share.
//
//   This class is a direct product of the first measurement run. With capture on the
//   WPF UI thread, the scanner's within-burst interval reached a p99 of 48 ms, which no
//   scanner can produce. WM_INPUT is posted to a message queue and its timestamp can
//   only be "when the loop reached it", and that thread was rebuilding list controls
//   every 100 ms. A low-level hook's callback is driven by the same queue, so both
//   channels slowed by differing amounts — and the resulting delta described neither
//   the hardware nor Windows, only how sluggish the UI was.
//
//   The hook and Raw Input must live on the *same* thread. Splitting them gives the two
//   channels different scheduling environments, each queuing and being interrupted on
//   its own — and the delta between them is the single thing being measured, so a second
//   scheduler is noise injected straight into the subject.
//
//   The thread must be a background thread. Its message loop blocks forever, and a
//   foreground thread would keep the whole process alive if Stop were ever missed —
//   "the program was closed but the process is still there with the hook installed" being
//   exactly what spec §19's clean-shutdown requirement exists to prevent.
//
// 包含的成员 / Members in this file:
//   IsRunning  是否正在捕获
//   Start      启动线程并等待钩子与窗口就绪
//   Stop       结束消息循环并等待线程退出
//   Dispose    同 Stop
// =============================================================================

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ScannerHelper.Win32.Native;
using ScannerHelper.Win32.Observation;

namespace ScannerHelper.Win32;

/// <summary>
/// 中文：独占输入捕获的专用线程。
/// English: The dedicated thread that owns input capture.
/// </summary>
public sealed class InputCaptureThread : IDisposable
{
    /// <summary>
    /// 中文：等待线程退出的上限。超时就不再等——诊断工具卡在关闭流程里，
    ///       比残留一条后台线程糟得多；而线程是后台线程，进程退出时会被回收。
    /// English: How long to wait for the thread to exit. Past that, stop waiting: a
    ///          diagnostic tool hanging on shutdown is worse than a lingering background
    ///          thread, and a background thread is reclaimed when the process exits.
    /// </summary>
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(2);

    private readonly ObservationBuffer _buffer;

    /// <summary>
    /// 中文：窗口类名带一个唯一后缀。同一进程内注册两次同名窗口类会失败，
    ///       而"停止后再开始"是这个工具的常规操作。
    /// English: The window class name carries a unique suffix. Registering the same
    ///          class name twice in one process fails, and stop-then-start again is
    ///          ordinary use of this tool.
    /// </summary>
    private readonly string _windowClassName =
        $"ScannerHelperInputCapture_{Guid.NewGuid():N}";

    /// <summary>
    /// 中文：线程启动完成（成功或失败）的信号。Start 必须等它——否则调用方
    ///       会在钩子还没装上时就以为捕获已经开始，而最初那几次按键会静静丢掉。
    /// English: Signals that startup finished, successfully or not. Start must wait on
    ///          it, or the caller believes capture began while the hook is not yet
    ///          installed and the first keystrokes vanish silently.
    /// </summary>
    private readonly ManualResetEventSlim _startupCompleted = new(initialState: false);

    private Thread? _captureThread;
    private uint _captureThreadId;
    private IntPtr _windowHandle;
    private ushort _windowClassAtom;
    private Exception? _startupFailure;

    /// <summary>
    /// 中文：窗口过程委托。必须持有，理由同钩子回调——窗口类里存的是函数指针。
    /// English: The window procedure delegate, held for the same reason as the hook
    ///          callback: the window class stores a function pointer.
    /// </summary>
    private WindowNative.WindowProcedure? _windowProcedure;

    private LowLevelKeyboardHook? _hook;
    private RawInputKeyboardListener? _rawInput;

    /// <summary>
    /// 中文：
    ///   构造捕获线程。此时不启动，调用 <see cref="Start"/> 才开始。
    ///   输入：buffer 观测事件的去处，不得为 null。
    /// English:
    ///   Creates the capture thread without starting it; <see cref="Start"/> does that.
    /// </summary>
    /// <exception cref="ArgumentNullException">
    /// 中文：buffer 为 null。 English: buffer is null.
    /// </exception>
    public InputCaptureThread(ObservationBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        _buffer = buffer;
    }

    /// <summary>
    /// 中文：是否正在捕获。
    /// English: Whether capture is running.
    /// </summary>
    public bool IsRunning { get; private set; }

    /// <summary>
    /// 中文：
    ///   启动捕获线程，并**阻塞等待**窗口与钩子就绪。
    ///   输入：无。输出：无。
    ///   步骤：
    ///     1. 起一条后台线程跑 <see cref="RunCaptureLoop"/>；
    ///     2. 等待启动完成信号；
    ///     3. 启动过程中出过错就把原始异常重新抛出。
    ///
    ///   步骤 2 的等待是必需的。不等的话，调用方会在钩子尚未装上时就认为
    ///   捕获已经开始，而这段空窗期里的按键会静静丢失——而且丢得毫无征兆，
    ///   排查时只会看到"最开始几个字符没记上"。
    ///
    ///   步骤 3 保留原始异常而不是包一层：安装失败最常见的原因是权限不匹配
    ///   （规格 §2.1 假设 A2），那条 Win32 错误信息本身就是最有用的线索，
    ///   包装成"启动失败"只会把它盖掉。
    /// English:
    ///   Starts the capture thread and blocks until the window and hook are ready.
    ///   Steps: (1) start a background thread running the loop; (2) wait for the startup
    ///   signal; (3) rethrow the original exception if startup failed.
    ///
    ///   The wait in step 2 is required. Without it the caller believes capture began
    ///   while the hook is not yet installed, and keystrokes in that gap vanish without
    ///   a trace — presenting later as "the first few characters were not recorded".
    ///
    ///   Step 3 preserves the original exception rather than wrapping it: the usual
    ///   cause is a privilege mismatch (spec §2.1, assumption A2), and that Win32
    ///   message is the most useful clue there is.
    /// </summary>
    /// <exception cref="Win32Exception">
    /// 中文：窗口创建、Raw Input 注册或钩子安装失败。
    /// English: Creating the window, registering Raw Input, or installing the hook failed.
    /// </exception>
    public void Start()
    {
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
            Name = "ScannerHelper input capture",
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
    ///   输入：无。输出：无。
    ///   步骤：
    ///     1. 向捕获线程投递 WM_QUIT，让消息循环自然退出；
    ///     2. 等待线程结束，最多等 <see cref="ShutdownTimeout"/>。
    ///
    ///   摘钩子、销毁窗口、注销窗口类都在捕获线程自己那一侧做（见
    ///   <see cref="RunCaptureLoop"/>）。这不是风格问题：钩子必须由**安装它的
    ///   那条线程**卸载，窗口也必须由创建它的线程销毁，从别的线程调用会失败。
    /// English:
    ///   Ends capture.
    ///   Steps: (1) post WM_QUIT so the message loop exits on its own; (2) join, up to
    ///   <see cref="ShutdownTimeout"/>.
    ///
    ///   Unhooking, destroying the window and unregistering the class all happen on the
    ///   capture thread itself. Not a stylistic choice: a hook must be removed by the
    ///   thread that installed it and a window destroyed by the thread that created it;
    ///   calling from elsewhere fails.
    /// </summary>
    public void Stop()
    {
        if (!IsRunning)
        {
            return;
        }

        // 步骤 1 / Step 1
        WindowNative.PostThreadMessageW(_captureThreadId, WindowNative.WM_QUIT, IntPtr.Zero, IntPtr.Zero);

        // 步骤 2 / Step 2
        _captureThread?.Join(ShutdownTimeout);
        _captureThread = null;
        IsRunning = false;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Stop();
        _startupCompleted.Dispose();
    }

    /// <summary>
    /// 中文：
    ///   捕获线程的主体。
    ///   步骤：
    ///     1. 记下线程 id，供 Stop 投递 WM_QUIT；
    ///     2. 注册窗口类并创建仅消息窗口；
    ///     3. 先注册 Raw Input，再安装钩子；
    ///     4. 无论成败都置起启动完成信号，放 Start 继续；
    ///     5. 跑消息循环，直到收到 WM_QUIT；
    ///     6. 在本线程上依次摘钩子、销毁窗口、注销窗口类。
    ///
    ///   步骤 3 的先后有意为之：先装钩子的话，在 Raw Input 注册完成之前那一小段
    ///   时间里钩子已经在记事件，而这些事件永远等不到对家，会全部堆成"未配对"
    ///   ——把一个本该为零、一旦不为零就该警觉的指标从一开始就掺进噪声。
    ///
    ///   步骤 4 必须在 finally 之外、且失败路径上也要执行，否则一旦启动失败，
    ///   Start 会永远阻塞在等待上——诊断工具变成一个卡死的窗口，比报错糟得多。
    /// English:
    ///   The capture thread's body.
    ///   Steps: (1) record the thread id for Stop to post WM_QUIT to; (2) register the
    ///   class and create the message-only window; (3) register Raw Input, then install
    ///   the hook; (4) signal startup complete either way, releasing Start; (5) pump
    ///   until WM_QUIT; (6) unhook, destroy the window and unregister the class, all on
    ///   this thread.
    ///
    ///   The order in step 3 is deliberate: installing the hook first leaves a window in
    ///   which it records events that can never find a counterpart, and those pile up as
    ///   "unpaired", seeding noise into a metric that should read zero and be alarming
    ///   when it does not.
    ///
    ///   Step 4 must also run on the failure path, or a failed startup leaves Start
    ///   blocked forever — turning the diagnostic tool into a frozen window, which is
    ///   considerably worse than an error message.
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
            _rawInput = new RawInputKeyboardListener(_buffer);
            _rawInput.Register(_windowHandle);

            _hook = new LowLevelKeyboardHook(_buffer);
            _hook.Install();
        }
        catch (Exception startupException)
        {
            _startupFailure = startupException;
            CleanUp();

            // 步骤 4 / Step 4（失败路径也必须放行 Start）
            _startupCompleted.Set();
            return;
        }

        // 步骤 4 / Step 4
        _startupCompleted.Set();

        // 步骤 5 / Step 5
        while (WindowNative.GetMessageW(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            WindowNative.DispatchMessageW(ref message);
        }

        // 步骤 6 / Step 6
        CleanUp();
    }

    /// <summary>
    /// 中文：
    ///   注册窗口类并创建仅消息窗口。
    ///
    ///   父窗口传 HWND_MESSAGE 就得到一个仅消息窗口：不显示、不被枚举、
    ///   收不到广播消息，只接收发给它的消息。这正是接收 `WM_INPUT` 所需要的
    ///   全部功能，也避免了一个不该出现在任务栏里的窗口。
    /// English:
    ///   Registers the class and creates the message-only window. Passing HWND_MESSAGE
    ///   as the parent yields a window that is invisible, not enumerated and receives no
    ///   broadcasts — everything receiving WM_INPUT needs, and nothing that would put a
    ///   stray window in the taskbar.
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
    ///   ★ 只做一件事：认出 `WM_INPUT`，**立刻**取时间戳，交给解析器。
    ///
    ///     时间戳必须是认出消息之后的第一条语句。整个 4a 要测的就是两条通道的
    ///     时差，那个差值可能只有一百多微秒；时间戳每晚一步，测出来的就多混进
    ///     一分我们自己代码的耗时。
    ///
    ///     这条线程上没有任何别的工作，正是为了让"消息何时被取到"尽可能贴近
    ///     "消息何时到达"。
    /// English:
    ///   The message-only window's procedure. It does one thing: recognize WM_INPUT,
    ///   take a timestamp immediately, and hand the payload to the parser.
    ///
    ///   The timestamp must be the first statement after recognition. The delta 4a
    ///   measures may be only a hundred-odd microseconds, and every step taken before
    ///   the timestamp folds more of our own cost into it. This thread does nothing else
    ///   precisely so that "when the message was retrieved" sits as close as possible to
    ///   "when the message arrived".
    /// </summary>
    private IntPtr OnWindowMessage(IntPtr windowHandle, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == NativeMethods.WM_INPUT)
        {
            var timestamp = Stopwatch.GetTimestamp();
            _rawInput?.HandleRawInput(lParam, timestamp);
            return IntPtr.Zero;
        }

        return WindowNative.DefWindowProcW(windowHandle, message, wParam, lParam);
    }

    /// <summary>
    /// 中文：
    ///   在捕获线程上释放全部原生资源。
    ///   顺序：先摘钩子（它影响全系统的每一次按键，最该先停），再销毁窗口，
    ///   最后注销窗口类。
    ///
    ///   每一步都独立地容错：其中一步失败不该阻止后面几步——残留一个窗口类
    ///   只是浪费一点资源，残留一个钩子却会继续拖慢整台机器的键盘。
    /// English:
    ///   Releases every native resource, on the capture thread. Order: unhook first
    ///   since it affects every keystroke system-wide, then destroy the window, then
    ///   unregister the class.
    ///
    ///   Each step tolerates its own failure so one cannot block the rest: a leaked
    ///   window class merely wastes a little memory, whereas a leaked hook keeps slowing
    ///   the entire machine's keyboard.
    /// </summary>
    private void CleanUp()
    {
        _hook?.Dispose();
        _hook = null;

        _rawInput?.Dispose();
        _rawInput = null;

        if (_windowHandle != IntPtr.Zero)
        {
            WindowNative.DestroyWindow(_windowHandle);
            _windowHandle = IntPtr.Zero;
        }

        if (_windowClassAtom != 0)
        {
            WindowNative.UnregisterClassW(_windowClassName, NativeMethods.GetCurrentModuleHandle());
            _windowClassAtom = 0;
        }

        _windowProcedure = null;
    }
}
