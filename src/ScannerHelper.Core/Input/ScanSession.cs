// =============================================================================
// ScanSession.cs
//
// 中文：
//   一次原子的扫描：缓冲区、时间戳、终止符处理、超时状态、完成结果（规格 §17）。
//
//   ★★ 复位是本类最容易写漏、而且漏了之后最难查的一件事。
//
//     完成之后复位，几乎每个人都会写。**失败之后复位**则很容易漏——而漏了
//     的表现是：这一枪超时失败了，缓冲区里的半截码留着；下一枪的字符接在
//     它后面，产出一个"前半截是上一次、后半截是这一次"的码。
//
//     Task 4a 的实测记录里恰好有这个形状：
//         期望  DGKJRDC5679F5NF
//         实得  DGKJRDC5679F5NDGKF5NF        两次传输叠在一起
//
//     那一次是**硬件**造成的，Scanner Helper 根本没运行（规格 §22.5）。
//     这正是危险所在：若我们再制造一个同样形状的软件缺陷，现场看到的现象
//     一模一样，报告上完全分辨不出是谁造成的。SS8 就是为此存在的。
//
//   ★ 终止符被消费，绝不进入结果（规格 §5.4、§10）。
//
//     整段原始扫描都被吞掉，扫码枪自带的那个回车也不例外。程序是否另外补
//     一个回车，由 AppendEnterAfterScan 这个独立设置决定（决策 D-10）。
//     两件事的关系是：默认情况下网页收到的回车数是 **0**，而不是"少了一个"。
//
//   ★ 超时从**最后一次输入**算起，不是从会话开始算起。
//
//     从开始算的话，任何一枪只要总时长超过超时值就会被判定中断——按实测的
//     1.1 毫秒字符间隔，300 毫秒的超时会把条码上限卡在约 270 个字符。
//     听起来很安全，直到某个站点换了更长的编码。
//
//   本类不解码、不解析、不校验、不输出。它只回答一个问题：**这一枪收到了什么，
//   以及它是怎么结束的。** 内容合不合理是解析与校验层的事（规格 §9）。
//
// English:
//   One atomic scan: buffer, timestamps, terminator handling, timeout state, completion
//   result (spec §17).
//
//   Resetting is the easiest thing here to leave out and the hardest to diagnose once left
//   out. Resetting after completion is written by almost everyone; resetting after
//   *failure* is easily missed — and when it is, a timed-out scan leaves half a code in the
//   buffer, the next scan's characters append to it, and out comes a code that is part
//   previous scan and part current.
//
//   Task 4a's measurements happen to contain exactly that shape: DGKJRDC5679F5NDGKF5NF
//   against an expected DGKJRDC5679F5NF, two transmissions overlapping. That instance was
//   produced by the *hardware*, with Scanner Helper not running at all (spec §22.5), and
//   that is precisely the danger: a software defect of the same shape would look identical
//   on site, leaving a bug report unable to say which was responsible. SS8 exists for this.
//
//   The terminator is consumed and never appears in the result (spec §5.4, §10). The whole
//   raw scan is swallowed, the scanner's own Enter included; whether the application emits
//   an Enter of its own is the separate AppendEnterAfterScan setting (decision D-10). The
//   relationship between the two is that by default the page receives zero Enters, not one
//   fewer.
//
//   The timeout runs from the last input, not from the session's start. From the start,
//   any scan whose total duration exceeded the timeout would be judged broken — at the
//   measured 1.1 ms inter-character interval a 300 ms timeout would cap barcodes at roughly
//   270 characters, which sounds safe until a site adopts a longer encoding.
//
//   This class does not decode, parse, validate or emit. It answers one question: what did
//   this scan receive, and how did it end. Whether the content is plausible belongs to
//   parsing and validation (spec §9).
//
// 包含的成员 / Members in this file:
//   IsActive       是否有一枪正在进行
//   Accept         接纳一个按键事件，返回本枪的结局（尚未结束则为 null）
//   CheckTimeout   检查无活动超时
//   Reset          丢弃当前会话
// =============================================================================

using System.Text;
using ScannerHelper.Core.Abstractions;
using ScannerHelper.Core.Domain;

namespace ScannerHelper.Core.Input;

/// <summary>
/// 中文：一次原子的扫描。
/// English: One atomic scan.
/// </summary>
public sealed class ScanSession
{
    private readonly ISystemClock _clock;
    private readonly ScanSessionOptions _options;
    private readonly StringBuilder _buffer = new();

