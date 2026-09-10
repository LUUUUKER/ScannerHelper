// =============================================================================
// Win32ScannerInputSource.cs
//
// 中文：
//   Task 4b 的拦截流水线：钩子、Raw Input、解码器、重放，围着 Core 的
//   ScanInputCoordinator 接成一条真正会**吞掉按键**的链路（规格 §17）。
//
//   4a 与 4b 的分界就在这个文件。4a 的 InputCaptureThread 只记录，因此可以
//   放心地在真实生产机上跑；本类会真的把按键从业务软件手里拿走，吞了不补，
//   笔记本键盘当场失灵（规格假设 A4：现场没有备用键盘可插）。
//
//   ★ 一枪的完整路径，以及每一步为什么在这里而不在别处。
//
//     1. 钩子回调（同步，必须立刻给答案）
//          取时间戳 → 解码出字符 → 交给协调器 → 把 HookAction 翻译成
//          HookDecision 交还 Windows。
//        解码必须在这里做：字符是 Core 判断"这一枪收完了没有"的依据，而
//        Core 不认识扫描码，也不该认识键盘布局（规格 §16 依赖方向向内）。
//
//     2. WM_INPUT（投递，拦不住任何东西，只揭示是谁按的）
//          交给协调器 → 之前被扣留的按键在这里得到结论。
//
//     3. WM_TIMER（周期推进）
//          关联超时与扫描超时。两者都是"某件事不再发生"的判定，而"不再发生"
//          本身不会产生任何事件来触发检查。
//
//     4. 补发（ReplayRequested）
//          判定为普通键盘的按键必须真的重新发出去。收到这个请求却不处理，
//          等于让工人的按键凭空消失。
//
//   ★ 默认**不绑定任何扫码枪**，而未绑定时整条流水线被旁路。
//
//     这不只是"默认值取保守的那个"。未绑定时关联器对每一个事件的结论必然是
//     "不是扫码枪"，扣留-重放因此**可以证明是白做的**——而它并不免费：
//     中文输入法的组字挂在真实按键事件上，扣留再合成一次是最容易打断组字的
//     操作；按住不放的重复、以及一切依赖按键时序的东西也都会被搅动。
//
//     所以未绑定时钩子回调直接放行，连协调器都不碰。代价是未绑定时热键也
//     不生效——但未绑定意味着根本不在扫码，热键此刻无事可做。
//
//     （这一条目前由本层把关。更彻底的做法是让关联器在未绑定时直接返回
//     PassThrough，那属于 Core 的判断，需要单独立一条决策并补测试。）
//
//   ★ 一个已知的、可测量的风险：解析在钩子回调里跑。
//
//     扫描的终止符是从钩子通道来的，于是"一枪收完"这件事发生在钩子回调内部，
//     而协调器紧接着就地做解析、校验、输出（见 ScanInputCoordinator 的
//     ProcessScanResult）。CLAUDE.md 明写钩子回调里不得跑正则。
//
//     实际耗时通常在微秒量级：条码只有二三十个字符，正则还带着 100 毫秒的
//     有限超时。但工人自配的正则一旦发生灾难性回溯，两次匹配就能吃掉 200
//     毫秒，逼近 LowLevelHooksTimeout 的 300 毫秒——而顶穿之后 Windows 会
//     悄悄摘掉钩子，进程照跑、界面照显示模式，原始条码直接流进业务软件
//     （规格 §19.1）。
//
//     本类不隐瞒这一点，而是把它变成可观测的：每一次回调都计时，
//     HookCallbackBudgetExceededCount 与 MaximumHookCallbackDuration 让它
//     从"不可见的风险"变成"看得见的数字"。真正的修法（把处理挪出回调）
//     要改 Core 的状态机时序，应当先立决策、再动。
//
// English:
//   Task 4b's interception pipeline: the hook, Raw Input, the decoder and replay, composed
//   around Core's ScanInputCoordinator into a chain that genuinely swallows keystrokes
//   (spec §17).
//
//   The 4a/4b boundary is this file. 4a's InputCaptureThread only records and is therefore
//   safe on a live production machine; this class actually takes keystrokes away from the
//   business application, and swallowed-without-replay means a dead laptop keyboard
//   (assumption A4: no spare keyboard on site).
//
//   One scan's full path: (1) the hook callback, synchronous and owing Windows an immediate
//   answer — timestamp, decode the character, ask the coordinator, translate HookAction into
//   HookDecision. Decoding belongs here because the character is what Core uses to decide
//   whether a scan is complete, and Core knows nothing of scan codes or keyboard layouts
//   (spec §16, dependencies point inward). (2) WM_INPUT, posted, unable to intercept anything
//   and only revealing who pressed the key; withheld keystrokes reach a verdict here.
//   (3) WM_TIMER, the periodic advance for the correlation and scan timeouts — both judge
//   that something has stopped happening, and stopping produces no event to trigger a check.
//   (4) ReplayRequested: a keystroke judged to be ordinary typing must actually be re-emitted,
//   or the operator's keypress vanishes.
//
//   No scanner is bound by default, and while unbound the whole pipeline is bypassed. That is
//   more than a conservative default: unbound, the correlator's verdict for every event is
//   necessarily "not the scanner", so withhold-and-replay is provably wasted — and it is not
//   free. IME composition hangs off real key events and withholding then synthesizing one is
//   the surest way to break it; key repeat and anything else depending on keystroke timing get
//   disturbed too. So while unbound the hook callback passes through without touching the
//   coordinator at all. The cost is that hotkeys do not act while unbound — but unbound means
//   nothing is being scanned, and there is nothing for them to do.
//
//   (That gate currently lives in this layer. The thorough fix is for the correlator to return
//   PassThrough directly while unbound, which is Core's judgment to make and needs a decision
//   of its own plus tests.)
//
//   A known, measurable risk: parsing runs inside the hook callback. The scan's terminator
//   arrives on the hook channel, so "the scan completed" happens inside the callback and the
//   coordinator parses, validates and emits right there (see ScanInputCoordinator's
//   ProcessScanResult), while CLAUDE.md forbids regex in the hook callback. Real cost is
//   usually microseconds — barcodes are twenty or thirty characters and the regex carries a
//   finite 100 ms timeout — but one operator-configured regex with catastrophic backtracking
//   can spend 200 ms across two matches, approaching LowLevelHooksTimeout's 300 ms. Blow that
//   budget and Windows silently removes the hook: the process keeps running, the UI keeps
//   showing a mode, and raw barcodes flow straight into the business application (spec §19.1).
//
//   This class does not paper over it but makes it observable: every callback is timed, and
//   HookCallbackBudgetExceededCount together with MaximumHookCallbackDuration turn an
//   invisible risk into a visible number. The real fix — moving processing out of the callback
//   — changes Core's state-machine timing and deserves a recorded decision first.
//
// 包含的成员 / Members in this file:
//   IsRunning / IsPaused / BoundDeviceHandle  当前状态
//   Start / Stop / Dispose                     生命周期
//   BindScanner / UnbindScanner                绑定与解绑
//   Pause / Resume                             安全阀（规格 §5.7）
//   EnumerateKeyboards / ResolveDevice         设备身份
//   诊断计数 / diagnostics counters
// =============================================================================

