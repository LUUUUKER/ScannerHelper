// =============================================================================
// ISkuValidator.cs
//
// 中文：
//   SKU 校验器的统一契约。V1 有三个基础实现（长度、字符集、校验正则）
//   和一个组合实现（规格 §9）。
//
//   校验是解析之后的独立一层：
//     原始码 → 解析 → 候选 SKU → 校验 → 有效 SKU
//   解析只负责"按规则取出一段"，校验才负责"取出来的这段合不合理"。
//   两层分开的好处是规则可以各自独立配置和测试，也让失败原因能精确指向
//   到底是取错了还是取出来的内容不对。
//
//   与 ISkuParser 相同的两条硬性约定：
//     - 实现必须纯粹且确定性，不碰 UI、设备、时钟或文件（规格 §17）；
//     - 对任何非 null 输入都不得抛异常，预期的坏输入表现为
//       ValidationResult.Invalid；sku 为 null 时抛 ArgumentNullException
//       （决策 D-1）。
//
//   一条额外约定：单个校验器最多产生**一条**失败。它只回答一个问题。
//   把多条失败聚合起来是 CompositeSkuValidator 的职责（决策 D-6）。
//
// English:
//   The contract for SKU validators. V1 has three basic implementations (length,
//   character set, validation regex) and one composite (spec §9).
//
//   Validation is a distinct layer after parsing:
//     raw → parse → candidate SKU → validate → valid SKU
//   Parsing extracts a substring by rule; validation judges whether what was
//   extracted is plausible. Keeping them separate lets each be configured and
//   tested independently, and lets a failure point precisely at either the
//   extraction or the extracted content.
//
//   Same two hard terms as ISkuParser: implementations are pure and deterministic
//   (spec §17), and never throw for non-null input — expected bad input appears as
//   ValidationResult.Invalid, while a null sku throws (decision D-1).
//
//   One further term: a single validator produces at most *one* failure. It
//   answers one question. Aggregating failures is CompositeSkuValidator's job
//   (decision D-6).
//
// 包含的类型 / Types in this file:
//   ISkuValidator
// =============================================================================

using ScannerHelper.Core.Domain;

namespace ScannerHelper.Core.Validation;

/// <summary>
/// 中文：校验一个已解析出的 SKU。
/// English: Validates an SKU that has already been parsed.
/// </summary>
public interface ISkuValidator
{
    /// <summary>
    /// 中文：
    ///   校验 SKU。
    ///   输入：sku 解析层产出的候选 SKU，不得为 null。
    ///   输出：<see cref="ValidationResult.Valid"/>，或
    ///         <see cref="ValidationResult.Invalid"/> 携带失败原因。
    ///   未启用的规则一律视为通过（规格 §9：没有启用任何校验规则时，
    ///   解析成功即视为有效）。
    /// English:
    ///   Validates the SKU. A rule that is not enabled always passes (spec §9:
    ///   with no rule enabled, a successful parse is valid).
    /// </summary>
    /// <exception cref="ArgumentNullException">
    /// 中文：sku 为 null 时抛出。 English: Thrown when sku is null.
    /// </exception>
    ValidationResult Validate(string sku);
}
