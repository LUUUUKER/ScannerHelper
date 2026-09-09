// =============================================================================
// CharacterSetSkuValidator.cs
//
// 中文：
//   按字符集校验 SKU（规格 §9.2）。
//
//   两个独立的维度：
//     预设         决定允许哪几类字符（数字 / 字母 / 字母加数字 / 再加 -_）
//     忽略大小写   只决定字母的大小写算不算数
//
//   这个划分必须被严格守住。开关只作用于字母的大小写判断，对数字、连字符、
//   下划线毫无影响（测试 C10），也绝不会波及校验正则（测试 V5）。一正一反
//   两条测试把它的作用范围夹死，防止日后被顺手扩大。
//
//   ★ 实现上最关键的一点：必须用 ASCII 专用的判断方法。
//     char.IsLetter('中') 返回 true，char.IsDigit('٣') 也返回 true——用它们
//     会让汉字通过"仅字母"校验、让阿拉伯数字通过"仅数字"校验。仓库条码是
//     ASCII 的，非 ASCII 字符出现在 SKU 里本身就是异常信号（很可能是键盘
//     布局解码错了），必须被拦下而不是悄悄放行。因此这里一律使用
//     char.IsAscii* 系列方法。测试 C11 钉住这一点。
//
//   "未启用" 用可空预设表达，而不是在枚举里塞 None。这样设置层可以无条件
//   构造全部校验器，由每个校验器自己声明是否施加约束，外层不必写一堆
//   "这项启用了吗" 的分支。
//
// English:
//   Validates an SKU against a character set (spec §9.2).
//
//   Two independent dimensions: the preset decides which kinds of character are
//   allowed; the ignore-case toggle decides only whether letter case matters. The
//   toggle must never affect digits, hyphens or underscores (test C10), and never
//   leak into the validation regex (test V5). Those two tests cage its scope.
//
//   The critical implementation point: ASCII-specific predicates are mandatory.
//   char.IsLetter('中') and char.IsDigit('٣') both return true, so using them
//   would let a Chinese character pass "letters only" and an Arabic-Indic digit
//   pass "numbers only". Warehouse barcodes are ASCII, and a non-ASCII character
//   in an SKU is itself an anomaly — most likely a keyboard-layout decoding
//   error — that must be caught rather than quietly accepted. Hence char.IsAscii*
//   throughout. Test C11 pins this down.
//
//   "Not enabled" is a nullable preset rather than a None enum member, so the
//   settings layer can build every validator unconditionally and let each declare
//   whether it constrains anything.
//
// 包含的成员 / Members in this file:
//   Preset      字符集预设，null 表示未启用
//   IgnoreCase  是否忽略字母大小写
//   Validate    执行字符集校验
//   IsAllowed   判断单个字符是否被当前预设允许
// =============================================================================

using ScannerHelper.Core.Domain;

namespace ScannerHelper.Core.Validation;

/// <summary>
/// 中文：字符集校验器。构造后不可变，可安全复用。
/// English: Character-set validator. Immutable after construction and safe to
///          reuse.
/// </summary>
public sealed class CharacterSetSkuValidator : ISkuValidator
{
    /// <summary>
    /// 中文：
    ///   构造字符集校验器并校验配置。
    ///   输入：preset 字符集预设，null 表示该项未启用；
    ///         ignoreCase 是否忽略字母大小写。
    ///   输出：校验器实例。
    ///   步骤：
    ///     1. 预设非 null 但不是已定义的枚举值时抛 ArgumentOutOfRangeException；
    ///     2. 保存配置。
    ///
    ///   步骤 1 防的是把任意整数强转成枚举传进来的情况——C# 的枚举不做范围
    ///   检查，(CharacterSetPreset)99 是合法表达式。若不拦下，运行期会落进
    ///   switch 的兜底分支，产生一个无法解释的失败。按决策 D-2，与扫到什么码
    ///   无关的配置错误一律在构造时拒绝。
    ///
    ///   ignoreCase 没有默认值，调用点必须显式写明。规格 §9.2 规定的默认
    ///   开启属于**设置项**的默认值（见 AppSettings），不是本类的默认参数——
    ///   把它放成默认参数会让调用点看不出当前到底是哪种行为。
    ///
    /// English:
    ///   Creates the validator and validates its configuration.
    ///   Steps: (1) reject a non-null preset that is not a defined enum value;
    ///   (2) store.
    ///
    ///   Step 1 guards against an arbitrary integer cast to the enum: C# does not
    ///   range-check enums, so (CharacterSetPreset)99 is a legal expression.
    ///   Unchecked it would fall into the switch's default arm at runtime and
    ///   produce an unexplainable failure. Per decision D-2, configuration errors
    ///   independent of the scanned code are rejected at construction.
    ///
    ///   ignoreCase has no default value; call sites must state it. Spec §9.2's
    ///   "enabled by default" is the default of a *setting* (see AppSettings), not
    ///   of this constructor — a default parameter here would hide which behavior
    ///   a given call site actually gets.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// 中文：preset 不是已定义的枚举值。
    /// English: preset is not a defined enum value.
    /// </exception>
    public CharacterSetSkuValidator(CharacterSetPreset? preset, bool ignoreCase)
    {
        // 步骤 1 / Step 1
        if (preset is CharacterSetPreset requestedPreset && !Enum.IsDefined(requestedPreset))
        {
            throw new ArgumentOutOfRangeException(
                nameof(preset), preset,
                "字符集预设不是已定义的枚举值。"
                + " The character set preset is not a defined enum value.");
        }

        // 步骤 2 / Step 2
        Preset = preset;
        IgnoreCase = ignoreCase;
    }