using System.ComponentModel;
using System.Diagnostics;
using ScannerHelper.Core.Domain;
using ScannerHelper.Core.Input;
using ScannerHelper.Win32.Observation;

namespace ScannerHelper.Win32;

/// <summary>
/// 中文：Windows 上的扫码枪输入来源：拦截、识别、补发（规格 §17）。
/// English: The scanner input source on Windows: intercept, identify, replay (spec §17).
/// </summary>
public sealed class Win32ScannerInputSource : ICaptureThreadWork, IDisposable
{
    /// <summary>
    /// 中文：
    ///   周期推进的间隔。
    ///
    ///   取 15 毫秒是因为 WM_TIMER 的分辨率本来就受系统时钟节拍限制
    ///   （约 15.6 毫秒），填更小的值不会更快，只会让人误以为更快。
    ///   关联窗口是 50 毫秒，被扣留的按键最坏等 50 + 一个节拍才放出来，
    ///   仍然远低于人能察觉的延迟。
    /// English:
    ///   The periodic advance interval. Fifteen milliseconds because WM_TIMER's resolution is
    ///   bounded by the system tick (about 15.6 ms) anyway, and a smaller value would not fire
    ///   sooner, only look as though it might. With a 50 ms correlation window a withheld
    ///   keystroke waits at worst 50 ms plus one tick — still far below what a person notices.
    /// </summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(15);

    /// <summary>
    /// 中文：
    ///   钩子回调的时间预算。超过就计一次数。
    ///
    ///   50 毫秒远低于 LowLevelHooksTimeout 的 300 毫秒，这是有意的：它是
    ///   一条**预警线**，不是失败线。等到真的顶穿 300 毫秒，钩子已经被摘掉，
    ///   而那时我们收不到任何通知（规格 §19.1）——预警必须发生在还来得及的
    ///   时候。
    /// English:
    ///   The hook callback's time budget; exceeding it increments a counter.
    ///
    ///   Fifty milliseconds sits well below LowLevelHooksTimeout's 300 ms deliberately: it is
    ///   a warning line, not a failure line. By the time 300 ms is actually exceeded the hook
    ///   has already been removed and no notification arrives (spec §19.1) — a warning has to
    ///   come while there is still time to act on it.
    /// </summary>
    public static readonly TimeSpan HookCallbackBudget = TimeSpan.FromMilliseconds(50);

