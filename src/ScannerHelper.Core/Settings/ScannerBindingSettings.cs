// =============================================================================
// ScannerBindingSettings.cs
//
// 中文：
//   已绑定扫码枪的身份信息（规格 §6）。
//
//   刻意保存多个身份字段，而不是只留一个"最可靠的"。原因是这些字段的可得性
//   因设备而异：不是每把扫码枪都会向 Windows 暴露序列号，也不是每台机器上
//   的设备路径都在重新插拔后保持不变。多存几项，重连时才能按可靠性从高到低
//   依次尝试匹配。
//
//   匹配的优先顺序由 DeviceIdentityMatcher 决定（Phase A 的纯逻辑），大致是：
//     设备路径 / 实例标识  最具体，优先
//     序列号               次之，若设备暴露了它
//     VID + PID            最后，且**不足以唯一确定**——两把同型号的扫码枪
//                          VID/PID 完全相同，仅凭这两项匹配可能绑错设备
//   规格 §6 明确要求"优先使用最具体的稳定身份，VID/PID 不保证唯一"。
//
// English:
//   Identity of the bound scanner (spec §6).
//
//   Several identity fields are stored deliberately rather than just "the most
//   reliable one", because availability varies by device: not every scanner exposes
//   a serial number to Windows, and not every machine keeps a device path stable
//   across replug. Storing several lets reconnection try them in order of
//   reliability.
//
//   The matching order is DeviceIdentityMatcher's decision (pure Phase A logic),
//   roughly: device path/instance first as the most specific, then serial number if
//   exposed, then VID+PID last — and VID+PID alone is *not* sufficient, since two
//   scanners of the same model share them and matching on those alone could bind
//   the wrong device. Spec §6 requires favoring the most specific stable identity
//   and notes VID/PID is not guaranteed unique.
//
// 包含的类型 / Types in this file:
//   ScannerBindingSettings
// =============================================================================

namespace ScannerHelper.Core.Settings;

/// <summary>
/// 中文：已绑定扫码枪的身份。字段按 Windows 实际暴露的内容填写，
///       未暴露的项为 null。
/// English: The bound scanner's identity. Fields are filled from whatever Windows
///          exposes; anything unavailable is null.
/// </summary>
public sealed class ScannerBindingSettings
{
    /// <summary>
    /// 中文：设备路径或实例标识。最具体的身份，重连匹配时优先使用。
    /// English: Device path or instance identifier — the most specific identity and
    ///          the first choice when matching on reconnect.
    /// </summary>
    public string? DevicePath { get; set; }

    /// <summary>
    /// 中文：厂商 ID（VID）。与 ProductId 合起来标识型号，**不标识个体**。
    /// English: Vendor ID. With ProductId it identifies a model, *not an
    ///          individual device*.
    /// </summary>
    public string? VendorId { get; set; }

    /// <summary>
    /// 中文：产品 ID（PID）。
    /// English: Product ID.
    /// </summary>
    public string? ProductId { get; set; }

    /// <summary>
    /// 中文：设备友好名，用于在设置页展示给工人看。仅供显示，不参与匹配——
    ///       多把同型号扫码枪的友好名往往完全相同（规格 §6 提到的
    ///       "多个一模一样的 HID Keyboard Device"）。
    /// English: A friendly name shown to the operator in Settings. Display only,
    ///          never used for matching — identical models usually share it, which
    ///          is the "several identical HID Keyboard Device entries" problem
    ///          spec §6 calls out.
    /// </summary>
    public string? FriendlyName { get; set; }

    /// <summary>
    /// 中文：设备序列号，若设备暴露了它。能唯一标识个体，匹配可靠性高于
    ///       VID/PID，但并非所有扫码枪都提供。
    /// English: The device serial number if exposed. It identifies an individual
    ///          device and is more reliable than VID/PID, but not every scanner
    ///          provides one.
    /// </summary>
    public string? SerialNumber { get; set; }
}
