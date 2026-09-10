// =============================================================================
// LowLevelKeyboardHook.cs
//
// 中文：
//   WH_KEYBOARD_LL 的托管封装。
//
//   ★★ 本类只负责**机制**，不持有任何**策略**。
//
//     它把按键从 Windows 那里取出来，把调用方的决定执行下去——放行或吞掉。
//     谁该被吞掉是 Core 的业务规则（扫码枪？暂停中？热键？），放在这里就
//     等于让 Windows 互操作层知道业务逻辑，而规格 §17 明确要求这两者分开。
//
//   ★★ 4a 与 4b 的区别在**调用方**，而且是结构上的区别。
//
//     规格 §4.3 把 spike 拆成两段，理由写得很直白：把二者合在一起做，等于
//     同时在调试"没验证过的逻辑"和"不了解的硬件行为"，而那正是时序启发式
//     被悄悄引入、用来让症状消失的典型路径。
//
//     4a 只观测，因此它的诊断工具敢在真实工作机上跑——但那个保证不能靠
//     "记得别返回 Swallow"。<see cref="CreateObserveOnly"/> 返回的钩子，
//     其回调恒为 PassThrough 且不接受任何外部输入：即便调用方想让它吞掉
//     某个键，也无从下手。
//
//     4b 需要拦截，走普通构造函数，由调用方提供决策。
//
//   ★ 必须装在有消息循环的线程上。
//
//     低层键盘钩子的回调是由系统投递到**安装它的那个线程**的消息队列里来
//     驱动的。装在一个没有消息泵的后台线程上，回调永远不会被调用，而
//     SetWindowsHookEx 会返回一个看起来完全正常的句柄——没有任何报错，
//     就是收不到事件。实际使用中就装在 WPF 的界面线程上。
//
// English:
//   A managed wrapper over WH_KEYBOARD_LL.
//
//   This class owns the mechanism and none of the policy. It takes keystrokes from Windows and
//   carries out the caller's verdict — pass through or swallow. What may be swallowed is Core's
//   business rule (the scanner? paused? a hotkey?), and deciding it here would put business logic
//   inside the interop layer, which spec §17 requires to stay separate.
//
//   The difference between 4a and 4b lies in the caller, structurally. Spec §4.3 splits the spike
//   for a plainly stated reason: doing both at once means debugging unproven logic against unknown
//   hardware behavior simultaneously, which is exactly how timing heuristics get introduced to make
//   symptoms disappear.
//
//   4a observes only, which is what makes its harness safe to run on a live working machine — and
//   that guarantee cannot rest on "remember not to return Swallow". The hook returned by
//   CreateObserveOnly has a callback that is unconditionally PassThrough and takes no outside
//   input: a caller wanting it to swallow a key has nowhere to reach. 4b needs interception and
//   goes through the general constructor, supplying its own decision.
//
//   The hook must be installed on a thread with a message loop. A low-level keyboard
//   hook's callback is driven through the message queue of the thread that installed
//   it. Installed on a background thread with no message pump, the callback is never
//   invoked while SetWindowsHookEx returns a perfectly healthy-looking handle — no
//   error, simply no events. In practice it is installed on the WPF UI thread.
//
// 包含的成员 / Members in this file:
//   IsInstalled  钩子当前是否已安装
//   Install      安装钩子
//   Uninstall    卸载钩子
//   Dispose      卸载钩子
// =============================================================================

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ScannerHelper.Win32.Native;
using ScannerHelper.Win32.Observation;

namespace ScannerHelper.Win32;

/// <summary>
/// 中文：低层键盘钩子。只负责机制，决策由调用方提供。
/// English: The low-level keyboard hook. Mechanism only; the caller supplies the policy.
/// </summary>
public sealed class LowLevelKeyboardHook : IDisposable
{
    private readonly KeyboardEventHandler _handler;

