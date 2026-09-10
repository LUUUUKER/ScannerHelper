// =============================================================================
// SendInputKeyboardOutputService.cs
//
// 中文：
//   <see cref="IKeyboardOutputService"/> 的唯一实现（规格 §17）。把扫描结果
//   送进当前获得焦点的业务软件。
//
//   ★ 文本逐字符以 KEYEVENTF_UNICODE 发出（规格 §2.2）。
//
//     原始扫描在程序内部已经被还原成一个 string，再以 Unicode 字符发出，
//     结果就不依赖当前键盘布局、不依赖 CapsLock、也不依赖扫码枪自己配的
//     键盘布局。改用模拟扫描码，这三种失效模式会全部回来。
//
//   ★ 回车作为**真正的按键**发出，不是字符 U+000D（决策 D-19）。
//
//     网页表单的提交行为通常挂在 Enter 的按键事件上（keydown 的
//     key === "Enter"），不是挂在收到一个回车字符上。当成字符发出去，
//     很可能文本框里多了个看不见的字符而表单根本没提交——而"扫完不提交"
//     恰恰是 AppendEnterAfterScan 这个设置要解决的问题（规格 §10）。
//     用错手段等于设置开了也没用，现场还极难判断是设置没生效还是网页不认。
//
//   ★ 代理对必须拆成两个码元分别发送。
//
//     KEYEVENTF_UNICODE 的 wScan 是一个 **UTF-16 码元**，不是一个码点。
//     基本多文种平面之外的字符（emoji 等）在 C# 的 string 里占两个 char，
//     必须作为两个连续的输入事件发出，接收方才能重组。逐 char 遍历正好
//     满足这一点——这不是巧合，而是 C# 的 char 与 UTF-16 码元本来就是
//     同一个东西。
//
//     仓库条码是 ASCII，这条路平时走不到。写明它是因为"逐 char 发送"
//     看起来像一个可以优化成"逐码点发送"的实现细节，而那个"优化"会把
//     代理对发坏。
//
//   ★ 每个事件都带上合成标记（规格 §5.6），否则本程序的输出会被自己的钩子
//     重新吃进去，形成 SendInput → 钩子 → SendInput 的无限递归。
//
// English:
//   The only implementation of <see cref="IKeyboardOutputService"/> (spec §17), delivering scan
//   results into whichever business application currently has focus.
//
//   Text goes out character by character through KEYEVENTF_UNICODE (spec §2.2). The raw scan has
//   already been reconstructed into a string inside the program, and re-emitting it as Unicode
//   characters makes the result independent of the active keyboard layout, of CapsLock, and of
//   the scanner's own configured layout. Simulating scan codes would bring all three failure
//   modes back.
//
//   Enter goes out as a genuine keystroke rather than the character U+000D (decision D-19). A web
//   form's submit behavior normally hangs off the Enter key event, not off receiving a carriage
//   return, so sending it as a character tends to leave an invisible character in the field while
//   the form does not submit — and "the page stops submitting" is exactly what
//   AppendEnterAfterScan exists to fix (spec §10). The wrong mechanism makes the setting
//   ineffective even when enabled, and on site that is very hard to tell from a page that does
//   not accept the input.
//
//   Surrogate pairs must be sent as two separate code units. KEYEVENTF_UNICODE's wScan is a
//   UTF-16 *code unit*, not a code point, so characters outside the basic multilingual plane
//   occupy two chars in a C# string and must go out as two consecutive input events for the
//   receiver to recombine them. Iterating per char satisfies this exactly — not by coincidence,
//   since a C# char and a UTF-16 code unit are the same thing. Warehouse barcodes are ASCII and
//   this path is never taken in practice; it is documented because "send per char" looks like an
//   implementation detail that could be "improved" into "send per code point", and that
//   improvement would corrupt surrogate pairs.
//
//   Every event carries the synthesized tag (spec §5.6), without which this application's output
//   is consumed by its own hook and recurses endlessly through SendInput.
//
// 包含的成员 / Members in this file:
//   EmitText   逐字符以 Unicode 发送
//   EmitEnter  发送一次真正的回车按键
// =============================================================================

using System.ComponentModel;
using System.Runtime.InteropServices;
using ScannerHelper.Core.Output;
using ScannerHelper.Win32.Native;

namespace ScannerHelper.Win32;

/// <summary>
/// 中文：用 SendInput 把结果送进业务软件。
/// English: Delivers results into the business application through SendInput.
/// </summary>
public sealed class SendInputKeyboardOutputService : IKeyboardOutputService
{
    /// <summary>
    /// 中文：回车的虚拟键码。
    /// English: Enter's virtual key code.
    /// </summary>
    private const ushort VkReturn = 0x0D;

    /// <summary>
    /// 中文：回车的硬件扫描码（主键盘区）。
    /// English: Enter's hardware scan code on the main keyboard.
    /// </summary>
    private const ushort ScanCodeReturn = 0x1C;

    private static readonly int InputSize = Marshal.SizeOf<SendInputNative.INPUT>();

