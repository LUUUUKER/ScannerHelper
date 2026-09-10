// =============================================================================
// KeyIdentity.cs
//
// 中文：
//   一次按键的身份，用于把两条输入通道上的事件对应起来（决策 D-11）。
//
//   ★★ 本类型里**没有虚拟键码**，这是 Task 4a 实测换来的，不是设计偏好。
//
//     测量日志（docs/TASK_4A_MEASUREMENTS_*.md 第 2 节）：
//
//       钩子报告            Raw Input 报告
//       0xA0 VK_LSHIFT      0x10 VK_SHIFT
//       0xA2 VK_LCONTROL    0x11 VK_CONTROL
//
//     同一次物理按键，钩子区分左右修饰键，Raw Input 只给通用码，而两者的
//     **扫描码始终一致**。按虚拟键码对应事件，结果是所有修饰键永远配不上——
//     诊断工具第一版就是这么写的，四百多个事件配不上对。
//
//     落到产品上，这个错误的表现是：**扫含大写字母的条码时识别不出来源**。
//     因为大写字母要按 Shift，而 Shift 配不上就意味着那一段的来源判定断了。
//     仓库条码几乎都含大写字母。
//
//     把身份做成一个**不包含虚拟键码的类型**，而不是仅仅在关联器里小心行事，
//     是为了让将来任何一次重写都无法重新引入这个错误——它在类型层面就不可表达。
//
//   ★ 扩展位不能省。
//
//     扫描码本身会重复：右 Ctrl 与左 Ctrl 的扫描码相同（0x1D），靠 E0 前缀
//     区分。少了这一位，左右修饰键会互相配错——而配错之后算出来的来源判定
//     是纯噪声，却和真数据长得一模一样。
//
//   ★ 按下与弹起必须分开。
//
//     一次按键产生按下与弹起两个事件，两条通道各看一遍。若身份不含方向，
//     某个键的弹起会跟另一次按下配成对，整条队列随即错位。
//
// English:
//   The identity of one keystroke, used to match events across the two input channels
//   (decision D-11).
//
//   This type contains no virtual key code, and that is a measured result rather than a
//   design preference. Task 4a recorded the hook reporting VK_LSHIFT (0xA0) and
//   VK_LCONTROL (0xA2) where Raw Input reported the generic VK_SHIFT (0x10) and
//   VK_CONTROL (0x11) for the same physical keystroke, while the scan codes always
//   agreed. Matching on virtual keys leaves every modifier permanently uncorrelated —
//   the harness's first version did exactly that and reported four hundred-odd unpaired
//   events.
//
//   In the product that fault presents as failing to identify the source of any barcode
//   containing a capital letter, since a capital requires Shift and an uncorrelated
//   Shift breaks the attribution for that stretch. Warehouse barcodes are almost
//   entirely capitals.
//
//   Making the identity a type that *cannot* hold a virtual key, rather than merely
//   being careful inside the correlator, means no future rewrite can reintroduce the
//   fault: it is not expressible.
//
//   The extended flag is required. Scan codes repeat — right Ctrl shares left Ctrl's
//   0x1D and only the E0 prefix separates them — and without it left and right modifiers
//   pair with each other, producing an attribution that is pure noise while looking
//   exactly like real data.
//
//   Direction must be part of the identity too. Each keystroke produces a down and an
//   up on both channels; without direction, one key's up pairs with another's down and
//   the whole queue shifts.
//
// 包含的类型 / Types in this file:
//   KeyIdentity
// =============================================================================

namespace ScannerHelper.Core.Domain;

/// <summary>
/// 中文：跨通道关联用的按键身份。**刻意不含虚拟键码**（决策 D-11）。
/// English: The key identity used for cross-channel correlation. Deliberately carries no
///          virtual key code (decision D-11).
/// </summary>
/// <param name="ScanCode">
/// 中文：硬件扫描码。两条通道对同一次按键报告的是同一个值——这是关联能成立的
///       全部依据。
/// English: The hardware scan code. Both channels report the same value for a given
///          keystroke, which is the entire basis on which correlation works.
/// </param>
/// <param name="IsExtended">
/// 中文：是否为扫描码带 E0 前缀的扩展键。用来区分扫描码相同的左右修饰键。
/// English: Whether the scan code carries an E0 prefix, separating left and right
///          modifiers that share a scan code.
/// </param>
/// <param name="IsKeyUp">
/// 中文：弹起为 true，按下为 false。
/// English: True for key up, false for key down.
/// </param>
public readonly record struct KeyIdentity(ushort ScanCode, bool IsExtended, bool IsKeyUp);
