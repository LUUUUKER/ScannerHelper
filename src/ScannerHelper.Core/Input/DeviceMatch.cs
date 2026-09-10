// =============================================================================
// DeviceMatch.cs
//
// 中文：
//   设备匹配的置信度，以及在一组候选设备中挑选结果的形状（规格 §6）。
//
//   ★ 为什么匹配结果是一个**置信度**，而不是一个布尔。
//
//     规格 §6 写得很明确：「匹配应当优先采用最具体的稳定身份。仅凭 VID/PID
//     不保证唯一。」——两把同型号的扫码枪，VID 和 PID 完全一样。
//
//     若匹配只回答"是/否"，那么"设备路径完全一致"和"型号碰巧相同"就被
//     压成了同一个答案。而规格 §6 又要求"重连时在**确信匹配**的情况下自动
//     恢复"——没有置信度，就无从判断哪一种情况配得上"确信"两个字。
//
//     后果是具体的：仓库里若有两把同型号的扫码枪，插上任意一把都会被自动
//     绑定，工人拿错了枪也毫无提示。而规格 §6 恰恰点名了这个场景
//     （"多个一模一样的 HID Keyboard Device"）。
//
//   ★ 为什么需要"有歧义"这个标志，而不是让调用方自己去数。
//
//     两台设备在**同一个置信度**上都匹配得上，这件事本身就是一个结论：
//     此时无论挑哪一个都是猜的。把它做成结果的一部分，调用方就不可能
//     "忘了检查"——而忘了检查的表现是自动绑错设备，且悄无声息。
//
// English:
//   How confident a device match is, plus the shape of a search across candidates (spec §6).
//
//   Why the result is a confidence rather than a boolean: spec §6 states that matching should
//   favor the most specific stable identity and that VID/PID alone is not guaranteed unique —
//   two scanners of the same model share both exactly.
//
//   A yes/no answer would flatten "the device path is identical" and "the model happens to be
//   the same" into one verdict. Yet spec §6 also requires reconnecting automatically only when
//   *confidently* matched, and without a confidence there is no way to say which case earns
//   the word "confidently".
//
//   The consequence is concrete: with two scanners of one model in the warehouse, plugging in
//   either would auto-bind and an operator who picked up the wrong gun would get no warning —
//   and spec §6 names that exact scenario (several identical HID Keyboard Device entries).
//
//   Why ambiguity is a flag rather than something the caller counts for itself: two devices
//   matching at the same confidence is itself a conclusion, since whichever is chosen is a
//   guess. Making it part of the result means a caller cannot forget to check, and forgetting
//   shows up as silently binding the wrong device.
//
// 包含的类型 / Types in this file:
//   DeviceMatchConfidence  匹配的可靠程度
//   DeviceMatchResult      在一组候选中挑选的结果
// =============================================================================

using ScannerHelper.Core.Domain;

namespace ScannerHelper.Core.Input;

/// <summary>
/// 中文：一次设备匹配有多可靠。数值越大越可靠，可以直接比较大小。
/// English: How reliable a device match is. Higher is more reliable and the values compare
///          directly.
/// </summary>
public enum DeviceMatchConfidence
{
    /// <summary>
    /// 中文：不匹配。
    ///
    ///       ★ 两边**都没有**某个字段，绝不算匹配。null 等于 null 在 C# 里是
    ///         true，但在身份匹配里是彻头彻尾的错误：那说的是"两台设备都没有
    ///         序列号"，而不是"两台设备的序列号相同"。
    ///
    ///         这一点在本项目里不是理论问题。Task 4a 实测发现笔记本内置键盘
    ///         走 ACPI，**根本没有 VID/PID**（规格假设 A4）。若"都没有"算作
    ///         匹配，内置键盘就会与任何一份缺字段的绑定记录匹配上，然后被
    ///         当成扫码枪——工人打的每一个字都会被吞进扫描缓冲区。
    /// English:
    ///   No match.
    ///
    ///   A field absent on *both* sides is never a match. null equals null in C#, but in
    ///   identity matching that is simply wrong: it says "neither device has a serial number",
    ///   not "the two devices have the same serial number".
    ///
    ///   This is not theoretical here. Task 4a found the laptop's built-in keyboard arriving
    ///   over ACPI with no VID/PID at all (spec assumption A4). If "both absent" counted as a
    ///   match, the built-in keyboard would match any binding record missing those fields and
    ///   be taken for the scanner, swallowing every character the operator types.
    /// </summary>
    None = 0,

    /// <summary>
    /// 中文：
    ///   仅 VID/PID 相同。**这只说明型号相同，不说明是同一台设备**（规格 §6）。
    ///
    ///   两把同型号的扫码枪走到这里是无法区分的，因此规格 §6 要求的"确信匹配
    ///   才自动重连"排除了这一档。它有用，但只够用来提示工人"看起来是同型号
    ///   的设备，要不要绑定它？"，不够用来替他做决定。
    /// English:
    ///   Only VID/PID agree, which identifies a *model* and not a device (spec §6).
    ///
    ///   Two scanners of one model are indistinguishable at this level, so spec §6's
    ///   "reconnect automatically when confidently matched" excludes it. It is useful enough to
    ///   ask the operator whether to bind an apparently identical model, and not enough to
    ///   decide for them.
    /// </summary>
    Low = 1,