    /// <inheritdoc />
    public void EmitText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.Length == 0)
        {
            return;
        }

        // 一次调用发送全部字符，而不是每个字符调一次 SendInput。
        //
        // ★ 这不只是性能考虑。SendInput 保证**一次调用内的事件不会被其他
        //   线程的输入插进来**。逐个调用的话，工人恰好在此刻按了一下键，
        //   那一下就可能落在我们发出的两个字符中间——业务软件收到的就是
        //   一个被插了字的条码，而这种错误极其罕见、无法复现。
        //
        // One call for every character rather than one SendInput per character. This is not only
        // about performance: SendInput guarantees that the events in a single call are not
        // interleaved with input from other threads. Called per character, a keystroke the
        // operator happens to make at that moment could land between two of ours, and the
        // business application would receive a barcode with a character spliced into it — an
        // error that is vanishingly rare and irreproducible.
        var inputs = new SendInputNative.INPUT[text.Length * 2];

        for (var index = 0; index < text.Length; index++)
        {
            // 每个字符要发按下与弹起两个事件。只发按下的话，某些应用会认为
            // 该键仍被按住，后续输入的行为就无从预料。
            // Each character needs a down and an up. Sending only the down leaves some
            // applications believing the key is still held, making later input unpredictable.
            inputs[index * 2] = UnicodeInput(text[index], isKeyUp: false);
            inputs[(index * 2) + 1] = UnicodeInput(text[index], isKeyUp: true);
        }

        Send(inputs, $"发送文本失败 / failed to emit text of length {text.Length}");
    }

    /// <inheritdoc />
    public void EmitEnter()
    {
        // ★ 用扫描码而不是 Unicode 字符，理由见文件头与决策 D-19。
        //   同时带上虚拟键码：某些应用读的是虚拟键而不是扫描码，两者都给上，
        //   接收方无论按哪一种方式解读都能认出这是 Enter。
        //
        // A scan code rather than a Unicode character; see the file header and decision D-19.
        // The virtual key is supplied as well, since some applications read that rather than the
        // scan code: providing both means the receiver recognizes Enter whichever it consults.
        SendInputNative.INPUT[] inputs =
        [
            EnterInput(isKeyUp: false),
            EnterInput(isKeyUp: true),
        ];

        Send(inputs, "发送回车失败 / failed to emit Enter");
    }

    private static SendInputNative.INPUT UnicodeInput(char codeUnit, bool isKeyUp)
        => new()
        {
            type = SendInputNative.INPUT_KEYBOARD,
            U = new SendInputNative.InputUnion
            {
                ki = new SendInputNative.KEYBDINPUT
                {
                    // 使用 KEYEVENTF_UNICODE 时 wVk 必须为 0。
                    // wVk must be zero with KEYEVENTF_UNICODE.
                    wVk = 0,
                    wScan = codeUnit,
                    dwFlags = SendInputNative.KEYEVENTF_UNICODE
                              | (isKeyUp ? SendInputNative.KEYEVENTF_KEYUP : 0),
                    time = 0,
                    dwExtraInfo = InjectedInputTag.Value,
                },
            },
        };

    private static SendInputNative.INPUT EnterInput(bool isKeyUp)
        => new()
        {
            type = SendInputNative.INPUT_KEYBOARD,
            U = new SendInputNative.InputUnion
            {
                ki = new SendInputNative.KEYBDINPUT
                {
                    wVk = VkReturn,
                    wScan = ScanCodeReturn,
                    dwFlags = isKeyUp ? SendInputNative.KEYEVENTF_KEYUP : 0,
                    time = 0,
                    dwExtraInfo = InjectedInputTag.Value,
                },
            },
        };

    /// <summary>
    /// 中文：
    ///   发送一批输入事件，并**检查返回值**。
    ///
    ///   ★ 检查返回值不是防御性编程的惯性。SendInput 失败时不抛异常，
    ///     只是返回一个比请求数小的数字。
    ///
    ///     最常见的失败原因是 UIPI：业务软件的完整性级别高于本程序。
    ///     规格 §2.1 的假设 A2 已经把"两者必须同级"写成了硬性要求，并说明
    ///     若业务软件被提权，整个用户态架构当场失效。
    ///
    ///     不检查的话，现场表现是"扫了码什么都没发生"，而日志里一片正常
    ///     ——规格 §19.1 说的正是这种"看起来在工作"的失效，它比崩溃更糟，
    ///     因为崩溃看得见。
    /// English:
    ///   Sends a batch of input events and checks the return value.
    ///
    ///   Checking is not defensive habit. SendInput does not throw on failure and merely returns
    ///   a number smaller than requested. The usual cause is UIPI, the business application
    ///   sitting at a higher integrity level than this program — spec §2.1's assumption A2 makes
    ///   matching levels a hard requirement and states that an elevated business application
    ///   collapses the entire user-mode architecture.
    ///
    ///   Unchecked, the symptom on site is "scanning does nothing" with a completely clean log:
    ///   spec §19.1's "looks like it is working" failure, which is worse than a crash because a
    ///   crash is visible.
    /// </summary>
    /// <exception cref="Win32Exception">
    /// 中文：部分或全部事件未能送入输入流。
    /// English: Some or all events could not be inserted into the input stream.
    /// </exception>
    private static void Send(SendInputNative.INPUT[] inputs, string failureDescription)
    {
        var inserted = SendInputNative.SendInput((uint)inputs.Length, inputs, InputSize);

        if (inserted == inputs.Length)
        {
            return;
        }

        throw new Win32Exception(
            Marshal.GetLastWin32Error(),
            $"{failureDescription}。请求 {inputs.Length} 个事件，实际送入 {inserted} 个。"
            + " 最常见的原因是业务软件以更高的完整性级别运行，UIPI 阻止了输入"
            + "（规格 §2.1 假设 A2 要求两者同级）。"
            + $" Requested {inputs.Length} events and inserted {inserted}. The usual cause is the"
            + " business application running at a higher integrity level, with UIPI blocking the"
            + " input (spec §2.1, assumption A2, requires matching levels).");
    }
}
