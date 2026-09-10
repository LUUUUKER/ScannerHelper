// =============================================================================
// DeviceIdentityMatcherTests.cs
//
// 中文：
//   设备身份匹配规则（Task 5a，用例编号 DM1~DM15）。
//
//   本文件里分量最重的两条：
//     DM7   笔记本内置键盘（走 ACPI，**没有 VID/PID**）绝不能与缺字段的绑定
//           记录匹配上。这条用例原计划里没有——它是 Task 4a 实测倒逼出来的
//           （规格假设 A4）。
//     DM11  两台同型号设备并列时必须报告有歧义，不得自动挑一台绑定。
//           规格 §6 点名了这个场景。
//
//   本组全部使用合成身份，不需要任何硬件——匹配规则是纯逻辑，与设备枚举
//   刻意分开（规格 §17）。
//
// English:
//   Device identity matching rules (Task 5a, cases DM1–DM15).
//
//   Two carry the most weight. DM7: the laptop's built-in keyboard, arriving over ACPI with no
//   VID/PID, must never match a binding record that is missing those fields — a case the
//   original plan did not contain, forced by Task 4a's measurements (spec assumption A4).
//   DM11: two devices of one model must be reported as ambiguous rather than one being
//   auto-bound, the scenario spec §6 names.
//
//   Everything here uses synthetic identities and needs no hardware: the matching rules are
//   pure logic, deliberately separated from device enumeration (spec §17).
//
// 包含的测试 / Tests in this file:
//   Identical_device_path_is_an_exact_match              DM1
//   Matching_serial_is_a_confident_match                 DM2
//   Matching_vid_pid_alone_is_low_confidence             DM3
//   Nothing_in_common_does_not_match                     DM4
//   Two_devices_without_a_device_path_do_not_match       DM5
//   Two_devices_without_a_serial_do_not_match            DM6
//   Built_in_keyboard_without_vid_pid_never_matches      DM7
//   Blank_fields_count_as_absent                         DM8
//   Device_paths_compare_case_insensitively              DM9
//   Contradicting_vid_defeats_a_matching_serial          DM10
//   Two_devices_of_one_model_are_ambiguous               DM11
//   A_more_specific_match_wins_without_ambiguity         DM12
//   Empty_candidate_list_finds_nothing                   DM13
//   Automatic_reconnection_requires_confidence_and_clarity DM14
//   Null_arguments_are_rejected                          DM15
// =============================================================================

using ScannerHelper.Core.Domain;
using ScannerHelper.Core.Input;
using ScannerHelper.Core.Settings;

namespace ScannerHelper.Core.Tests.Input;

public class DeviceIdentityMatcherTests
{
    /// <summary>
    /// 中文：试点用的那把扫码枪，取自 Task 4a 的实测记录。
    ///       用真实观测到的路径而不是编造的字符串，测试才贴近现场。
    /// English: The pilot's scanner, taken from Task 4a's measurements. Using a genuinely
    ///          observed path rather than an invented string keeps the tests close to the field.
    /// </summary>
    private const string ScannerPath =
        @"\\?\HID#VID_0581&PID_0115&MI_00#7&242c9e3&0&0000#{884b96c3-56ef-11d1-bc8c-00a0c91405dd}";

    /// <summary>
    /// 中文：笔记本内置键盘，同样取自 Task 4a 的实测记录。
    ///       ★ 注意它走 ACPI，**既没有 VID 也没有 PID**（规格假设 A4）。
    /// English: The laptop's built-in keyboard, also from Task 4a. Note that it arrives over
    ///          ACPI with neither a VID nor a PID (spec assumption A4).
    /// </summary>
    private const string BuiltInKeyboardPath =
        @"\\?\ACPI#MSFT0001#4&b6e66aa&0#{884b96c3-56ef-11d1-bc8c-00a0c91405dd}";

    private static ScannerDeviceIdentity Scanner(
        string? path = ScannerPath,
        string? vendorId = "0581",
        string? productId = "0115",
        string? serialNumber = null,
        string? friendlyName = "HID Keyboard Device")
        => new(path, vendorId, productId, friendlyName, serialNumber);

    private static ScannerBindingSettings Binding(
        string? path = ScannerPath,
        string? vendorId = "0581",
        string? productId = "0115",
        string? serialNumber = null)
        => new()
        {
            DevicePath = path,
            VendorId = vendorId,
            ProductId = productId,
            SerialNumber = serialNumber,
            FriendlyName = "HID Keyboard Device",
        };

