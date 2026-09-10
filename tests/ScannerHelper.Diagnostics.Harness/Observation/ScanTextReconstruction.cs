// =============================================================================
// ScanTextReconstruction.cs
//
// 中文：
//   把捕获到的按键事件流还原成人能读的文本。
//
//   ★ 这个文件回答的是全项目当下最要紧的一个问题：
//
//       字符究竟丢在钩子**之前**，还是钩子**之后**？
//
//     实测中，扫码枪往记事本里扫同一个条码五十次，出现了若干残缺结果：
//     `DGKJRDF`（截断）、`DGKjDC5679F5NF`（大写变小写、还少一个字符）、
//     `DGKJRDC5679F5NDGKF5NF`（两次传输叠在一起）。
//
//     这两种可能的后果完全相反：
//
//       丢在钩子之后（Windows → 应用这一段）
//         → 钩子这里看到的是完整的码。我们的架构恰好能修好它：原始按键
//           全部吞掉，自己用 SendInput 按可控节奏重发。这是产品的加分项。
//
//       丢在钩子之前（扫码枪 → Windows 这一段）
//         → 我们会忠实地捕获一个残缺的码，再原样发出去，而界面显示一切正常。
//           这就是规格 §19.1 说的"静默的错误数据"——比崩溃更糟，因为崩溃
//           看得见。属于 4b 硬性关卡必须拦下的情况。
//
//     把钩子看到的事件流还原成文本，跟应用里实际收到的内容一对比，答案立刻
//     就有了。这件事没法靠推理，只能靠对照。
//
//   ★ 一处必须说清楚的局限：这里用的是**美式键盘布局的固定映射**。
//
//     正确的做法是 ToUnicodeEx 加上当前键盘布局与按键状态，但那个 API 是
//     有状态的（死键、组合键），在诊断工具里引入它反而会带进新的不确定性。
//     仓库条码是 ASCII 的字母数字，美式映射足够。若将来发现现场条码含有
//     符号且解出来不对，那正是需要换成 ToUnicodeEx 的信号——而不是把这里的
//     映射表越补越长。
//
//     CapsLock 的初始状态无从得知，这里一律按"关闭"处理。若重建结果整体
//     大小写颠倒，那本身就是一条值得记下的观察。
//
// English:
//   Reconstructs readable text from a captured key event stream.
//
//   This file answers the most pressing question in the project right now: are
//   characters lost *before* the hook or *after* it?
//
//   In testing, fifty scans of one barcode into Notepad produced several damaged
//   results: truncations, an uppercase letter arriving lowercase alongside a dropped
//   character, and two transmissions overlapping.
//
//   The two possibilities have opposite consequences. Lost after the hook — between
//   Windows and the application — means the hook saw the complete code, and this
//   product's architecture fixes the problem outright by swallowing the raw keystrokes
//   and re-emitting at a controlled pace. Lost before the hook means we faithfully
//   capture a damaged code and emit it while the UI reports success: spec §19.1's
//   silently wrong data, worse than a crash because a crash is visible, and something
//   4b's hard gate must catch.
//
//   Reconstructing what the hook saw and comparing it against what the application
//   received settles it. No amount of reasoning substitutes for that comparison.
//
//   One limitation to state plainly: this uses a fixed US keyboard mapping. The correct
//   approach is ToUnicodeEx with the active layout and key state, but that API is
//   stateful — dead keys, compositions — and bringing it into a diagnostic tool would
//   introduce fresh uncertainty. Warehouse barcodes are ASCII alphanumerics and a US
//   mapping suffices. If site barcodes ever contain symbols that decode wrongly, that
//   is the signal to move to ToUnicodeEx, not to keep extending this table.
//
//   CapsLock's initial state is unknowable, so it is treated as off. Reconstruction
//   coming out uniformly case-inverted is itself an observation worth recording.
//
// 包含的类型 / Types in this file:
//   ReconstructedScan       一段连发还原出来的内容
//   ScanTextReconstruction  还原逻辑
// =============================================================================

using System.Text;

namespace ScannerHelper.Diagnostics.Harness.Observation;

