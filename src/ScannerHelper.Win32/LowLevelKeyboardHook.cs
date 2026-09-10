// =============================================================================
// LowLevelKeyboardHook.cs
//
// 中文：
//   WH_KEYBOARD_LL 的托管封装（Task 4a — 只观测，不拦截）。
//
//   ★★ 本阶段的最重要约束：**每一个事件都必须原样放行。**
//
//     规格 §4.3 把 spike 拆成 4a 和 4b 两段，理由写得很直白：把二者合在一起
//     做，等于同时在调试"没验证过的逻辑"和"不了解的硬件行为"，而那正是
//     时序启发式被悄悄引入、用来让症状消失的典型路径。
//
//     4a 只回答问题，不改变任何行为。因此本类**没有**任何吞掉按键的代码路径——
//     不是"默认放行"，而是根本没有另一条路可走。回调唯一的出口就是
//     CallNextHookEx。这使得本工具可以安全地在真实工作机上运行：它对系统的
//     影响仅限于每次按键多几十纳秒。
//
//     拦截与重放是 4b 的事，而且必须建立在 4a 量出来的数据之上。
//
//   ★ 必须装在有消息循环的线程上。
//
//     低层键盘钩子的回调是由系统投递到**安装它的那个线程**的消息队列里来
//     驱动的。装在一个没有消息泵的后台线程上，回调永远不会被调用，而
//     SetWindowsHookEx 会返回一个看起来完全正常的句柄——没有任何报错，
//     就是收不到事件。实际使用中就装在 WPF 的界面线程上。
//
// English:
//   A managed wrapper over WH_KEYBOARD_LL (Task 4a — observe only, never intercept).
//
//   The overriding constraint at this stage: every event is passed through unchanged.
//
//   Spec §4.3 splits the spike into 4a and 4b for a plainly stated reason: doing both
//   at once means debugging unproven logic against unknown hardware behavior
//   simultaneously, which is exactly how timing heuristics get introduced to make
//   symptoms disappear.
//
//   4a answers questions and changes no behavior. This class therefore contains no
//   code path that swallows a keystroke — not "passes through by default", but no
//   other route at all. The callback's only exit is CallNextHookEx. That is what makes
//   the tool safe to run on a real working machine: its entire effect on the system is
//   a few dozen nanoseconds per keystroke.
//
//   Interception and replay belong to 4b, and must be built on what 4a measures.
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
/// 中文：低层键盘钩子。4a 阶段只观测，绝不拦截任何按键。
/// English: The low-level keyboard hook. In 4a it observes only and never intercepts.
/// </summary>
public sealed class LowLevelKeyboardHook : IDisposable
{
    private readonly ObservationBuffer _buffer;

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
    ///   输入：buffer 观测事件的去处，不得为 null。
    /// English:
    ///   Creates the wrapper without installing anything; call <see cref="Install"/>
    ///   for that. buffer is where observed events go and must not be null.
    /// </summary>
    /// <exception cref="ArgumentNullException">
    /// 中文：buffer 为 null。 English: buffer is null.
    /// </exception>
    public LowLevelKeyboardHook(ObservationBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        _buffer = buffer;
        _callback = OnKeyboardEvent;
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

            _buffer.Write(new ObservedInputEvent(
                Channel: InputChannel.Hook,
                Timestamp: timestamp,
                VirtualKey: (ushort)keyEvent.vkCode,
                ScanCode: (ushort)keyEvent.scanCode,
                IsKeyUp: (keyEvent.flags & KeyboardHookNative.LLKHF_UP) != 0,
                IsInjected: (keyEvent.flags & injectedFlags) != 0,

                // 钩子通道拿不到设备身份——这正是 4a 要研究的问题本身。
                // The hook channel has no device identity; that is 4a's subject.
                DeviceHandle: 0));
        }

        // 步骤 4 / Step 4 —— 唯一的出口，永远放行 / the only exit; always passes through
        return KeyboardHookNative.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }
}
