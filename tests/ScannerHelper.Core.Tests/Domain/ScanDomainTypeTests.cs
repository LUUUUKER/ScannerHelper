// =============================================================================
// ScanDomainTypeTests.cs
//
// 中文：
//   领域类型（架构变更后只剩 KeyEvent，用例 EV1）。
//
//   ★ 原来的 EV2~EV5 测的是 KeyIdentity、RawInputEvent、ScanResult——三个随
//     旧架构一起退役的类型。它们存在的理由是「把扫码枪的字符从键盘流里认出来」
//     以及「一枪可能收到一半就断了」，而这两件事在串口模式下都不存在：来源由
//     端口给出，帧边界由终止符给出。删掉它们不是放弃覆盖，是被覆盖的东西没有了。
//     见 ARCHITECTURE_CHANGE_SERIAL.md §4、TASK_4B_FINDING_20260910.md。
//
//   ★ KeyEvent 留下来了，但它现在**只服务于热键路由**（F8 / F10 / Esc）。
//     它上面的 Character 与 IsInjected 两个字段已经没有消费者——那是扣留-重放
//     时代的遗留。热键任务会决定用 RegisterHotKey 还是低层钩子，届时一并收拾；
//     在那之前先如实标注，免得有人以为它们还在被用。
//
// English:
//   Domain types; after the architecture change only KeyEvent remains (case EV1).
//
//   The former EV2–EV5 covered KeyIdentity, RawInputEvent and ScanResult, three types retired
//   with the old architecture. They existed to pick the scanner out of the keyboard stream and to
//   describe a scan that arrived half-finished, neither of which exists on a serial port: the port
//   gives the source and the terminator gives the boundary. Deleting them abandons no coverage;
//   what they covered is gone. See ARCHITECTURE_CHANGE_SERIAL.md §4 and
//   TASK_4B_FINDING_20260910.md.
//
//   KeyEvent stays, but now serves hotkey routing alone (F8/F10/Esc). Its Character and
//   IsInjected fields have no consumer left — leftovers from the withhold-and-replay era. The
//   hotkey task will decide between RegisterHotKey and a low-level hook and tidy them then; until
//   it does, this note says so plainly rather than letting someone assume they are still in use.
//
// 包含的测试 / Tests in this file:
//   Key_event_carries_everything_the_pipeline_needs   EV1
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
}
