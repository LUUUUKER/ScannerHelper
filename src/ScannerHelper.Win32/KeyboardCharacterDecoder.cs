// =============================================================================
// KeyboardCharacterDecoder.cs
//
// 中文：
//   把一次按键翻译成字符（决策 D-14）。这是 Core 与 Win32 分工的 Win32 那一半：
//   Core 只处理字符，解码所需的键盘布局、修饰键状态、CapsLock 全在这里。
//
//   ★★ 本文件的核心，是一个**只有在拦截模式下才会暴露**的陷阱：
//
//       我们把按键吞掉了，所以 Windows 的键盘状态**不再反映工人按了什么**。
//
//     `GetKeyboardState` 返回的是系统认为的按键状态。而扣留-重放的设计里，
//     Shift 的按下事件被我们吞掉了、还没补发，于是系统眼中 Shift **根本没被
//     按下**。拿它去解码，扫码枪发来的大写字母会全部解成小写。
//
//     Task 4a 的诊断工具没有踩到这个坑，因为它是**只观测、不拦截**的——
//     按键照常送达系统，系统状态自然是对的。一旦进入 4b 的拦截模式，
//     这个前提就没了。
//
//     所以修饰键状态必须**我们自己维护**：观察每一个经过的按键事件，
//     自己记住 Shift/Ctrl/Alt 的按下与弹起。这不是优化，是拦截模式下唯一
//     正确的做法。
//
//   ★ CapsLock 的初始状态无从推断，只能向系统要一次。
//
//     它是一个**切换态**而不是按下态，我们观察不到"当前是开还是关"，
//     只能观察到"被按了一下"。所以构造时向系统问一次初始值，之后靠观察
//     到的按下事件自己翻转。
//
//     这里有一个已知的漂移风险：若 CapsLock 的按下被吞掉后由于某种原因
//     没有补发，系统状态与我们的记录就会分家。表现是"扫出来的码整体
//     大小写颠倒"——这恰好是一个**看得见**的症状，所以宁可让它以这种
//     方式暴露，也不要每次解码都去问系统（那会在拦截模式下拿到错的答案）。
//
//   ★ 键盘布局取**前台窗口**的，并且带缓存。
//
//     扫码枪发出的是扫描码，Windows 用接收方的布局翻译成字符，而接收方是
//     业务软件。用本进程的布局会在两者不同时解出不一样的字符。
//
//     缓存是因为解码发生在钩子回调路径上（规格 §19 要求它极快）。刷新间隔
//     取 500 毫秒：一次扫描只有几十毫秒，布局在一枪之内不可能变；而工人
//     切换输入法之后，最迟半秒就会被跟上。
//
// English:
//   Translates a keystroke into a character (decision D-14) — the Win32 half of the Core/Win32
//   split. Core deals only in characters, while the keyboard layout, modifier state and CapsLock
//   that decoding needs all live here.
//
//   The heart of this file is a trap that only appears once interception begins: because we
//   swallow keystrokes, Windows' keyboard state no longer reflects what the operator pressed.
//
//   GetKeyboardState returns what the system believes. Under withhold-and-replay the Shift key's
//   down event has been swallowed and not yet replayed, so as far as the system is concerned
//   Shift is not down at all. Decoding from it turns every capital the scanner sends into a
//   lowercase letter.
//
//   Task 4a's harness never hit this because it observed without intercepting: keystrokes reached
//   the system normally and the system's state was correct. Entering 4b's intercepting mode
//   removes that premise.
//
//   Modifier state must therefore be maintained here, by watching every key event that passes and
//   remembering the downs and ups of Shift, Ctrl and Alt ourselves. That is not an optimization
//   but the only correct approach under interception.
//
//   CapsLock's initial state cannot be inferred and must be asked of the system once. It is a
//   *toggle* rather than a held state, so we can observe "it was pressed" but never "it is
//   currently on". The constructor asks the system for the initial value and observed presses
//   flip it thereafter.
//
//   A known drift risk comes with that: if a swallowed CapsLock press is for some reason never
//   replayed, the system's state and ours part company, presenting as a scan whose case is
//   uniformly inverted. That is a *visible* symptom, and letting it surface that way is better
//   than asking the system on every decode, which under interception returns the wrong answer.
//
//   The keyboard layout is the foreground window's and is cached. A scanner emits scan codes that
//   Windows translates with the receiver's layout, and the receiver is the business application;
//   using this process's layout would decode differently whenever the two differ. Caching is
//   because decoding sits on the hook callback path, which spec §19 requires to be extremely
//   fast. The refresh interval is 500 ms: one scan lasts tens of milliseconds and a layout cannot
//   change within it, while an operator switching input method is picked up within half a second.
//
// 包含的成员 / Members in this file:
//   Decode        把一次按键翻译成字符，并顺带更新修饰键状态
//   ResetState    重置自己维护的按键状态
// =============================================================================

