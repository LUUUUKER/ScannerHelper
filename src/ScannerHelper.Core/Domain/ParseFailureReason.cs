// =============================================================================
// ParseFailureReason.cs
//
// 中文：
//   解析失败的原因码。
//
//   这是枚举而非字符串，原因见测试计划 D5/D6：Core 不得产出任何面向用户的
//   文案。规格 §12 要求界面同时支持英文和简体中文，若这里写死 "SKU too short"
//   之类的字符串，中文界面就无法正确显示——而且这个缺陷要等到做本地化时
//   才会暴露。UI 层负责把原因码映射到 .resx 资源。
//
//   每个原因码都必须指向一个**不同的排查动作**。若两种情况的处理方式完全
//   相同，就不该拆成两个码；反之，只要现场需要区别对待，就必须能区分。
//
// English:
//   Reason codes for a failed parse.
//
//   An enum rather than a string (test plan D5/D6): Core must emit no
//   user-facing text. Spec §12 requires both English and Simplified Chinese, so
//   a hardcoded "SKU too short" here could never render correctly in Chinese —
//   and the defect would stay hidden until localization work began. The UI maps
//   these codes to .resx resources.
//
//   Every code must point at a *different* corrective action. If two situations
//   are handled identically they should not be separate codes; if the shop floor
//   must treat them differently, they must be distinguishable.
//
// 包含的类型 / Types in this file:
//   ParseFailureReason
// =============================================================================

namespace ScannerHelper.Core.Domain;

/// <summary>
/// 中文：解析失败的原因。随解析器种类增加而扩展。
/// English: Why a parse failed. Extended as more parser kinds are added.
/// </summary>
public enum ParseFailureReason
{
    /// <summary>
    /// 中文：原始条码为空。指向硬件或捕获链路——扫码枪什么都没送来。
    /// English: The raw code was empty. Points at hardware or the capture path —
    ///          the scanner delivered nothing.
    /// </summary>
    EmptyInput,

    /// <summary>
    /// 中文：解析规则指定的位置超出了原始条码的长度。指向配置——规则配错了，
    ///       或者扫到了不符合预期格式的条码。
    /// English: The rule points past the end of the raw code. Points at
    ///          configuration — a wrong rule, or a barcode not in the expected
    ///          format.
    /// </summary>
    OutOfBounds,

    /// <summary>
    /// 中文：正则完全没有匹配上。指向"规则与这个条码不符"——要么规则写错，
    ///       要么扫到了不该扫的码。
    /// English: The regex did not match at all. Means "the rule does not fit this
    ///          code" — either the rule is wrong or the wrong item was scanned.
    /// </summary>
    NoMatch,

    /// <summary>
    /// 中文：正则匹配成功，但取不到配置指定的那个捕获组——索引超出实际组数，
    ///       或该组本次未参与匹配。指向"捕获组索引这一项配错了"，与 NoMatch
    ///       是不同的排查方向：整体是匹配上了的。
    /// English: The regex matched, but the configured capture group is
    ///          unavailable — the index exceeds the group count, or the group did
    ///          not participate. Points at the capture-group index setting, not at
    ///          the barcode: the pattern itself did match.
    /// </summary>
    MissingCaptureGroup,

    /// <summary>
    /// 中文：正则匹配超时被中断。指向"规则本身写法有问题、需要重写"——通常是
    ///       嵌套量词导致的灾难性回溯。必须与 NoMatch 区分：那是改规则内容，
    ///       这是改规则写法（规格 §8.2）。
    /// English: The match timed out. Points at the pattern needing a rewrite —
    ///          usually catastrophic backtracking from nested quantifiers. Must
    ///          stay distinct from NoMatch: that one means change what the rule
    ///          matches, this one means change how it is written (spec §8.2).
    /// </summary>
    RegexTimeout,
}