/// <summary>
/// 中文：一段连发（约等于一次扫描）还原出来的内容。
/// English: One burst — roughly one scan — as reconstructed.
/// </summary>
/// <param name="Text">
/// 中文：还原出的文本。不含终止用的回车。
/// English: The reconstructed text, excluding the terminating Enter.
/// </param>
/// <param name="EndedWithEnter">
/// 中文：这一段是否以回车结束。规格 §5.4 假定扫描后缀是回车；若实测有相当比例
///       的扫描**不**以回车结束，那个假定就需要重新审视。
/// English: Whether the burst ended with Enter. Spec §5.4 assumes Enter is the scan
///          suffix; a meaningful share of bursts ending otherwise would put that
///          assumption back in question.
/// </param>
/// <param name="KeyDownCount">
/// 中文：这一段里的按下事件数，含修饰键。与 <paramref name="Text"/> 的长度对不上
///       是正常的——一个大写字母要按 Shift 再按字母，两个按下换一个字符。
/// English: Key-down events in this burst, modifiers included. Not matching
///          <paramref name="Text"/>'s length is normal: an uppercase letter costs a
///          Shift press plus the letter, two key-downs for one character.
/// </param>
public readonly record struct ReconstructedScan(string Text, bool EndedWithEnter, int KeyDownCount);

/// <summary>
/// 中文：把按键事件流还原成文本。
/// English: Reconstructs text from a key event stream.
/// </summary>
public static class ScanTextReconstruction
{
    /// <summary>
    /// 中文：回车的虚拟键码。扫描的终止符（规格 §5.4）。
    /// English: Enter's virtual key code; the scan terminator (spec §5.4).
    /// </summary>
    private const ushort VkReturn = 0x0D;

    private const ushort VkTab = 0x09;
    private const ushort VkSpace = 0x20;
    private const ushort VkShift = 0x10;
    private const ushort VkLeftShift = 0xA0;
    private const ushort VkRightShift = 0xA1;

    /// <summary>
    /// 中文：
    ///   把一串按键事件切成若干段，并把每一段还原成文本。
    ///   输入：keys 按时间顺序排列的按键事件；burstGapTicks 段与段之间的空隙阈值。
    ///   输出：每一段的还原结果。
    ///   步骤：
    ///     1. 顺序遍历事件，维护 Shift 是否按下；
    ///     2. 修饰键只更新状态，不产出字符；
    ///     3. 遇到回车：结束当前段，标记为以回车结束；
    ///     4. 其余按下事件：按当前 Shift 状态解码成字符，追加到当前段；
    ///     5. 与上一个按下事件的间隔超过阈值时，先结束当前段再开新段。
    ///
    ///   步骤 5 放在追加之前：空隙是**这个事件与上一个**之间的，所以要先据此
    ///   决定它属于新段还是旧段，再把它放进去。顺序反了会把每一段的第一个字符
    ///   错误地留在上一段末尾——而那种错误在结果里看起来只是"偶尔多一个字符"，
    ///   很难察觉。
    /// English:
    ///   Splits a chronological key event stream into bursts and reconstructs each.
    ///   Steps: (1) walk the events tracking whether Shift is held; (2) modifiers update
    ///   state and produce no character; (3) Enter closes the current burst and marks it
    ///   as Enter-terminated; (4) any other key-down decodes under the current Shift
    ///   state and is appended; (5) a gap above the threshold closes the current burst
    ///   before the event is appended.
    ///
    ///   Step 5 precedes the append: the gap is between *this* event and the previous
    ///   one, so it must decide which burst this event belongs to before the event is
    ///   placed. Reversed, each burst's first character would be left at the end of the
    ///   previous one — an error that reads merely as "occasionally one extra character"
    ///   and is correspondingly hard to spot.
    /// </summary>
    /// <exception cref="ArgumentNullException">
    /// 中文：keys 为 null。 English: keys is null.
    /// </exception>
    public static IReadOnlyList<ReconstructedScan> Split(
        IReadOnlyList<RecordedKey> keys, long burstGapTicks)
    {
        ArgumentNullException.ThrowIfNull(keys);

        var scans = new List<ReconstructedScan>();
        var current = new StringBuilder();
        var currentKeyDownCount = 0;
        var isShiftDown = false;
        long? previousKeyDownTimestamp = null;

        foreach (var key in keys)
        {
            // 步骤 1、2 / Steps 1–2
            if (IsShift(key.VirtualKey))
            {
                isShiftDown = !key.IsKeyUp;

                if (!key.IsKeyUp)
                {
                    currentKeyDownCount++;
                }

                continue;
            }

            if (key.IsKeyUp)
            {
                continue;
            }

            // 步骤 5 / Step 5 —— 先判断分段，再处理本事件
            if (previousKeyDownTimestamp is { } previous
                && key.Timestamp - previous > burstGapTicks
                && current.Length > 0)
            {
                scans.Add(new ReconstructedScan(
                    current.ToString(), EndedWithEnter: false, currentKeyDownCount));
                current.Clear();
                currentKeyDownCount = 0;
            }

            previousKeyDownTimestamp = key.Timestamp;
            currentKeyDownCount++;

            // 步骤 3 / Step 3
            if (key.VirtualKey == VkReturn)
            {
                scans.Add(new ReconstructedScan(
                    current.ToString(), EndedWithEnter: true, currentKeyDownCount));
                current.Clear();
                currentKeyDownCount = 0;
                continue;
            }

            // 步骤 4 / Step 4
            current.Append(Decode(key.VirtualKey, isShiftDown));
        }

        if (current.Length > 0)
        {
            scans.Add(new ReconstructedScan(
                current.ToString(), EndedWithEnter: false, currentKeyDownCount));
        }

        return scans;
    }