using System.Diagnostics;
using System.Text;
using ScannerHelper.Win32.Native;

namespace ScannerHelper.Win32;

/// <summary>
/// 中文：把按键翻译成字符。**非线程安全**——它自己维护按键状态，
///       必须与钩子回调在同一条线程上使用（见 InputCaptureThread）。
/// English: Translates keystrokes into characters. Not thread-safe: it maintains key state of its
///          own and must be used on the same thread as the hook callback (see InputCaptureThread).
/// </summary>
public sealed class KeyboardCharacterDecoder
{
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;
    private const int VkCapital = 0x14;
    private const int VkLeftShift = 0xA0;
    private const int VkRightShift = 0xA1;
    private const int VkLeftControl = 0xA2;
    private const int VkRightControl = 0xA3;
    private const int VkLeftMenu = 0xA4;
    private const int VkRightMenu = 0xA5;

    /// <summary>
    /// 中文：按下位。键盘状态数组里，最高位表示该键当前被按住。
    /// English: The down bit. In a key state array the high bit marks a key as currently held.
    /// </summary>
    private const byte KeyDownBit = 0x80;

    /// <summary>
    /// 中文：切换位。最低位表示切换态，CapsLock 用得到。
    /// English: The toggle bit. The low bit marks a toggled state, which CapsLock uses.
    /// </summary>
    private const byte KeyToggledBit = 0x01;

    /// <summary>
    /// 中文：键盘布局的缓存刷新间隔。理由见文件头。
    /// English: How often the cached keyboard layout is refreshed; see the file header.
    /// </summary>
    private static readonly TimeSpan LayoutCacheLifetime = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// 中文：
    ///   我们自己维护的键盘状态。
    ///
    ///   ★ 这个数组存在的唯一理由，是 GetKeyboardState 在拦截模式下会给出
    ///     **错误**的答案——我们吞掉的按键，系统并不知道被按过。
    /// English:
    ///   The key state we maintain ourselves.
    ///
    ///   It exists for one reason: GetKeyboardState gives the wrong answer under interception,
    ///   since the system does not know about the keystrokes we swallowed.
    /// </summary>
    private readonly byte[] _keyState = new byte[256];

    private readonly StringBuilder _decodeBuffer = new(8);

    private IntPtr _cachedLayout;
    private long _layoutCachedAtTicks;

    /// <summary>
    /// 中文：
    ///   构造解码器，并向系统询问 CapsLock 的初始状态。
    ///
    ///   只在构造时问这一次。之后靠观察到的按下事件自己翻转——理由见文件头：
    ///   拦截模式下系统的状态不再可信，而 CapsLock 是切换态，观察不到当前值。
    /// English:
    ///   Creates the decoder and asks the system for CapsLock's initial state.
    ///
    ///   Asked once, at construction; observed presses flip it thereafter. Per the file header,
    ///   the system's state is no longer trustworthy under interception, and CapsLock being a
    ///   toggle means its current value cannot be observed.
    /// </summary>
    public KeyboardCharacterDecoder()
    {
        if ((KeyboardDecoderNative.GetKeyState(VkCapital) & 1) != 0)
        {
            _keyState[VkCapital] = KeyToggledBit;
        }
    }

