// =============================================================================
// DeviceIdentityMatcher.cs
//
// 中文：
//   判断眼前这台设备是不是配置里记录的那把扫码枪，以及有多确信（规格 §6、§17）。
//
//   纯逻辑，与设备枚举完全分开——枚举需要 Windows，匹配规则不需要。因此
//   这里的每一条规则都能用合成身份穷尽测试，而 Phase B 只需要负责"把设备
//   列出来"这一件事（规格 §17 明确要求这样切分）。
//
//   ★★ 本文件里最重要的一条规则：**两边都没有的字段，绝不算匹配。**
//
//     `a.SerialNumber == b.SerialNumber` 在两边都是 null 时返回 true。
//     C# 的语义没错，但它回答的是另一个问题：它说的是"两台设备都没有序列号"，
//     而不是"两台设备的序列号相同"。
//
//     这在本项目里不是理论问题。Task 4a 实测发现笔记本内置键盘走 ACPI：
//         \\?\ACPI#MSFT0001#4&b6e66aa&0#{884b96c3-...}
//     **没有 VID，也没有 PID**（规格假设 A4）。若"都没有"算作匹配，
//     内置键盘会与任何一份缺字段的绑定记录匹配上，然后被当成扫码枪——
//     结果是工人打的每一个字都被吞进扫描缓冲区，键盘彻底失灵。
//
//     而那正是规格 §5.7 里 PAUSED 存在的理由所描述的灾难场景。一个匹配 bug
//     就足以制造它。
//
//   ★ 硬性标识互相矛盾时，一律判为不匹配。
//
//     若两边都报了 VID 而值不同，那就是两台不同的设备——此时即便序列号
//     碰巧相同也不能算匹配。序列号只在厂商范围内保证唯一，遇到 "1"、"0000"
//     这种偷懒的序列号，跨厂商撞车完全可能。
//
//     先排除矛盾再看序列号，比反过来安全：宁可要求工人重新绑定一次，
//     也不要把一台别的设备认成扫码枪。
//
//   ★ 优先级顺序来自规格 §6："优先采用最具体的稳定身份（设备路径 > 序列号 >
//     VID/PID），仅凭 VID/PID 不保证唯一。"
//
// English:
//   Decides whether the device in front of us is the scanner recorded in configuration, and
//   with what confidence (spec §6, §17).
//
//   Pure logic, kept entirely separate from device enumeration: enumeration needs Windows and
//   the matching rules do not. Every rule here is therefore exhaustively testable against
//   synthetic identities, leaving Phase B responsible only for listing devices — the split
//   spec §17 asks for explicitly.
//
//   The most important rule in this file: a field absent on both sides is never a match.
//   `a.SerialNumber == b.SerialNumber` returns true when both are null. The C# semantics are
//   correct and answer a different question: they say "neither device has a serial number", not
//   "the two devices have the same serial number".
//
//   This is not theoretical here. Task 4a found the laptop's built-in keyboard arriving over
//   ACPI as \\?\ACPI#MSFT0001#4&b6e66aa&0#{884b96c3-...}, with no VID and no PID at all (spec
//   assumption A4). If "both absent" counted as a match, the built-in keyboard would match any
//   binding record missing those fields and be taken for the scanner — swallowing every
//   character the operator types and leaving the keyboard entirely dead. Which is the disaster
//   scenario spec §5.7 gives as the reason PAUSED exists; a single matching bug is enough to
//   produce it.
//
//   Contradictory hard identifiers mean no match. If both sides report a VID and the values
//   differ, these are two different devices, and a coincidentally equal serial number does not
//   change that: serials are only guaranteed unique within a vendor, and lazy ones such as "1"
//   or "0000" collide across vendors readily. Excluding contradictions before consulting the
//   serial is the safer order — better to ask the operator to rebind than to mistake some other
//   device for the scanner.
//
//   The priority order comes from spec §6: favor the most specific stable identity (device path
//   > serial > VID/PID), and VID/PID alone is not guaranteed unique.
//
// 包含的成员 / Members in this file:
//   Match          单台设备与绑定记录的匹配置信度
//   FindBestMatch  在一组候选中挑出最佳匹配，并指出是否有歧义
// =============================================================================

