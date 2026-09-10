// =============================================================================
// ScanDomainTypeTests.cs
//
// 中文：
//   Task 6 领域类型的守卫测试（对应 TEST_PLAN_TASK_6.md 的 EV1~EV5）。
//
//   与 ResultTypeGuardTests 同一性质：测的不是行为，而是**类型系统的形状**。
//   现有实现天然满足这些性质，因此写完即绿——它们的价值不在今天，而在拦住
//   将来那些"看起来很合理"的改动。
//
//   ★ 关于 EV6（Core 里不得出现面向用户的文案）。
//
//     本文件**没有**为它单独写测试，因为它已经被覆盖了：
//     ResultTypeGuardTests 的 D6 扫的是 `typeof(ParseResult).Assembly` 的**全部
//     导出类型**，Task 6 这几个类型自然落在其中。再写一条只会得到一条重复的
//     测试，而重复的守卫比缺失的守卫更麻烦——将来改动时两处都要改，
//     漏改一处就变成两条互相矛盾的规则。
//
//     这里记下这件事，是因为"为什么没有 EV6"本身需要一个答案，否则下一个
//     读测试计划的人会以为它被忘了。
//
// English:
//   Guard tests for Task 6's domain types (EV1–EV5 in TEST_PLAN_TASK_6.md).
//
//   The same character as ResultTypeGuardTests: these test the shape of the type system
//   rather than behavior. The implementation satisfies them by construction, so they pass on
//   arrival; their value is in blocking future changes that look reasonable in isolation.
//
//   On EV6 — no user-facing prose in Core — there is deliberately no separate test here,
//   because it is already covered: ResultTypeGuardTests' D6 sweeps every exported type of
//   typeof(ParseResult).Assembly, which these types belong to. A second test would only
//   duplicate it, and a duplicated guard is worse than a missing one: both must change
//   together, and missing one leaves two rules contradicting each other.
//
//   This is recorded because "why is there no EV6" needs an answer, or the next person
//   reading the test plan assumes it was forgotten.
//
// 包含的测试 / Tests in this file:
//   Key_event_carries_everything_the_pipeline_needs   EV1
//   Correlation_identity_excludes_the_virtual_key     EV2
//   Raw_input_event_carries_a_device_and_the_same_identity  EV3
//   Failed_scan_exposes_no_completed_value            EV4
//   Failed_scan_retains_what_was_received             EV5
// =============================================================================

using System.Reflection;
using ScannerHelper.Core.Domain;

namespace ScannerHelper.Core.Tests.Domain;

public class ScanDomainTypeTests
{
    /// <summary>
    /// 中文：
    ///   EV1 —— KeyEvent 带齐了整条链路需要的东西。
    ///
    ///   字符是**可空**的，这一点值得单独断言：修饰键不产出字符，用 null 表示
    ///   "没有字符"，而不是空格或空字符——扫描内容里空格是合法字符，两者混同
    ///   会让 Shift 悄悄变成一个空格混进条码里。
    /// English:
    ///   EV1 — KeyEvent carries everything the pipeline needs.
    ///
    ///   The character being nullable deserves its own assertion: modifiers produce none, and
    ///   null says "no character" rather than a space or a NUL. A space is a legitimate
    ///   character inside scanned content, so conflating them would quietly turn a Shift into
    ///   a space inside a barcode.
    /// </summary>
    [Fact]
    public void Key_event_carries_everything_the_pipeline_needs()
    {
        var keyEvent = new KeyEvent(
            Timestamp: TimeSpan.FromMilliseconds(120),
            ScanCode: 0x20,
            IsExtended: false,
            IsKeyUp: false,
            VirtualKey: 0x44,
            Character: 'D',
            IsInjected: false);

        Assert.Equal(TimeSpan.FromMilliseconds(120), keyEvent.Timestamp);
        Assert.Equal(0x20, keyEvent.ScanCode);
        Assert.Equal('D', keyEvent.Character);
        Assert.False(keyEvent.IsInjected);

        // 修饰键：有按键，没有字符 / a modifier: a keystroke with no character
        var shiftEvent = keyEvent with { ScanCode = 0x2A, VirtualKey = 0xA0, Character = null };
        Assert.Null(shiftEvent.Character);
    }

