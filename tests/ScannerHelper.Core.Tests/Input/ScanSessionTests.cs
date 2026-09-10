// =============================================================================
// ScanSessionTests.cs
//
// 中文：
//   扫描会话的行为测试（对应 TEST_PLAN_TASK_6.md 的 SS1~SS12）。
//
//   本文件里分量最重的两条：
//     SS5  超时边界。取在哪一侧对行为影响甚微，但必须明确并钉住，否则两次
//          改动可能各自换一个方向，而这类改动不会有任何东西变红。
//     SS8  失败之后必须复位。漏了的表现是"前半截是上一次、后半截是这一次"
//          的码——而 Task 4a 实测到**硬件本身**就会产生这个形状，两者在
//          现场无法分辨（规格 §22.5）。
//
// English:
//   Behavior tests for the scan session (SS1–SS12 in TEST_PLAN_TASK_6.md).
//
//   Two carry the most weight. SS5 pins the timeout boundary: which side it falls on barely
//   affects behavior, but leaving it unstated lets two successive changes each pick a
//   direction with nothing turning red. SS8 pins resetting after failure, whose absence
//   produces a code that is part previous scan and part current — a shape Task 4a found the
//   *hardware* producing, making the two indistinguishable on site (spec §22.5).
//
// 包含的测试 / Tests in this file:
//   Characters_accumulate_in_order                          SS1
//   Terminator_completes_the_scan_and_is_not_in_the_result  SS2
//   Inactivity_discards_the_incomplete_scan                 SS3
//   Timeout_runs_from_the_last_input_not_the_session_start  SS4
//   Timeout_boundary_is_inclusive                           SS5
//   Terminator_with_an_empty_buffer_fails                   SS6
//   Session_resets_after_completion                         SS7
//   Session_resets_after_timeout                            SS8
//   Modifier_keys_never_appear_as_characters                SS9
//   Whitespace_is_never_trimmed                             SS10
//   Scan_exceeding_the_maximum_length_fails                 SS11
//   Timeout_follows_the_injected_clock                      SS12
// =============================================================================

using ScannerHelper.Core.Domain;
using ScannerHelper.Core.Input;
using ScannerHelper.Core.Tests.TestDoubles;

namespace ScannerHelper.Core.Tests.Input;

public class ScanSessionTests
{
    private readonly TestSystemClock _clock = new();

    private ScanSession CreateSession(ScanSessionOptions? options = null)
        => new(_clock, options);

    /// <summary>
    /// 中文：制造一个携带指定字符的按下事件。扫描码与虚拟键码在本组测试里
    ///       无关紧要——会话只看字符（决策 D-14）。
    /// English: Builds a key-down carrying the given character. Scan codes and virtual keys
    ///          are irrelevant here: the session only looks at characters (decision D-14).
    /// </summary>
    private KeyEvent Character(char character)
        => new(_clock.MonotonicNow, ScanCode: 0x20, IsExtended: false, IsKeyUp: false,
            VirtualKey: 0x44, Character: character, IsInjected: false);

    private KeyEvent Modifier()
        => new(_clock.MonotonicNow, ScanCode: 0x2A, IsExtended: false, IsKeyUp: false,
            VirtualKey: 0xA0, Character: null, IsInjected: false);

    private KeyEvent KeyUp(char character)
        => Character(character) with { IsKeyUp = true };

    /// <summary>
    /// 中文：把一串字符依次喂进会话，每个字符之间推进 1 毫秒（贴近实测的
    ///       扫码枪节奏，约 1.1 毫秒）。返回最后一次 Accept 的结果。
    /// English: Feeds a string one character at a time, advancing 1 ms between them (close
    ///          to the measured scanner cadence of about 1.1 ms), returning the last result.
    /// </summary>
    private ScanResult? Feed(ScanSession session, string characters)
    {
        ScanResult? result = null;
        foreach (var character in characters)
        {
            result = session.Accept(Character(character));
            _clock.Advance(TimeSpan.FromMilliseconds(1));
        }

        return result;
    }

    /// <summary>
    /// 中文：SS1 —— 字符按到达顺序累积。用试点实际使用的条码内容，
    ///       让测试读起来贴近真实事件流。
    /// English: SS1 — characters accumulate in arrival order, using the pilot's actual
    ///          barcode so the test reads like the real event stream.
    /// </summary>
    [Fact]
    public void Characters_accumulate_in_order()
    {
        var session = CreateSession();

        Assert.Null(Feed(session, "DGKJRDC5679F5NF"));

        var result = session.Accept(Character('\r'));

        var completed = Assert.IsType<ScanResult.Completed>(result);
        Assert.Equal("DGKJRDC5679F5NF", completed.RawCode);
    }