using ScannerHelper.Core.Domain;
using ScannerHelper.Core.Settings;

namespace ScannerHelper.Core.Input;

/// <summary>
/// 中文：把观测到的设备身份与配置里记录的绑定作比较。
/// English: Compares an observed device identity against the binding recorded in configuration.
/// </summary>
public static class DeviceIdentityMatcher
{
    /// <summary>
    /// 中文：
    ///   判断一台设备与绑定记录的匹配置信度。
    ///   输入：observed 观测到的设备身份；binding 配置里记录的绑定。均不得为 null。
    ///   输出：置信度。
    ///   步骤：
    ///     1. 参数为 null 时抛 ArgumentNullException；
    ///     2. 设备路径两边都有且相同 → Exact（规格 §6 里最具体的身份）；
    ///     3. VID 或 PID 两边都有但不同 → None，这是矛盾，不必再看别的；
    ///     4. 序列号两边都有且相同 → High；
    ///     5. VID 与 PID 两边都有且都相同 → Low（只说明型号相同）；
    ///     6. 其余 → None。
    ///
    ///   ★ 每一步的"两边都有"都不能省。省掉之后，两个 null 会被判为相等，
    ///     于是没有序列号的设备与没有序列号的绑定"匹配"上了——而它们之间
    ///     其实没有任何共同点。笔记本内置键盘正是这样一台没有 VID/PID 的
    ///     设备（规格假设 A4），把它认成扫码枪意味着工人的键盘彻底失灵。
    ///
    ///   ★ 步骤 3 排在步骤 4 之前是有意的。两边报了不同的 VID 就说明是两台
    ///     不同的设备，此时序列号碰巧相同也不能算数——序列号只在厂商范围内
    ///     保证唯一，"1" 这种偷懒的序列号跨厂商撞车完全可能。
    ///
    ///   ★ 步骤 2 里设备路径的比较**忽略大小写**：Windows 的设备路径本身
    ///     大小写不敏感，同一台设备在不同 API、不同版本上返回的大小写可能
    ///     不同。区分大小写会让同一台设备在重连时匹配不上，表现为"每次插上
    ///     都要求重新绑定"。
    /// English:
    ///   Returns how confidently a device matches the recorded binding.
    ///   Steps: (1) reject nulls; (2) device paths present on both sides and equal yields Exact,
    ///   spec §6's most specific identity; (3) a VID or PID present on both sides but differing
    ///   yields None, a contradiction that settles it; (4) serials present on both sides and
    ///   equal yields High; (5) VID and PID present on both sides and equal yields Low, which
    ///   identifies only the model; (6) otherwise None.
    ///
    ///   The "present on both sides" clause is required at every step. Without it two nulls
    ///   compare equal, so a device with no serial number "matches" a binding with no serial
    ///   number while having nothing whatever in common with it. The laptop's built-in keyboard
    ///   is exactly such a device, with no VID/PID (spec assumption A4), and taking it for the
    ///   scanner leaves the operator's keyboard entirely dead.
    ///
    ///   Step 3 deliberately precedes step 4: differing VIDs mean two different devices, and a
    ///   coincidentally equal serial cannot override that, serials being unique only within a
    ///   vendor and lazy ones such as "1" colliding across vendors readily.
    ///
    ///   Step 2 compares device paths case-insensitively. Windows device paths are themselves
    ///   case-insensitive and the same device can come back differently cased from different
    ///   APIs or Windows versions; comparing case-sensitively would fail to match the same
    ///   device on reconnect, presenting as "it asks me to rebind every time I plug it in".
    /// </summary>
    /// <exception cref="ArgumentNullException">
    /// 中文：observed 或 binding 为 null。 English: observed or binding is null.
    /// </exception>
    public static DeviceMatchConfidence Match(
        ScannerDeviceIdentity observed, ScannerBindingSettings binding)
    {
        // 步骤 1 / Step 1
        ArgumentNullException.ThrowIfNull(observed);
        ArgumentNullException.ThrowIfNull(binding);

        // 步骤 2 / Step 2
        if (BothPresentAndEqual(observed.DevicePath, binding.DevicePath))
        {
            return DeviceMatchConfidence.Exact;
        }

        // 步骤 3 / Step 3 —— 矛盾即不匹配 / a contradiction settles it
        if (BothPresentAndDifferent(observed.VendorId, binding.VendorId)
            || BothPresentAndDifferent(observed.ProductId, binding.ProductId))
        {
            return DeviceMatchConfidence.None;
        }

        // 步骤 4 / Step 4
        if (BothPresentAndEqual(observed.SerialNumber, binding.SerialNumber))
        {
            return DeviceMatchConfidence.High;
        }

        // 步骤 5 / Step 5
        if (BothPresentAndEqual(observed.VendorId, binding.VendorId)
            && BothPresentAndEqual(observed.ProductId, binding.ProductId))
        {
            return DeviceMatchConfidence.Low;
        }

        // 步骤 6 / Step 6
        return DeviceMatchConfidence.None;
    }