    /// <summary>
    /// 中文：
    ///   序列号相同。序列号标识**个体**，因此足以确信（规格 §6 把它排在
    ///   设备路径之后、VID/PID 之前）。
    ///
    ///   但并非所有扫码枪都向 Windows 暴露序列号——这正是不能只靠它的原因，
    ///   也是 ScannerBindingSettings 要同时存好几项身份的原因。
    /// English:
    ///   Serial numbers agree. A serial identifies an individual device and is therefore
    ///   confident enough (spec §6 ranks it below the device path and above VID/PID).
    ///
    ///   Not every scanner exposes one to Windows, which is why it cannot be relied on alone
    ///   and why ScannerBindingSettings stores several identity fields at once.
    /// </summary>
    High = 2,

    /// <summary>
    /// 中文：
    ///   设备路径完全一致。这是最具体的身份（规格 §6）。
    ///
    ///   ★ 但它**未必跨会话稳定**——设备路径里含有拓扑位置信息，换一个 USB
    ///     口就可能变。Task 4a 第 6 节专门留了这个待验项：拔插与重启之后各
    ///     导出一份报告，逐字对比设备路径是否相同。
    ///
    ///     所以"最具体"和"最稳定"不是同一回事，这也正是要保留序列号与
    ///     VID/PID 作为退路的原因。
    /// English:
    ///   The device paths are identical, the most specific identity there is (spec §6).
    ///
    ///   It is not necessarily stable across sessions, though: a device path encodes topology
    ///   and can change when the device moves to another USB port. Task 4a §6 leaves this open
    ///   deliberately, to be settled by exporting a report before and after a replug and a
    ///   reboot and comparing paths character by character.
    ///
    ///   "Most specific" and "most stable" are therefore not the same thing, which is why the
    ///   serial number and VID/PID are kept as fallbacks.
    /// </summary>
    Exact = 3,
}

/// <summary>
/// 中文：在一组候选设备中挑选出的匹配结果。
/// English: The outcome of searching a set of candidate devices.
/// </summary>
/// <param name="Device">
/// 中文：最佳匹配的设备，没有任何匹配时为 null。
/// English: The best matching device, or null when nothing matched.
/// </param>
/// <param name="Confidence">
/// 中文：最佳匹配的置信度。
/// English: That match's confidence.
/// </param>
/// <param name="IsAmbiguous">
/// 中文：
///   是否有多台设备在**同一个置信度**上都匹配得上。
///
///   ★ 为 true 时绝不能自动绑定——此时挑哪一台都是猜的。规格 §6 点名了这个
///     场景："不要强迫用户从多个一模一样的 HID Keyboard Device 条目里挑。"
///     既然人都分不清，程序更不该替他随便选一个。
/// English:
///   Whether several devices matched at the same confidence.
///
///   Never auto-bind when true: any choice would be a guess. Spec §6 names the scenario — do
///   not force users to pick from opaque entries such as multiple identical HID Keyboard
///   Device names. If a person cannot tell them apart, the program has no business picking one
///   on their behalf.
/// </param>
public readonly record struct DeviceMatchResult(
    ScannerDeviceIdentity? Device,
    DeviceMatchConfidence Confidence,
    bool IsAmbiguous)
{
    /// <summary>
    /// 中文：
    ///   是否可以**自动**重连到这台设备（规格 §6："在确信匹配的情况下自动重连"）。
    ///
    ///   两个条件缺一不可：
    ///     置信度至少为 High —— 仅凭 VID/PID 只能确认型号，两把同型号的枪
    ///                          在那一档完全无法区分。
    ///     没有歧义         —— 多台设备并列时挑哪一台都是猜的。
    ///
    ///   不满足时并不是"失败"，而是需要人来确认。规格 §6 的重新绑定流程本来
    ///   就是让工人扫一枪来指认设备——那条路一直在，自动重连只是省掉它的一个
    ///   优化。宁可让工人多扫一枪，也不要悄悄绑错设备：绑错的后果是他打的
    ///   每一个字都被吞进扫描缓冲区，而界面显示一切正常。
    /// English:
    ///   Whether this device may be reconnected to automatically (spec §6: "reconnect
    ///   automatically when confidently matched").
    ///
    ///   Both conditions are required. The confidence must be at least High, since VID/PID
    ///   alone confirms only the model and two guns of one model are indistinguishable there;
    ///   and there must be no ambiguity, since a tie makes any choice a guess.
    ///
    ///   Falling short is not a failure but a request for human confirmation. Spec §6's
    ///   rebinding flow already asks the operator to identify the device by scanning once, and
    ///   that path never goes away — automatic reconnection is only an optimization that skips
    ///   it. Better one extra scan than silently binding the wrong device, whose consequence is
    ///   that every character the operator types is swallowed into the scan buffer while the UI
    ///   reports everything as normal.
    /// </summary>
    public bool CanReconnectAutomatically
        => Confidence >= DeviceMatchConfidence.High && !IsAmbiguous;

    /// <summary>
    /// 中文：一个"什么都没匹配上"的结果。
    /// English: A result meaning nothing matched.
    /// </summary>
    public static DeviceMatchResult NoMatch { get; }
        = new(Device: null, DeviceMatchConfidence.None, IsAmbiguous: false);
}
