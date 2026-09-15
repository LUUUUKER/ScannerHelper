// =============================================================================
// GlobalHotkeyListener.cs
//
// 中文：
//   全局热键：注册、注销、按下时通知。
//
//   ★ 它拥有一个自己的仅消息窗口，而不是挂在界面窗口上。
//
//     界面窗口会被隐藏（最小化即 Compact，规格 §11.3）、会在 Compact 与 Full
//     之间来回创建销毁。热键若绑在某个界面窗口的句柄上，那个窗口一销毁，注册
//     就随之失效——现场表现是"用了一会儿之后 F8 就不管用了"，而且和某个操作
//     顺序相关，极难复现。
//
//     仅消息窗口的生命周期由本类自己掌握，和界面无关。
//
//   ★ 窗口建在**调用方的线程**上，而 WM_HOTKEY 由那条线程的消息循环取出。
//
//     在 WPF 里这意味着：从界面线程构造本类，热键回调就天然在界面线程上，
//     订阅方不需要切线程。这一点和串口那边刚好相反（那边是自己的读取线程），
//     所以两处都在文档里写明，免得有人按错的那一份去推断。
//
//   ★ 只在需要的那段时间里注册，这是规格 §7 的要求，也是对别的程序的礼貌。
//
//     裸键注册会把这个键从整个系统里拿走。Esc 尤其如此——它在日常网页操作里
//     太常用，无条件吞掉会让工人没法关弹窗、没法退出输入框。规格 §7 因此规定
//     F10 与 Esc 只在有待决错误时才生效；本类把这条规则实现成"那时才注册"，
//     于是"放行"不是一个判断的结果，而是我们压根没去抢它。
//
// English:
//   Global hotkeys: register, unregister, notify on press.
//
//   It owns a message-only window of its own rather than riding on a UI window. UI windows get
//   hidden (minimize means Compact, spec §11.3) and are created and destroyed as the view switches.
//   A hotkey bound to such a handle dies with the window, presenting as "F8 stops working after a
//   while" in a way that depends on the order of operations and is very hard to reproduce. This
//   window's lifetime belongs to this class and has nothing to do with the UI.
//
//   The window is created on the caller's thread and WM_HOTKEY is retrieved by that thread's
//   message loop. In WPF that means constructing this from the UI thread puts the callback on the
//   UI thread, so subscribers need no marshalling — the opposite of the serial source, which
//   raises on its own reader thread. Both are documented so that nobody reasons from the wrong one.
//
//   Registration is confined to the moments it is needed, which spec §7 requires and which is also
//   courtesy toward other programs. A bare key registration takes that key from the whole system,
//   and Escape especially: it is far too common in ordinary web use to swallow unconditionally, and
//   doing so would leave the operator unable to dismiss a dialog or leave a field. Spec §7
//   therefore makes F10 and Escape effective only while an error is pending, and this class
//   implements that as "only then is it registered" — so passing through is not the outcome of a
//   judgment but the absence of any attempt to take the key.
//
// 包含的类型 / Types in this file:
//   ScannerHotkey
//   HotkeyPressedEventArgs
//   GlobalHotkeyListener
// =============================================================================

using System.ComponentModel;
using System.Runtime.InteropServices;
using ScannerHelper.Win32.Native;

using ScannerHelper.Core.Input;

namespace ScannerHelper.Win32;

/// <summary>
/// 中文：本产品用到的三个热键（规格 §7）。
/// English: The three hotkeys this product uses (spec §7).
/// </summary>
public enum ScannerHotkey
{
    /// <summary>
    /// 中文：
    ///   切换 SN / SKU。默认 Insert，设置里可改（见 HotkeySettings.ToggleMode）。
    ///
    ///   ★ 本程序现在**只注册这一个**全局热键。
    ///
    ///     F10（强制发送）与 Esc（丢弃）在 2026-09-15 随那两个功能一起去掉了，
    ///     见 docs/CHANGE_ERROR_HANDLING.md。这里也把枚举值删掉，而不是留着
    ///     不用——留着的话，谁调一次 Register(ForceSend) 就会把 F10 从整个系统
    ///     里拿走，而这个程序界面上再也没有任何地方提到 F10，排查的人根本不会
    ///     往这儿想。没有用户的能力就不该留在代码里。
    /// English:
    ///   Toggles SN/SKU. Insert by default, changeable in Settings (see HotkeySettings.ToggleMode).
    ///
    ///   This is now the only global hotkey the program registers. F10 (Force Send) and Escape
    ///   (Discard) went with those features on 2026-09-15; see docs/CHANGE_ERROR_HANDLING.md. Their
    ///   enum values are deleted rather than left unused: left in place, one call to
    ///   Register(ForceSend) would take F10 away from the whole system while nothing in this
    ///   program's UI mentions F10 any more, and nobody investigating would think to look here. A
    ///   capability with no user does not belong in the code.
    /// </summary>
    ToggleMode = 1,
}

