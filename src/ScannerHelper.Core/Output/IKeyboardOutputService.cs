// =============================================================================
// IKeyboardOutputService.cs
//
// 中文：
//   把结果送进业务软件的契约（规格 §2.2、§10）。Core 只定义它，唯一的实现
//   在 ScannerHelper.Win32——因为它要直接调 SendInput（规格 §17）。
//
//   ★ 内容一律以 Unicode 字符发出，一次一个（规格 §2.2）。
//
//     原始扫描在本程序内部已经被还原成一个 string，再以 Unicode 字符重新
//     发出，结果就**不依赖**当前键盘布局、不依赖 CapsLock 状态、也不依赖
//     扫码枪自己配的键盘布局。改用模拟扫描码的方式，这三种失效模式会全部
//     回来（规格 §22.3 专门记了输入侧仍然受布局影响这件事）。
//
//   ★★ 为什么"发文本"和"发回车"是两个方法，而不是在文本末尾拼一个 '\r'。
//
//     这不是接口设计上的洁癖，是一个真实的技术差别。
//
//     以 KEYEVENTF_UNICODE 发出 U+000D，接收方收到的是一个**字符**。而网页
//     表单的提交行为通常挂在 Enter 的**按键事件**上（keydown 的 key === "Enter"），
//     不是挂在收到一个回车字符上。所以把 '\r' 当成普通字符发出去，很可能
//     文本框里多了个看不见的字符，而表单根本没提交。
//
//     可靠的做法是发一次真正的 Enter 按键（虚拟键 VK_RETURN）。把它拆成
//     独立的方法，实现才有机会用正确的手段去做，而不是被"末尾拼一个字符"
//     这个签名逼进错误的路径。
//
//     附带好处：EmitText 的契约因此变得干净——**它只发给它的内容，一个字符
//     都不多**（规格 §10：不修剪、不补齐、不改大小写）。要不要补回车是调用方
//     的策略（AppendEnterAfterScan，决策 D-10），不该藏在输出实现里。
//
//   ★ 每一个合成事件都必须被打上标记（规格 §5.6）。
//
//     否则本程序发出去的输出会被自己的钩子重新看到，再被当成输入处理一遍，
//     形成 SendInput → 钩子 → SendInput 的无限递归。Task 4a 已确认合成事件
//     不出现在 Raw Input 通道上，因此钩子那一侧的标记是唯一的识别手段。
//
//   规格 §19.1 的心跳探测也走这条通道：它发一个带同样标记的探测事件，
//   确认钩子还能看到它。因此标记不只是防递归，也是"钩子是否还活着"这件事
//   的判定基础。
//
// English:
//   The contract for delivering results to the business application (spec §2.2, §10). Core
//   defines it; the only implementation lives in ScannerHelper.Win32 because it calls
//   SendInput directly (spec §17).
//
//   Content is emitted as Unicode characters, one at a time (spec §2.2). The raw scan has
//   already been reconstructed into a string inside this application, and re-emitting it as
//   Unicode characters makes the result independent of the active keyboard layout, of
//   CapsLock, and of the scanner's own configured layout. Simulating scan codes would bring
//   all three failure modes back (spec §22.3 records that the *input* side remains
//   layout-sensitive).
//
//   Why emitting text and emitting Enter are two methods rather than appending '\r' to the
//   text: this is a real technical difference, not interface fastidiousness. Sending U+000D
//   through KEYEVENTF_UNICODE delivers a *character*, whereas a web form's submit behavior
//   normally hangs off the Enter *key event* (keydown with key === "Enter") rather than off
//   receiving a carriage-return character. Emitting '\r' as an ordinary character therefore
//   tends to put an invisible character in the field while the form does not submit at all.
//
//   The reliable approach is a genuine Enter keystroke (virtual key VK_RETURN). Splitting it
//   into its own method is what lets the implementation do that, instead of being forced down
//   the wrong path by a signature that says "append a character".
//
//   A side benefit: EmitText's contract becomes clean — it emits exactly what it was given
//   and not one character more (spec §10: no trimming, padding or case conversion). Whether
//   an Enter follows is the caller's policy (AppendEnterAfterScan, decision D-10) and does not
//   belong hidden inside the output implementation.
//
//   Every synthesized event must be tagged (spec §5.6), or this application's own output is
//   seen again by its own hook and processed as input, recursing endlessly through SendInput.
//   Task 4a confirmed synthesized events do not appear on the Raw Input channel, so the tag on
//   the hook side is the only means of recognizing them.
//
//   Spec §19.1's heartbeat probe uses this same channel, emitting a probe carrying the same
//   tag and confirming the hook still observes it. The tag is therefore not only the recursion
//   guard but the basis for deciding whether the hook is still alive.
//
// 包含的类型 / Types in this file:
//   IKeyboardOutputService
// =============================================================================