    /// <summary>
    /// 中文：
    ///   把一次按键翻译成字符，并顺带更新自己维护的修饰键状态。
    ///   输入：virtualKey 虚拟键码；scanCode 扫描码；isKeyUp 是否为弹起。
    ///   输出：对应的字符；修饰键、无对应字符的键、死键均返回 null。
    ///   步骤：
    ///     1. 先更新修饰键与 CapsLock 状态——**必须在解码之前**，因为
    ///        Shift 的按下要影响的正是它自己之后那些按键的解码；
    ///     2. 弹起不产出字符，直接返回；
    ///     3. 取（缓存的）前台窗口键盘布局；
    ///     4. 用**我们自己的**按键状态调用 ToUnicodeEx，并关掉内核状态修改；
    ///     5. 返回第一个字符，或 null。
    ///
    ///   ★ 步骤 1 必须在步骤 4 之前，而且必须包含**弹起**事件。Shift 弹起
    ///     若不记录，我们的状态里 Shift 会一直按着，之后所有字母都解成大写。
    ///
    ///   ★ 步骤 4 传入的是 <see cref="_keyState"/> 而不是 GetKeyboardState 的
    ///     结果。这是本文件存在的全部意义：我们吞掉的按键，系统并不知道
    ///     被按过，拿系统状态去解码会把大写全解成小写。
    ///
    ///   步骤 5 只取第一个字符：多字符的结果来自死键组合等情形，条码不会
    ///   出现。取第一个而不是拼起来，是因为拼起来会让一次按键产出两个字符，
    ///   而扫描缓冲区按"一次按键一个字符"计数——多出来的字符会让长度校验
    ///   莫名其妙地失败。
    /// English:
    ///   Translates one keystroke into a character while updating the modifier state.
    ///   Steps: (1) update modifiers and CapsLock first, since a Shift press must affect the
    ///   decoding of the keys that follow it; (2) key-ups produce no character; (3) take the
    ///   cached foreground keyboard layout; (4) call ToUnicodeEx with *our* key state and kernel
    ///   state modification disabled; (5) return the first character, or null.
    ///
    ///   Step 1 must precede step 4 and must include key-ups: without recording the Shift release
    ///   our state would hold Shift down forever and every later letter would decode as a capital.
    ///
    ///   Step 4 passes _keyState rather than GetKeyboardState's result, which is this file's
    ///   entire purpose: the system does not know about the keystrokes we swallowed, and decoding
    ///   from its state turns capitals into lowercase.
    ///
    ///   Step 5 takes only the first character. Multi-character results come from dead-key
    ///   combinations and the like, which barcodes do not produce. Taking the first rather than
    ///   concatenating avoids one keystroke yielding two characters, since the scan buffer counts
    ///   one character per keystroke and the extra would make length validation fail
    ///   inexplicably.
    /// </summary>
    public char? Decode(ushort virtualKey, ushort scanCode, bool isKeyUp)
    {
        // 步骤 1 / Step 1
        UpdateModifierState(virtualKey, isKeyUp);

        // 步骤 2 / Step 2
        if (isKeyUp)
        {
            return null;
        }

        // 步骤 3 / Step 3
        var layout = GetCachedForegroundLayout();

        // 步骤 4 / Step 4
        _decodeBuffer.Clear();
        var written = KeyboardDecoderNative.ToUnicodeEx(
            virtualKey,
            scanCode,
            _keyState,
            _decodeBuffer,
            _decodeBuffer.Capacity,
            KeyboardDecoderNative.TOUNICODE_DO_NOT_CHANGE_KERNEL_STATE,
            layout);

        // 步骤 5 / Step 5
        //
        // written 为负表示死键：它本身不产出字符，只影响下一个键。条码里
        // 不会出现，返回 null 即可——但绝不能当作"解码失败"去重试，
        // 重试会把死键状态又推进一次。
        //
        // A negative result means a dead key: it produces no character of its own and only
        // affects the next key. Barcodes do not contain them, so null is the right answer — but
        // it must never be treated as "decoding failed" and retried, since retrying would advance
        // the dead-key state again.
        return written > 0 ? _decodeBuffer[0] : null;
    }

    /// <summary>
    /// 中文：
    ///   重置自己维护的按键状态，只保留 CapsLock 的切换态。
    ///
    ///   从 PAUSED 恢复之后应当调用：暂停期间所有按键都直接放行，我们没有
    ///   观察到它们的弹起，因此记录下来的修饰键状态很可能是错的——例如
    ///   暂停前 Shift 正按着，恢复后我们仍以为它按着，于是之后所有字母
    ///   都解成大写。
    ///
    ///   CapsLock 的切换态保留，因为它不受"错过了弹起"的影响。
    /// English:
    ///   Resets the tracked key state, keeping only CapsLock's toggle.
    ///
    ///   Call it after resuming from PAUSED: while paused every keystroke passed straight through
    ///   and their releases went unobserved, so the recorded modifier state is likely wrong — with
    ///   Shift held at the moment of pausing, we would still believe it held on resume and decode
    ///   every later letter as a capital.
    ///
    ///   CapsLock's toggle survives, being unaffected by a missed release.
    /// </summary>
    public void ResetState()
    {
        var capsLock = _keyState[VkCapital];
        Array.Clear(_keyState);
        _keyState[VkCapital] = capsLock;
    }