/// <summary>
/// 中文：某个热键被按下了。
/// English: A hotkey was pressed.
/// </summary>
public sealed class HotkeyPressedEventArgs(ScannerHotkey hotkey) : EventArgs
{
    /// <summary>中文：哪一个。 English: Which one.</summary>
    public ScannerHotkey Hotkey { get; } = hotkey;
}

/// <summary>
/// 中文：全局热键的注册与分发。
/// English: Registration and dispatch of global hotkeys.
/// </summary>
public sealed class GlobalHotkeyListener : IDisposable
{
    private readonly string _windowClassName = $"ScannerHelperHotkeys_{Guid.NewGuid():N}";
    private readonly HashSet<ScannerHotkey> _registered = [];

    private WindowNative.WindowProcedure? _windowProcedure;
    private IntPtr _windowHandle;
    private ushort _windowClassAtom;
    private bool _isDisposed;

    /// <summary>
    /// 中文：
    ///   构造监听器并建好仅消息窗口。
    ///   **必须在有消息循环的线程上构造**——在 WPF 里就是界面线程。
    /// English:
    ///   Creates the listener and its message-only window. Must be constructed on a thread with a
    ///   message loop, which in WPF means the UI thread.
    /// </summary>
    /// <exception cref="Win32Exception">
    /// 中文：窗口类注册或窗口创建失败。
    /// English: Registering the class or creating the window failed.
    /// </exception>
    public GlobalHotkeyListener()
    {
        _windowProcedure = OnWindowMessage;

        var windowClass = new WindowNative.WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WindowNative.WNDCLASSEXW>(),
            lpfnWndProc = _windowProcedure,
            hInstance = NativeMethods.GetCurrentModuleHandle(),
            lpszClassName = _windowClassName,
        };

