// =============================================================================
// InterceptionPipeline.cs
//
// 中文：
//   Task 4b 的组装根：把 Core 的纯逻辑与 Win32 的原生实现拼成一条完整的
//   流水线，供诊断工具驱动。
//
//   ★ 这是整个项目里第一次把两边真正接在一起，因此也是第一次能问出
//     「它到底行不行」——4b 是规格明写的硬性关卡，答案只能来自真实硬件。
//
//   ★ 界面不直接碰 Core 的类型，也不直接订阅协调器的事件。
//
//     协调器的 ScanProcessed 是在**钩子回调里**触发的（终止符从钩子通道来，
//     一枪收完这件事就发生在回调内部）。界面若直接订阅，WPF 的事件处理、
//     数据绑定、控件刷新就全都跑进了那个必须极快的回调里——一次超时
//     Windows 就悄悄摘掉钩子（规格 §19.1）。
//
//     所以这里把结局塞进一个队列，界面按自己的节奏来取。队列每一枪入队一次，
//     不是每个按键一次，这点开销可以接受。
//
// English:
//   Task 4b's composition root: Core's pure logic and Win32's native implementation assembled
//   into one pipeline for the diagnostic tool to drive.
//
//   This is the first time the two halves are genuinely connected, and therefore the first time
//   the question "does it actually work" can be asked. 4b is a hard gate in the spec and the
//   answer can only come from real hardware.
//
//   The UI never touches Core's types directly and never subscribes to the coordinator's events.
//   ScanProcessed fires inside the hook callback — the terminator arrives on the hook channel,
//   so a scan completing happens within the callback — and a UI subscriber would drag WPF event
//   handling, data binding and control refreshes into a callback that must stay extremely fast.
//   One overrun and Windows silently removes the hook (spec §19.1).
//
//   So outcomes go into a queue and the UI drains it at its own pace. One enqueue per scan
//   rather than per keystroke, which is affordable.
//
// 包含的类型 / Types in this file:
//   InterceptionPipeline  组装根
//   PipelineSnapshot      给界面看的一份即时快照
//   ScanLogEntry          一枪的结局
// =============================================================================

using System.Collections.Concurrent;
using System.Globalization;
using ScannerHelper.Core.Domain;
using ScannerHelper.Core.Input;
using ScannerHelper.Core.Modes;
using ScannerHelper.Core.Parsing;
using ScannerHelper.Core.Settings;
using ScannerHelper.Core.Validation;
using ScannerHelper.Win32;

namespace ScannerHelper.Diagnostics.Harness.Interception;

/// <summary>
/// 中文：一枪的结局，已经转成界面能直接显示的文本。
/// English: One scan's outcome, already turned into text the UI can show directly.
/// </summary>
/// <param name="At">中文：处理完成的挂钟时刻。 English: The wall-clock time it finished.</param>
/// <param name="Mode">中文：当时的模式。 English: The mode in force.</param>
/// <param name="RawCode">中文：扫码枪发来的原始内容。 English: What the scanner sent.</param>
/// <param name="Result">中文：结局描述。 English: A description of the outcome.</param>
/// <param name="IsFailure">中文：是否失败。 English: Whether it failed.</param>
public readonly record struct ScanLogEntry(
    DateTimeOffset At,
    ScanMode Mode,
    string RawCode,
    string Result,
    bool IsFailure);

/// <summary>
/// 中文：给界面看的一份即时快照。一次取齐，避免界面逐个属性去读而读到
///       彼此不一致的数字。
/// English: One instantaneous snapshot for the UI. Taken all at once so the UI does not read
///          properties one by one and end up displaying mutually inconsistent numbers.
/// </summary>
/// <remarks>
/// 中文：
///   ★ 这份快照里最重要的一个数字是 <c>UnresolvedEventCount</c>。
///
///     它数的是"扣留了却始终没等到 Raw Input、只好按超时规则当成普通键盘
///     补发出去"的按键（决策 D-13）。工人打字时它不为 0 只是延迟大了一点；
///     **扫码期间它不为 0，意味着条码的原始字符正以扫描码的形式漏进业务
///     软件**——正是规格禁止的那件事，而且现场看到的就是"结果里多了一两个
///     字母"、"一枪被回车劈成两行"。
///
///     其余的计数只说明流水线在动，只有这一个说明它做对没做对。
/// English:
///   The most important number here is UnresolvedEventCount: keystrokes that were withheld,
///   never met their Raw Input counterpart, and had to be released as ordinary typing by the
///   expiry rule (decision D-13). While a person types, a non-zero value only means slightly
///   more latency. During a scan it means the barcode's raw characters are leaking into the
///   business application as scan codes — exactly what the spec forbids, and what shows up on
///   screen as "an extra letter or two in the result" and "one scan split across two lines".
///
///   The other counters say the pipeline is moving; only this one says whether it is right.
/// </remarks>
public readonly record struct PipelineSnapshot(
    bool IsRunning,
    bool IsPaused,
    bool IsBound,
    bool ObserveOnly,
    ScanPipelineState State,
    ScanMode Mode,
    string? PendingErrorRawCode,
    long SwallowedCount,
    long PassedThroughCount,
    long ReplayedCount,
    long RawInputCount,
    long ScanCount,
    long UnresolvedEventCount,
    long DiscardedRawInputCount,
    TimeSpan MaximumHookCallbackDuration,
    long HookCallbackBudgetExceededCount,
    long FaultCount,
    string? LastFault);