    /// <summary>
    /// 中文：
    ///   EV2 —— 关联身份里不含虚拟键码（决策 D-11）。
    ///
    ///   ★ 它看起来像是 D-11 的重复，其实不是：D-11 是**关联器**的性质，
    ///     本条是**类型**的性质。
    ///
    ///     把身份做成一个结构上装不下虚拟键码的类型，意味着将来任何一次
    ///     关联器重写都无法重新引入这个错误——它不可表达。只守住算法的话，
    ///     下一次重写仍然可以重蹈覆辙，而这个覆辙上一次花掉了一整轮实测
    ///     才被发现（Task 4a 第 2 节，四百多个配不上的事件）。
    ///
    ///   同时断言 KeyEvent 上**确实有**虚拟键码。这一点很重要：本条守的是
    ///   "虚拟键码可得，而身份刻意不用它"，不是"虚拟键码不存在"。若哪天
    ///   KeyEvent 把虚拟键码删了，本条会变绿但守卫已经名存实亡——加上这条
    ///   断言，那种情况会被拦下来。
    /// English:
    ///   EV2 — the correlation identity excludes the virtual key (decision D-11).
    ///
    ///   It looks like a restatement of D-11 and is not: D-11 is a property of the
    ///   *correlator*, this is a property of the *type*. Making the identity structurally
    ///   incapable of holding a virtual key means no future rewrite of the correlator can
    ///   reintroduce the fault — it is not expressible. Guarding the algorithm alone would
    ///   leave the next rewrite free to repeat a mistake that cost a full measurement run to
    ///   find (Task 4a §2, four hundred-odd unpaired events).
    ///
    ///   It also asserts that KeyEvent *does* have a virtual key, which matters: the guard is
    ///   "the virtual key is available and the identity deliberately declines it", not "there
    ///   is no virtual key". Were KeyEvent to drop the field, this would go green while the
    ///   guard became hollow; the extra assertion catches that.
    /// </summary>
    [Fact]
    public void Correlation_identity_excludes_the_virtual_key()
    {
        var identityPropertyNames = typeof(KeyIdentity)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .ToArray();

        Assert.DoesNotContain(identityPropertyNames,
            name => name.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("VkCode", StringComparison.OrdinalIgnoreCase));

        // 前提：虚拟键码确实是可得的 —— 守的是"可得而不用"
        // Premise: the virtual key really is available; the guard is "available and declined"
        Assert.NotNull(typeof(KeyEvent).GetProperty(nameof(KeyEvent.VirtualKey)));

        // 两条通道对修饰键给出不同虚拟键码时，身份仍然相同
        // With the channels disagreeing about a modifier's virtual key, the identity matches
        var hookShift = new KeyEvent(
            TimeSpan.Zero, ScanCode: 0x2A, IsExtended: false, IsKeyUp: false,
            VirtualKey: 0xA0, Character: null, IsInjected: false);

        var rawShift = new RawInputEvent(
            TimeSpan.Zero, ScanCode: 0x2A, IsExtended: false, IsKeyUp: false, DeviceId: 7);

        Assert.Equal(hookShift.Identity, rawShift.Identity);
    }

    /// <summary>
    /// 中文：
    ///   EV3 —— RawInputEvent 带设备身份，并给出与 KeyEvent 可直接比较的关联身份。
    ///
    ///   设备用 long 表示而不是原生句柄类型：Core 只拿它做相等比较，从不解释。
    ///   它是**会话内**标识，拔插一次就会变，绝不可持久化——跨会话的绑定身份
    ///   是设备路径（规格 §6）。
    /// English:
    ///   EV3 — RawInputEvent carries a device identity and an identity directly comparable
    ///   with KeyEvent's. The device is a long rather than a native handle: Core only compares
    ///   it and never interprets it. It is session-scoped, changes across a replug, and must
    ///   never be persisted — the cross-session binding identity is the device path (spec §6).
    /// </summary>
    [Fact]
    public void Raw_input_event_carries_a_device_and_the_same_identity()
    {
        var rawInputEvent = new RawInputEvent(
            Timestamp: TimeSpan.FromMilliseconds(5),
            ScanCode: 0x20,
            IsExtended: false,
            IsKeyUp: false,
            DeviceId: 1001);

        Assert.Equal(1001, rawInputEvent.DeviceId);
        Assert.Equal(typeof(long), typeof(RawInputEvent)
            .GetProperty(nameof(RawInputEvent.DeviceId))!.PropertyType);

        Assert.Equal(typeof(KeyIdentity), typeof(RawInputEvent)
            .GetProperty(nameof(RawInputEvent.Identity))!.PropertyType);
    }