        _windowClassAtom = WindowNative.RegisterClassExW(ref windowClass);
        if (_windowClassAtom == 0)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "注册热键窗口类失败。 Failed to register the hotkey window class.");
        }

        _windowHandle = WindowNative.CreateWindowExW(
            0, _windowClassName, null, 0, 0, 0, 0, 0,
            WindowNative.HWND_MESSAGE, IntPtr.Zero, windowClass.hInstance, IntPtr.Zero);

        if (_windowHandle == IntPtr.Zero)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "创建热键窗口失败。 Failed to create the hotkey window.");
        }
    }

    /// <summary>
    /// 中文：热键被按下。**在构造本类的那条线程上触发**（WPF 里是界面线程）。
    /// English: A hotkey was pressed, raised on the thread that constructed this — the UI thread
    ///          in WPF.
    /// </summary>
    public event EventHandler<HotkeyPressedEventArgs>? Pressed;

    /// <summary>
    /// 中文：
    ///   模式切换键的配置写法，例如 "Insert"、"Ctrl+Alt+S"（见 HotkeyKeyCatalog）。
    ///   改了之后要先 Unregister 再 Register 才生效。
    /// English:
    ///   How the mode-toggle key is configured, e.g. "Insert" or "Ctrl+Alt+S" (see
    ///   HotkeyKeyCatalog). Changing it takes effect after an Unregister followed by a Register.
    /// </summary>
    public string ToggleModeChord { get; set; } = "Insert";

    /// <summary>
    /// 中文：
    ///   键名 → Windows 虚拟键码。Core 只认名字（它不能引用任何 Windows 的东西），
    ///   这张表是名字落到本平台上的那一步。
    /// English:
    ///   Key name to Windows virtual-key code. Core deals only in names, since it may reference
    ///   nothing from Windows; this table is where a name lands on this platform.
    /// </summary>
    private static readonly Dictionary<string, ushort> VirtualKeys =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Insert"] = 0x2D,
            ["Delete"] = 0x2E,
            ["Home"] = 0x24,
            ["End"] = 0x23,
            ["PageUp"] = 0x21,
            ["PageDown"] = 0x22,
            ["Pause"] = 0x13,
            ["ScrollLock"] = 0x91,
            ["Apps"] = 0x5D,
            ["NumpadAdd"] = 0x6B,
            ["NumpadSubtract"] = 0x6D,
            ["NumpadMultiply"] = 0x6A,
            ["NumpadDivide"] = 0x6F,
            ["Escape"] = 0x1B,
            ["F1"] = 0x70,
            ["F2"] = 0x71,
            ["F3"] = 0x72,
            ["F4"] = 0x73,
            ["F6"] = 0x75,
            ["F7"] = 0x76,
            ["F8"] = 0x77,
            ["F9"] = 0x78,
            ["F10"] = 0x79,
        };

    /// <summary>
    /// 中文：把配置里的写法解析成 RegisterHotKey 要的两个数。
    /// English: Resolves the configured text into the two numbers RegisterHotKey needs.
    /// </summary>
    private static bool TryResolve(string chordText, out uint modifiers, out ushort virtualKey)
    {
        modifiers = HotkeyNative.MOD_NONE;
        virtualKey = 0;

        if (!HotkeyKeyCatalog.TryParse(chordText, out var chord))
        {
            return false;
        }

        if (!VirtualKeys.TryGetValue(chord.KeyName, out virtualKey))
        {
            // 单个字母（"S"）不在表里，按 ASCII 直接得到虚拟键码——
            // A~Z 的虚拟键码就是它们大写字母的 ASCII 值。
            // Single letters ("S") are absent from the table: A-Z virtual-key codes are simply the
            // ASCII values of their uppercase forms.
            if (chord.KeyName.Length != 1 || !char.IsAsciiLetter(chord.KeyName[0]))
            {
                return false;
            }

            virtualKey = (ushort)char.ToUpperInvariant(chord.KeyName[0]);
        }

        if (chord.Modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            modifiers |= HotkeyNative.MOD_ALT;
        }

        if (chord.Modifiers.HasFlag(HotkeyModifiers.Control))
        {
            modifiers |= HotkeyNative.MOD_CONTROL;
        }

        if (chord.Modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            modifiers |= HotkeyNative.MOD_SHIFT;
        }

        return true;
    }

    /// <summary>
    /// 中文：
    ///   注册一个热键。已经注册过就直接返回 true。
    ///   输出：拿到了返回 true；被别的程序占着返回 false。
    ///
    ///   ★ 拿不到不是异常，是常态：别的程序可能先注册了同一个裸键。调用方必须
    ///     处理 false——界面上那句"按 F8 切换模式"在拿不到的时候是一句假话，
    ///     而规格 §19.1 的原则是界面绝不显示它没有验证过的东西，那既包括运行
    ///     状态，也包括一句操作提示。
    /// English:
    ///   Registers one hotkey, returning true if already registered, and false when another
    ///   program holds the key.
    ///
    ///   Failure is ordinary rather than exceptional, and callers must handle it: the hint "press
    ///   F8 to switch mode" is a false statement when the key could not be taken, and spec §19.1's
    ///   principle that the UI never presents what it has not verified covers instructions as much
    ///   as operational state.
    /// </summary>
    public bool Register(ScannerHotkey hotkey)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        if (_registered.Contains(hotkey))
        {
            return true;
        }

        // ★ 只有切换键是可配置的（见 HotkeySettings.ToggleMode）。
        //
        //   强制发送与取消保持 F10 / Esc 不动：它们**只在有一枪卡住等决定的
        //   那几秒里**被注册，其余时间根本不占用（见 MainWindow 的 SyncHotkeys），
        //   所以它们撞上别的软件的窗口极窄。切换键不一样——它是常驻的，
        //   而现场正是常驻的那一个撞上了。
        //
        // Only the toggle is configurable (see HotkeySettings.ToggleMode). Force Send and Cancel
        // stay on F10 and Esc because they are registered only during the few seconds a scan is
        // waiting on a decision (see MainWindow's SyncHotkeys) and hold nothing the rest of the
        // time, so their window for clashing is very narrow. The toggle is different: it is held
        // permanently, and the permanent one is what clashed on site.
        if (!TryResolve(ToggleModeChord, out var modifiers, out var virtualKey))
        {
            // 配置里的键名认不出来。不回退到别的键——见 HotkeyKeyCatalog.TryParse：
            // 悄悄换一个键会让界面显示的和实际生效的不一致。返回 false，
            // 界面那句"按 X 切换模式"就会换成"这台机器上不可用"。
            // The configured name is unrecognized. No fallback to another key — see
            // HotkeyKeyCatalog.TryParse: substituting silently makes the UI disagree with reality.
            // Returning false turns the "press X to switch" hint into "unavailable on this machine".
            return false;
        }

        var registered = HotkeyNative.RegisterHotKey(
            _windowHandle,
            (int)hotkey,
            modifiers | HotkeyNative.MOD_NOREPEAT,
            virtualKey);

        if (registered)
        {
            _registered.Add(hotkey);
        }

        return registered;
    }

    /// <summary>
    /// 中文：注销一个热键。没注册过是空操作。注销之后这个键立刻回到别的程序手里。
    /// English: Unregisters one hotkey; a no-op if it was never registered. The key returns to
    ///          other programs immediately.
    /// </summary>
    public void Unregister(ScannerHotkey hotkey)
    {
        if (_isDisposed || !_registered.Remove(hotkey))
        {
            return;
        }

        HotkeyNative.UnregisterHotKey(_windowHandle, (int)hotkey);
    }

    /// <summary>
    /// 中文：是否已经注册着某个热键。
    /// English: Whether a hotkey is currently registered.
    /// </summary>
    public bool IsRegistered(ScannerHotkey hotkey) => _registered.Contains(hotkey);

    /// <summary>
    /// 中文：
    ///   注销全部热键并销毁窗口。
    ///
    ///   ★ 必须真的注销。热键是**系统范围**的：进程没了而注册还挂着，那个键在
    ///     别的程序里就一直失灵，而工人根本不会把"F8 不好使了"和"那个扫码程序"
    ///     联系起来。Windows 在进程退出时会清理，但我们不该把干净收尾寄托在
    ///     那上面——规格 §19 要求的是干净收尾，不是"操作系统会兜底"。
    /// English:
    ///   Unregisters everything and destroys the window.
    ///
    ///   Unregistering must actually happen. Hotkeys are system-wide: a registration outliving the
    ///   process leaves that key dead in every other program, and nobody connects "F8 stopped
    ///   working" with "that scanner program". Windows cleans up on exit, but clean shutdown must
    ///   not rest on that — spec §19 asks for clean shutdown, not for the operating system to cover
    ///   for us.
    /// </summary>
    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;

        foreach (var hotkey in _registered)
        {
            HotkeyNative.UnregisterHotKey(_windowHandle, (int)hotkey);
        }

        _registered.Clear();

        if (_windowHandle != IntPtr.Zero)
        {
            WindowNative.DestroyWindow(_windowHandle);
            _windowHandle = IntPtr.Zero;
        }

        if (_windowClassAtom != 0)
        {
            WindowNative.UnregisterClassW(_windowClassName, NativeMethods.GetCurrentModuleHandle());
            _windowClassAtom = 0;
        }

        _windowProcedure = null;
    }

    /// <summary>
    /// 中文：
    ///   仅消息窗口的窗口过程。只认 WM_HOTKEY，其余交给默认处理。
    ///   wParam 就是注册时给的 id，也就是 <see cref="ScannerHotkey"/> 的值。
    /// English:
    ///   The message-only window's procedure. It recognizes WM_HOTKEY and defers everything else;
    ///   wParam is the id used to register, which is the <see cref="ScannerHotkey"/> value.
    /// </summary>
    private IntPtr OnWindowMessage(IntPtr windowHandle, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == HotkeyNative.WM_HOTKEY)
        {
            var hotkey = (ScannerHotkey)(int)wParam;

            if (Enum.IsDefined(hotkey))
            {
                Pressed?.Invoke(this, new HotkeyPressedEventArgs(hotkey));
            }

            return IntPtr.Zero;
        }

        return WindowNative.DefWindowProcW(windowHandle, message, wParam, lParam);
    }
}