    /// <summary>
    /// 中文：
    ///   SS2 —— 终止符结束扫描，且**不出现在结果里**（规格 §5.4、§10）。
    ///
    ///   整段原始扫描都被吞掉，扫码枪自带的这个回车也不例外。程序是否另外补
    ///   一个回车，由 AppendEnterAfterScan 这个独立设置决定（决策 D-10）。
    ///   两件事的关系是：**默认情况下网页收到的回车数是 0，而不是"少了一个"**
    ///   ——规格 §10 特意点明了这一点，因为很多网页表单靠回车提交。
    /// English:
    ///   SS2 — the terminator ends the scan and does not appear in the result (spec §5.4,
    ///   §10). The whole raw scan is swallowed, the scanner's own Enter included. Whether the
    ///   application emits one of its own is the separate AppendEnterAfterScan setting
    ///   (decision D-10). By default the page therefore receives zero Enters, not one fewer —
    ///   a point spec §10 makes explicitly, because web forms commonly submit on Enter.
    /// </summary>
    [Fact]
    public void Terminator_completes_the_scan_and_is_not_in_the_result()
    {
        var session = CreateSession();
        Feed(session, "ABC");

        var completed = Assert.IsType<ScanResult.Completed>(session.Accept(Character('\r')));

        Assert.Equal("ABC", completed.RawCode);
        Assert.DoesNotContain('\r', completed.RawCode);
        Assert.False(session.IsActive);
    }

    /// <summary>
    /// 中文：SS3 —— 超时后这次不完整的扫描被丢弃，携带已收到的部分内容。
    ///       部分内容只用于错误展示与诊断（规格 §10、§15），**不是**给 F10
    ///       强制发送用的——规格 §5.4 要求超时的扫描不产生任何业务输入。
    /// English: SS3 — on timeout the incomplete scan is discarded, carrying what had been
    ///          received. That partial content is for the error display and diagnostics
    ///          (spec §10, §15), not for Force Send: spec §5.4 requires a timed-out scan to
    ///          produce no business input at all.
    /// </summary>
    [Fact]
    public void Inactivity_discards_the_incomplete_scan()
    {
        var session = CreateSession();
        Feed(session, "DGKJRD");

        _clock.Advance(TimeSpan.FromMilliseconds(300));
        var failed = Assert.IsType<ScanResult.Failed>(session.CheckTimeout());

        Assert.Equal(ScanFailureReason.InactivityTimeout, failed.Reason);
        Assert.Equal("DGKJRD", failed.PartialRawCode);
        Assert.False(session.IsActive);
    }

    /// <summary>
    /// 中文：
    ///   SS4 —— 超时从**最后一次输入**算起，不是从会话开始算起。
    ///
    ///   从开始算的话，任何一枪只要总时长超过超时值就会被判定中断。按 Task 4a
    ///   实测的 1.1 毫秒字符间隔，300 毫秒的超时会把条码上限卡在约 270 个字符
    ///   ——听起来很安全，直到某个站点换了更长的编码。而那时的表现是"长条码
    ///   总是扫不全"，非常难联想到超时的算法。
    ///
    ///   本条让一枪的总时长远超超时值，但每个字符之间的间隔都很小，扫描必须
    ///   正常完成。
    /// English:
    ///   SS4 — the timeout runs from the last input, not from the session's start.
    ///
    ///   From the start, any scan whose total duration exceeded the timeout would be judged
    ///   broken. At Task 4a's measured 1.1 ms interval a 300 ms timeout would cap barcodes at
    ///   roughly 270 characters — safe-sounding until a site adopts a longer encoding, at
    ///   which point the symptom is "long barcodes never scan completely" and nothing points
    ///   at the timeout's arithmetic.
    ///
    ///   Here the scan's total duration far exceeds the timeout while every gap within it is
    ///   small, and the scan must complete normally.
    /// </summary>
    [Fact]
    public void Timeout_runs_from_the_last_input_not_the_session_start()
    {
        var session = CreateSession(new ScanSessionOptions
        {
            InactivityTimeout = TimeSpan.FromMilliseconds(300),
        });

        // 400 个字符、每个间隔 5 毫秒 —— 总时长 2 秒，远超 300 毫秒的超时
        // 400 characters 5 ms apart: two seconds in total, far past the 300 ms timeout
        for (var index = 0; index < 400; index++)
        {
            Assert.Null(session.Accept(Character('A')));
            _clock.Advance(TimeSpan.FromMilliseconds(5));
            Assert.Null(session.CheckTimeout());
        }

        var completed = Assert.IsType<ScanResult.Completed>(session.Accept(Character('\r')));
        Assert.Equal(400, completed.RawCode.Length);
    }