    /// <summary>
    /// 中文：DM1 —— 设备路径完全一致是最高置信度（规格 §6：最具体的身份）。
    /// English: DM1 — an identical device path is the highest confidence (spec §6's most
    ///          specific identity).
    /// </summary>
    [Fact]
    public void Identical_device_path_is_an_exact_match()
    {
        Assert.Equal(
            DeviceMatchConfidence.Exact,
            DeviceIdentityMatcher.Match(Scanner(), Binding()));
    }

    /// <summary>
    /// 中文：
    ///   DM2 —— 设备路径变了但序列号相同，仍然足以确信（规格 §6 的次优身份）。
    ///
    ///   这正是设备路径不够用的那种情形：路径里含拓扑位置信息，换一个 USB 口
    ///   就可能变。Task 4a 第 6 节把"路径跨会话是否稳定"列为待验项，而这条
    ///   用例说明了退路存在的意义——就算路径变了，序列号还能认出同一台设备。
    /// English:
    ///   DM2 — a changed device path with a matching serial is still confident (spec §6's
    ///   second-choice identity).
    ///
    ///   This is precisely where the device path falls short: it encodes topology and can change
    ///   when the device moves to another USB port. Task 4a §6 lists path stability across
    ///   sessions as an open question, and this case shows why the fallback matters — the serial
    ///   still recognizes the same device when the path has moved.
    /// </summary>
    [Fact]
    public void Matching_serial_is_a_confident_match()
    {
        var confidence = DeviceIdentityMatcher.Match(
            Scanner(path: @"\\?\HID#VID_0581&PID_0115&MI_00#8&1a2b3c4&0&0000#{884b96c3}",
                serialNumber: "S1234567"),
            Binding(serialNumber: "S1234567"));

        Assert.Equal(DeviceMatchConfidence.High, confidence);
    }

    /// <summary>
    /// 中文：
    ///   DM3 —— 只有 VID/PID 相同是**低**置信度。
    ///
    ///   规格 §6 明说仅凭 VID/PID 不保证唯一：两把同型号的扫码枪，这两项
    ///   完全一样。所以它只够用来提示"看起来是同型号的设备"，不够用来
    ///   替工人决定绑定哪一台。
    /// English:
    ///   DM3 — VID/PID alone is low confidence. Spec §6 states it is not guaranteed unique: two
    ///   scanners of one model share both exactly. It suffices to suggest "this looks like the
    ///   same model" and not to decide which device to bind.
    /// </summary>
    [Fact]
    public void Matching_vid_pid_alone_is_low_confidence()
    {
        var confidence = DeviceIdentityMatcher.Match(
            Scanner(path: @"\\?\HID#VID_0581&PID_0115&MI_00#9&different&0&0000#{884b96c3}"),
            Binding());

        Assert.Equal(DeviceMatchConfidence.Low, confidence);
    }

    /// <summary>
    /// 中文：DM4 —— 毫无共同点时不匹配。
    /// English: DM4 — nothing in common does not match.
    /// </summary>
    [Fact]
    public void Nothing_in_common_does_not_match()
    {
        var confidence = DeviceIdentityMatcher.Match(
            Scanner(path: @"\\?\HID#VID_1234&PID_5678#a&b&0&0000#{884b96c3}",
                vendorId: "1234", productId: "5678"),
            Binding());

        Assert.Equal(DeviceMatchConfidence.None, confidence);
    }

    /// <summary>
    /// 中文：
    ///   DM5 —— 两边都没有设备路径时，**不算**路径匹配。
    ///
    ///   ★ 这是本文件要守的核心错误：`a == b` 在两边都是 null 时返回 true。
    ///     C# 的语义没错，但它回答的是另一个问题——"两台设备都没有路径"
    ///     不等于"两台设备的路径相同"。
    ///
    ///   本条把 VID/PID 也设成不同，确保结论只可能来自路径这一项：
    ///   若判成了 Exact，那一定是两个 null 被当成了相等。
    /// English:
    ///   DM5 — two devices with no device path do not match on the path.
    ///
    ///   The core error this file guards: `a == b` returns true for two nulls. The C# semantics
    ///   are right and answer a different question — "neither device has a path" is not "the two
    ///   devices have the same path".
    ///
    ///   VID/PID are made to differ as well, so the verdict can only come from the path: an
    ///   Exact result would mean two nulls were treated as equal.
    /// </summary>
    [Fact]
    public void Two_devices_without_a_device_path_do_not_match()
    {
        var confidence = DeviceIdentityMatcher.Match(
            Scanner(path: null, vendorId: "1111", productId: "2222"),
            Binding(path: null));

        Assert.True(confidence == DeviceMatchConfidence.None,
            "两边都没有设备路径不能算作路径匹配。null == null 在 C# 里是 true，"
            + "但那说的是「两台设备都没有路径」，不是「两台设备的路径相同」。"
            + $" 实际判定为 {confidence}。");
    }

