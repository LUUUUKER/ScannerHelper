// =============================================================================
// HotkeyConflictDetector.cs
//
// 中文：
//   检测热键配置里的重复分配（规格 §13.3：检测冲突、拒绝非法的重复分配）。
//
//   ★ 为什么这件事必须在**保存设置时**拦下，而不能留到运行时。
//
//     假设工人不小心把"取消"也设成了 F8。运行时会发生什么？路由按顺序匹配，
//     第一个命中的是"切换模式"，于是 F8 切模式，而"取消"这个热键**永远
//     不生效**——而且毫无提示。
//
//     现场表现是：Esc……哦不，是 F8，按了能切模式；但错误状态下怎么按都
//     取消不掉。工人会以为是错误处理坏了，而实际是两个热键撞了。这类故障
//     的排查成本极高，因为症状（取消失灵）和原因（模式键重复）之间没有
//     任何看得见的联系。
//
//     在保存那一刻拒绝，工人立刻就知道是自己刚改的那一项有问题。这与
//     决策 D-2 是同一条原则：与运行时输入无关的配置错误，一律在配置的
//     那一刻拒绝。
//
//   ★ 未分配（null 或空白）不算重复。
//
//     规格 §13.3 规定暂停热键**默认未分配**，而且四个热键理论上可以全都
//     不分配。若把"两个都是未分配"当成冲突，默认配置本身就存不下去了。
//
//     判空用 IsNullOrWhiteSpace 而不是 == null：设置页的输入框被清空后交出来
//     的通常是空串而非 null，纯空格更是常见的误输入。只判 null 的话，
//     两个空串会被当成"重复分配了同一个键"，而那个"键"根本不存在。
//
//   ★ 比较忽略大小写。
//
//     配置里存的是 "F8"、"Escape" 这样的名字。"f8" 与 "F8" 指的是同一个键，
//     若区分大小写，工人手输一个小写就绕过了冲突检测，然后在运行时撞车——
//     而运行时的映射是不区分大小写的，两者对不上。
//
// English:
//   Detects duplicate assignments in hotkey configuration (spec §13.3: detect conflicts and
//   refuse invalid duplicate assignments).
//
//   Why this must be caught when settings are saved rather than at runtime: suppose the operator
//   accidentally sets Cancel to F8 as well. At runtime the router matches in order, Toggle Mode
//   matches first, F8 switches the mode — and the Cancel hotkey simply never works, with no
//   indication whatsoever.
//
//   On site that reads as: F8 switches the mode fine, but nothing cancels a pending error no
//   matter how it is pressed. The operator concludes error handling is broken, when in fact two
//   hotkeys collided. Such faults are expensive to diagnose because there is no visible
//   connection between the symptom (cancel does nothing) and the cause (the mode key is
//   duplicated). Refusing at save time tells the operator immediately that the setting they just
//   changed is the problem. This is decision D-2's principle again: configuration errors
//   independent of runtime input are rejected the moment the configuration is made.
//
//   Unassigned entries — null or blank — are not duplicates. Spec §13.3 leaves the pause hotkey
//   unassigned by default, and in principle all four could be, so treating "both unassigned" as
//   a conflict would make the default configuration unsavable. The check uses IsNullOrWhiteSpace
//   rather than == null because a cleared text box usually yields an empty string and
//   whitespace-only input is a common slip; checking only for null would count two empty strings
//   as duplicate assignments of a key that does not exist.
//
//   Comparison ignores case. Configuration stores names like "F8" and "Escape", where "f8" and
//   "F8" mean the same key. Case-sensitive comparison would let a hand-typed lowercase slip past
//   the check and collide at runtime, where the mapping is case-insensitive and the two would no
//   longer agree.
//
// 包含的成员 / Members in this file:
//   FindConflicts  找出被重复分配的键名
// =============================================================================

using ScannerHelper.Core.Settings;

namespace ScannerHelper.Core.Input;

/// <summary>
/// 中文：热键配置的冲突检测。
/// English: Conflict detection for hotkey configuration.
/// </summary>
public static class HotkeyConflictDetector
{
    /// <summary>
    /// 中文：
    ///   找出被重复分配给多个功能的键名。
    ///   输入：hotkeys 热键配置，不得为 null。
    ///   输出：被重复分配的键名，按原样返回（不改大小写），无冲突时为空。
    ///   步骤：
    ///     1. hotkeys 为 null 时抛 ArgumentNullException；
    ///     2. 取出四个热键，丢掉未分配的；
    ///     3. 忽略大小写分组，返回出现多于一次的键名。
    ///
    ///   ★ 返回冲突的**键名**而不是布尔，是为了让设置页能说清楚问题：
    ///     "F8 被分配给了多个功能"远比"热键配置有冲突"有用——后者会让工人
    ///     对着四个输入框逐个猜。规格 §13.4 要求非法配置不能静默保存，
    ///     而"不能保存"若不附带原因，工人就只能撤销自己刚做的全部修改。
    ///
    ///   步骤 3 返回的是**原样的键名**，不是统一大写过的。工人看到的应当是
    ///   他自己输进去的那个字符串，而不是程序规范化之后的版本——后者会让他
    ///   一时对不上自己改的是哪一项。
    /// English:
    ///   Returns the key names assigned to more than one function, as written (case unchanged),
    ///   or empty when there is no conflict.
    ///   Steps: (1) reject null; (2) take the four hotkeys and drop the unassigned; (3) group
    ///   case-insensitively and return those appearing more than once.
    ///
    ///   Returning the conflicting *names* rather than a boolean lets the Settings page explain
    ///   the problem: "F8 is assigned to more than one function" is far more use than "the hotkey
    ///   configuration has a conflict", which leaves the operator guessing across four fields.
    ///   Spec §13.4 requires an invalid configuration not to save silently, and a refusal without
    ///   a reason leaves them undoing everything they just changed.
    ///
    ///   Step 3 returns the names as written rather than upper-cased, so the operator sees the
    ///   string they typed rather than a normalized version they may not immediately recognize.
    /// </summary>
    /// <exception cref="ArgumentNullException">
    /// 中文：hotkeys 为 null。 English: hotkeys is null.
    /// </exception>
    public static IReadOnlyList<string> FindConflicts(HotkeySettings hotkeys)
    {
        // 步骤 1 / Step 1
        ArgumentNullException.ThrowIfNull(hotkeys);

        // 步骤 2 / Step 2
        string?[] assignments =
        [
            hotkeys.ToggleMode,
            hotkeys.ForceSend,
            hotkeys.Cancel,
            hotkeys.PauseResume,
        ];

        // 步骤 3 / Step 3
        return assignments
            .Where(assignment => !string.IsNullOrWhiteSpace(assignment))
            .Select(assignment => assignment!.Trim())
            .GroupBy(assignment => assignment, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.First())
            .ToArray();
    }
}