    /// <summary>
    /// 中文：
    ///   SS5 —— 超时边界：差一个刻度仍在进行，达到超时值即算超时。
    ///
    ///   ★ 边界取在哪一侧，对现场行为的影响小到可以忽略——差的是一个时钟刻度。
    ///     但它必须被**明确并钉住**，理由与行为无关：`>` 与 `>=` 的代码长度
    ///     一模一样，改动时不会有任何东西提醒你方向变了。两次独立的改动各自
    ///     选一个方向，就会来回摇摆，而每一次摇摆都悄无声息。
    ///
    ///     这类测试的价值不在于捍卫某个具体取值，而在于让"这里有一个决定"
    ///     变得可见。
    /// English:
    ///   SS5 — the timeout boundary: one tick short still runs, reaching the timeout ends it.
    ///
    ///   Which side the boundary falls on affects the shop floor negligibly — one clock tick.
    ///   But it must be stated and pinned for a reason unrelated to behavior: `>` and `>=`
    ///   are the same length of code, and changing one warns nobody that the direction moved.
    ///   Two independent changes each picking a side would oscillate, silently every time.
    ///
    ///   The value of a test like this is not defending a particular choice but making it
    ///   visible that a choice exists.
    /// </summary>
    [Fact]
    public void Timeout_boundary_is_inclusive()
    {
        var timeout = TimeSpan.FromMilliseconds(300);

        var stillScanning = CreateSession(new ScanSessionOptions { InactivityTimeout = timeout });
        stillScanning.Accept(Character('A'));
        _clock.Advance(timeout - TimeSpan.FromTicks(1));
        Assert.True(stillScanning.CheckTimeout() is null,
            "差一个刻度不算超时——这一侧的选择必须被钉住，因为 > 与 >= 的改动不会有任何提示。");

        _clock.Advance(TimeSpan.FromTicks(1));
        Assert.True(stillScanning.CheckTimeout() is ScanResult.Failed,
            "达到超时值即算超时。");
    }

    /// <summary>
    /// 中文：
    ///   SS6 —— 终止符到达时缓冲区为空，判为失败而不是"成功扫到空串"（决策 D-15）。
    ///
    ///   条码不可能是空的，所以终止符前面一个字符都没有，只能说明字符在到达
    ///   我们之前就全丢了——Task 4a 已实测到这条链路确实会丢字符（规格 §22.5）。
    ///
    ///   ★ 若当作成功，一个空串会一路走到输出，静静写进仓库系统。规格 §19.1
    ///     反复强调的正是这类"静默的错误数据"：程序看起来一切正常，界面显示
    ///     成功，而写进去的是错的。
    /// English:
    ///   SS6 — a terminator arriving with an empty buffer fails rather than succeeding with
    ///   an empty string (decision D-15). A barcode cannot be empty, so nothing before the
    ///   terminator means every character was lost before reaching us — and Task 4a confirmed
    ///   this path does lose characters (spec §22.5).
    ///
    ///   Treated as success, an empty string would travel all the way to output and quietly
    ///   enter the warehouse system: spec §19.1's silently wrong data, where the program looks
    ///   fine, the UI reports success, and what was written is wrong.
    /// </summary>
    [Fact]
    public void Terminator_with_an_empty_buffer_fails()
    {
        var session = CreateSession();

        var failed = Assert.IsType<ScanResult.Failed>(session.Accept(Character('\r')));

        Assert.Equal(ScanFailureReason.EmptyScan, failed.Reason);
        Assert.Equal(string.Empty, failed.PartialRawCode);
        Assert.False(session.IsActive);
    }

    /// <summary>
    /// 中文：SS7 —— 完成之后会话复位，下一枪从干净状态开始。
    /// English: SS7 — the session resets after completion and the next scan starts clean.
    /// </summary>
    [Fact]
    public void Session_resets_after_completion()
    {
        var session = CreateSession();

        Feed(session, "FIRST");
        Assert.IsType<ScanResult.Completed>(session.Accept(Character('\r')));

        Feed(session, "SECOND");
        var second = Assert.IsType<ScanResult.Completed>(session.Accept(Character('\r')));

        Assert.Equal("SECOND", second.RawCode);
    }