    /// <summary>
    /// 中文：
    ///   在一组候选设备中挑出与绑定记录最匹配的那一台。
    ///   输入：candidates 当前连接的设备；binding 配置里记录的绑定。均不得为 null。
    ///   输出：最佳匹配、它的置信度，以及是否有歧义。
    ///   步骤：
    ///     1. 参数为 null 时抛 ArgumentNullException；
    ///     2. 逐台计算置信度，取最高的那一档；
    ///     3. 最高档为 None 时返回"没有匹配"；
    ///     4. 统计有多少台并列在最高档，多于一台即为有歧义。
    ///
    ///   ★ 步骤 4 是本方法存在的理由。单看一台设备永远发现不了歧义——
    ///     它自己匹配得好好的。只有把候选放在一起比，"有两台都一样匹配得上"
    ///     才浮得出来。
    ///
    ///     规格 §6 点名了这个场景："不要强迫用户从多个一模一样的
    ///     『HID Keyboard Device』条目里挑。"若仓库里有两把同型号的扫码枪，
    ///     它们在 VID/PID 这一档上完全无法区分。此时自动挑一台绑定，工人
    ///     拿错了枪也不会有任何提示——而正确的做法是让他扫一枪来指认
    ///     （规格 §6 的重新绑定流程）。
    ///
    ///   ★ 有歧义时**仍然返回一台设备**，而不是返回 null。
    ///
    ///     因为界面需要拿它来告诉工人"找到了 2 台看起来一样的设备"。
    ///     真正阻止自动绑定的是 CanReconnectAutomatically，而不是把设备藏起来
    ///     ——藏起来会让调用方少一份可以展示给工人的信息。
    /// English:
    ///   Picks the candidate that best matches the recorded binding, reporting the confidence
    ///   and whether the choice was ambiguous.
    ///   Steps: (1) reject nulls; (2) score every candidate and take the highest confidence;
    ///   (3) return no match when that is None; (4) count how many tie at the top, more than one
    ///   being ambiguous.
    ///
    ///   Step 4 is why this method exists. Ambiguity can never be seen one device at a time —
    ///   each matches perfectly well on its own. Only comparing the candidates together surfaces
    ///   "two of these match equally".
    ///
    ///   Spec §6 names the scenario: do not force users to pick from opaque entries such as
    ///   multiple identical HID Keyboard Device names. Two scanners of one model in the
    ///   warehouse are indistinguishable at the VID/PID level, and auto-binding one of them
    ///   would give an operator holding the wrong gun no warning at all — where the right answer
    ///   is to have them identify it by scanning (spec §6's rebinding flow).
    ///
    ///   An ambiguous result still returns a device rather than null, because the UI needs it to
    ///   tell the operator that two apparently identical devices were found. What prevents
    ///   automatic binding is CanReconnectAutomatically, not hiding the device — hiding it would
    ///   only deprive the caller of something worth showing.
    /// </summary>
    /// <exception cref="ArgumentNullException">
    /// 中文：candidates 或 binding 为 null。 English: candidates or binding is null.
    /// </exception>
    public static DeviceMatchResult FindBestMatch(
        IReadOnlyList<ScannerDeviceIdentity> candidates, ScannerBindingSettings binding)
    {
        // 步骤 1 / Step 1
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(binding);

        // 步骤 2 / Step 2
        var bestConfidence = DeviceMatchConfidence.None;
        ScannerDeviceIdentity? bestDevice = null;
        var tiedCount = 0;

        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            var confidence = Match(candidate, binding);

            if (confidence == DeviceMatchConfidence.None)
            {
                continue;
            }

            if (confidence > bestConfidence)
            {
                bestConfidence = confidence;
                bestDevice = candidate;
                tiedCount = 1;
                continue;
            }

            // 步骤 4 / Step 4 —— 并列在最高档
            // Step 4 — tied at the top
            if (confidence == bestConfidence)
            {
                tiedCount++;
            }
        }