    /// <summary>
    /// 中文：字符集预设。null 表示该项未启用，不对字符作任何约束。
    /// English: The preset; null means the rule is not enabled and no constraint
    ///          is imposed.
    /// </summary>
    public CharacterSetPreset? Preset { get; }

    /// <summary>
    /// 中文：是否忽略字母大小写。仅影响字母判断，对数字与符号无影响。
    /// English: Whether letter case is ignored. Affects letters only; digits and
    ///          symbols are unaffected.
    /// </summary>
    public bool IgnoreCase { get; }

    /// <summary>
    /// 中文：
    ///   校验 SKU 的每个字符是否都在允许的字符集内。
    ///   输入：sku 候选 SKU，不得为 null。
    ///   输出：Valid，或 Invalid 携带一条 IllegalCharacter。
    ///   步骤：
    ///     1. sku 为 null 时抛 ArgumentNullException（决策 D-1）；
    ///     2. 预设未启用时直接通过；
    ///     3. 从左到右扫描，遇到第一个不被允许的字符即返回失败，
    ///        携带该字符及其 1-based 位置；
    ///     4. 全部字符都被允许则通过。
    ///
    ///   空串在步骤 3 的循环中不会进入循环体，因此直接落到步骤 4 通过
    ///   （决策 D-5）。空串不含任何非法字符，字符集校验没有理由拒绝它；
    ///   "不能为空" 是长度校验的职责。两层各管各的，否则一个空 SKU 会同时
    ///   产生两条失败，工人看到重复的抱怨。
    ///
    ///   步骤 3 遇到第一个即返回，不继续扫描剩余字符：单个校验器最多产生
    ///   一条失败（ISkuValidator 契约），且一个定位好的位置已足够排查。
    ///
    ///   按 char 逐个遍历意味着代理对（如 emoji）会被拆成两半。这对条码
    ///   场景无影响——代理项的任一半都不是 ASCII，本就会被判为非法并在其
    ///   所在位置报出。
    ///
    /// English:
    ///   Steps: (1) throw on null; (2) pass immediately if the preset is not
    ///   enabled; (3) scan left to right and return on the first disallowed
    ///   character with its 1-based position; (4) otherwise pass.
    ///
    ///   An empty string never enters the loop and falls through to step 4
    ///   (decision D-5): it contains no illegal character, so this rule has no
    ///   grounds to reject it. "Must not be empty" belongs to the length rule;
    ///   keeping the layers separate avoids an empty SKU producing two failures
    ///   and showing the operator a duplicated complaint.
    ///
    ///   Iterating by char splits surrogate pairs such as emoji. That is harmless
    ///   here: neither half is ASCII, so it is rejected anyway and reported at its
    ///   position.
    /// </summary>
    public ValidationResult Validate(string sku)
    {
        // 步骤 1 / Step 1
        ArgumentNullException.ThrowIfNull(sku);

        // 步骤 2 / Step 2
        if (Preset is not CharacterSetPreset activePreset)
        {
            return ValidationResult.Valid.Instance;
        }

        // 步骤 3 / Step 3
        for (var characterIndex = 0; characterIndex < sku.Length; characterIndex++)
        {
            var character = sku[characterIndex];
            if (!IsAllowed(character, activePreset))
            {
                return new ValidationResult.Invalid(
                    new ValidationFailure.IllegalCharacter(
                        character,
                        Position: characterIndex + 1));   // 1-based，供工人阅读
            }
        }

        // 步骤 4 / Step 4
        return ValidationResult.Valid.Instance;
    }

    /// <summary>
    /// 中文：
    ///   判断单个字符是否被指定预设允许。
    ///   输入：character 待判断的字符；preset 当前生效的预设。
    ///   输出：允许返回 true。
    ///
    ///   ★ 全部判断都走 char.IsAscii* 系列，绝不使用 char.IsLetter /
    ///     char.IsDigit。后者会把汉字判为字母、把阿拉伯数字判为数字，
    ///     使非 ASCII 字符通过校验。而 SKU 中出现非 ASCII 字符往往意味着
    ///     键盘布局解码出了问题，正是最需要被拦下的情况（测试 C11）。
    ///
    ///   字母的判断受 IgnoreCase 影响：
    ///     忽略大小写   A-Z 与 a-z 都允许
    ///     区分大小写   仅 A-Z
    ///   数字与符号不受该开关影响（测试 C10）。
    ///
    /// English:
    ///   Whether one character is allowed by the given preset.
    ///
    ///   All checks use char.IsAscii*, never char.IsLetter / char.IsDigit — those
    ///   would count '中' as a letter and '٣' as a digit, admitting non-ASCII
    ///   characters. Non-ASCII in an SKU usually means keyboard-layout decoding
    ///   went wrong, exactly the case most worth catching (test C11).
    ///
    ///   Letters honor IgnoreCase (A-Z plus a-z, or A-Z alone); digits and symbols
    ///   do not (test C10).
    /// </summary>
    private bool IsAllowed(char character, CharacterSetPreset preset)
    {
        var isAllowedLetter = IgnoreCase
            ? char.IsAsciiLetter(character)
            : char.IsAsciiLetterUpper(character);

        return preset switch
        {
            CharacterSetPreset.Numbers =>
                char.IsAsciiDigit(character),

            CharacterSetPreset.Letters =>
                isAllowedLetter,

            CharacterSetPreset.LettersNumbers =>
                isAllowedLetter || char.IsAsciiDigit(character),

            CharacterSetPreset.LettersNumbersDashUnderscore =>
                isAllowedLetter || char.IsAsciiDigit(character) || character is '-' or '_',

            // 构造函数已拒绝未定义的枚举值，此分支不可达。
            // Unreachable: the constructor rejects undefined enum values.
            _ => throw new InvalidOperationException(
                $"未处理的字符集预设 / Unhandled character set preset: {preset}"),
        };
    }
}
