// =============================================================================
// SkuValidatorFactory.cs
//
// 中文：
//   把一份校验配置变成一个组合校验器（规格 §9）。
//
//   与 SkuParserFactory 对称的另一半桥。规格 §9 的两条规则在这里落地：
//   全部启用的规则都必须通过，且没有启用任何规则时解析成功即视为有效。
//
//   ★ 三个校验器**无条件全部构造**，不在这里判断"这项启没启用"。
//
//     三个基础校验器都自带"未启用即通过"的语义（长度用 int?、字符集用可空
//     预设、校验正则用可空模式）。若在这里再写一遍"启用了才加进去"，
//     "什么算启用"这件事就有了两份定义，而两份定义迟早会不一致。
//     SkuValidationSettings 的注释里已经把这个设计写明：设置层无条件把全部
//     字段交给对应的校验器，由校验器自己声明是否施加约束。
//
//     代价是组合校验器里恒有三个成员，其中未启用的那些每次扫描都会被调用
//     一次并立刻返回通过。这是三次虚调用，相对于一次扫描的整体开销可以忽略。
//
//   ★ 关于校验正则为空白：这里按**未启用**处理，与解析层刻意相反。
//
//     校验规则全部可选（规格 §9），留空就是"不启用这条规则"，这是常见配置——
//     很多站点只需要解析，不需要额外校验。而解析是 SKU 模式的必经步骤，
//     空模式在那边属于配置错误。
//
//     判空用 IsNullOrWhiteSpace 而不是 == null：设置页的文本框被清空后交出来
//     的通常是空串而非 null。若只判 null，new Regex("") 会被当作一条已启用的
//     规则接受，而它匹配一切——界面上显示"校验已启用"，实际上什么都没校验。
//     这比不启用更糟：工人以为有一道防线，其实没有。
//
//   IgnoreCase 只传给字符集校验器，绝不传给正则校验器（规格 §9.2、测试 V5）。
//   RegexSkuValidator 根本不接受大小写参数，所以这条约束在编译期就成立，
//   不依赖本类写对。
//
// English:
//   Turns a validation configuration into a composite validator (spec §9).
//
//   The symmetric half of SkuParserFactory. Spec §9 lands here: every enabled rule
//   must pass, and with no rule enabled a successful parse is valid.
//
//   All three validators are constructed unconditionally; this class does not decide
//   whether a rule is "enabled". Each basic validator already carries
//   "not enabled means pass" in its own shape (int? for length, a nullable preset for
//   the character set, a nullable pattern for the regex). Repeating "add it only if
//   enabled" here would give "what counts as enabled" two definitions, and two
//   definitions eventually disagree. SkuValidationSettings documents this design
//   already: the settings layer hands every field over unconditionally and lets each
//   validator declare whether it constrains anything.
//
//   The cost is a composite that always holds three members, with the disabled ones
//   called once per scan and returning valid immediately — three virtual calls,
//   negligible against the cost of a scan.
//
//   A blank validation regex is treated as "not enabled", deliberately the opposite
//   of the parsing layer. Validation rules are all optional (spec §9) and blank means
//   the rule is off, which is a common configuration since many sites need parsing
//   only. Parsing, by contrast, is mandatory in SKU mode, so a blank pattern there is
//   a configuration error.
//
//   The check is IsNullOrWhiteSpace rather than == null: a cleared text box usually
//   yields an empty string. Checking only for null would accept new Regex("") as an
//   enabled rule that matches everything — the screen reporting "validation enabled"
//   while nothing is validated. That is worse than being off, because the operator
//   believes a safeguard exists when it does not.
//
//   IgnoreCase reaches the character-set validator only, never the regex one
//   (spec §9.2, test V5). RegexSkuValidator accepts no case parameter at all, so that
//   constraint holds at compile time rather than depending on this class getting it
//   right.
//
// 包含的成员 / Members in this file:
//   Create  按配置构造组合校验器
// =============================================================================

using ScannerHelper.Core.Settings;

namespace ScannerHelper.Core.Validation;

/// <summary>
/// 中文：按校验配置构造 <see cref="CompositeSkuValidator"/>。
/// English: Builds a <see cref="CompositeSkuValidator"/> from a validation
///          configuration.
/// </summary>
public static class SkuValidatorFactory
{
    /// <summary>
    /// 中文：
    ///   按配置构造组合校验器。
    ///   输入：settings 校验配置，不得为 null。
    ///   输出：包含长度、字符集、校验正则三个成员的组合校验器。
    ///   步骤：
    ///     1. settings 为 null 时抛 ArgumentNullException；
    ///     2. 构造长度校验器，合法性（下限不得大于上限等）由构造函数判定；
    ///     3. 构造字符集校验器，把 IgnoreCase 一并传入；
    ///     4. 构造正则校验器，模式为空白时传 null 表示未启用；
    ///     5. 组合三者返回。
    ///
    ///   成员顺序即失败在结果中的排列顺序（见 CompositeSkuValidator）。
    ///   固定为长度 → 字符集 → 正则，从最基本到最具体，使同一种错误组合
    ///   每次在错误界面上看起来都一样，工人不必每次重新定位。
    ///
    /// English:
    ///   Builds the composite described by the configuration.
    ///   Steps: (1) reject null; (2) build the length validator, with validity
    ///   (minimum not above maximum, and so on) decided by its constructor; (3) build
    ///   the character-set validator, passing IgnoreCase through; (4) build the regex
    ///   validator, passing null for a blank pattern to mean "not enabled";
    ///   (5) combine.
    ///
    ///   Member order is the order failures appear in the result (see
    ///   CompositeSkuValidator). It is fixed as length, character set, regex — most
    ///   basic to most specific — so the same combination of problems always looks the
    ///   same on the error screen and the operator need not re-orient each time.
    /// </summary>
    /// <exception cref="ArgumentNullException">
    /// 中文：settings 为 null。 English: settings is null.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// 中文：最小长度大于最大长度，或校验正则语法错误。
    /// English: The minimum length exceeds the maximum, or the validation regex is not
    ///          valid syntax.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// 中文：长度为负，或字符集预设不是已定义的枚举值。
    /// English: A negative length, or a character-set preset that is not a defined
    ///          enum value.
    /// </exception>
    public static CompositeSkuValidator Create(SkuValidationSettings settings)
    {
        // 步骤 1 / Step 1
        ArgumentNullException.ThrowIfNull(settings);

        // 步骤 2 / Step 2
        var lengthValidator = new LengthSkuValidator(
            settings.MinimumLength, settings.MaximumLength);

        // 步骤 3 / Step 3
        var characterSetValidator = new CharacterSetSkuValidator(
            settings.CharacterSet, settings.IgnoreCase);

        // 步骤 4 / Step 4 —— 空白模式即未启用 / a blank pattern means not enabled
        var regexValidator = new RegexSkuValidator(
            string.IsNullOrWhiteSpace(settings.ValidationRegexPattern)
                ? null
                : settings.ValidationRegexPattern);

        // 步骤 5 / Step 5
        return new CompositeSkuValidator(
            [lengthValidator, characterSetValidator, regexValidator]);
    }
}