    /// <summary>
    /// 中文：DM6 —— 两边都没有序列号时，不算序列号匹配。理由同 DM5。
    ///       并非所有扫码枪都向 Windows 暴露序列号，所以"两边都没有"是**常态**，
    ///       而不是边界情况——这条路径每次重连都会走到。
    /// English: DM6 — two devices with no serial do not match on the serial, for DM5's reason.
    ///          Not every scanner exposes one, so "both absent" is the norm rather than an edge
    ///          case: this path is taken on every reconnect.
    /// </summary>
    [Fact]
    public void Two_devices_without_a_serial_do_not_match()
    {
        var confidence = DeviceIdentityMatcher.Match(
            Scanner(path: @"\\?\HID#VID_9999&PID_8888#x&y&0&0000#{884b96c3}",
                vendorId: "9999", productId: "8888", serialNumber: null),
            Binding(serialNumber: null));

        Assert.Equal(DeviceMatchConfidence.None, confidence);
    }

    /// <summary>
    /// 中文：
    ///   DM7 —— 笔记本内置键盘绝不能与缺 VID/PID 的绑定记录匹配上。
    ///
    ///   ★★ 这条用例原计划里没有，是 Task 4a 实测倒逼出来的。
    ///
    ///     实测记录（docs/TASK_4A_MEASUREMENTS_*.md 第 5 节）：
    ///         HID Keyboard Device     VID_0581 PID_0115   \\?\HID#VID_0581...
    ///         Standard PS/2 Keyboard  —        —          \\?\ACPI#MSFT0001...
    ///
    ///     内置键盘走 ACPI，**既没有 VID 也没有 PID**（规格假设 A4）。原计划
    ///     里的匹配用例默认每台设备都有 VID/PID，因为那是 USB 设备的常识——
    ///     而工位是笔记本这件事，是后来才确认的。
    ///
    ///   ★ 若"两边都没有"算作匹配，会发生什么：
    ///
    ///     一份缺 VID/PID 的绑定记录（例如上一次绑定时设备没报，或者配置被
    ///     手工编辑过）会与内置键盘匹配上。于是内置键盘被当成扫码枪，工人
    ///     打的每一个字都被吞进扫描缓冲区——**键盘彻底失灵**。
    ///
    ///     而那正是规格 §5.7 里 PAUSED 存在的理由所描述的灾难场景。一个匹配
    ///     bug 就足以制造它。规格假设 A4 还补了一刀：笔记本没有备用键盘可插，
    ///     唯一的出路是用触摸板点暂停按钮。
    /// English:
    ///   DM7 — the laptop's built-in keyboard must never match a binding record missing VID/PID.
    ///
    ///   This case was not in the original plan and was forced by Task 4a's measurements, which
    ///   recorded the scanner as VID_0581/PID_0115 and the built-in keyboard as ACPI#MSFT0001
    ///   with neither (spec assumption A4). The plan's matching cases assumed every device has a
    ///   VID/PID, that being common knowledge for USB devices — and the workstation being a
    ///   laptop was only confirmed later.
    ///
    ///   If "both absent" counted as a match, a binding record missing VID/PID — because the
    ///   device did not report them when bound, or because the configuration was hand-edited —
    ///   would match the built-in keyboard. The keyboard would be taken for the scanner and every
    ///   character the operator types swallowed into the scan buffer, leaving it entirely dead.
    ///
    ///   Which is the disaster scenario spec §5.7 gives as the reason PAUSED exists; a single
    ///   matching bug is enough to produce it. Assumption A4 twists the knife: a laptop has no
    ///   spare keyboard, and the only way out is the touchpad and the pause button.
    /// </summary>
    [Fact]
    public void Built_in_keyboard_without_vid_pid_never_matches()
    {
        var builtInKeyboard = new ScannerDeviceIdentity(
            DevicePath: BuiltInKeyboardPath,
            VendorId: null,
            ProductId: null,
            FriendlyName: "Standard PS/2 Keyboard",
            SerialNumber: null);

        var bindingMissingVidPid = new ScannerBindingSettings
        {
            DevicePath = ScannerPath,
            VendorId = null,
            ProductId = null,
            SerialNumber = null,
            FriendlyName = "HID Keyboard Device",
        };

        var confidence = DeviceIdentityMatcher.Match(builtInKeyboard, bindingMissingVidPid);

        Assert.True(confidence == DeviceMatchConfidence.None,
            "笔记本内置键盘走 ACPI，没有 VID/PID（规格假设 A4）。若「两边都没有」"
            + "算作匹配，它会被当成扫码枪，工人打的每一个字都被吞进扫描缓冲区，"
            + "键盘彻底失灵——而笔记本没有备用键盘可插。"
            + $" 实际判定为 {confidence}。");
    }

