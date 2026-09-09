// =============================================================================
// SkuValidationSettings.cs
//
// 中文：
//   SKU 校验规则的配置（规格 §9）。
//
//   三条规则各自独立、都可选；全部启用时必须全部通过（规格 §9）。未启用一律
//   用 null 表达，与三个校验器的构造参数形状一一对应——设置层可以无条件把
//   全部字段交给对应的校验器，由校验器自己判断是否施加约束，中间不需要写
//   一堆"这项启用了吗"的分支。
//
//   IgnoreCase 是个例外：它不是"一条规则"，而是**字符集规则的一个修饰**，
//   因此是 bool 而非可空。它只影响字符集校验中字母的大小写判断，对数字、
//   符号、长度、校验正则一律无影响——这一点由测试 C10 与 V5 从两侧钉住。
//
// English:
//   Configuration for SKU validation (spec §9).
//
//   Three independent optional rules; all enabled ones must pass. "Not enabled" is
//   null throughout, matching the constructor shape of the three validators, so the
//   settings layer can hand every field over unconditionally and let each validator
//   decide whether it constrains anything — no "is this one enabled" branching in
//   between.
//
//   IgnoreCase is the exception: it is not a rule but a *modifier* of the
//   character-set rule, hence a bool rather than a nullable. It affects only letter
//   case within character-set validation — never digits, symbols, length, or the
//   validation regex — which tests C10 and V5 pin from both sides.
//
// 包含的类型 / Types in this file:
//   SkuValidationSettings
// =============================================================================

using ScannerHelper.Core.Domain;

namespace ScannerHelper.Core.Settings;

/// <summary>
/// 中文：SKU 校验规则配置。全部规则可选，null 表示未启用。
/// English: SKU validation configuration. Every rule is optional; null means the
///          rule is not enabled.
/// </summary>
public sealed class SkuValidationSettings
{
    /// <summary>
    /// 中文：最小长度，null 表示不限制下界（规格 §9.1）。
    /// English: Minimum length; null imposes no lower bound (spec §9.1).
    /// </summary>
    public int? MinimumLength { get; set; }

    /// <summary>
    /// 中文：最大长度，null 表示不限制上界。与最小长度相等即表示精确长度。
    /// English: Maximum length; null imposes no upper bound. Equal to the minimum
    ///          expresses an exact length.
    /// </summary>
    public int? MaximumLength { get; set; }

    /// <summary>
    /// 中文：字符集预设，null 表示不作字符集限制（规格 §9.2）。
    /// English: Character-set preset; null imposes no character constraint
    ///          (spec §9.2).
    /// </summary>
    public CharacterSetPreset? CharacterSet { get; set; }

    /// <summary>
    /// 中文：字符集校验是否忽略字母大小写，**默认开启**（规格 §9.2）。
    ///
    ///       默认开启是因为：仅仅由于条码里出现一个小写字母就拒收，比放过它
    ///       要糟得多。确实需要强制大写的站点应当用校验正则 ^[A-Z]+$ 表达，
    ///       那条路径不受本开关影响。
    /// English: Whether character-set validation ignores letter case; **on by
    ///          default** (spec §9.2). Rejecting an otherwise-valid barcode purely
    ///          for a lowercase letter is a worse failure than accepting one. A site
    ///          that genuinely requires uppercase should say so with ^[A-Z]+$, a
    ///          path this toggle does not touch.
    /// </summary>
    public bool IgnoreCase { get; set; } = true;

    /// <summary>
    /// 中文：校验正则，null 表示不作正则校验（规格 §9.3）。
    ///       与解析正则是**两条完全独立**的规则，改一条不影响另一条。
    /// English: The validation regex; null means no regex check (spec §9.3). Fully
    ///          independent of the parsing regex — changing one does not affect the
    ///          other.
    /// </summary>
    public string? ValidationRegexPattern { get; set; }
}