namespace ScannerHelper.Core.Output;

/// <summary>
/// 中文：把文本送进当前获得焦点的业务软件。
/// English: Delivers text into whichever business application currently has focus.
/// </summary>
public interface IKeyboardOutputService
{
    /// <summary>
    /// 中文：
    ///   发送一段文本。
    ///   输入：text 要发送的内容，不得为 null；空串是合法的（什么都不发）。
    ///   输出：无。
    ///
    ///   ★ **原样发送，一个字符都不多也不少**（规格 §10）：
    ///     不修剪首尾空白、不补齐长度、不改大小写、不追加任何终止符。
    ///
    ///     不修剪这一条尤其要守住（决策 D-9）。扫码枪本不该发出空白；真发出
    ///     了，那是异常，应当由校验层暴露出来。在这里悄悄抹掉，异常就永远
    ///     查不到了，而仓库系统里会多出一批看起来正常、实际来路不明的数据。
    ///
    ///   实现必须逐字符以 KEYEVENTF_UNICODE 发出（规格 §2.2），并给每一个
    ///   合成事件打上标记，使本程序的捕获链路能认出并忽略它（规格 §5.6）。
    /// English:
    ///   Emits text exactly as given, not one character more or fewer (spec §10): no
    ///   trimming of surrounding whitespace, no padding, no case conversion, no terminator
    ///   appended. text must not be null; an empty string is legitimate and emits nothing.
    ///
    ///   The no-trimming rule especially must hold (decision D-9). A scanner should not emit
    ///   whitespace; if it does, that is an anomaly for validation to surface. Removed
    ///   quietly here it could never be found, and the warehouse system would accumulate data
    ///   that looks normal and came from nowhere identifiable.
    ///
    ///   Implementations emit character by character through KEYEVENTF_UNICODE (spec §2.2)
    ///   and tag every synthesized event so this application's capture pipeline recognizes
    ///   and ignores it (spec §5.6).
    /// </summary>
    /// <exception cref="ArgumentNullException">
    /// 中文：text 为 null。 English: text is null.
    /// </exception>
    void EmitText(string text);

    /// <summary>
    /// 中文：
    ///   发送一次回车按键。
    ///
    ///   ★ 必须作为**真正的按键**发出（虚拟键 VK_RETURN），不能当作字符
    ///     U+000D 用 KEYEVENTF_UNICODE 发。
    ///
    ///     网页表单的提交行为通常挂在 Enter 的按键事件上，不是挂在收到一个
    ///     回车字符上。当成字符发出去，很可能文本框里多了个看不见的字符，
    ///     而表单根本没提交——而"扫完不提交"正是 AppendEnterAfterScan 这个
    ///     设置要解决的问题（规格 §10），用错手段等于设置开了也没用，
    ///     且现场极难判断是设置没生效还是网页不认。
    ///
    ///   何时调用由调用方按 AppendEnterAfterScan 决定（决策 D-10，默认关闭）。
    ///   本方法本身不看任何设置——它只负责"发一次回车"这一件事。
    ///
    ///   同样要打上合成事件标记（规格 §5.6）。
    /// English:
    ///   Emits one Enter keystroke.
    ///
    ///   It must be sent as a genuine keystroke (virtual key VK_RETURN) rather than as the
    ///   character U+000D through KEYEVENTF_UNICODE. A web form's submit behavior normally
    ///   hangs off the Enter key event rather than off receiving a carriage return, so sending
    ///   it as a character tends to leave an invisible character in the field while the form
    ///   does not submit — and "the page stops submitting" is precisely what the
    ///   AppendEnterAfterScan setting exists to fix (spec §10). Using the wrong mechanism
    ///   makes the setting ineffective even when enabled, and on site it is very hard to tell
    ///   a setting that did not take effect from a page that does not accept the input.
    ///
    ///   When to call it is the caller's decision under AppendEnterAfterScan (decision D-10,
    ///   off by default). This method reads no setting; it emits one Enter and nothing else.
    ///
    ///   Synthesized events must be tagged here too (spec §5.6).
    /// </summary>
    void EmitEnter();
}