/// <summary>
/// 中文：Task 4b 的组装根。
/// English: Task 4b's composition root.
/// </summary>
public sealed class InterceptionPipeline : IDisposable
{
    /// <summary>
    /// 中文：F8 的虚拟键码。规格 §13.3 的默认热键。
    /// English: F8's virtual key code, spec §13.3's default hotkey.
    /// </summary>
    private const ushort VkF8 = 0x77;

    /// <summary>中文：F10。 English: F10.</summary>
    private const ushort VkF10 = 0x79;

    /// <summary>中文：Esc。 English: Escape.</summary>
    private const ushort VkEscape = 0x1B;

    private readonly InputEventCorrelator _correlator;
    private readonly ScanInputCoordinator _coordinator;
    private readonly ModeManager _modeManager;
    private readonly Win32ScannerInputSource _source;

    /// <summary>
    /// 中文：
    ///   全部合成输出的出口。
    ///
    ///   ★ 它同时被两边持有：作为 IKeyboardOutputService 交给协调器（协调器
    ///     用它发扫描结果），也交给输入来源（输入来源用它排队补发、并在
    ///     消息循环上把队列排空）。共用一个实例不是省事，而是**必需**——
    ///     补发与文本输出的先后关系有意义，分成两个队列就等于把工人看到的
    ///     字符顺序交给两次独立的排空。详见 DeferredKeyboardOutput。
    /// English:
    ///   The single exit for all synthesized output.
    ///
    ///   Held by both sides: handed to the coordinator as its IKeyboardOutputService, and to the
    ///   input source, which queues replays into it and drains it on the message loop. Sharing
    ///   one instance is required rather than convenient — the ordering between replay and text
    ///   output carries meaning, and two queues would hand the order the operator sees to two
    ///   independent drains. See DeferredKeyboardOutput.
    /// </summary>
    private readonly DeferredKeyboardOutput _output = new();

    private readonly ConcurrentQueue<ScanLogEntry> _scanLog = new();

    private bool _isDisposed;

    /// <summary>
    /// 中文：
    ///   组装整条流水线。
    ///   输入：settings 使用的配置；null 表示用全新安装的默认值。
    ///
    ///   ★ 启动模式恒为 SN，且不持久化（规格 §5.2）。ModeManager 的默认值
    ///     已经是 SN，这里不去覆盖它，正是为了让"启动即 SN"只有一个出处。
    /// English:
    ///   Assembles the pipeline. settings may be null for a fresh install's defaults.
    ///
    ///   The startup mode is always SN and is never persisted (spec §5.2). ModeManager already
    ///   defaults to SN and is deliberately not overridden here, so that "starts in SN" has a
    ///   single origin.
    /// </summary>
    public InterceptionPipeline(AppSettings? settings = null)
    {
        var effectiveSettings = settings ?? new AppSettings();

        _correlator = new InputEventCorrelator(StopwatchSystemClock.Instance);
        _modeManager = new ModeManager();

        _coordinator = new ScanInputCoordinator(
            _correlator,
            new ScanSession(StopwatchSystemClock.Instance),
            _modeManager,
            SkuParserFactory.Create(effectiveSettings.SkuParsing),
            SkuValidatorFactory.Create(effectiveSettings.SkuValidation),
            _output,

            // 暂停/恢复刻意不绑热键（规格 §13.3）：安全阀存在的意义是救
            // "键盘失灵"，而给它配一个默认热键等于暗示"出事了按这个键"——
            // 那恰恰是最需要它时最没用的东西。界面上的按钮才是它的入口。
            //
            // Pause/Resume deliberately has no hotkey (spec §13.3): the valve exists to rescue
            // "the keyboard stopped working", and a default hotkey would suggest a keystroke is
            // the way out — exactly what fails when it is needed. The button is its entrance.
            new HotkeyCoordinator(new HotkeyBindings(
                ToggleMode: VkF8, ForceSend: VkF10, Cancel: VkEscape, PauseResume: null)));

        _coordinator.ScanProcessed += OnScanProcessed;
        _source = new Win32ScannerInputSource(_coordinator, _correlator, _output);
    }