        // 步骤 3 / Step 3
        if (bestDevice is null)
        {
            return DeviceMatchResult.NoMatch;
        }

        return new DeviceMatchResult(bestDevice, bestConfidence, IsAmbiguous: tiedCount > 1);
    }

    /// <summary>
    /// 中文：
    ///   两个身份字段是否**都有值且相同**。
    ///
    ///   ★ "都有值"这半句是本文件的核心。少了它，两个 null 会被判为相等，
    ///     于是"两台设备都没有序列号"被当成了"两台设备的序列号相同"。
    ///
    ///   空白按"没有值"处理，与项目其他地方一致（见 SkuValidatorFactory）：
    ///   设备真报了一个空字符串，那和没报没有区别，不该被当成一个可以拿来
    ///   匹配的身份。
    ///
    ///   比较忽略大小写：Windows 的设备路径与十六进制的 VID/PID 都大小写
    ///   不敏感，同一台设备在不同 API 上返回的大小写可能不同。区分大小写会
    ///   让同一台设备重连时匹配不上，表现为"每次插上都要求重新绑定"。
    /// English:
    ///   Whether two identity fields are both present and equal.
    ///
    ///   "Both present" is the crux of this file. Without it two nulls compare equal, turning
    ///   "neither device has a serial number" into "the two devices have the same serial number".
    ///
    ///   Blank counts as absent, consistently with the rest of the project (see
    ///   SkuValidatorFactory): a device that genuinely reported an empty string reported nothing
    ///   useful, and that is not an identity worth matching on.
    ///
    ///   Comparison ignores case. Windows device paths and hexadecimal VID/PID values are both
    ///   case-insensitive, and the same device can come back differently cased from different
    ///   APIs; comparing case-sensitively would fail to match it on reconnect, presenting as "it
    ///   asks me to rebind every time I plug it in".
    /// </summary>
    private static bool BothPresentAndEqual(string? observed, string? recorded)
        => !string.IsNullOrWhiteSpace(observed)
           && !string.IsNullOrWhiteSpace(recorded)
           && string.Equals(observed.Trim(), recorded.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 中文：两个身份字段是否**都有值但不同**——即互相矛盾。
    ///       只有一边有值不算矛盾：那只是信息不全，不是"说的是两台设备"。
    /// English: Whether two identity fields are both present and differ, that is, contradict.
    ///          One side alone having a value is not a contradiction but merely incomplete
    ///          information.
    /// </summary>
    private static bool BothPresentAndDifferent(string? observed, string? recorded)
        => !string.IsNullOrWhiteSpace(observed)
           && !string.IsNullOrWhiteSpace(recorded)
           && !string.Equals(observed.Trim(), recorded.Trim(), StringComparison.OrdinalIgnoreCase);
}
