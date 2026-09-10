// =============================================================================
// IScannerInputSource.cs
//
// 中文：
//   扫码枪输入的来源。Core 定义契约，平台层实现它（规格 §16，依赖向内）。
//
//   ★ 这个接口的形状本身就是架构变更的结论。
//
//     旧架构下，Core 必须处理按键级的事件：钩子事件、Raw Input 事件、两者的
//     关联、扣留与补发、超时。那是因为扫码枪伪装成键盘，而我们要从键盘流里
//     把它认出来——一件被 2026-09-10 的实测证明做不到的事
//     （TASK_4B_FINDING_20260910.md）。
//
//     串口模式下，一次扫描**天然就是一个完整的原始码**：串口的终止符给出了
//     边界，来源就是那个端口，不需要推断。所以这个接口只有一个事件、一个字符串。
//
//     整个 Core 因此少掉了：关联器、按键领域类型、扣留队列、超时推进、
//     以及它们的全部状态。这不是简化的结果，是问题变小了。
//
//   ★ 为什么仍然要有一个接口，而不是让 Core 直接用串口。
//
//     Core 目标框架是 net8.0、不引用任何东西、必须能在 macOS 上构建与单元测试
//     （规格 §16）。串口是平台的东西。而且这个接口让「一次扫描到达之后会发生
//     什么」可以完全脱离硬件测试——扫码枪拿不到的时候也能推进业务逻辑，
//     这一点在 Phase A 已经证明过价值。
//
// English:
//   The source of scanner input. Core defines the contract and the platform layer implements it
//   (spec §16; dependencies point inward).
//
//   The shape of this interface is itself the conclusion of the architecture change. Under the old
//   design Core had to handle keystroke-level events — hook events, Raw Input events, correlation
//   between them, withholding, replay, timeouts — because the scanner impersonated a keyboard and
//   had to be picked out of the keyboard stream, which measurement on 2026-09-10 proved impossible
//   (TASK_4B_FINDING_20260910.md).
//
//   On a serial port a scan is inherently one complete raw code: the terminator gives the boundary
//   and the source is the port, with nothing to infer. So this interface carries one event and one
//   string, and Core loses the correlator, the keystroke domain types, the pending queues, the
//   periodic advance and all their state. That is not simplification; the problem got smaller.
//
//   An interface still exists because Core targets net8.0, references nothing and must build and
//   unit-test on macOS (spec §16) — and because it lets "what happens once a scan arrives" be
//   tested with no hardware at all, which proved its worth throughout Phase A.
//
// 包含的类型 / Types in this file:
//   ScanReceivedEventArgs
//   IScannerInputSource
// =============================================================================

namespace ScannerHelper.Core.Input;

/// <summary>
/// 中文：收到了一次完整的扫描。
/// English: One complete scan arrived.
/// </summary>
public sealed class ScanReceivedEventArgs : EventArgs
{
    /// <summary>
    /// 中文：构造事件。
    /// English: Creates the event.
    /// </summary>
    /// <exception cref="ArgumentNullException">
    /// 中文：rawCode 为 null。 English: rawCode is null.
    /// </exception>
    public ScanReceivedEventArgs(string rawCode)
    {
        ArgumentNullException.ThrowIfNull(rawCode);
        RawCode = rawCode;
    }

    /// <summary>
    /// 中文：
    ///   扫码枪发来的原始内容，**不含终止符**。
    ///
    ///   来源负责去掉终止符：那是传输层的东西，不是条码的一部分。让它流进业务
    ///   逻辑，就会在「长度校验」这类地方莫名其妙地多出一个字符——而那种错
    ///   查起来极其费劲，因为它看不见。
    /// English:
    ///   What the scanner sent, with the terminator removed.
    ///
    ///   Stripping it is the source's job: a terminator belongs to the transport rather than to
    ///   the barcode, and letting it through adds an inexplicable extra character to things like
    ///   length validation — a bug that is painful to find precisely because it is invisible.
    /// </summary>
    public string RawCode { get; }
}

/// <summary>
/// 中文：扫码枪输入的来源。
/// English: The source of scanner input.
/// </summary>
public interface IScannerInputSource : IDisposable
{
    /// <summary>
    /// 中文：
    ///   收到一次完整的扫描。
    ///
    ///   ★ 实现方通常在**自己的读取线程**上触发它，订阅方必须自己处理线程切换。
    ///     接口不替订阅方决定这件事：界面需要切回界面线程，而单元测试和无界面的
    ///     处理逻辑都不需要，替它们统一切换只会平白多一层。
    /// English:
    ///   A complete scan arrived.
    ///
    ///   Implementations typically raise this on their own reader thread, and subscribers are
    ///   responsible for marshalling. The interface does not decide that for them: a UI must hop
    ///   to its own thread while unit tests and headless processing need no hop at all, and
    ///   marshalling on their behalf would only add a layer.
    /// </summary>
    event EventHandler<ScanReceivedEventArgs>? ScanReceived;

    /// <summary>
    /// 中文：
    ///   来源出了故障，例如设备被拔掉。
    ///
    ///   ★ 必须有人处理它。设备没了却不告诉工人，界面就会继续显示一个它没有
    ///     验证过的「正常」状态——规格 §19.1 禁止的正是这件事。
    /// English:
    ///   The source faulted, the device having been unplugged for instance.
    ///
    ///   Somebody must handle it. A vanished device that nobody is told about leaves the UI
    ///   presenting a "normal" state it has not verified, which spec §19.1 forbids.
    /// </summary>
    event EventHandler<Exception>? Faulted;

    /// <summary>
    /// 中文：是否已连接并正在接收。
    /// English: Whether it is connected and receiving.
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// 中文：
    ///   距离最近一次收到扫描过了多久；从未收到过则为 null。
    ///
    ///   ★ 这是新架构下的心跳（ARCHITECTURE_CHANGE_SERIAL.md §5.1）。
    ///     有人把枪切回键盘模式之后，这条链路会**安安静静地什么都不发生**：
    ///     端口开着、不报错、也没有数据，而扫码枪正直接往业务软件里打字。
    ///     「多久没动静了」是唯一能看见这件事的量。
    /// English:
    ///   How long since the last scan, or null if none has ever arrived.
    ///
    ///   This is the heartbeat under the new architecture (ARCHITECTURE_CHANGE_SERIAL.md §5.1).
    ///   Once someone switches the scanner back to keyboard mode this link goes quietly inert —
    ///   port open, no error, no data — while the scanner types raw barcodes into the business
    ///   application. How long it has been silent is the only quantity that shows it.
    /// </summary>
    TimeSpan? TimeSinceLastScan { get; }
}
