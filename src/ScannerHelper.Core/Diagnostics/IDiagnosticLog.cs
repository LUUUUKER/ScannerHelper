// =============================================================================
// IDiagnosticLog.cs
//
// 中文：
//   诊断日志的契约（规格 §15）。Core 定义写什么，平台层决定写到哪儿。
//
//   ★ 日志是"现场出了事之后唯一还能问的人"。
//
//     这个程序装在仓库的笔记本上，出问题时开发者不在场，工人也说不清刚才发生了
//     什么——他只会说"扫码没反应了"。规格 §15 列出的那些字段，每一个都是为了
//     回答一句具体的追问：是哪一枪、当时什么模式、解析出了什么、为什么没过、
//     工人后来按了什么。
//
//   ★ 写日志**绝不能挡住正在干活的那条路**（规格 §15 最后一句）。
//
//     扫描是在串口读取线程上处理的，那条线程一停，下一枪就要排队。磁盘偶尔会卡
//     （杀毒软件扫描、机械盘、网络盘），而一次几十毫秒的写入卡顿会直接变成工人
//     手上的停顿。所以实现方必须是**异步**的：这个接口的每一个方法都必须立刻返回。
//
//     旧架构里这条要求更硬——那时处理跑在钩子回调上，卡一下就会被 Windows 摘掉
//     钩子。现在钩子没了，但"别挡住干活"这条理由本身没有变。
//
//   ★ 只有一个方法，参数是一个已经组装好的事件。
//
//     不做成 Info/Warn/Error 那种分级接口：这个日志不是给开发者看堆栈的，是给
//     现场排查用的**事件流水**。每一条都对应工人那边真实发生过的一件事，而不是
//     代码里的一次分支。
//
// English:
//   The diagnostic log's contract (spec §15). Core defines what is written; the platform layer
//   decides where.
//
//   The log is the only witness left once something goes wrong on site: the developer is not there,
//   and the operator can only say "scanning stopped working". Every field spec §15 lists exists to
//   answer one specific follow-up — which scan, which mode, what was parsed, why it failed, what
//   the operator pressed afterwards.
//
//   Logging must never block the path that is doing the work (spec §15's closing line). Scans are
//   processed on the serial reader thread, and stalling it queues the next scan behind it. Disks
//   stall occasionally — antivirus scans, spinning media, network shares — and a write that takes
//   tens of milliseconds becomes a pause in the operator's hands. Implementations must therefore be
//   asynchronous: every method here must return immediately.
//
//   The requirement was harder under the old architecture, where processing ran inside a hook
//   callback and one stall had Windows remove the hook. The hook is gone; the reason not to block
//   the work is not.
//
//   There is one method taking an assembled event rather than an Info/Warn/Error hierarchy: this
//   log is not a developer's stack trace but a field investigator's event stream, where each line
//   corresponds to something that really happened to the operator rather than to a branch in the
//   code.
//
// 包含的类型 / Types in this file:
//   IDiagnosticLog
// =============================================================================

namespace ScannerHelper.Core.Diagnostics;

/// <summary>
/// 中文：诊断日志（规格 §15）。
/// English: The diagnostic log (spec §15).
/// </summary>
public interface IDiagnosticLog
{
    /// <summary>
    /// 中文：
    ///   记一件事。**必须立刻返回**，实现方自己去异步落盘。
    /// English:
    ///   Records one event. Must return immediately; the implementation persists it asynchronously.
    /// </summary>
    void Write(DiagnosticEvent diagnosticEvent);
}