    /// <summary>
    /// 中文：DM8 —— 空串与纯空格按"没有值"处理，与项目其他地方一致
    ///       （见 SkuValidatorFactory、HotkeyConflictDetector）。
    ///       设备真报了一个空字符串，那和没报没有区别，不该被当成一个可以
    ///       拿来匹配的身份。
    /// English: DM8 — empty and whitespace-only values count as absent, consistently with the
    ///          rest of the project (SkuValidatorFactory, HotkeyConflictDetector). A device that
    ///          reported an empty string reported nothing useful, and that is not an identity
    ///          worth matching on.
    /// </summary>
    [Fact]
    public void Blank_fields_count_as_absent()
    {
        var confidence = DeviceIdentityMatcher.Match(
            Scanner(path: "   ", vendorId: "", productId: "  ", serialNumber: ""),
            Binding(path: "", vendorId: "  ", productId: "", serialNumber: "   "));

        Assert.Equal(DeviceMatchConfidence.None, confidence);
    }

    /// <summary>
    /// 中文：
    ///   DM9 —— 设备路径的比较忽略大小写。
    ///
    ///   Windows 的设备路径本身大小写不敏感，同一台设备在不同 API、不同
    ///   Windows 版本上返回的大小写可能不同。区分大小写会让同一台设备重连时
    ///   匹配不上，现场表现是"每次插上都要求重新绑定"——而工人根本不会把
    ///   这件事和大小写联系起来。
    /// English:
    ///   DM9 — device paths compare case-insensitively.
    ///
    ///   Windows device paths are themselves case-insensitive and the same device can come back
    ///   differently cased from different APIs or Windows versions. Comparing case-sensitively
    ///   would fail to match it on reconnect, presenting as "it asks me to rebind every time I
    ///   plug it in" — and no operator would ever connect that to letter case.
    /// </summary>
    [Fact]
    public void Device_paths_compare_case_insensitively()
    {
        Assert.Equal(
            DeviceMatchConfidence.Exact,
            DeviceIdentityMatcher.Match(
                Scanner(path: ScannerPath.ToUpperInvariant()),
                Binding(path: ScannerPath.ToLowerInvariant())));
    }

    /// <summary>
    /// 中文：
    ///   DM10 —— VID 互相矛盾时，序列号相同也不算匹配。
    ///
    ///   ★ 两边都报了 VID 而值不同，说明是两台不同的设备。此时序列号碰巧
    ///     相同不能推翻这个结论：序列号只在厂商范围内保证唯一，遇到 "1"、
    ///     "0000" 这种偷懒的序列号，跨厂商撞车完全可能。
    ///
    ///     先排矛盾再看序列号，比反过来安全：宁可要求工人重新绑定一次，
    ///     也不要把一台别的设备认成扫码枪。
    /// English:
    ///   DM10 — a contradicting VID defeats a matching serial.
    ///
    ///   Both sides reporting a VID with different values means two different devices, and a
    ///   coincidentally equal serial cannot overturn that: serials are unique only within a
    ///   vendor, and lazy ones such as "1" or "0000" collide across vendors readily.
    ///
    ///   Excluding contradictions before consulting the serial is the safer order — better to
    ///   ask the operator to rebind than to mistake some other device for the scanner.
    /// </summary>
    [Fact]
    public void Contradicting_vid_defeats_a_matching_serial()
    {
        var confidence = DeviceIdentityMatcher.Match(
            Scanner(path: @"\\?\HID#VID_9999&PID_0115#a&b&0&0000#{884b96c3}",
                vendorId: "9999", serialNumber: "1"),
            Binding(serialNumber: "1"));

        Assert.True(confidence == DeviceMatchConfidence.None,
            "两边报的 VID 不同就说明是两台不同的设备，序列号碰巧相同不能推翻它。"
            + "序列号只在厂商范围内保证唯一，「1」这种偷懒的序列号跨厂商撞车"
            + "完全可能。");
    }

