// =============================================================================
// SkuParserFactory.cs
//
// 中文：
//   把一份解析配置变成一个可用的解析器（规格 §8）。
//
//   在此之前，SkuParsingSettings 和两个 ISkuParser 实现是两座孤岛：配置知道
//   用户填了什么，解析器知道怎么解析，但没有任何代码把前者变成后者。本类
//   就是那一段桥。
//
//   ★ 本类**只组装、不判定**：所有合法性检查都由解析器自己的构造函数完成
//     （决策 D-2）。这不是偷懒，而是让约束只有一份定义——"起始位置必须 ≥ 1"
//     这条规则若在这里也抄一遍，两处迟早会不一致，而不一致的那一刻没有任何
//     测试会变红。
//
//     因此本类会**如实抛出**构造函数的异常。设置页据此判断配置能否保存
//     （规格 §13.4：非法配置不能静默保存）：保存前先调一次 Create，抛异常就
//     说明这份规则不能用。
//
//     异常消息是中英双语的开发者文案，**不能直接显示给工人**（决策 D-6 要求
//     Core 不产出面向用户的文案）。设置页负责把异常类型映射为 .resx 里的
//     本地化提示。
//
//   ★ 关于正则模式为空白：这里按**配置错误**处理，抛异常。
//
//     这与校验层的处理刻意相反：校验规则可选，留空即"不启用这条规则"；
//     而解析是 SKU 模式的必经步骤，没有规则就产不出 SKU。若把空模式当成
//     "不启用"，SKU 模式下每一枪都会走进一个无人定义的分支。设置页在保存
//     那一刻拦下它，比工人在现场扫第一枪时才发现要好得多。
//
//     判空用 IsNullOrWhiteSpace 而不是 == null：设置页的文本框被清空后交出来
//     的通常是空串而非 null，纯空格更是常见的误输入。若只判 null，一个空串
//     模式会被 new Regex("") 接受并匹配一切，界面上还显示着"规则已启用"。
//
// English:
//   Turns a parsing configuration into a usable parser (spec §8).
//
//   Until now SkuParsingSettings and the two ISkuParser implementations were two
//   islands: the settings know what the user typed, the parsers know how to parse,
//   and nothing turned one into the other. This class is that bridge.
//
//   It only composes and never judges: every validity check belongs to the parser
//   constructors (decision D-2). Not laziness — it keeps each constraint defined
//   once. Restating "the start position must be >= 1" here would eventually let the
//   two copies drift, and nothing would turn red at the moment they did.
//
//   Constructor exceptions therefore propagate unchanged. The Settings page uses
//   that as its verdict (spec §13.4: an invalid configuration must not save
//   silently) by calling Create before saving and treating a throw as "this rule
//   cannot be used".
//
//   Those exception messages are bilingual developer prose and must not be shown to
//   an operator (decision D-6 keeps user-facing text out of Core). The Settings page
//   maps the exception type onto localized text from .resx.
//
//   A blank regex pattern is treated as a configuration error and throws. This is
//   deliberately the opposite of the validation layer, where every rule is optional
//   and blank means "not enabled". Parsing is a mandatory step in SKU mode: with no
//   rule there is no SKU. Treating a blank pattern as "not enabled" would send every
//   SKU-mode scan into a branch nobody defined. Catching it when Settings is saved
//   beats the operator discovering it on the floor at the first scan.
//
//   The check is IsNullOrWhiteSpace rather than == null: a cleared text box usually
//   yields an empty string rather than null, and whitespace-only input is a common
//   slip. Checking only for null would let new Regex("") be accepted, matching
//   everything, while the screen still reports the rule as enabled.
//
// 包含的成员 / Members in this file:
//   Create  按配置构造对应的解析器
// =============================================================================

using ScannerHelper.Core.Settings;

namespace ScannerHelper.Core.Parsing;

/// <summary>
/// 中文：按解析配置构造 <see cref="ISkuParser"/>。
/// English: Builds an <see cref="ISkuParser"/> from a parsing configuration.
/// </summary>
public static class SkuParserFactory
{
    /// <summary>
    /// 中文：
    ///   按配置构造解析器。
    ///   输入：settings 解析配置，不得为 null。
    ///   输出：与 <see cref="SkuParsingSettings.RuleType"/> 对应的解析器实例。
    ///   步骤：
    ///     1. settings 为 null 时抛 ArgumentNullException；
    ///     2. 按规则类型分派：
    ///        固定位置 —— 用起始位置与长度构造，合法性由构造函数判定；
    ///        正则     —— 模式为空白时抛 ArgumentException，否则用模式与
    ///                    捕获组索引构造；
    ///     3. 规则类型不是已定义的枚举值时抛 ArgumentOutOfRangeException。
    ///
    ///   步骤 3 防的是把任意整数强转成枚举、或配置文件被手工编辑成一个不存在
    ///   的规则类型。C# 的枚举不做范围检查，JSON 里写一个 99 也能反序列化成功，
    ///   若不拦下就会落进 switch 的兜底分支，产生一个无法解释的失败。
    ///
    /// English:
    ///   Builds the parser described by the configuration.
    ///   Steps: (1) reject null; (2) dispatch on the rule type — fixed position uses
    ///   the start and length, with validity decided by the constructor; regex
    ///   rejects a blank pattern and otherwise uses the pattern and group index;
    ///   (3) reject a rule type that is not a defined enum value.
    ///
    ///   Step 3 guards against an arbitrary integer cast to the enum, or a
    ///   hand-edited configuration naming a rule type that does not exist. C# does
    ///   not range-check enums and a 99 in the JSON deserializes successfully;
    ///   unchecked it would fall into the switch's default arm and produce an
    ///   unexplainable failure.
    /// </summary>
    /// <exception cref="ArgumentNullException">
    /// 中文：settings 为 null。 English: settings is null.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// 中文：正则模式为空白，或正则语法错误。
    /// English: The regex pattern is blank, or is not valid regex syntax.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// 中文：起始位置、长度或捕获组索引取值非法，或规则类型未定义。
    /// English: An invalid start position, length or capture-group index, or an
    ///          undefined rule type.
    /// </exception>
    public static ISkuParser Create(SkuParsingSettings settings)
    {
        // 步骤 1 / Step 1
        ArgumentNullException.ThrowIfNull(settings);

        // 步骤 2、3 / Steps 2–3
        switch (settings.RuleType)
        {
            case SkuParsingRuleType.FixedPosition:
                return new FixedPositionSkuParser(settings.StartPosition, settings.Length);

            case SkuParsingRuleType.Regex:
                if (string.IsNullOrWhiteSpace(settings.RegexPattern))
                {
                    throw new ArgumentException(
                        "解析规则类型为正则时，正则模式不能为空——解析是 SKU 模式的"
                        + "必经步骤，没有规则就产不出 SKU。"
                        + " A regex parsing rule requires a pattern: parsing is a"
                        + " mandatory step in SKU mode and cannot yield an SKU without one.",
                        nameof(settings));
                }

                return new RegexSkuParser(settings.RegexPattern, settings.CaptureGroupIndex);

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(settings), settings.RuleType,
                    "解析规则类型不是已定义的枚举值。"
                    + " The parsing rule type is not a defined enum value.");
        }
    }
}