    private static bool IsShift(ushort virtualKey)
        => virtualKey is VkShift or VkLeftShift or VkRightShift;

    /// <summary>
    /// 中文：
    ///   把一个虚拟键码解码成字符。
    ///   输入：virtualKey 虚拟键码；isShiftDown 当前 Shift 是否按下。
    ///   输出：对应的字符串；无法映射时返回形如 <c>&lt;0x1F&gt;</c> 的占位。
    ///
    ///   ★ 无法映射时给占位而不是**跳过**。跳过会让还原结果看起来是一个干净的
    ///     短字符串，与"扫描本身就少了几个字符"完全无法区分——而那正是本工具
    ///     要分辨的东西。占位符让"这里有个键但我不认识"看得见。
    /// English:
    ///   Decodes one virtual key, returning a placeholder such as &lt;0x1F&gt; when it
    ///   cannot be mapped.
    ///
    ///   A placeholder rather than skipping: skipping would make the reconstruction look
    ///   like a clean short string, indistinguishable from "the scan itself was missing
    ///   characters" — which is precisely the distinction this tool exists to draw. The
    ///   placeholder makes "a key was here and I did not recognize it" visible.
    /// </summary>
    private static string Decode(ushort virtualKey, bool isShiftDown)
    {
        switch (virtualKey)
        {
            case VkSpace:
                return " ";

            // 制表符在还原结果里必须看得见。GS1 条码可能内嵌 Tab 作为分隔符，
            // 而规格 §22.4 明说 V1 不支持这种条码——若这里出现 ⇥，那就是遇上了。
            // Tab must be visible in the reconstruction. GS1 codes can embed it as a
            // separator, which spec §22.4 says V1 does not support — a ⇥ here means one
            // turned up.
            case VkTab:
                return "⇥";

            case >= 0x30 and <= 0x39:
                return isShiftDown
                    ? ")!@#$%^&*("[virtualKey - 0x30].ToString()
                    : ((char)virtualKey).ToString();

            case >= 0x41 and <= 0x5A:
                return isShiftDown
                    ? ((char)virtualKey).ToString()
                    : char.ToLowerInvariant((char)virtualKey).ToString();

            case 0xBA: return isShiftDown ? ":" : ";";
            case 0xBB: return isShiftDown ? "+" : "=";
            case 0xBC: return isShiftDown ? "<" : ",";
            case 0xBD: return isShiftDown ? "_" : "-";
            case 0xBE: return isShiftDown ? ">" : ".";
            case 0xBF: return isShiftDown ? "?" : "/";
            case 0xC0: return isShiftDown ? "~" : "`";
            case 0xDB: return isShiftDown ? "{" : "[";
            case 0xDC: return isShiftDown ? "|" : "\\";
            case 0xDD: return isShiftDown ? "}" : "]";
            case 0xDE: return isShiftDown ? "\"" : "'";

            default:
                return $"<0x{virtualKey:X2}>";
        }
    }
}

/// <summary>
/// 中文：一次按键记录。还原文本需要按下与弹起都在场——Shift 的状态靠它们维护。
/// English: One recorded keystroke. Reconstruction needs both downs and ups, since
///          Shift state is tracked from them.
/// </summary>
/// <param name="Timestamp">
/// 中文：钩子那一侧的时间戳。理由见 PairedObservation.HookTimestamp。
/// English: The hook-side timestamp; see PairedObservation.HookTimestamp for why.
/// </param>
/// <param name="VirtualKey">中文：虚拟键码。 English: The virtual key.</param>
/// <param name="IsKeyUp">中文：按下还是弹起。 English: Down or up.</param>
public readonly record struct RecordedKey(long Timestamp, ushort VirtualKey, bool IsKeyUp);