    /// <summary>
    /// 中文：
    ///   更新自己维护的修饰键与 CapsLock 状态。
    ///
    ///   ★ 左右修饰键要同时更新**具体键**和**通用键**。
    ///
    ///     钩子报告的是 VK_LSHIFT（0xA0），而 ToUnicodeEx 查的是 VK_SHIFT（0x10）。
    ///     只设具体键的话，解码时查通用键查不到，Shift 等于没按——大写全变
    ///     小写。这与 Task 4a 发现的"两条通道虚拟键码不一致"是同一个根源：
    ///     Windows 在不同层面用不同粒度表示同一个键。
    /// English:
    ///   Updates the tracked modifier and CapsLock state.
    ///
    ///   Left and right modifiers must update both the specific and the generic key. The hook
    ///   reports VK_LSHIFT (0xA0) while ToUnicodeEx consults VK_SHIFT (0x10), so setting only the
    ///   specific one leaves the generic lookup empty, Shift counts as unpressed, and capitals
    ///   become lowercase. This shares a root with Task 4a's finding that the two channels
    ///   disagree about virtual keys: Windows represents one key at different granularities in
    ///   different layers.
    /// </summary>
    private void UpdateModifierState(ushort virtualKey, bool isKeyUp)
    {
        // CapsLock 是切换态：按下时翻转，弹起不做任何事。
        // CapsLock is a toggle: flip on the press and do nothing on the release.
        if (virtualKey == VkCapital)
        {
            if (!isKeyUp)
            {
                _keyState[VkCapital] ^= KeyToggledBit;
            }

            return;
        }

        if (!IsModifier(virtualKey))
        {
            return;
        }

        SetDown(virtualKey, !isKeyUp);

        var generic = virtualKey switch
        {
            VkLeftShift or VkRightShift => VkShift,
            VkLeftControl or VkRightControl => VkControl,
            VkLeftMenu or VkRightMenu => VkMenu,
            _ => 0,
        };

        if (generic == 0)
        {
            return;
        }

        // 通用键的状态是"左右任一按着即为按着"。只看当前这一个键的话，
        // 松开右 Shift 会把仍然按着的左 Shift 也一并清掉。
        // The generic key is down while *either* side is. Looking only at this key would clear a
        // still-held left Shift when the right one is released.
        var eitherSideDown = generic switch
        {
            VkShift => IsDown(VkLeftShift) || IsDown(VkRightShift),
            VkControl => IsDown(VkLeftControl) || IsDown(VkRightControl),
            _ => IsDown(VkLeftMenu) || IsDown(VkRightMenu),
        };

        SetDown((ushort)generic, eitherSideDown);
    }

    private static bool IsModifier(ushort virtualKey)
        => virtualKey is VkShift or VkControl or VkMenu
            or VkLeftShift or VkRightShift
            or VkLeftControl or VkRightControl
            or VkLeftMenu or VkRightMenu;

    private bool IsDown(int virtualKey) => (_keyState[virtualKey] & KeyDownBit) != 0;

    private void SetDown(ushort virtualKey, bool isDown)
    {
        if (isDown)
        {
            _keyState[virtualKey] |= KeyDownBit;
        }
        else
        {
            _keyState[virtualKey] &= unchecked((byte)~KeyDownBit);
        }
    }

    /// <summary>
    /// 中文：
    ///   取前台窗口的键盘布局，带缓存。
    ///
    ///   解码发生在钩子回调路径上，规格 §19 要求那里极快。三次 P/Invoke 本身
    ///   很便宜，但按每次按键计就没必要——一枪扫描几十个字符，布局在这几十
    ///   毫秒里不可能变。
    ///
    ///   刷新间隔 500 毫秒：远大于一次扫描的时长，因此一枪之内布局恒定；
    ///   又远小于人切换输入法之后开始打字的时间，因此切换能被及时跟上。
    /// English:
    ///   Returns the foreground window's keyboard layout, cached.
    ///
    ///   Decoding sits on the hook callback path, which spec §19 requires to be extremely fast.
    ///   Three P/Invokes are cheap in themselves but pointless per keystroke: a scan is dozens of
    ///   characters and the layout cannot change within those few dozen milliseconds.
    ///
    ///   The 500 ms interval is far longer than a scan, so the layout is constant within one, and
    ///   far shorter than the gap between switching input method and starting to type, so a switch
    ///   is picked up in time.
    /// </summary>
    private IntPtr GetCachedForegroundLayout()
    {
        var now = Stopwatch.GetTimestamp();
        var age = Stopwatch.GetElapsedTime(_layoutCachedAtTicks, now);

        if (_cachedLayout != IntPtr.Zero && age < LayoutCacheLifetime)
        {
            return _cachedLayout;
        }

        var foregroundWindow = KeyboardDecoderNative.GetForegroundWindow();
        var threadId = foregroundWindow == IntPtr.Zero
            ? 0
            : KeyboardDecoderNative.GetWindowThreadProcessId(foregroundWindow, IntPtr.Zero);

        _cachedLayout = KeyboardDecoderNative.GetKeyboardLayout(threadId);
        _layoutCachedAtTicks = now;

        return _cachedLayout;
    }
}