    /// <summary>
    /// 中文：
    ///   SS8 —— **超时之后也必须复位。**
    ///
    ///   ★ 这是本文件最重要的一条。完成之后复位几乎每个人都会写，失败之后复位
    ///     很容易漏——而漏了的表现是：这一枪超时失败了，半截码留在缓冲区里，
    ///     下一枪的字符接在它后面，产出一个"前半截是上一次、后半截是这一次"
    ///     的码。
    ///
    ///     Task 4a 的实测记录里恰好有这个形状：
    ///         期望  DGKJRDC5679F5NF
    ///         实得  DGKJRDC5679F5NDGKF5NF
    ///
    ///     而那一次是**硬件**造成的，Scanner Helper 根本没运行（规格 §22.5）。
    ///     这正是危险所在：若我们再制造一个同样形状的软件缺陷，现场看到的
    ///     现象一模一样，报告上完全分辨不出是谁造成的——排查会一直往硬件那边
    ///     找，而问题在软件里。
    /// English:
    ///   SS8 — the session must reset after a timeout too, and this is the most important
    ///   case here. Resetting after completion is written by almost everyone; resetting after
    ///   failure is easily missed, and when it is, a timed-out scan leaves half a code in the
    ///   buffer for the next scan's characters to append to, producing a code that is part
    ///   previous scan and part current.
    ///
    ///   Task 4a's measurements contain exactly that shape — DGKJRDC5679F5NDGKF5NF against an
    ///   expected DGKJRDC5679F5NF — and that instance came from the *hardware*, with Scanner
    ///   Helper not running at all (spec §22.5). Hence the danger: a software defect of the
    ///   same shape looks identical on site, and a bug report cannot say which caused it.
    ///   Investigation would keep pointing at the hardware while the fault sat in software.
    /// </summary>
    [Fact]
    public void Session_resets_after_timeout()
    {
        var session = CreateSession();

        // 第一枪扫到一半就断了 / the first scan breaks off part-way
        Feed(session, "DGKJRD");
        _clock.Advance(TimeSpan.FromMilliseconds(300));
        Assert.IsType<ScanResult.Failed>(session.CheckTimeout());

        // 第二枪必须是干净的 / the second must be clean
        Feed(session, "DGKJRDC5679F5NF");
        var second = Assert.IsType<ScanResult.Completed>(session.Accept(Character('\r')));

        Assert.True(second.RawCode == "DGKJRDC5679F5NF",
            "超时之后没有复位，上一枪的残留会接在这一枪前面，产出"
            + "「前半截是上一次、后半截是这一次」的码。Task 4a 实测到硬件本身"
            + $"就会产生这个形状，两者在现场无法分辨。实得：{second.RawCode}");
    }

    /// <summary>
    /// 中文：SS9 —— 修饰键不产出字符，但算作活动。
    ///       算作活动是必要的：Shift 按下也是扫码枪在活动的证据，不算的话，
    ///       "Shift 按下、然后正常继续"的序列会从 Shift 那一刻开始计时，
    ///       平白削掉一段超时余量。
    /// English: SS9 — modifiers produce no character but do count as activity. They must: a
    ///          Shift press is evidence the scanner is working, and ignoring it would start
    ///          the clock at the Shift and shave a stretch of headroom off the timeout for no
    ///          reason.
    /// </summary>
    [Fact]
    public void Modifier_keys_never_appear_as_characters()
    {
        var session = CreateSession();

        session.Accept(Modifier());
        Assert.True(session.IsActive, "修饰键应当算作活动，会话已经开始。");

        session.Accept(Character('A'));
        session.Accept(Modifier());
        session.Accept(Character('B'));

        var completed = Assert.IsType<ScanResult.Completed>(session.Accept(Character('\r')));
        Assert.Equal("AB", completed.RawCode);
    }

    /// <summary>
    /// 中文：SS10 —— 前后空白一律不修剪（决策 D-9）。
    ///       扫码枪本不该发出空白；真发出了，那是异常，应当由校验层暴露出来，
    ///       而不是被这一层悄悄抹掉。抹掉之后，异常就永远查不到了。
    /// English: SS10 — surrounding whitespace is never trimmed (decision D-9). A scanner
    ///          should not emit whitespace; if it does, that is an anomaly for validation to
    ///          surface rather than for this layer to hide. Hidden here, it can never be
    ///          found at all.
    /// </summary>
    [Fact]
    public void Whitespace_is_never_trimmed()
    {
        var session = CreateSession();

        Feed(session, "  AB  ");
        var completed = Assert.IsType<ScanResult.Completed>(session.Accept(Character('\r')));

        Assert.Equal("  AB  ", completed.RawCode);
    }

