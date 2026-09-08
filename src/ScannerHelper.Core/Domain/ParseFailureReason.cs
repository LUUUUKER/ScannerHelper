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
}