    /// <summary>
    /// 中文：扫描后是否自动补一个回车（规格 §10，默认关闭）。
    /// English: Whether to append an Enter after a scan (spec §10, off by default).
    /// </summary>
    public bool AppendEnterAfterScan
    {
        get => _coordinator.AppendEnterAfterScan;
        set => _coordinator.AppendEnterAfterScan = value;
    }

    /// <summary>
    /// 中文：诊断用：只观测不吞掉。详见 <see cref="Win32ScannerInputSource.ObserveOnly"/>。
    /// English: Diagnostic: observe without swallowing. See
    ///          <see cref="Win32ScannerInputSource.ObserveOnly"/>.
    /// </summary>
    public bool ObserveOnly
    {
        get => _source.ObserveOnly;
        set => _source.ObserveOnly = value;
    }

    /// <summary>
    /// 中文：当前模式。
    /// English: The current mode.
    /// </summary>
    public ScanMode Mode => _modeManager.CurrentMode;

    /// <summary>
    /// 中文：是否正在拦截。
    /// English: Whether interception is running.
    /// </summary>
    public bool IsRunning => _source.IsRunning;

    /// <summary>
    /// 中文：列出当前接在机器上的键盘类设备。
    /// English: Lists the keyboard-class devices currently attached.
    /// </summary>
    public IReadOnlyList<(nint Handle, ScannerDeviceIdentity Identity)> EnumerateKeyboards()
        => _source.EnumerateKeyboards();

    /// <summary>
    /// 中文：启动拦截。此时还没有绑定扫码枪，因此什么都不会被吞掉。
    /// English: Starts interception. No scanner is bound yet, so nothing is swallowed.
    /// </summary>
    public void Start() => _source.Start();

    /// <summary>
    /// 中文：停止拦截。
    /// English: Stops interception.
    /// </summary>
    public void Stop() => _source.Stop();

    /// <summary>
    /// 中文：绑定一把扫码枪。绑定之后它的输入才开始被拦截。
    /// English: Binds a scanner; only then is its input intercepted.
    /// </summary>
    public void BindScanner(nint deviceHandle) => _source.BindScanner(deviceHandle);

    /// <summary>
    /// 中文：解绑。流水线随即被旁路。
    /// English: Unbinds; the pipeline is then bypassed.
    /// </summary>
    public void UnbindScanner() => _source.UnbindScanner();

    /// <summary>
    /// 中文：进入暂停（规格 §5.7）。
    /// English: Enters PAUSED (spec §5.7).
    /// </summary>
    public void Pause() => _source.Pause();

    /// <summary>
    /// 中文：离开暂停。
    /// English: Leaves PAUSED.
    /// </summary>
    public void Resume() => _source.Resume();

    /// <summary>
    /// 中文：
    ///   用鼠标切换 SN / SKU。
    ///
    ///   ★ F8 也能切，但 F8 只认非扫码枪的物理键盘（规格 §7），而在这个工具里
    ///     我们恰恰要单独验证「扫码枪发出的 F8 不切换模式」这一条。手上得有一个
    ///     不经过热键路由的切换途径，否则没法把两者分开验。
    /// English:
    ///   Toggles SN/SKU with the mouse.
    ///
    ///   F8 toggles too, but only from a non-scanner physical keyboard (spec §7) — and this tool
    ///   exists partly to verify that a scanner-emitted F8 does *not* toggle. Separating the two
    ///   requires a route that bypasses hotkey routing entirely.
    /// </summary>
    public void ToggleMode() => _modeManager.Toggle();

    /// <summary>
    /// 中文：强制发送当前待决的错误内容（F10 的等价动作，规格 §10）。
    /// English: Force-sends the pending error's raw content, F10's equivalent (spec §10).
    /// </summary>
    public bool ForceSend() => _coordinator.ForceSend();

    /// <summary>
    /// 中文：取消当前待决的错误（Esc 的等价动作）。
    /// English: Cancels the pending error, Escape's equivalent.
    /// </summary>
    public bool Cancel() => _coordinator.Cancel();