    /// <summary>
    /// 中文：
    ///   EV4 —— 失败的扫描结果不暴露任何"扫描内容"（决策 D-7）。
    ///
    ///   与 ParseResult 同样的构造：抽象基类型持有私有构造函数，分支全部 sealed，
    ///   外部无法派生出第三个分支，模式匹配因此穷尽。
    ///
    ///   与 D4 相同的诚实口径：完全封闭做不到——C# 为非 sealed 的 record 生成
    ///   一个 protected 拷贝构造函数，语言不允许把它声明为 private。这个洞在
    ///   实践中无关紧要，但测试不该断言一件不成立的事，所以这里只断言真正
    ///   成立的部分。
    /// English:
    ///   EV4 — a failed scan result exposes no scan content (decision D-7). The same
    ///   construction as ParseResult: a private constructor on the abstract base and sealed
    ///   cases, so no third case can be derived and matching is exhaustive.
    ///
    ///   The same honest accounting as D4: full closure is unattainable, since C# generates a
    ///   protected copy constructor for a non-sealed record and forbids declaring it private.
    ///   The hole does not matter in practice, but a test must not assert something untrue, so
    ///   only what holds is asserted.
    /// </summary>
    [Fact]
    public void Failed_scan_exposes_no_completed_value()
    {
        string[] completedValuePropertyNames = ["RawCode", "Value", "Result", "ScannedCode"];

        var failurePropertyNames = typeof(ScanResult.Failed)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();

        Assert.DoesNotContain(failurePropertyNames,
            name => completedValuePropertyNames.Contains(name));

        var parameterlessConstructors = typeof(ScanResult)
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(constructor => constructor.GetParameters().Length == 0)
            .ToArray();

        Assert.All(parameterlessConstructors, constructor =>
            Assert.True(constructor.IsPrivate,
                "ScanResult 的无参构造函数必须是 private，否则外部可以派生出新的结果分支，"
                + "模式匹配不再穷尽。"));

        Assert.True(typeof(ScanResult.Completed).IsSealed);
        Assert.True(typeof(ScanResult.Failed).IsSealed);
    }

    /// <summary>
    /// 中文：
    ///   EV5 —— 失败的扫描结果保留已经收到的部分内容。
    ///
    ///   ★ 它的用途与 ParseResult.Failure 的原始码**不同**，别混为一谈。
    ///
    ///     ParseResult.Failure 的原始码是给 F10 强制发送用的（规格 §10）。
    ///     ScanResult.Failed 的**不是**——规格 §5.4 对扫描超时写得很清楚：
    ///     丢弃这次不完整的扫描，不产生任何业务输入。半截码本来就不是完整条码，
    ///     发出去等于主动写入错误数据。
    ///
    ///     它是给错误界面和诊断日志用的（规格 §10、§15）。工人看到"扫到一半
    ///     就断了，收到的是 DGKJRD"，才知道是枪没对准还是条码破损；只说
    ///     "扫描超时"等于什么都没说。Task 4a 已实测到这类残缺确实会发生
    ///     （规格 §22.5）。
    ///
    ///   字段名叫 PartialRawCode 而不是 RawCode，正是为了让调用点一眼看出
    ///   它不是一个完整的条码——本条顺带把这个命名钉住。
    /// English:
    ///   EV5 — a failed scan retains what had been received.
    ///
    ///   Its purpose differs from ParseResult.Failure's raw code and must not be conflated.
    ///   That one exists for Force Send (spec §10); this one does not — spec §5.4 requires a
    ///   timed-out scan to be discarded with no business input produced, since half a code was
    ///   never a complete barcode and emitting it would volunteer bad data.
    ///
    ///   This exists for the error display and the diagnostic log (spec §10, §15). "The scan
    ///   broke off after DGKJRD" tells the operator whether the gun was misaligned or the label
    ///   is damaged; "scan timeout" says nothing. Task 4a confirmed such damage genuinely
    ///   occurs (spec §22.5).
    ///
    ///   The field is named PartialRawCode rather than RawCode so a call site can see at a
    ///   glance that it is not a complete barcode, and this case pins that naming.
    /// </summary>
    [Fact]
    public void Failed_scan_retains_what_was_received()
    {
        var failed = new ScanResult.Failed(ScanFailureReason.InactivityTimeout, "DGKJRD");

        Assert.Equal("DGKJRD", failed.PartialRawCode);
        Assert.Equal(ScanFailureReason.InactivityTimeout, failed.Reason);

        // 命名本身是守卫的一部分 / the name is part of the guard
        Assert.NotNull(typeof(ScanResult.Failed).GetProperty("PartialRawCode"));
        Assert.Null(typeof(ScanResult.Failed).GetProperty("RawCode"));

        // 空串是合法的：终止符到达时缓冲区可能本来就是空的（决策 D-15）
        // An empty string is legitimate: the buffer may have been empty at the terminator (D-15)
        var empty = new ScanResult.Failed(ScanFailureReason.EmptyScan, string.Empty);
        Assert.Equal(string.Empty, empty.PartialRawCode);
    }
}
