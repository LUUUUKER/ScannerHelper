// =============================================================================
// ScannerDeviceIdentity.cs
//
// 中文：
//   一台输入设备的身份（规格 §6）。
//
//   这是**观测到的**身份：Raw Input 在某次输入事件里报告了这台设备是谁。
//   与之相对，ScannerBindingSettings 是**记录下来的**身份，即上次绑定时存进
//   配置文件的那一份。DeviceIdentityMatcher 的职责就是判断"眼前这台"与
//   "记下来那台"是不是同一台，以及有多大把握。
//
//   ★ 为什么不复用 ScannerBindingSettings 一个类型就够了：
//
//     两者的生命周期和约束完全不同。配置类型必须可变、要有无参构造，
//     因为 System.Text.Json 要往里填字段；而观测身份是一次输入事件的事实，
//     产生之后不该再被任何人改动——一个可变的身份对象意味着关联逻辑跑到
//     一半时它可能已经变了。做成不可变 record 还顺带得到值相等语义，
//     匹配逻辑写起来直接得多。
//
//     把持久化形状和领域形状分开，也意味着将来配置格式迁移不会波及匹配逻辑。
//
//   全部字段可空，因为 Windows 暴露多少完全取决于设备：不是每把扫码枪都
//   报序列号，也不是每台机器上的设备路径都在重新插拔后保持不变。字段"存在
//   与否"本身就是匹配时的重要信息，所以不能用空串顶替 null——那会把
//   "设备没报这一项"和"设备报了一个空值"混为一谈。
//
// English:
//   The identity of one input device (spec §6).
//
//   This is the *observed* identity: who Raw Input said the device was on a given
//   input event. ScannerBindingSettings is the *recorded* identity — what was
//   written to the configuration file when the scanner was bound.
//   DeviceIdentityMatcher decides whether the device in front of us is the one on
//   record, and with what confidence.
//
//   Why not reuse ScannerBindingSettings for both: their lifetimes and constraints
//   differ entirely. A settings type must be mutable with a parameterless
//   constructor so System.Text.Json can fill it in, whereas an observed identity is
//   a fact about one input event and must not change afterwards — a mutable identity
//   could shift halfway through the correlation logic. An immutable record also
//   brings value equality, which makes matching considerably more direct to write.
//
//   Keeping the persisted shape separate from the domain shape also means a future
//   configuration migration cannot disturb the matching logic.
//
//   Every field is nullable because what Windows exposes depends entirely on the
//   device: not every scanner reports a serial number, and not every machine keeps a
//   device path stable across replug. Whether a field is present is itself
//   information the matcher uses, so an empty string must never stand in for null —
//   that would conflate "the device did not report this" with "the device reported a
//   blank value".
//
// 包含的类型 / Types in this file:
//   ScannerDeviceIdentity
// =============================================================================

namespace ScannerHelper.Core.Domain;

/// <summary>
/// 中文：一次输入事件中观测到的设备身份。不可变。
/// English: A device identity as observed on one input event. Immutable.
/// </summary>
/// <param name="DevicePath">
/// 中文：设备路径或实例标识。最具体的身份，匹配时优先使用。
/// English: Device path or instance identifier — the most specific identity and the
///          first choice when matching.
/// </param>
/// <param name="VendorId">
/// 中文：厂商 ID（VID）。与 <paramref name="ProductId"/> 合起来只标识**型号**，
///       不标识个体——两把同型号扫码枪的 VID/PID 完全相同（规格 §6）。
/// English: Vendor ID. With <paramref name="ProductId"/> it identifies a *model*,
///          never an individual device: two scanners of the same model share both
///          (spec §6).
/// </param>
/// <param name="ProductId">
/// 中文：产品 ID（PID）。
/// English: Product ID.
/// </param>
/// <param name="FriendlyName">
/// 中文：设备友好名，仅供在设置页展示给工人。**不参与匹配**——多把同型号
///       设备的友好名往往一模一样，正是规格 §6 提到的"多个完全相同的
///       HID Keyboard Device"那个问题。
/// English: A friendly name, shown to the operator in Settings. Never used for
///          matching: identical models usually share it, which is precisely the
///          "several identical HID Keyboard Device entries" problem spec §6 raises.
/// </param>
/// <param name="SerialNumber">
/// 中文：设备序列号，若设备暴露了它。能标识个体，可靠性高于 VID/PID，
///       但并非所有扫码枪都提供。
/// English: The serial number if the device exposes one. It identifies an individual
///          device and is more reliable than VID/PID, but not every scanner has one.
/// </param>
public sealed record ScannerDeviceIdentity(
    string? DevicePath,
    string? VendorId,
    string? ProductId,
    string? FriendlyName,
    string? SerialNumber);