    private readonly ScanInputCoordinator _coordinator;
    private readonly IInputEventCorrelator _correlator;
    private readonly KeyboardCharacterDecoder _decoder = new();
    private readonly KeystrokeReplayService _replay = new();
    private readonly RawInputDeviceResolver _resolver = new();
    private readonly MessageOnlyCaptureHost _host;

    /// <summary>
    /// 中文：
    ///   一次调用里攒下来的待补发按键，攒完一起发。
    ///
    ///   ★ 合并成一次 SendInput 不只是省开销。SendInput 保证**一次调用里的
    ///     事件不会被别的输入插进来**；拆成多次，两次调用之间就可能挤进真实
    ///     按键，补发出去的顺序于是和工人按下的顺序不一样了。
    ///
    ///   复用同一个 List 而不是每次新建：补发发生在钩子回调路径上，而对象
    ///   分配带来的 GC 暂停正是可能顶穿 LowLevelHooksTimeout 的原因之一。
    /// English:
    ///   Keystrokes to replay, accumulated within one call and sent together.
    ///
    ///   Batching into a single SendInput is not only about overhead: SendInput guarantees that
    ///   nothing else is interleaved into one call's events. Split across several calls, real
    ///   keystrokes can arrive between them and what is replayed no longer matches the order
    ///   the operator typed.
    ///
    ///   The list is reused rather than reallocated: replay happens on the hook callback path,
    ///   and the GC pause an allocation can trigger is among the things able to blow the
    ///   LowLevelHooksTimeout budget.
    /// </summary>
    private readonly List<KeyEvent> _replayBatch = [];

    private LowLevelKeyboardHook? _hook;
    private RawInputKeyboardListener? _rawInput;

    private long _boundDeviceHandle;
    private long _maximumHookCallbackTicks;
    private long _hookCallbackBudgetExceededCount;
    private long _faultCount;
    private Exception? _lastFault;

    /// <summary>
    /// 中文：
    ///   构造输入来源。此时不启动，调用 <see cref="Start"/> 才开始拦截。
    ///   输入：coordinator 输入状态机；correlator 该状态机使用的关联器。
    ///         两者不得为 null，且必须是**同一条**流水线上的那一对。
    ///
    ///   关联器要单独传进来，是因为绑定扫码枪要设它的 BoundScannerDeviceId，
    ///   而协调器没有把这个转出来。传错一个（比如传另一个关联器）的后果是
    ///   绑定形同虚设：界面显示已绑定，实际什么都不吞。
    /// English:
    ///   Creates the source without starting it; <see cref="Start"/> begins interception.
    ///   coordinator and correlator must both be given and must be the pair belonging to the
    ///   same pipeline.
    ///
    ///   The correlator is passed separately because binding a scanner sets its
    ///   BoundScannerDeviceId and the coordinator does not expose it. Passing the wrong one —
    ///   a different correlator — makes binding a no-op: the UI shows a bound scanner while
    ///   nothing is ever swallowed.
    /// </summary>
    /// <exception cref="ArgumentNullException">
    /// 中文：任一参数为 null。 English: Either argument is null.
    /// </exception>
    public Win32ScannerInputSource(ScanInputCoordinator coordinator, IInputEventCorrelator correlator)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(correlator);

        _coordinator = coordinator;
        _correlator = correlator;
        _coordinator.ReplayRequested += OnReplayRequested;