    /// <summary>
    /// 中文：最后一次收到输入的时刻。超时从这里算起，不是从会话开始算起。
    ///       null 表示当前没有会话在进行。
    /// English: When input was last received. The timeout runs from here, not from the
    ///          session's start. Null means no session is in progress.
    /// </summary>
    private TimeSpan? _lastInputAt;

    /// <summary>
    /// 中文：
    ///   构造扫描会话。
    ///   输入：clock 注入的时间源，不得为 null；options 配置，null 时取默认值。
    /// English:
    ///   Creates the session. clock must not be null; options defaults when null.
    /// </summary>
    /// <exception cref="ArgumentNullException">
    /// 中文：clock 为 null。 English: clock is null.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// 中文：超时非正，或最大长度小于 1。
    /// English: A non-positive timeout, or a maximum length below one.
    /// </exception>
    public ScanSession(ISystemClock clock, ScanSessionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(clock);

        var resolvedOptions = options ?? new ScanSessionOptions();

        if (resolvedOptions.InactivityTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), resolvedOptions.InactivityTimeout,
                "扫描无活动超时必须为正值。取零意味着每一个字符到达的同一刻就超时，"
                + "任何一枪都无法完成。"
                + " The scan inactivity timeout must be positive. Zero times out at the"
                + " instant each character arrives, so no scan can ever complete.");
        }

        if (resolvedOptions.MaximumLength < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), resolvedOptions.MaximumLength,
                "一次扫描允许的最大字符数必须大于等于 1。"
                + " The maximum scan length must be at least 1.");
        }

        _clock = clock;
        _options = resolvedOptions;
    }

    /// <summary>
    /// 中文：当前是否有一枪正在进行。
    /// English: Whether a scan is currently in progress.
    /// </summary>
    public bool IsActive => _lastInputAt is not null;

    /// <summary>
    /// 中文：
    ///   接纳一个按键事件。
    ///   输入：keyEvent 已确认来自扫码枪的按键。
    ///   输出：本枪已经结束时返回结局；仍在进行中返回 null。
    ///   步骤：
    ///     1. 弹起事件一律忽略——一次按键的按下与弹起说的是同一件事，
    ///        两个都算等于把每个字符数两遍，而且"按住多久"与"两个字符隔多久"
    ///        是不同的问题；
    ///     2. 记下本次输入时刻，超时从这里重新计算；
    ///     3. 没有字符的按键（修饰键）只算活动，不进缓冲区；
    ///     4. 终止符到达：缓冲区为空则失败（决策 D-15），否则完成；
    ///     5. 超出长度上限则失败（决策 D-16）；
    ///     6. 其余字符入缓冲区，返回 null 表示继续。
    ///
    ///   ★ 步骤 2 对修饰键同样执行。Shift 按下也是扫码枪在活动的证据，
    ///     不把它算作活动的话，一个"Shift 按下、然后正常继续"的序列会在
    ///     Shift 那一刻开始计时，平白削掉一段超时余量。
    ///
    ///   ★ 步骤 4 的空缓冲区判定：条码不可能是空的，所以终止符前面一个字符
    ///     都没有，只能说明字符在到达我们之前就全丢了。刻意不当作"成功扫到
    ///     空串"——那会让一个空值一路走到输出，静静写进仓库系统。
    /// English:
    ///   Accepts one keystroke already attributed to the scanner, returning how the scan
    ///   ended or null while it continues.
    ///   Steps: (1) ignore key-ups; (2) record the input time, restarting the timeout;
    ///   (3) a keystroke with no character (a modifier) counts as activity but is not
    ///   buffered; (4) on the terminator, fail if the buffer is empty (D-15) and otherwise
    ///   complete; (5) fail past the length bound (D-16); (6) buffer anything else.
    ///
    ///   Key-ups are ignored because a keystroke's down and up say the same thing, so
    ///   counting both counts every character twice — and "how long a key was held" is a
    ///   different question from "how far apart two characters were".
    ///
    ///   Step 2 applies to modifiers too. A Shift press is evidence the scanner is active,
    ///   and not counting it would start the clock at the Shift and shave a stretch of
    ///   headroom off the timeout for no reason.
    ///
    ///   Step 4's empty-buffer case: a barcode cannot be empty, so a terminator with
    ///   nothing before it means every character was lost before reaching us. Deliberately
    ///   not treated as a successful scan of "", which would let an empty value travel all
    ///   the way to output and quietly enter the warehouse system.
    /// </summary>
    public ScanResult? Accept(in KeyEvent keyEvent)
    {
        // 步骤 1 / Step 1
        if (keyEvent.IsKeyUp)
        {
            return null;
        }

        // 步骤 2 / Step 2
        _lastInputAt = keyEvent.Timestamp;

        // 步骤 3 / Step 3
        if (keyEvent.Character is not { } character)
        {
            return null;
        }

        // 步骤 4 / Step 4
        if (character == _options.Terminator)
        {
            return _buffer.Length == 0
                ? CompleteWith(new ScanResult.Failed(ScanFailureReason.EmptyScan, string.Empty))
                : CompleteWith(new ScanResult.Completed(_buffer.ToString()));
        }

        // 步骤 5 / Step 5
        if (_buffer.Length >= _options.MaximumLength)
        {
            return CompleteWith(
                new ScanResult.Failed(ScanFailureReason.TooLong, _buffer.ToString()));
        }

        // 步骤 6 / Step 6
        _buffer.Append(character);
        return null;
    }

    /// <summary>
    /// 中文：
    ///   检查无活动超时。
    ///   输入：无（时间经 ISystemClock 读取）。
    ///   输出：已超时则返回失败结局，否则 null。
    ///
    ///   ★ 调用方必须**周期性**调用它，不能只在有事件时调用。
    ///
    ///     扫描中断的定义就是"不再有字符到来"。若只在收到事件时才检查，
    ///     一枪扫到一半彻底断掉之后，会一直挂在进行中状态，直到下一枪的第一个
    ///     字符到来——而那时它已经把半截旧码和新码接在一起了，正是本类文件头
    ///     所说的那个最难查的故障。
    ///
    ///   超时判定用 >=：达到超时值即算超时。边界取在哪一侧对行为影响甚微，
    ///   但必须明确并被测试钉住（用例 SS5），否则两次改动可能各自换一个方向。
    /// English:
    ///   Checks the inactivity timeout, returning a failure once it has elapsed.
    ///
    ///   The caller must call this periodically rather than only when events arrive. A
    ///   broken scan is by definition one where no further character comes; checking only on
    ///   events would leave a half-finished scan sitting in progress until the *next* scan's
    ///   first character arrived — by which point it has joined half an old code to a new
    ///   one, the hardest-to-diagnose failure described in this file's header.
    ///
    ///   The comparison is >=: reaching the timeout counts as timed out. Which side the
    ///   boundary falls on barely affects behavior, but it must be stated and pinned by a
    ///   test (case SS5), or two successive changes may each pick a different direction.
    /// </summary>
    public ScanResult? CheckTimeout()
    {
        if (_lastInputAt is not { } lastInputAt)
        {
            return null;
        }

        if (_clock.MonotonicNow - lastInputAt < _options.InactivityTimeout)
        {
            return null;
        }

        return CompleteWith(
            new ScanResult.Failed(ScanFailureReason.InactivityTimeout, _buffer.ToString()));
    }

    /// <summary>
    /// 中文：丢弃当前会话，回到没有扫描在进行的状态。
    ///       从 PAUSED 恢复、重新绑定扫码枪之后应当调用——那些时刻之前攒下的
    ///       半截内容都已经失去意义，留着只会混进下一枪。
    /// English: Discards the current session. Call it after resuming from PAUSED or
    ///          rebinding the scanner: whatever had accumulated has lost its meaning and
    ///          would otherwise bleed into the next scan.
    /// </summary>
    public void Reset()
    {
        _buffer.Clear();
        _lastInputAt = null;
    }

    /// <summary>
    /// 中文：
    ///   收尾：先复位，再交出结局。
    ///
    ///   ★ 顺序不能反，而且**每一条结束路径都必须经过这里**。
    ///
    ///     无论完成还是失败，会话都要立刻回到干净状态。失败之后忘记复位，
    ///     半截码会留在缓冲区里，与下一枪接成一个"前半截是上一次"的码——
    ///     而 Task 4a 已实测到硬件本身就会产生这个形状（规格 §22.5），
    ///     两者在现场无法分辨。
    ///
    ///     把复位收进这一个方法，而不是在四条结束路径上各写一次，正是为了
    ///     让"漏掉一条"变得不可能。
    /// English:
    ///   Finishes up: reset first, then hand back the result.
    ///
    ///   The order matters and every terminating path must go through here. Completed or
    ///   failed, the session returns to a clean state immediately. Forgetting to reset after
    ///   a failure leaves half a code in the buffer to be joined to the next scan — and Task
    ///   4a found the hardware producing that same shape (spec §22.5), making the two
    ///   indistinguishable on site.
    ///
    ///   Folding the reset into this one method, rather than repeating it on four
    ///   terminating paths, is what makes missing one impossible.
    /// </summary>
    private ScanResult CompleteWith(ScanResult result)
    {
        Reset();
        return result;
    }
}