    /// <summary>
    /// 中文：★ 回调委托必须由托管代码持有，不能只作为参数传给
    ///       SetWindowsHookEx 就撒手。否则 GC 会把它回收掉，而 Windows 手里
    ///       还留着那个函数指针——下一次按键就会跳进一段已释放的内存，
    ///       表现为进程随机崩溃，且崩溃点与真正的原因毫无关系，极难排查。
    /// English: The callback delegate must be held by managed code rather than merely
    ///          handed to SetWindowsHookEx. Otherwise the GC collects it while Windows
    ///          still holds the function pointer, and the next keystroke jumps into
    ///          freed memory — a random crash whose location has nothing to do with
    ///          the cause, and which is correspondingly hard to diagnose.
    /// </summary>
    private readonly KeyboardHookNative.LowLevelKeyboardProc _callback;

    private IntPtr _hookHandle;

    /// <summary>
    /// 中文：
    ///   构造钩子封装。此时并不安装钩子，安装请调用 <see cref="Install"/>。
    ///   输入：handler 每次按键的处理与决策，不得为 null。
    ///
    ///   ★ 只观测的用法请走 <see cref="CreateObserveOnly"/>，不要自己传一个
    ///     "总是返回 PassThrough" 的 handler。区别在于前者**结构上**无法吞掉
    ///     按键，后者只是**这次**没吞——而 4a 的诊断工具敢在真实工作机上跑，
    ///     靠的正是前者那种保证。
    /// English:
    ///   Creates the wrapper without installing anything; call <see cref="Install"/> for that.
    ///   handler both records and decides for each keystroke and must not be null.
    ///
    ///   For observe-only use go through <see cref="CreateObserveOnly"/> rather than passing a
    ///   handler that happens to always return PassThrough. The former structurally cannot
    ///   swallow while the latter merely does not this time — and what makes 4a's harness safe to
    ///   run on a live working machine is the former kind of guarantee.
    /// </summary>
    /// <exception cref="ArgumentNullException">
    /// 中文：handler 为 null。 English: handler is null.
    /// </exception>
    public LowLevelKeyboardHook(KeyboardEventHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        _handler = handler;
        _callback = OnKeyboardEvent;
    }

    /// <summary>
    /// 中文：
    ///   建一个**只观测、绝不拦截**的钩子（Task 4a）。
    ///   输入：buffer 观测事件的去处，不得为 null。
    ///
    ///   ★ 这个工厂方法存在的理由，是把 4a 的安全性质保留成一件**结构上**
    ///     成立的事，而不是一句注释。
    ///
    ///     4b 需要拦截，所以钩子本身必须能吞掉按键。但 4a 的诊断工具敢在
    ///     真实工作机上跑，靠的正是"它没有任何一条通向吞掉的代码路径"。
    ///     两者不能都靠同一个构造函数加一句"记得别返回 Swallow"来保证。
    ///
    ///     这里返回的钩子，其回调恒为 PassThrough，且那个 lambda 不接受
    ///     任何外部输入——即便调用方想让它吞掉某个键，也无从下手。
    /// English:
    ///   Creates a hook that observes and never intercepts (Task 4a).
    ///
    ///   This factory exists to keep 4a's safety property structural rather than a comment. 4b
    ///   needs interception, so the hook itself must be able to swallow — yet what makes 4a's
    ///   harness safe to run on a live working machine is that it has no code path to swallowing
    ///   at all. Both cannot rest on one constructor plus a note saying "remember not to return
    ///   Swallow".
    ///
    ///   The hook returned here has a callback that is unconditionally PassThrough, in a lambda
    ///   that takes no outside input: a caller wanting it to swallow a key has nowhere to reach.
    /// </summary>
    /// <exception cref="ArgumentNullException">
    /// 中文：buffer 为 null。 English: buffer is null.
    /// </exception>
    public static LowLevelKeyboardHook CreateObserveOnly(ObservationBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        return new LowLevelKeyboardHook((in ObservedInputEvent observedEvent) =>
        {
            buffer.Write(observedEvent);
            return HookDecision.PassThrough;
        });
    }

    /// <summary>
    /// 中文：钩子当前是否已安装。
    /// English: Whether the hook is currently installed.
    /// </summary>
    public bool IsInstalled => _hookHandle != IntPtr.Zero;