    /// <summary>
    /// 中文：取一份即时快照。
    /// English: Takes an instantaneous snapshot.
    /// </summary>
    public PipelineSnapshot TakeSnapshot()
        => new(
            IsRunning: _source.IsRunning,
            IsPaused: _source.IsPaused,
            IsBound: _source.IsBound,
            ObserveOnly: _source.ObserveOnly,
            State: _coordinator.State,
            Mode: _modeManager.CurrentMode,
            PendingErrorRawCode: _coordinator.PendingError?.RawCode,
            SwallowedCount: _source.SwallowedCount,
            PassedThroughCount: _source.PassedThroughCount,
            ReplayedCount: _source.ReplayedCount,
            RawInputCount: _source.RawInputCount,
            ScanCount: _source.ScanCount,
            UnresolvedEventCount: _correlator.UnresolvedEventCount,
            DiscardedRawInputCount: _correlator.DiscardedRawInputCount,
            MaximumHookCallbackDuration: _source.MaximumHookCallbackDuration,
            HookCallbackBudgetExceededCount: _source.HookCallbackBudgetExceededCount,
            FaultCount: _source.FaultCount,
            LastFault: _source.LastFault?.Message);

    /// <summary>
    /// 中文：取走自上次调用以来的所有扫描结局。
    /// English: Takes every scan outcome recorded since the previous call.
    /// </summary>
    public IReadOnlyList<ScanLogEntry> DrainScanLog()
    {
        List<ScanLogEntry>? drained = null;

        while (_scanLog.TryDequeue(out var entry))
        {
            drained ??= [];
            drained.Add(entry);
        }

        return drained ?? [];
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _coordinator.ScanProcessed -= OnScanProcessed;
        _source.Dispose();
    }

    /// <summary>
    /// 中文：
    ///   把一枪的结局记下来。
    ///
    ///   ★ 本方法跑在钩子回调里，所以只做「转成字符串、入队」这一件事。
    ///     格式化用的是不变文化：这里的文本是给工程师看的记录，不该随机器的
    ///     区域设置变形（那会让两台机器上导出的报告对不上）。
    /// English:
    ///   Records one scan's outcome.
    ///
    ///   This runs inside the hook callback, so it does exactly one thing: format and enqueue.
    ///   Formatting uses the invariant culture — this text is an engineer's record and should
    ///   not vary with a machine's regional settings, which would make reports exported on two
    ///   machines disagree.
    /// </summary>
    private void OnScanProcessed(object? sender, ScanProcessedEventArgs eventArgs)
    {
        var (rawCode, result, isFailure) = Describe(eventArgs.Outcome);

        _scanLog.Enqueue(new ScanLogEntry(
            At: DateTimeOffset.Now,
            Mode: _modeManager.CurrentMode,
            RawCode: rawCode,
            Result: result,
            IsFailure: isFailure));
    }

    /// <summary>
    /// 中文：把结局转成人能读的一行。
    /// English: Turns an outcome into one readable line.
    /// </summary>
    private static (string RawCode, string Result, bool IsFailure) Describe(ScanOutcome outcome)
        => outcome switch
        {
            ScanOutcome.Emit emit => (
                emit.RawCode,
                emit.Text == emit.RawCode
                    ? "已发出 / emitted"
                    : $"已发出 / emitted：{emit.Text}",
                false),

            ScanOutcome.ScanFailed failed => (
                failed.Failure.PartialRawCode,
                $"扫描未完成 / scan incomplete：{failed.Failure.Reason}",
                true),

            ScanOutcome.ParseFailed parseFailed => (
                parseFailed.Failure.RawCode,
                $"解析失败 / parse failed：{parseFailed.Failure.Reason}（等 F10 或 Esc）",
                true),

            ScanOutcome.ValidationFailed validationFailed => (
                validationFailed.RawCode,
                $"校验失败 / validation failed：{validationFailed.Sku} —— "
                + string.Join(
                    "，",
                    validationFailed.Failure.Failures.Select(DescribeValidationFailure))
                + "（等 F10 或 Esc）",
                true),

            _ => (string.Empty, outcome.ToString() ?? string.Empty, true),
        };

    /// <summary>
    /// 中文：把一条校验失败原因转成文本。
    /// English: Turns one validation failure into text.
    /// </summary>
    private static string DescribeValidationFailure(ValidationFailure failure)
        => failure switch
        {
            ValidationFailure.TooShort tooShort => string.Format(
                CultureInfo.InvariantCulture,
                "太短 {0}<{1}",
                tooShort.ActualLength,
                tooShort.MinimumLength),

            ValidationFailure.TooLong tooLong => string.Format(
                CultureInfo.InvariantCulture,
                "太长 {0}>{1}",
                tooLong.ActualLength,
                tooLong.MaximumLength),

            ValidationFailure.IllegalCharacter illegal => string.Format(
                CultureInfo.InvariantCulture,
                "非法字符 '{0}' @{1}",
                illegal.Character,
                illegal.Position),

            _ => failure.ToString() ?? string.Empty,
        };
}
