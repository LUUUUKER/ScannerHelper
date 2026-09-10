// =============================================================================
// NativeMethods.cs
//
// 中文：
//   若干处原生声明共用的常量与入口点。
//
//   本项目里所有 P/Invoke 都遵守两条约定：
//     1. 一律显式调用 W 版本（GetModuleHandleW 而不是 GetModuleHandle）。
//        让 CharSet 去自动选 A/W 是一个隐式决定，而 A 版本在处理设备路径这类
//        可能含非 ASCII 字符的字符串时会静默截断。
//     2. 失败一律用 Marshal.GetLastWin32Error 取错误码并包成
//        Win32Exception 抛出。原生 API 返回 0 或 -1 本身说明不了任何事，
//        错误码才能说明是"权限不够"还是"参数不对"。
//
// English:
//   Constants and entry points shared by several native declarations.
//
//   Every P/Invoke here follows two rules. First, call the W entry point explicitly
//   (GetModuleHandleW, not GetModuleHandle): letting CharSet pick A or W is an
//   implicit decision, and the A variants silently mangle strings such as device
//   paths that may contain non-ASCII characters. Second, surface failures as a
//   Win32Exception built from Marshal.GetLastWin32Error — a bare 0 or -1 return
//   explains nothing, while the error code distinguishes "not permitted" from
//   "bad argument".
//
// 包含的类型 / Types in this file:
//   NativeMethods
// =============================================================================

using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ScannerHelper.Win32.Native;

/// <summary>
/// 中文：共用的原生常量与入口点。
/// English: Shared native constants and entry points.
/// </summary>
internal static class NativeMethods
{
    /// <summary>
    /// 中文：原始输入到达的窗口消息。窗口过程收到它之后，用
    ///       <see cref="RawInputNative.GetRawInputData"/> 取出内容。
    /// English: The window message announcing raw input. The window procedure then
    ///          reads the payload with
    ///          <see cref="RawInputNative.GetRawInputData"/>.
    /// </summary>
    internal const int WM_INPUT = 0x00FF;

    /// <summary>
    /// 中文：
    ///   取得当前进程主模块的句柄。
    ///   输入：无。输出：模块句柄。
    ///
    ///   装 WH_KEYBOARD_LL 时要用它。低层键盘钩子不需要把代码注入到别的进程
    ///   （这一点与老式的 WH_KEYBOARD 不同——那个真的需要一个 DLL），但
    ///   SetWindowsHookEx 仍然要一个非空的模块句柄。
    /// English:
    ///   Returns a handle to the current process's main module, required when
    ///   installing WH_KEYBOARD_LL. A low-level keyboard hook injects no code into
    ///   other processes — unlike the older WH_KEYBOARD, which genuinely needs a DLL
    ///   — but SetWindowsHookEx still wants a non-null module handle.
    /// </summary>
    /// <exception cref="Win32Exception">
    /// 中文：取句柄失败。 English: The handle could not be obtained.
    /// </exception>
    internal static IntPtr GetCurrentModuleHandle()
    {
        var moduleHandle = GetModuleHandleW(null);
        if (moduleHandle == IntPtr.Zero)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "无法取得当前进程的模块句柄，钩子无法安装。"
                + " Could not obtain the current module handle; the hook cannot be installed.");
        }

        return moduleHandle;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetModuleHandleW([MarshalAs(UnmanagedType.LPWStr)] string? lpModuleName);
}
