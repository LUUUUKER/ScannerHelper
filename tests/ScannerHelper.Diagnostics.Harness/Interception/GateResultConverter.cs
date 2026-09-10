// =============================================================================
// GateResultConverter.cs
//
// 中文：
//   把「未测/通过/失败」这个枚举接到三个单选按钮上。
//
//   ★ 为什么不用三个 bool 属性省掉这个转换器。
//
//     三个 bool 表达的是八种组合，其中七种是无意义的——「既通过又失败」、
//     「三个都没选」。而结论天然只有三种取值，用枚举表达就不可能构造出
//     那七种状态。这与项目里 ParseResult、ScanOutcome 用封闭类型的理由一致：
//     让不该存在的状态在类型上就无法表达，而不是靠界面代码小心地维持一致。
//
//     代价是需要这个转换器；收益是导出报告时不必再问「三个 bool 都是 false
//     算什么」。
//
//   ConvertBack 只在「被选中」时回写。单选按钮组里选中一个会让另外两个
//   变成未选中，那两次回调必须什么都不做——否则刚写进去的值会被紧随其后的
//   「未选中」回调覆盖掉，表现是点了没反应。
//
// English:
//   Binds the not-tested/passed/failed enum to three radio buttons.
//
//   Three booleans would express eight combinations, seven of them meaningless — "both passed
//   and failed", "none of the three selected". The verdict has exactly three values, and an
//   enum makes those seven states unconstructible. Same reasoning as ParseResult and ScanOutcome
//   elsewhere in this project: make impossible states inexpressible in the type rather than
//   having UI code carefully maintain consistency. The cost is this converter; the benefit is
//   never having to ask what "all three booleans false" means when exporting the report.
//
//   ConvertBack writes only when a button becomes checked. Selecting one in a group unchecks the
//   other two, and those two callbacks must do nothing — otherwise the value just written is
//   immediately overwritten by an "unchecked" callback, presenting as a click that does nothing.
//
// 包含的类型 / Types in this file:
//   GateResultConverter
// =============================================================================

using System.Globalization;
using System.Windows.Data;

namespace ScannerHelper.Diagnostics.Harness.Interception;

/// <summary>
/// 中文：<see cref="GateResult"/> 与单选按钮之间的转换器。
/// English: Converts between <see cref="GateResult"/> and a radio button's checked state.
/// </summary>
public sealed class GateResultConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is GateResult result
           && parameter is string expected
           && Enum.TryParse<GateResult>(expected, out var parsed)
           && result == parsed;

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not true
            || parameter is not string expected
            || !Enum.TryParse<GateResult>(expected, out var parsed))
        {
            // ★ 未选中不代表任何结论，回写会覆盖掉刚选好的那个值。
            // An unchecked state carries no verdict; writing back would overwrite the value just
            // chosen.
            return Binding.DoNothing;
        }

        return parsed;
    }
}
