// =============================================================================
// ScanPipelineState.cs
//
// 中文：
//   输入流水线的状态。
//
//   ★ 这个枚举比规格 §5.1 少了两个状态，而少掉的那两个正是架构变更的度量。
//
//     IDENTIFYING（"有按键被扣留，正在等 Raw Input 揭示来源"）没有了：串口来的
//     数据来源是确定的，没有什么要识别。它之所以曾经存在，是因为扫码枪伪装成
//     键盘，而我们必须先扣留、再判断——那件事在 2026-09-10 被实测证明做不到
//     （TASK_4B_FINDING_20260910.md）。
//
//     SCANNING（"正在逐个字符地收集一枪"）也没有了：串口的终止符给出了帧边界，
//     一次扫描到达时就已经是完整的。旧架构里那个状态要配合超时、最大长度、
//     字符累积一起工作，因为它随时可能被打断。
//
//     剩下的四个状态是这个产品真正的业务：闲着、正在处理、等工人决定、暂停。
//     见 ARCHITECTURE_CHANGE_SERIAL.md §4。
//
// English:
//   The input pipeline's state.
//
//   This enum has two fewer states than spec §5.1, and the two that went are the measure of the
//   architecture change. IDENTIFYING — a keystroke withheld while Raw Input reveals its source —
//   is gone because data arriving on a serial port has a known source; it existed only because the
//   scanner impersonated a keyboard and had to be withheld before it could be judged, which
//   measurement on 2026-09-10 proved impossible (TASK_4B_FINDING_20260910.md). SCANNING —
//   collecting a scan character by character — is gone because the serial terminator gives the
//   frame boundary and a scan is already complete when it arrives; the old state needed timeouts,
//   a maximum length and character accumulation because it could be interrupted at any moment.
//
//   The four that remain are this product's actual business: idle, processing, awaiting the
//   operator, paused. See ARCHITECTURE_CHANGE_SERIAL.md §4.
//
// 包含的类型 / Types in this file:
//   ScanPipelineState
// =============================================================================

namespace ScannerHelper.Core.Input;

/// <summary>
/// 中文：输入流水线的状态（规格 §5.1，经架构变更后简化）。
/// English: The input pipeline's state (spec §5.1, simplified by the architecture change).
/// </summary>
public enum ScanPipelineState
{
    /// <summary>中文：空闲，等待下一枪。 English: Idle, awaiting the next scan.</summary>
    Idle,

    /// <summary>
    /// 中文：
    ///   正在按当前模式处理（解析、校验）。
    ///
    ///   ★ 这个状态在现实中几乎瞬间就过去了——解析与校验都是纯计算，正则还带着
    ///     100 毫秒的有限超时。它之所以仍然存在，是因为规格 §5.1 明确列出了
    ///     PROCESSING：状态机的形状本身是规格的一部分，把一个"反正很快"的阶段
    ///     省掉，会让代码与规格对不上，日后读规格的人找不到它对应哪一段。
    /// English:
    ///   Processing under the current mode: parsing and validation.
    ///
    ///   In practice this passes almost instantly — both steps are pure computation and the regex
    ///   carries a finite 100 ms timeout. It exists because spec §5.1 lists PROCESSING explicitly:
    ///   the shape of the state machine is part of the specification, and omitting a stage on the
    ///   grounds that it is fast leaves code and spec out of correspondence, so a later reader
    ///   cannot find which code answers to which paragraph.
    /// </summary>
    Processing,

    /// <summary>
    /// 中文：有一个错误在等工人决定（强制发送或取消，规格 §10）。
    /// English: An error awaits the operator's decision — Force Send or Cancel (spec §10).
    /// </summary>
    PendingError,

    /// <summary>
    /// 中文：
    ///   暂停。解析与校验被绕过，扫码枪的内容原样发出（决策 D-26、规格 §5.7）。
    ///
    ///   ★ 注意它**不是**"什么都不做"。旧架构下暂停意味着钩子放行一切、流水线
    ///     完全旁路；串口模式下键盘从未被碰过，那个意思不存在了，于是暂停变成
    ///     "跳过规则、照样干活"。界面必须一直显示它开着——工人看不见的话，会有人
    ///     在暂停下干一整天，把原始码当成 SKU 录进仓库系统。
    /// English:
    ///   Paused: parsing and validation are bypassed and the scanner's content is emitted
    ///   unchanged (decision D-26, spec §5.7).
    ///
    ///   Note that this is not "do nothing". Under the old architecture pausing meant the hook
    ///   passed everything through and the pipeline was bypassed entirely; with the keyboard never
    ///   touched that meaning is gone, and pausing became "skip the rules and keep working". The UI
    ///   must keep showing that it is on: unseen, it lets somebody work a whole shift in it, filing
    ///   raw codes into the warehouse system as if they were SKUs.
    /// </summary>
    Paused,
}