    /// <summary>
    /// 中文：
    ///   DM11 —— 两台同型号设备并列时报告有歧义，且**不得**自动重连。
    ///
    ///   ★ 规格 §6 点名了这个场景："不要强迫用户从多个一模一样的
    ///     『HID Keyboard Device』条目里挑。"
    ///
    ///     仓库里若有两把同型号的扫码枪，它们在 VID/PID 这一档上完全无法
    ///     区分。此时自动挑一台绑定，工人拿错了枪也不会有任何提示——他会
    ///     一直扫，而数据来自一台没被绑定的设备。正确的做法是让他扫一枪来
    ///     指认（规格 §6 的重新绑定流程）。
    ///
    ///   ★ 有歧义时仍然返回一台设备而不是 null：界面需要拿它来告诉工人
    ///     "找到了 2 台看起来一样的设备"。真正阻止自动绑定的是
    ///     CanReconnectAutomatically，而不是把设备藏起来。
    /// English:
    ///   DM11 — two devices of one model are reported as ambiguous and must not auto-reconnect.
    ///
    ///   Spec §6 names the scenario: do not force users to pick from opaque entries such as
    ///   multiple identical HID Keyboard Device names. Two scanners of one model in the warehouse
    ///   are indistinguishable at the VID/PID level, and auto-binding one would give an operator
    ///   holding the wrong gun no warning — they would keep scanning while the data came from an
    ///   unbound device. The right answer is to have them identify it by scanning (spec §6's
    ///   rebinding flow).
    ///
    ///   An ambiguous result still returns a device rather than null, because the UI needs it to
    ///   say that two apparently identical devices were found. What prevents automatic binding is
    ///   CanReconnectAutomatically, not hiding the device.
    /// </summary>
    [Fact]
    public void Two_devices_of_one_model_are_ambiguous()
    {
        var firstGun = Scanner(path: @"\\?\HID#VID_0581&PID_0115&MI_00#7&aaaa&0&0000#{884b96c3}");
        var secondGun = Scanner(path: @"\\?\HID#VID_0581&PID_0115&MI_00#7&bbbb&0&0000#{884b96c3}");

        var result = DeviceIdentityMatcher.FindBestMatch([firstGun, secondGun], Binding());

        Assert.Equal(DeviceMatchConfidence.Low, result.Confidence);
        Assert.True(result.IsAmbiguous,
            "两把同型号的扫码枪在 VID/PID 这一档上无法区分，必须报告有歧义。");
        Assert.NotNull(result.Device);

        Assert.True(!result.CanReconnectAutomatically,
            "有歧义时挑哪一台都是猜的，绝不能自动绑定。工人拿错了枪也不会有"
            + "任何提示——他会一直扫，而数据来自一台没被绑定的设备（规格 §6）。");
    }

    /// <summary>
    /// 中文：DM12 —— 一台精确匹配、一台仅型号相同时，取精确的那台，且没有歧义。
    ///       歧义只在**同一置信度**上并列才成立；不同档次之间有明确的优先级
    ///       （规格 §6：优先采用最具体的稳定身份）。
    /// English: DM12 — with one exact match and one model-only match, the exact one wins and
    ///          there is no ambiguity. A tie requires the same confidence; across levels there is
    ///          a clear priority (spec §6: favor the most specific stable identity).
    /// </summary>
    [Fact]
    public void A_more_specific_match_wins_without_ambiguity()
    {
        var exactGun = Scanner();
        var sameModelGun = Scanner(
            path: @"\\?\HID#VID_0581&PID_0115&MI_00#7&other&0&0000#{884b96c3}");

        var result = DeviceIdentityMatcher.FindBestMatch([sameModelGun, exactGun], Binding());

        Assert.Equal(DeviceMatchConfidence.Exact, result.Confidence);
        Assert.Equal(exactGun, result.Device);
        Assert.False(result.IsAmbiguous);
        Assert.True(result.CanReconnectAutomatically);
    }

