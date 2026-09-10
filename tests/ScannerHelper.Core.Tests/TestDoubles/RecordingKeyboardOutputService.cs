// =============================================================================
// RecordingKeyboardOutputService.cs
//
// 中文：
//   记录下每一次输出调用的假输出服务。
//
//   ★ 它把"发出去了什么"变成可断言的事实，而这正是本组测试的全部要害。
//
//     输出是整条流水线的**唯一出口**——错误数据要写进仓库系统，只能从这里
//     出去。规格 §19.1 反复强调的"静默的错误数据"，具体形态就是：程序看起来
//     一切正常、界面显示成功，而这里发出去的内容是错的。
//
//     因此这里既要能断言"发了什么"，也要能断言"**什么都没发**"。后者往往
//     更重要：解析失败、校验失败、扫描超时，这三种情况下自动发送任何东西
//     都是缺陷（规格 §10：失败绝不自动发送）。
//
//   ★ 文本与回车分开记录，不合并成一个字符串。
//
//     若把回车当成文本末尾的一个 '\r' 记下来，"发了文本再发一次回车"与
//     "发了一段末尾带回车的文本"在断言里就分不开了。而这两者在真实世界里
//     是不同的事：前者是一次真正的 Enter 按键，后者是一个字符——网页表单
//     认前者不认后者（详见 IKeyboardOutputService）。
//
//     分开记录，AppendEnterAfterScan 的两种状态才能被精确地钉住：
//     关闭时 EnterCount 必须是 0，开启时必须**恰好**是 1，而不是"大于 0"。
//
// English:
//   A fake output service that records every call.
//
//   It turns "what was emitted" into an assertable fact, which is the whole point of this
//   group of tests. Output is the pipeline's only exit: wrong data can reach the warehouse
//   system only through here, and spec §19.1's silently wrong data takes exactly this shape —
//   the program looking fine, the UI reporting success, and what left through this door being
//   wrong.
//
//   So it must support asserting both what was emitted and that *nothing* was. The latter is
//   often the more important: a parse failure, a validation failure or a scan timeout must
//   emit nothing automatically (spec §10: failures never auto-emit).
//
//   Text and Enter are recorded separately rather than merged. Recording the Enter as a
//   trailing '\r' would make "text then an Enter keystroke" indistinguishable from "text
//   ending in a carriage return" — and those are different things in the real world: the first
//   is a genuine Enter key press, the second a character, and web forms accept the first and
//   not the second (see IKeyboardOutputService).
//
//   Kept separate, AppendEnterAfterScan's two states can be pinned precisely: zero Enters when
//   off, and *exactly* one when on rather than merely "more than zero".
//
// 包含的类型 / Types in this file:
//   RecordingKeyboardOutputService
// =============================================================================

using ScannerHelper.Core.Output;

namespace ScannerHelper.Core.Tests.TestDoubles;

/// <summary>
/// 中文：记录调用的假输出服务。
/// English: A recording fake output service.
/// </summary>
public sealed class RecordingKeyboardOutputService : IKeyboardOutputService
{
    private readonly List<string> _emittedText = [];

    /// <summary>
    /// 中文：依次发出过的文本。
    /// English: The texts emitted, in order.
    /// </summary>
    public IReadOnlyList<string> EmittedText => _emittedText;

    /// <summary>
    /// 中文：发出回车的次数。**恰好为几**是可断言的，这一点很重要——
    ///       AppendEnterAfterScan 开启时必须恰好一次，多发一次在网页上
    ///       可能就是多提交了一次表单。
    /// English: How many Enters were emitted. The exact count is assertable, which matters:
    ///          with AppendEnterAfterScan on it must be exactly one, and one extra could mean
    ///          one extra form submission on the page.
    /// </summary>
    public int EnterCount { get; private set; }

    /// <summary>
    /// 中文：是否什么都没发出过。失败路径上这个必须为 true（规格 §10）。
    /// English: Whether nothing at all was emitted. Must hold on every failure path
    ///          (spec §10).
    /// </summary>
    public bool EmittedNothing => _emittedText.Count == 0 && EnterCount == 0;

    /// <inheritdoc />
    public void EmitText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        _emittedText.Add(text);
    }

    /// <inheritdoc />
    public void EmitEnter() => EnterCount++;

    /// <summary>
    /// 中文：清空记录，供一个测试里连续验证多枪时使用。
    /// English: Clears the record, for tests that verify several scans in succession.
    /// </summary>
    public void Clear()
    {
        _emittedText.Clear();
        EnterCount = 0;
    }
}