    /// <summary>
    /// 中文：
    ///   SS11 —— 超过长度上限时失败，缓冲区不会无限增长（决策 D-16）。
    ///
    ///   防的不是"条码太长"这种业务情况，而是某台设备被错认成扫码枪、于是
    ///   有人打字被当成了一次永不结束的扫描，或者某个键卡住了。规格 §19 要求
    ///   这类无法确信的情况安全失败并留下诊断，而不是让缓冲区一直涨。
    /// English:
    ///   SS11 — exceeding the length bound fails rather than letting the buffer grow
    ///   (decision D-16). It guards not "the barcode is too long" but a device mistaken for
    ///   the scanner turning somebody's typing into a scan that never ends, or a stuck key.
    ///   Spec §19 requires such a situation to fail safely and be surfaced rather than
    ///   degrade quietly.
    /// </summary>
    [Fact]
    public void Scan_exceeding_the_maximum_length_fails()
    {
        var session = CreateSession(new ScanSessionOptions { MaximumLength = 8 });

        Assert.Null(Feed(session, "ABCDEFGH"));

        var failed = Assert.IsType<ScanResult.Failed>(session.Accept(Character('I')));

        Assert.Equal(ScanFailureReason.TooLong, failed.Reason);
        Assert.Equal("ABCDEFGH", failed.PartialRawCode);
        Assert.False(session.IsActive);
    }

    /// <summary>
    /// 中文：SS12 —— 超时读的是注入的单调时钟，不是挂钟。
    ///       挂钟会因 NTP 校时跳变，用它算超时会在现场表现为"偶尔莫名其妙
    ///       丢一枪"，且完全无法复现。
    /// English: SS12 — the timeout reads the injected monotonic clock, not the wall clock,
    ///          which jumps under NTP correction and would present as "we occasionally lose a
    ///          scan for no reason", irreproducibly.
    /// </summary>
    [Fact]
    public void Timeout_follows_the_injected_clock()
    {
        var session = CreateSession();
        session.Accept(Character('A'));

        _clock.AdvanceWallClock(TimeSpan.FromHours(1));

        Assert.Null(session.CheckTimeout());
        Assert.True(session.IsActive);
    }

    /// <summary>
    /// 中文：弹起事件被忽略，不进缓冲区。
    ///       一次按键的按下与弹起说的是同一件事，两个都算等于把每个字符数两遍。
    /// English: Key-ups are ignored and never buffered. A keystroke's down and up say the
    ///          same thing, and counting both counts every character twice.
    /// </summary>
    [Fact]
    public void Key_up_events_are_ignored()
    {
        var session = CreateSession();

        session.Accept(Character('A'));
        session.Accept(KeyUp('A'));
        session.Accept(Character('B'));
        session.Accept(KeyUp('B'));

        var completed = Assert.IsType<ScanResult.Completed>(session.Accept(Character('\r')));
        Assert.Equal("AB", completed.RawCode);
    }

    /// <summary>
    /// 中文：非法配置在构造时即被拒绝（与决策 D-2 一脉相承）。
    ///       超时取零意味着每个字符到达的同一刻就超时，任何一枪都无法完成——
    ///       现场表现为"扫码完全没反应"，而这与硬件故障看起来一模一样。
    /// English: Invalid configuration is rejected at construction, consistent with decision
    ///          D-2. A zero timeout expires at the instant each character arrives so no scan
    ///          can ever complete, presenting as "scanning does nothing at all" — identical in
    ///          appearance to a hardware fault.
    /// </summary>
    [Fact]
    public void Invalid_options_are_rejected_at_construction()
    {
        Assert.Throws<ArgumentNullException>(() => new ScanSession(null!));

        Assert.Throws<ArgumentOutOfRangeException>(() => new ScanSession(
            _clock, new ScanSessionOptions { InactivityTimeout = TimeSpan.Zero }));

        Assert.Throws<ArgumentOutOfRangeException>(() => new ScanSession(
            _clock, new ScanSessionOptions { MaximumLength = 0 }));
    }
}