        _host = new MessageOnlyCaptureHost(this, "ScannerHelper scanner input");
    }

    /// <summary>
    /// 中文：是否正在拦截。
    /// English: Whether interception is running.
    /// </summary>
    public bool IsRunning => _host.IsRunning;

    /// <summary>
    /// 中文：
    ///   是否处于暂停（规格 §5.7）。
    ///
    ///   ★ 这个值在捕获线程上变、在界面线程上读，因此只适合显示。想据它做
    ///     判断的地方要留意：读到 false 的下一刻它可能已经是 true 了。
    ///     真正的顺序保证来自"暂停这个动作本身也投递到捕获线程"——见
    ///     <see cref="Pause"/>。
    /// English:
    ///   Whether the pipeline is paused (spec §5.7).
    ///
    ///   The value changes on the capture thread and is read on the UI thread, so it suits
    ///   display rather than decisions: a false read may already be true a moment later. The
    ///   real ordering guarantee comes from pausing itself being posted to the capture thread —
    ///   see <see cref="Pause"/>.
    /// </summary>
    public bool IsPaused => _coordinator.IsPaused;

    /// <summary>
    /// 中文：
    ///   当前绑定的扫码枪设备句柄，0 表示未绑定。
    ///
    ///   ★ 这是**会话内**的句柄，绝不可持久化。跨会话的绑定身份是设备路径
    ///     （规格 §6，见 ScannerDeviceIdentity）。
    /// English:
    ///   The bound scanner's device handle, zero meaning unbound.
    ///
    ///   It is session-scoped and must never be persisted; the cross-session binding identity
    ///   is the device path (spec §6, see ScannerDeviceIdentity).
    /// </summary>
    public nint BoundDeviceHandle => (nint)Interlocked.Read(ref _boundDeviceHandle);

    /// <summary>
    /// 中文：是否已绑定扫码枪。未绑定时整条流水线被旁路，什么都不吞。
    /// English: Whether a scanner is bound. While unbound the pipeline is bypassed and nothing
    ///          is swallowed.
    /// </summary>
    public bool IsBound => Interlocked.Read(ref _boundDeviceHandle) != 0;

    /// <summary>
    /// 中文：
    ///   钩子回调实测的最长耗时。
    ///   在捕获线程上写、在界面线程上读，因此只用于显示，不要拿去做判断。
    /// English:
    ///   The longest hook callback measured. Written on the capture thread and read on the UI
    ///   thread, so display it rather than branching on it.
    /// </summary>
    public TimeSpan MaximumHookCallbackDuration
        => StopwatchSystemClock.ToMonotonic(Interlocked.Read(ref _maximumHookCallbackTicks));

    /// <summary>
    /// 中文：
    ///   钩子回调超过 <see cref="HookCallbackBudget"/> 的次数。
    ///   不为零就值得查——它在告诉你离"钩子被悄悄摘掉"还有多远（规格 §19.1）。
    /// English:
    ///   How many hook callbacks exceeded <see cref="HookCallbackBudget"/>. Anything but zero
    ///   is worth investigating: it says how close this is to the hook being silently removed
    ///   (spec §19.1).
    /// </summary>
    public long HookCallbackBudgetExceededCount => Interlocked.Read(ref _hookCallbackBudgetExceededCount);

    /// <summary>
    /// 中文：流水线抛出异常的次数。
    /// English: How many times the pipeline threw.
    /// </summary>
    public long FaultCount => Interlocked.Read(ref _faultCount);

    /// <summary>
    /// 中文：最近一次异常，null 表示没有出过错。
    /// English: The most recent exception, or null if there has been none.
    /// </summary>
    public Exception? LastFault => Volatile.Read(ref _lastFault);

    /// <summary>
    /// 中文：
    ///   启动拦截。阻塞等待窗口、Raw Input 注册与钩子就绪。
    ///
    ///   ★ 启动之后并不会立刻吞掉任何东西：默认未绑定扫码枪，流水线被旁路。
    ///     要真的开始拦截，得先 <see cref="BindScanner"/>。
    /// English:
    ///   Starts interception, blocking until the window, the Raw Input registration and the
    ///   hook are ready.
    ///
    ///   Nothing is swallowed immediately afterwards: no scanner is bound by default and the
    ///   pipeline is bypassed. Interception begins only after <see cref="BindScanner"/>.
    /// </summary>
    /// <exception cref="Win32Exception">
    /// 中文：窗口创建、Raw Input 注册或钩子安装失败。
    /// English: Creating the window, registering Raw Input, or installing the hook failed.
    /// </exception>
    public void Start() => _host.Start();

    /// <summary>
    /// 中文：
    ///   停止拦截。
    ///
    ///   还扣留着的按键会在捕获线程收尾时放出去并补发（见 OnCaptureStopping），
    ///   不在这里做：那些队列属于捕获线程，而且放出去要摘掉钩子之后才最干净。
    /// English:
    ///   Stops interception.
    ///
    ///   Anything still withheld is released and replayed during the capture thread's teardown
    ///   (see OnCaptureStopping) rather than here: those queues belong to that thread, and the
    ///   release is cleanest once the hook has been removed.
    /// </summary>
    public void Stop() => _host.Stop();

    /// <inheritdoc />
    public void Dispose()
    {
        _coordinator.ReplayRequested -= OnReplayRequested;
        _host.Dispose();
    }

    /// <summary>
    /// 中文：
    ///   绑定一把扫码枪，从此它的输入会被拦截。
    ///   输入：deviceHandle Raw Input 报告的设备句柄，不得为 0。
    ///
    ///   ★ 绑定之前先把已经扣留着的按键放出去。绑定的那一刻队列里那几下是在
    ///     "还没有扫码枪"的规则下扣留的，让它们跨过规则变更去适用新规则，
    ///     等于用一条工人按下时还不存在的规则去处置他的按键。
    ///
    ///   设置动作投递到捕获线程执行：关联器的状态归它所有，从界面线程直接改
    ///   就是在钩子回调正在读同一批队列的时候动它。
    /// English:
    ///   Binds a scanner, after which its input is intercepted. deviceHandle is the device
    ///   handle Raw Input reports and must not be zero.
    ///
    ///   Anything already withheld is released first. Those keystrokes were withheld under the
    ///   rule "there is no scanner", and carrying them across the rule change would judge the
    ///   operator's keypresses by a rule that did not exist when they were made.
    ///
    ///   The change is posted to the capture thread: the correlator's state belongs to it, and
    ///   assigning from the UI thread would mutate queues the hook callback is reading.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// 中文：deviceHandle 为 0。 English: deviceHandle is zero.
    /// </exception>
    public void BindScanner(nint deviceHandle)
    {
        if (deviceHandle == 0)
        {
            throw new ArgumentException(
                "设备句柄不得为 0；解绑请调用 UnbindScanner。"
                + " The device handle must not be zero; call UnbindScanner to unbind.",
                nameof(deviceHandle));
        }

        _host.Post(() =>
        {
            ReleaseWithheldKeystrokes();
            _correlator.BoundScannerDeviceId = deviceHandle;
            Interlocked.Exchange(ref _boundDeviceHandle, deviceHandle);
        });
    }

    /// <summary>
    /// 中文：
    ///   解绑扫码枪。流水线随即被旁路，什么都不再吞。
    ///
    ///   解绑不是"关掉功能"，而是回到默认的安全状态：不知道哪一把是扫码枪时，
    ///   宁可什么都不拦，也不要拦下工人的键盘。
    /// English:
    ///   Unbinds the scanner; the pipeline is bypassed and nothing is swallowed any more.
    ///
    ///   Unbinding is not "turning the feature off" but a return to the safe default: with no
    ///   knowledge of which device is the scanner, intercept nothing rather than intercept the
    ///   operator's keyboard.
    /// </summary>
    public void UnbindScanner()
        => _host.Post(() =>
        {
            ReleaseWithheldKeystrokes();
            _correlator.BoundScannerDeviceId = null;
            Interlocked.Exchange(ref _boundDeviceHandle, 0);
        });

    /// <summary>
    /// 中文：
    ///   进入暂停（规格 §5.7）。
    ///
    ///   ★ 这个动作绝大多数时候来自鼠标点击，也就是界面线程——而
    ///     <see cref="ScanInputCoordinator.Pause"/> 会释放被扣留的按键、
    ///     会动关联器的队列，那些状态属于捕获线程。所以必须投递过去。
    ///     直接调是一个不报任何错、只在现场偶发的数据竞争。
    /// English:
    ///   Enters PAUSED (spec §5.7).
    ///
    ///   The action almost always comes from a mouse click, meaning the UI thread — while
    ///   <see cref="ScanInputCoordinator.Pause"/> releases withheld keystrokes and mutates the
    ///   correlator's queues, state that belongs to the capture thread. So it is posted.
    ///   Calling directly is a data race that raises nothing and appears only occasionally, on
    ///   site.
    /// </summary>
    public void Pause()
        => _host.Post(() =>
        {
            _coordinator.Pause();
            FlushReplays();
        });

    /// <summary>
    /// 中文：
    ///   离开暂停（规格 §5.7）。
    ///
    ///   ★ 恢复时必须重置解码器自己维护的按键状态。
    ///     暂停期间所有按键都直接放行，我们没有观察到它们的弹起——例如暂停
    ///     前 Shift 正按着，恢复后我们仍以为它按着，之后每一个字母都会解成
    ///     大写。CapsLock 的切换态不受影响，因此保留（见 ResetState）。
    /// English:
    ///   Leaves PAUSED (spec §5.7).
    ///
    ///   The decoder's tracked key state must be reset on resume. While paused every keystroke
    ///   passed straight through and their releases went unobserved: with Shift held at the
    ///   moment of pausing we would still believe it held, and every later letter would decode
    ///   as a capital. CapsLock's toggle is unaffected and survives (see ResetState).
    /// </summary>
    public void Resume()
        => _host.Post(() =>
        {
            _decoder.ResetState();
            _coordinator.Resume();
        });

    /// <summary>
    /// 中文：
    ///   列出当前接在机器上的键盘类设备。给绑定界面用。
    ///   可以从任意线程调用：它只问 Windows 要一份快照，不碰流水线状态。
    /// English:
    ///   Lists the keyboard-class devices currently attached, for the binding UI. Callable from
    ///   any thread: it asks Windows for a snapshot and touches no pipeline state.
    /// </summary>
    public IReadOnlyList<(nint Handle, ScannerDeviceIdentity Identity)> EnumerateKeyboards()
        => _resolver.EnumerateKeyboards();

    /// <summary>
    /// 中文：查出某个设备句柄的身份。可以从任意线程调用。
    /// English: Resolves one device handle's identity. Callable from any thread.
    /// </summary>
    public ScannerDeviceIdentity ResolveDevice(nint deviceHandle) => _resolver.Resolve(deviceHandle);

    /// <inheritdoc />
    TimeSpan ICaptureThreadWork.TickInterval => TickInterval;

    /// <summary>
    /// 中文：
    ///   在捕获线程上装好两条通道。
    ///   顺序有意为之：**先注册 Raw Input，再安装钩子**。
    ///
    ///   反过来的话，在 Raw Input 注册完成之前那一小段时间里钩子已经在扣留
    ///   按键，而这些事件永远等不到揭示来源的对家，只能一路等到超时才被补发。
    ///   表现就是"程序一启动，最开始按的那几下要过一会儿才出现"。
    /// English:
    ///   Installs both channels on the capture thread, Raw Input first and the hook second.
    ///
    ///   The other order leaves a window in which the hook is already withholding keystrokes
    ///   whose counterparts can never arrive to reveal their source, so they wait out the full
    ///   timeout before being replayed — presenting as "the first few keys after startup take a
    ///   moment to appear".
    /// </summary>
    void ICaptureThreadWork.OnCaptureStarted(IntPtr windowHandle)
    {
        _rawInput = new RawInputKeyboardListener(OnRawInputObserved);
        _rawInput.Register(windowHandle);

        _hook = new LowLevelKeyboardHook(OnHookEvent);
        _hook.Install();
    }

    /// <inheritdoc />
    void ICaptureThreadWork.OnRawInputMessage(IntPtr rawInputHandle, long timestamp)
        => _rawInput?.HandleRawInput(rawInputHandle, timestamp);

    /// <summary>
    /// 中文：
    ///   周期推进：关联超时与扫描超时，随后把因此产生的补发一并发出去。
    ///   异常必须兜住：这里跑在消息循环上，抛出去会掀翻整条线程，而线程一停，
    ///   钩子还挂着却再也没人处理 WM_INPUT——被扣留的按键永远等不到结论。
    /// English:
    ///   The periodic advance for both timeouts, followed by whatever replay it produced.
    ///   Exceptions must be contained: this runs on the message loop and letting one escape
    ///   tears down the thread, leaving the hook installed with nothing processing WM_INPUT and
    ///   withheld keystrokes never reaching a verdict.
    /// </summary>
    void ICaptureThreadWork.OnTick()
    {
        try
        {
            _coordinator.Tick();
            FlushReplays();
        }
        catch (Exception tickException)
        {
            RecordFault(tickException);
        }
    }

    /// <summary>
    /// 中文：
    ///   在捕获线程上收尾。
    ///   步骤：
    ///     1. 先摘钩子——它影响全系统的每一次按键，最该先停；
    ///     2. 把还扣留着的按键放出去并补发；
    ///     3. 注销 Raw Input。
    ///
    ///   ★ 步骤 2 必须在步骤 1 **之后**。先放再摘的话，放出去的那一瞬间钩子
    ///     还挂着，补发的合成事件会再经过钩子一遍——虽然会被"合成事件立刻
    ///     放行"挡住（规格 §5.6），但那是多余的一圈；先摘干净再补，路径最短，
    ///     也不依赖任何别的守卫成立。
    /// English:
    ///   Teardown on the capture thread: (1) unhook first, since it affects every keystroke on
    ///   the machine; (2) release and replay whatever is still withheld; (3) unregister Raw
    ///   Input.
    ///
    ///   Step 2 must follow step 1. Released first, the replayed synthesized events would pass
    ///   through the still-installed hook once more — caught by "synthesized events pass through
    ///   immediately" (spec §5.6), but a needless round trip. Unhooking first is the shortest
    ///   path and rests on no other guard holding.
    /// </summary>
    void ICaptureThreadWork.OnCaptureStopping()
    {
        // 步骤 1 / Step 1
        _hook?.Dispose();
        _hook = null;

        // 步骤 2 / Step 2
        ReleaseWithheldKeystrokes();

        // 步骤 3 / Step 3
        _rawInput?.Dispose();
        _rawInput = null;
    }

    /// <summary>
    /// 中文：
    ///   钩子回调的处置。
    ///   步骤：
    ///     1. 未绑定扫码枪就直接放行，连协调器都不碰（见文件头）；
    ///     2. 计时，供预算统计；
    ///     3. 解码出字符，组装成 Core 的 KeyEvent；
    ///     4. 交给协调器，把它的答案翻译成给 Windows 的答案；
    ///     5. 把这一轮攒下的补发发出去。
    ///
    ///   ★ 异常一律兜住，且兜住之后一律**放行**。
    ///
    ///     异常穿回原生调用栈的后果是进程当场终止。而在放行与吞掉之间，
    ///     出错时只能选放行：吞掉一个不该吞的键，工人的键盘就少了一下，
    ///     笔记本工位没有备用键盘可插（规格假设 A4）；放行一个本该吞掉的
    ///     扫码枪字符，业务软件里多一个看得见、能改的字符。两个方向的代价
    ///     完全不对称。
    ///
    ///   ★ 步骤 3 对合成事件不解码。解码器维护的是**物理键盘**的修饰键状态，
    ///     而合成事件（含我们自己补发出去的那些）不是工人按的。何况我们的
    ///     文本输出走 KEYEVENTF_UNICODE，虚拟键恒为 0，拿去解码毫无意义。
    /// English:
    ///   The hook callback's verdict.
    ///   Steps: (1) pass through untouched while no scanner is bound, without consulting the
    ///   coordinator (see this file's header); (2) time the callback for the budget counter;
    ///   (3) decode the character and build Core's KeyEvent; (4) ask the coordinator and
    ///   translate its answer into Windows'; (5) send whatever replay accumulated.
    ///
    ///   Every exception is caught, and a caught exception always passes through. An exception
    ///   unwinding into native code terminates the process outright; and between passing through
    ///   and swallowing, a failure can only choose passing through. Swallow a key that should
    ///   not have been and the operator's keyboard loses a keystroke with no spare keyboard to
    ///   plug in (assumption A4); pass through a scanner character that should have been
    ///   swallowed and the business application gains one visible, correctable character. The
    ///   two directions are not comparable.
    ///
    ///   Step 3 does not decode synthesized events: the decoder tracks the *physical* keyboard's
    ///   modifier state, and a synthesized event — including our own replay — was not pressed by
    ///   the operator. Our text output uses KEYEVENTF_UNICODE with a virtual key of zero in any
    ///   case, which decoding would make no sense of.
    /// </summary>
    private HookDecision OnHookEvent(in ObservedInputEvent observedEvent)
    {
        // 步骤 1 / Step 1
        if (Interlocked.Read(ref _boundDeviceHandle) == 0)
        {
            return HookDecision.PassThrough;
        }

        // 步骤 2 / Step 2
        var startedAt = Stopwatch.GetTimestamp();

        try
        {
            // 步骤 3 / Step 3
            var keyEvent = new KeyEvent(
                Timestamp: StopwatchSystemClock.ToMonotonic(observedEvent.Timestamp),
                ScanCode: observedEvent.ScanCode,
                IsExtended: observedEvent.IsExtended,
                IsKeyUp: observedEvent.IsKeyUp,
                VirtualKey: observedEvent.VirtualKey,
                Character: observedEvent.IsInjected
                    ? null
                    : _decoder.Decode(observedEvent.VirtualKey, observedEvent.ScanCode, observedEvent.IsKeyUp),
                IsInjected: observedEvent.IsInjected);

            // 步骤 4 / Step 4
            var action = _coordinator.OnHookEvent(keyEvent);

            // 步骤 5 / Step 5
            FlushReplays();

            return action == HookAction.Swallow ? HookDecision.Swallow : HookDecision.PassThrough;
        }
        catch (Exception hookException)
        {
            RecordFault(hookException);
            return HookDecision.PassThrough;
        }
        finally
        {
            RecordCallbackDuration(Stopwatch.GetTimestamp() - startedAt);
        }
    }

    /// <summary>
    /// 中文：
    ///   一条 Raw Input 到手：它揭示了某次按键是谁按的。
    ///   之前被扣留的按键在这里得出结论，因此紧接着要把补发发出去。
    /// English:
    ///   One raw-input event arrived, revealing who pressed a key. Previously withheld
    ///   keystrokes reach their verdict here, so the replay batch is flushed straight after.
    /// </summary>
    private void OnRawInputObserved(in ObservedInputEvent observedEvent)
    {
        if (Interlocked.Read(ref _boundDeviceHandle) == 0)
        {
            return;
        }

        try
        {
            _coordinator.OnRawInputEvent(new RawInputEvent(
                Timestamp: StopwatchSystemClock.ToMonotonic(observedEvent.Timestamp),
                ScanCode: observedEvent.ScanCode,
                IsExtended: observedEvent.IsExtended,
                IsKeyUp: observedEvent.IsKeyUp,
                DeviceId: observedEvent.DeviceHandle));

            FlushReplays();
        }
        catch (Exception rawInputException)
        {
            RecordFault(rawInputException);
        }
    }

    /// <summary>
    /// 中文：
    ///   收到补发请求，先攒起来。
    ///
    ///   ★ 不在这里立刻发。一次协调器调用可能连着放出好几个按键（例如从
    ///     PAUSED 进入时的 Flush），逐个发就是逐次 SendInput，而两次调用
    ///     之间可能挤进真实按键，补发出去的顺序于是和工人按下的顺序不一样。
    ///     攒成一批、一次发完，Windows 保证这一批中间不会被插进别的输入。
    /// English:
    ///   A replay was requested; accumulate it.
    ///
    ///   Nothing is sent here. One coordinator call can release several keystrokes at once —
    ///   entering PAUSED flushes them all — and sending each on its own means one SendInput per
    ///   key, with real keystrokes able to arrive between calls so that what is replayed no
    ///   longer matches the order the operator typed. Batched into one call, Windows guarantees
    ///   nothing is interleaved.
    /// </summary>
    private void OnReplayRequested(object? sender, ReplayRequestedEventArgs eventArgs)
        => _replayBatch.Add(eventArgs.KeyEvent);

    /// <summary>
    /// 中文：
    ///   把攒下的按键补发出去。
    ///
    ///   ★ 失败必须记下来，绝不能静静吞掉：补发失败意味着工人的按键真的丢了。
    ///     最常见的原因是 UIPI——业务软件以更高的完整性级别运行，我们的合成
    ///     输入进不去（规格 §2.1 假设 A2 要求两者同级）。
    ///
    ///   无论成败都清空批次。不清的话，失败之后这一批会在下一次补发时又发
    ///   一遍——工人得到的是重复的字符，比丢一个更难查。
    /// English:
    ///   Sends the accumulated keystrokes.
    ///
    ///   Failure must be recorded and never swallowed: a failed replay means the operator's
    ///   keystrokes really were lost. The usual cause is UIPI — the business application runs at
    ///   a higher integrity level and our synthesized input cannot reach it (spec §2.1's
    ///   assumption A2 requires them to match).
    ///
    ///   The batch is cleared either way. Left in place after a failure it would be sent again
    ///   with the next flush, giving the operator duplicated characters — harder to diagnose
    ///   than a missing one.
    /// </summary>
    private void FlushReplays()
    {
        if (_replayBatch.Count == 0)
        {
            return;
        }

        try
        {
            _replay.Replay(_replayBatch);
        }
        catch (Exception replayException)
        {
            RecordFault(replayException);
        }
        finally
        {
            _replayBatch.Clear();
        }
    }

    /// <summary>
    /// 中文：
    ///   把关联器里扣留着的按键全部放出去并补发。
    ///
    ///   停止、绑定、解绑之前都要做这一步。规格 §19 明写不得无限期吞掉普通
    ///   键盘输入，而"程序停了，刚才那两下再也没出来"正是无限期。
    /// English:
    ///   Releases and replays everything the correlator is withholding.
    ///
    ///   Done before stopping, binding and unbinding. Spec §19 forbids swallowing ordinary
    ///   keyboard input indefinitely, and "the program stopped and those last two keys never
    ///   appeared" is indefinite.
    /// </summary>
    private void ReleaseWithheldKeystrokes()
    {
        try
        {
            var released = _correlator.Flush();

            for (var index = 0; index < released.Count; index++)
            {
                _replayBatch.Add(released[index].Event);
            }

            FlushReplays();
        }
        catch (Exception releaseException)
        {
            RecordFault(releaseException);
        }
    }

    /// <summary>
    /// 中文：记下一次回调耗时，更新最大值与超预算计数。
    /// English: Records one callback's duration, updating the maximum and the over-budget count.
    /// </summary>
    private void RecordCallbackDuration(long elapsedTicks)
    {
        if (elapsedTicks > Interlocked.Read(ref _maximumHookCallbackTicks))
        {
            Interlocked.Exchange(ref _maximumHookCallbackTicks, elapsedTicks);
        }

        if (StopwatchSystemClock.ToMonotonic(elapsedTicks) > HookCallbackBudget)
        {
            Interlocked.Increment(ref _hookCallbackBudgetExceededCount);
        }
    }

    /// <summary>
    /// 中文：
    ///   记下一次异常。
    ///
    ///   ★ 只记录，不在这里通知任何人。通知意味着调用订阅方的代码，而这可能
    ///     发生在钩子回调里——订阅方做什么、要多久，我们无从保证，而一次
    ///     超时就够 Windows 悄悄摘掉钩子（规格 §19.1）。界面按自己的节奏来读。
    /// English:
    ///   Records one exception.
    ///
    ///   It only records and notifies no one here. Notifying means running a subscriber's code,
    ///   possibly inside the hook callback, and what a subscriber does or how long it takes is
    ///   beyond our control — while one overrun is enough for Windows to silently remove the
    ///   hook (spec §19.1). The UI reads these at its own pace.
    /// </summary>
    private void RecordFault(Exception fault)
    {
        Interlocked.Increment(ref _faultCount);
        Volatile.Write(ref _lastFault, fault);
        Debug.WriteLine($"扫码流水线异常。 Scanner pipeline fault: {fault}");
    }
}
