// =============================================================================
// ValidationFailure.cs
//
// 中文：
//   一条校验失败。封闭判别式类型，每个分支只携带**该种失败自己需要的数据**。
//
//   为什么不用"枚举 + 一堆可空字段"：
//     那样每个分支都要面对一堆与自己无关的 null，UI 组装文案时必须先判断
//     "这个原因码下哪些字段才有意义"，而编译器无法帮忙检查。用封闭判别式，
//     TooShort 上只有 ActualLength 和 MinimumLength，取不到不相干的字段，
//     模式匹配时也能穷尽。
//
//   为什么携带数据而不是文案（测试计划 D5/D6）：
//     规格 §12 要求界面同时支持英文和简体中文。若 Core 直接产出
//     "SKU is too short" 这样的句子，中文界面就无法正确显示，而且这个缺陷
//     要等到做本地化时才会暴露。这里只提供事实——实际长度 6、要求下限 8——
//     由 UI 层结合 .resx 组装成 "SKU 长度应为 8 位，实际 6 位"。
//
//   每个分支都必须对应一个**不同的修复动作**。若两种失败的处理方式完全相同，
//   就不该拆成两个分支。
//
// English:
//   A single validation failure. A closed discriminated type where each case
//   carries only the data that case needs.
//
//   Why not an enum plus nullable fields: every case would then face fields
//   irrelevant to it, the UI would have to know which fields are meaningful for
//   which reason code, and the compiler could not help. Here TooShort exposes
//   ActualLength and MinimumLength and nothing else, and matching is exhaustive.
//
//   Why data rather than prose (test plan D5/D6): spec §12 requires English and
//   Simplified Chinese. A sentence produced inside Core could never render in
//   Chinese, and the defect would stay hidden until localization began. This type
//   supplies facts — actual length 6, required minimum 8 — and the UI composes the
//   localized sentence from .resx.
//
// 包含的类型 / Types in this file:
//   ValidationFailure           抽象基类型，构造函数私有，分支集合封闭
//   ValidationFailure.TooShort  短于要求的下限
//   ValidationFailure.TooLong   长于要求的上限
// =============================================================================

namespace ScannerHelper.Core.Domain;

/// <summary>
/// 中文：一条校验失败的具体内容。
/// English: The specifics of one validation failure.
/// </summary>
public abstract record ValidationFailure
{
    /// <summary>
    /// 中文：私有构造函数。仅嵌套类型可访问，从而把分支集合封闭在本文件内。
    /// English: Private constructor, reachable only by nested types, sealing the
    ///          set of cases to this file.
    /// </summary>
    private ValidationFailure()
    {
    }

    /// <summary>
    /// 中文：SKU 短于配置的最小长度。
    /// English: The SKU is shorter than the configured minimum.
    /// </summary>
    /// <param name="ActualLength">
    /// 中文：实际长度。 English: The actual length.
    /// </param>
    /// <param name="MinimumLength">
    /// 中文：配置要求的最小长度。 English: The configured minimum.
    /// </param>
    public sealed record TooShort(int ActualLength, int MinimumLength) : ValidationFailure;

    /// <summary>
    /// 中文：SKU 长于配置的最大长度。
    /// English: The SKU is longer than the configured maximum.
    /// </summary>
    /// <param name="ActualLength">
    /// 中文：实际长度。 English: The actual length.
    /// </param>
    /// <param name="MaximumLength">
    /// 中文：配置要求的最大长度。 English: The configured maximum.
    /// </param>
    public sealed record TooLong(int ActualLength, int MaximumLength) : ValidationFailure;
}