    /// <summary>
    /// 中文：DM13 —— 候选为空时返回"没有匹配"，不抛异常。
    ///       扫码枪被拔掉时枚举出来就是空的，这是正常情形而非错误
    ///       （规格 §6 要求处理断开与重连）。
    /// English: DM13 — an empty candidate list finds nothing and does not throw. Enumeration
    ///          returns nothing while the scanner is unplugged, which is a normal situation
    ///          rather than an error (spec §6 requires handling disconnect and reconnect).
    /// </summary>
    [Fact]
    public void Empty_candidate_list_finds_nothing()
    {
        var result = DeviceIdentityMatcher.FindBestMatch([], Binding());

        Assert.Null(result.Device);
        Assert.Equal(DeviceMatchConfidence.None, result.Confidence);
        Assert.False(result.IsAmbiguous);
        Assert.False(result.CanReconnectAutomatically);
    }

    /// <summary>
    /// 中文：
    ///   DM14 —— 自动重连要求"确信"且"无歧义"，两个条件缺一不可（规格 §6）。
    ///
    ///   逐档检查一遍，是因为这个判定是**唯一**阻止自动绑错设备的地方。
    ///   把门槛降到 Low，两把同型号的枪就会互相顶替；忘了检查歧义，
    ///   并列的两台会随便挑一台。两种情况的现场表现一模一样：工人拿着
    ///   一把没绑定的枪一直扫，而界面显示一切正常。
    /// English:
    ///   DM14 — automatic reconnection requires both confidence and clarity (spec §6).
    ///
    ///   Every level is checked because this predicate is the only thing preventing an automatic
    ///   bind to the wrong device. Lowering the bar to Low would let two guns of one model stand
    ///   in for each other; forgetting the ambiguity check would pick arbitrarily between two
    ///   tied devices. Both look identical on site: the operator scans away with an unbound gun
    ///   while the UI reports everything as normal.
    /// </summary>
    [Fact]
    public void Automatic_reconnection_requires_confidence_and_clarity()
    {
        var exact = new DeviceMatchResult(Scanner(), DeviceMatchConfidence.Exact, false);
        var high = new DeviceMatchResult(Scanner(), DeviceMatchConfidence.High, false);
        var low = new DeviceMatchResult(Scanner(), DeviceMatchConfidence.Low, false);
        var ambiguousExact = new DeviceMatchResult(Scanner(), DeviceMatchConfidence.Exact, true);

        Assert.True(exact.CanReconnectAutomatically);
        Assert.True(high.CanReconnectAutomatically);

        Assert.True(!low.CanReconnectAutomatically,
            "仅凭 VID/PID 只能确认型号，两把同型号的枪在这一档完全无法区分，"
            + "不足以自动重连（规格 §6）。");

        Assert.True(!ambiguousExact.CanReconnectAutomatically,
            "有歧义时挑哪一台都是猜的，无论置信度多高都不能自动绑定。");

        Assert.False(DeviceMatchResult.NoMatch.CanReconnectAutomatically);
    }

    /// <summary>
    /// 中文：DM15 —— 参数为 null 时抛 ArgumentNullException（决策 D-1）。
    ///       与 Core 中其他公开入口对 null 的处理保持一致：null 只可能是
    ///       调用方缺陷，不是预期的坏输入。
    /// English: DM15 — null arguments throw (decision D-1), consistently with every other public
    ///          entry point in Core: null can only be a caller defect, not expected bad input.
    /// </summary>
    [Fact]
    public void Null_arguments_are_rejected()
    {
        Assert.Throws<ArgumentNullException>(
            () => DeviceIdentityMatcher.Match(null!, Binding()));

        Assert.Throws<ArgumentNullException>(
            () => DeviceIdentityMatcher.Match(Scanner(), null!));

        Assert.Throws<ArgumentNullException>(
            () => DeviceIdentityMatcher.FindBestMatch(null!, Binding()));

        Assert.Throws<ArgumentNullException>(
            () => DeviceIdentityMatcher.FindBestMatch([Scanner()], null!));
    }
}