    /// <summary>
    /// 中文：
    ///   安装钩子。
    ///   输入：无。输出：无。
    ///   必须在有消息循环的线程上调用，见文件头说明。重复安装是空操作。
    /// English:
    ///   Installs the hook. Must be called on a thread with a message loop; see the
    ///   file header. Installing twice is a no-op.
    /// </summary>
    /// <exception cref="Win32Exception">
    /// 中文：安装失败。 English: Installation failed.
    /// </exception>
    public void Install()
    {
        if (IsInstalled)
        {
            return;
        }

        _hookHandle = KeyboardHookNative.SetWindowsHookExW(
            KeyboardHookNative.WH_KEYBOARD_LL,
            _callback,
            NativeMethods.GetCurrentModuleHandle(),
            0);

        if (_hookHandle == IntPtr.Zero)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "安装低层键盘钩子失败。若业务软件以管理员权限运行，"
                + "而本程序没有，UIPI 会阻止钩子观测到它的按键（规格 §2.1 假设 A2）。"
                + " Failed to install the low-level keyboard hook. If the business"
                + " application runs elevated and this one does not, UIPI prevents the"
                + " hook from observing its keystrokes (spec §2.1, assumption A2).");
        }
    }

    /// <summary>
    /// 中文：
    ///   卸载钩子。未安装时是空操作。
    ///   规格 §19 要求程序退出时干净地摘钩子并注销输入注册。
    /// English:
    ///   Uninstalls the hook; a no-op when not installed. Spec §19 requires unhooking
    ///   and unregistering cleanly on shutdown.
    /// </summary>
    public void Uninstall()
    {
        if (!IsInstalled)
        {
            return;
        }

        KeyboardHookNative.UnhookWindowsHookEx(_hookHandle);
        _hookHandle = IntPtr.Zero;
    }

    /// <inheritdoc />
    public void Dispose() => Uninstall();

    /// <summary>
    /// 中文：
    ///   钩子回调。
    ///   输入：nCode、wParam、lParam 由系统提供。
    ///   输出：始终返回 <c>CallNextHookEx</c> 的结果，即**始终放行**。
    ///   步骤：
    ///     1. nCode 小于 0 时按约定不作任何处理，直接转交；
    ///     2. 立刻取高分辨率时间戳；
    ///     3. 读出按键数据并写入观测缓冲区；
    ///     4. 转交给钩子链上的下一个钩子。
    ///
    ///   ★ 步骤 2 必须是处理事件时做的第一件事。整个 4a 要测的就是本回调与
    ///     WM_INPUT 之间的时差；时间戳取得越晚，测出来的值里就越多地混进了
    ///     我们自己代码的耗时，而不是两条通道真实的先后关系。
    ///
    ///   ★ 本方法里没有任何一条通向"吞掉按键"的路径。这是 4a 的定义
    ///     （规格 §4.3：安装钩子但原样放行每一个事件），也是本工具敢在真实
    ///     工作机上运行的全部理由。
    ///
    ///   关于步骤 3 的 PtrToStructure：对于 KBDLLHOOKSTRUCT 这种全部由基本
    ///   类型构成的可直接复制结构体，它在 .NET 上退化为一次内存复制，不产生
    ///   堆分配。真正需要防的是**对象**分配带来的 GC 压力——一次 GC 暂停就
    ///   可能顶穿 LowLevelHooksTimeout，而那会让 Windows 悄悄摘掉钩子
    ///   （规格 §19.1）。观测事件用值类型加预分配环形缓冲区，正是为此。
    ///
    /// English:
    ///   The hook callback. Always returns CallNextHookEx's result — always passes
    ///   through.
    ///   Steps: (1) forward untouched when nCode is negative, as required; (2) take a
    ///   high-resolution timestamp immediately; (3) read the keystroke and record it;
    ///   (4) forward to the next hook in the chain.
    ///
    ///   Step 2 must be the first thing done for an event. All of 4a measures the delta
    ///   between this callback and WM_INPUT; the later the timestamp is taken, the more
    ///   of our own code's cost is folded into a figure that is supposed to describe
    ///   the two channels' real ordering.
    ///
    ///   There is no path here that swallows a keystroke. That is the definition of 4a
    ///   (spec §4.3: install the hook but pass every event through unchanged) and the
    ///   whole reason this tool is safe to run on a live working machine.
    ///
    ///   On PtrToStructure in step 3: for a blittable struct of primitives such as
    ///   KBDLLHOOKSTRUCT it degrades to a memory copy on .NET and allocates nothing on
    ///   the heap. What genuinely has to be avoided is *object* allocation and the GC
    ///   pressure it creates — one GC pause can blow the LowLevelHooksTimeout budget
    ///   and have Windows silently remove the hook (spec §19.1). That is why observed
    ///   events are value types in a preallocated ring buffer.
    /// </summary>
    private IntPtr OnKeyboardEvent(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // 步骤 1 / Step 1
        if (nCode >= 0)
        {
            // 步骤 2 / Step 2 —— 必须最先做 / must come first
            var timestamp = Stopwatch.GetTimestamp();

            // 步骤 3 / Step 3
            var keyEvent = Marshal.PtrToStructure<KeyboardHookNative.KBDLLHOOKSTRUCT>(lParam);

            const uint injectedFlags =
                KeyboardHookNative.LLKHF_INJECTED | KeyboardHookNative.LLKHF_LOWER_IL_INJECTED;

            var observedEvent = new ObservedInputEvent(
                Channel: InputChannel.Hook,
                Timestamp: timestamp,
                VirtualKey: (ushort)keyEvent.vkCode,
                ScanCode: (ushort)keyEvent.scanCode,
                IsExtended: (keyEvent.flags & KeyboardHookNative.LLKHF_EXTENDED) != 0,
                IsKeyUp: (keyEvent.flags & KeyboardHookNative.LLKHF_UP) != 0,
                IsInjected: (keyEvent.flags & injectedFlags) != 0,

                // 钩子通道拿不到设备身份——这正是 4a 研究的问题本身。
                // The hook channel has no device identity; that is 4a's subject.
                DeviceHandle: 0);

            // 步骤 4 / Step 4
            //
            // ★ 决策交给 handler，而不是在这里判断。这条边界很重要：
            //   本类只负责"把事件取出来、把决定执行下去"，不持有任何关于
            //   扫码枪、模式、暂停的知识。谁能被吞掉是 Core 的业务规则，
            //   放在这里就等于让 Windows 互操作层知道业务逻辑（规格 §17）。
            //
            // The decision belongs to the handler rather than to this method. The boundary
            // matters: this class extracts the event and carries out the verdict, holding no
            // knowledge of scanners, modes or pausing. What may be swallowed is Core's business
            // rule, and deciding it here would put business logic inside the interop layer
            // (spec §17).
            if (_handler(observedEvent) == HookDecision.Swallow)
            {
                // ★ 返回非零即吞掉：业务软件永远不会看到这次按键（规格 §5.4）。
                //
                //   这是整个产品唯一能阻止原始条码泄漏进业务软件的地方。
                //   Task 4a 实测钩子 100% 先于 WM_INPUT 到达，因此走到这里时
                //   往往还不知道是谁按的——调用方按规格 §5.3 先吞后补，
                //   而"补"是它的责任，不是这里的。
                //
                // A non-zero return swallows it and the business application never sees the
                // keystroke (spec §5.4). This is the one place in the product able to stop a raw
                // barcode leaking into the business application. Task 4a measured the hook
                // preceding WM_INPUT 100% of the time, so at this point the source is usually
                // still unknown: the caller swallows first and replays later per spec §5.3, and
                // the replaying is its responsibility rather than this method's.
                return new IntPtr(1);
            }
        }

        // 步骤 5 / Step 5 —— 放行，转交给钩子链上的下一个钩子
        // Step 5 — pass through to the next hook in the chain
        return KeyboardHookNative.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }
}
