// =============================================================================
// RawInputDeviceResolver.cs
//
// 中文：
//   把 Raw Input 的设备句柄解析成可读、可持久化的设备身份（规格 §6）。
//
//   设备句柄本身只在**本次会话**有效：拔掉再插上，同一台设备会拿到一个不同的
//   句柄。所以绑定不能存句柄，得存设备路径这类跨会话稳定的东西——这正是
//   4a 的第 4 个问题要实测确认的。
//
//   设备路径的两种典型形状（后者见规格假设 A4）：
//     \\?\HID#VID_05E0&PID_1200#7&2e0e0f3a&0&0000#{884b96c3-...}   USB 扫码枪
//     \\?\ACPI#PNP0303#4&1cd7c62c&0#{884b96c3-...}                 笔记本内置键盘
//
//   ★ 内置键盘不是 USB HID 设备，因此**没有 VID/PID**。
//     "取 VID/PID 来区分设备"这个想当然的做法，在笔记本上对内置键盘直接
//     失效。设备路径是唯一对两者都成立的身份，这也是规格 §6 要求"优先使用
//     最具体的稳定身份"的实际含义。
//
//   友好名从注册表取，而不是从设备路径猜。同一型号的多把扫码枪路径不同、
//   友好名却完全相同，所以友好名**只用于显示给工人看，绝不参与匹配**——
//   规格 §6 提到的"多个一模一样的 HID Keyboard Device"就是这个问题。
//
// English:
//   Resolves a Raw Input device handle into a readable, persistable identity (spec §6).
//
//   The handle itself is valid for this session only: unplug and replug and the same
//   device gets a different one. Binding therefore cannot store a handle and must
//   store something stable across sessions, such as the device path — which is exactly
//   what 4a's fourth question exists to confirm empirically.
//
//   Two typical shapes of device path, the second per spec assumption A4:
//     \\?\HID#VID_05E0&PID_1200#7&2e0e0f3a&0&0000#{884b96c3-...}   a USB scanner
//     \\?\ACPI#PNP0303#4&1cd7c62c&0#{884b96c3-...}                 a laptop keyboard
//
//   A built-in keyboard is not a USB HID device and therefore has no VID/PID. The
//   obvious-looking approach of telling devices apart by VID/PID simply does not work
//   for the internal keyboard on a laptop. The device path is the only identity that
//   holds for both, which is what spec §6's "favor the most specific stable identity"
//   means in practice.
//
//   Friendly names come from the registry rather than being guessed from the path.
//   Several scanners of one model have different paths but identical friendly names,
//   so the name is for display only and never participates in matching — the "several
//   identical HID Keyboard Device entries" problem spec §6 raises.
//
// 包含的成员 / Members in this file:
//   Resolve             句柄 → 设备身份（带缓存）
//   EnumerateKeyboards  枚举当前连接的全部键盘类设备
//   ReadDeviceName      读取设备接口路径
//   ExtractHexId        从路径中取 VID / PID
//   ExtractSerialNumber 从路径中取可能的序列号
//   ReadFriendlyName    从注册表读友好名
// =============================================================================

using System.Runtime.InteropServices;
using Microsoft.Win32;
using ScannerHelper.Core.Domain;
using ScannerHelper.Win32.Native;

namespace ScannerHelper.Win32;

/// <summary>
/// 中文：把 Raw Input 设备句柄解析为 <see cref="ScannerDeviceIdentity"/>。
/// English: Resolves Raw Input device handles into <see cref="ScannerDeviceIdentity"/>.
/// </summary>
public sealed class RawInputDeviceResolver
{
    /// <summary>
    /// 中文：
    ///   句柄到身份的缓存。
    ///
    ///   缓存不是可选的优化。解析一次身份要读注册表，而设备句柄会跟着**每一个**
    ///   按键事件到来——扫一枪就是几十次。在钩子和消息循环所在的那个线程上
    ///   每个按键读一次注册表，正是规格 §19 明令禁止的那类操作。
    ///
    ///   句柄在本次会话内稳定，所以缓存是安全的；设备重新插拔会得到新句柄，
    ///   于是自然地重新解析一次，不需要失效逻辑。
    /// English:
    ///   Handle-to-identity cache. Not an optional optimization: resolving an identity
    ///   reads the registry, and a device handle arrives with *every* key event —
    ///   dozens per scan. Reading the registry per keystroke on the thread that owns
    ///   the hook and the message loop is precisely what spec §19 forbids.
    ///
    ///   Handles are stable within a session, so caching is safe; a replugged device
    ///   gets a new handle and is naturally re-resolved, needing no invalidation logic.
    /// </summary>
    private readonly Dictionary<nint, ScannerDeviceIdentity> _identityCache = [];

    /// <summary>
    /// 中文：
    ///   把设备句柄解析为身份。
    ///   输入：deviceHandle Raw Input 报告的设备句柄。
    ///   输出：设备身份；无法取得设备名时各字段均为 null。
    ///   步骤：
    ///     1. 命中缓存则直接返回；
    ///     2. 读取设备接口路径；
    ///     3. 从路径中提取 VID、PID 与可能的序列号；
    ///     4. 从注册表读友好名；
    ///     5. 存入缓存并返回。
    ///
    ///   步骤 2 失败（设备恰好在此刻被拔掉）时不抛异常，而是返回一个全空的
    ///   身份。诊断工具的职责是如实记录观测到的现象，包括"这台设备的身份
    ///   查不到"——为此让整个测量中断是本末倒置。
    /// English:
    ///   Resolves a device handle into an identity, with every field null when the
    ///   device name cannot be read.
    ///   Steps: (1) return a cached identity; (2) read the interface path; (3) extract
    ///   VID, PID and any serial number; (4) read the friendly name from the registry;
    ///   (5) cache and return.
    ///
    ///   Step 2 does not throw when it fails — the device having just been unplugged,
    ///   say — and returns an empty identity instead. A diagnostic tool's job is to
    ///   record what it observed, including "this device's identity could not be
    ///   read"; aborting the whole measurement over it would invert the priorities.
    /// </summary>
    public ScannerDeviceIdentity Resolve(nint deviceHandle)
    {
        // 步骤 1 / Step 1
        if (_identityCache.TryGetValue(deviceHandle, out var cached))
        {
            return cached;
        }

        // 步骤 2 / Step 2
        var devicePath = ReadDeviceName(deviceHandle);

        // 步骤 3、4 / Steps 3–4
        var identity = new ScannerDeviceIdentity(
            DevicePath: devicePath,
            VendorId: ExtractHexId(devicePath, "VID_"),
            ProductId: ExtractHexId(devicePath, "PID_"),
            FriendlyName: ReadFriendlyName(devicePath),
            SerialNumber: ExtractSerialNumber(devicePath));

        // 步骤 5 / Step 5
        _identityCache[deviceHandle] = identity;
        return identity;
    }

    /// <summary>
    /// 中文：
    ///   枚举当前连接的全部键盘类设备。
    ///   输入：无。输出：句柄与身份的列表。
    ///
    ///   4a 的第 4 与第 6 个问题靠它回答：重新插拔或重启之后再枚举一次，
    ///   对比设备路径是否保持不变；以及笔记本内置键盘是否确实被 Raw Input
    ///   枚举出来、它的设备名长什么样。
    ///
    ///   采用"先问大小、再取内容"的两次调用模式，这是 Raw Input 全部返回
    ///   变长数据的 API 的固定用法。
    /// English:
    ///   Enumerates every currently connected keyboard-class device as handle and
    ///   identity pairs.
    ///
    ///   This answers 4a's fourth and sixth questions: enumerate again after a replug
    ///   or reboot and compare whether device paths held; and confirm that a laptop's
    ///   built-in keyboard really is enumerated by Raw Input, and what its device name
    ///   looks like.
    ///
    ///   Uses the ask-for-the-size-then-fetch two-call pattern that every variable
    ///   length Raw Input API requires.
    /// </summary>
    public IReadOnlyList<(nint Handle, ScannerDeviceIdentity Identity)> EnumerateKeyboards()
    {
        uint deviceCount = 0;
        var entrySize = (uint)Marshal.SizeOf<RawInputNative.RAWINPUTDEVICELIST>();

        if (RawInputNative.GetRawInputDeviceList(IntPtr.Zero, ref deviceCount, entrySize)
            == unchecked((uint)-1) || deviceCount == 0)
        {
            return [];
        }

        var listBuffer = Marshal.AllocHGlobal((int)(entrySize * deviceCount));
        try
        {
            var returned = RawInputNative.GetRawInputDeviceList(listBuffer, ref deviceCount, entrySize);
            if (returned == unchecked((uint)-1))
            {
                return [];
            }

            var keyboards = new List<(nint, ScannerDeviceIdentity)>();
            for (var index = 0; index < returned; index++)
            {
                var entry = Marshal.PtrToStructure<RawInputNative.RAWINPUTDEVICELIST>(
                    listBuffer + (int)(index * entrySize));

                if (entry.dwType != RawInputNative.RIM_TYPEKEYBOARD)
                {
                    continue;
                }

                keyboards.Add((entry.hDevice, Resolve(entry.hDevice)));
            }

            return keyboards;
        }
        finally
        {
            Marshal.FreeHGlobal(listBuffer);
        }
    }

    /// <summary>
    /// 中文：
    ///   读取设备接口路径。
    ///   输入：deviceHandle 设备句柄。输出：路径，读不到时为 null。
    ///   同样是"先问大小、再取内容"的两次调用模式。
    /// English:
    ///   Reads the device interface path, or null when it cannot be read. The same
    ///   two-call size-then-fetch pattern.
    /// </summary>
    private static string? ReadDeviceName(nint deviceHandle)
    {
        uint characterCount = 0;
        if (RawInputNative.GetRawInputDeviceInfoW(
                deviceHandle, RawInputNative.RIDI_DEVICENAME, IntPtr.Zero, ref characterCount)
            == unchecked((uint)-1) || characterCount == 0)
        {
            return null;
        }

        // characterCount 是字符数，不是字节数；UTF-16 每字符两字节。
        // characterCount counts characters, not bytes; UTF-16 uses two per character.
        var nameBuffer = Marshal.AllocHGlobal((int)(characterCount * sizeof(char)));
        try
        {
            if (RawInputNative.GetRawInputDeviceInfoW(
                    deviceHandle, RawInputNative.RIDI_DEVICENAME, nameBuffer, ref characterCount)
                == unchecked((uint)-1))
            {
                return null;
            }

            return Marshal.PtrToStringUni(nameBuffer);
        }
        finally
        {
            Marshal.FreeHGlobal(nameBuffer);
        }
    }

    /// <summary>
    /// 中文：
    ///   从设备路径中取出 VID 或 PID 的四位十六进制值。
    ///   输入：devicePath 设备路径；prefix "VID_" 或 "PID_"。
    ///   输出：四位十六进制字符串；路径里没有该前缀时返回 null。
    ///
    ///   ★ 返回 null 是常态而非异常：笔记本内置键盘走 ACPI，路径里根本没有
    ///     VID/PID（规格假设 A4）。调用方绝不能假定这两项一定有值。
    /// English:
    ///   Extracts the four hex digits following "VID_" or "PID_" in a device path, or
    ///   null when the prefix is absent.
    ///
    ///   Null is the normal case rather than an error: a laptop's built-in keyboard
    ///   arrives over ACPI and its path contains no VID or PID at all (spec assumption
    ///   A4). Callers must never assume these are populated.
    /// </summary>
    private static string? ExtractHexId(string? devicePath, string prefix)
    {
        if (devicePath is null)
        {
            return null;
        }

        var prefixIndex = devicePath.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (prefixIndex < 0)
        {
            return null;
        }

        const int hexDigitCount = 4;
        var valueStart = prefixIndex + prefix.Length;
        if (valueStart + hexDigitCount > devicePath.Length)
        {
            return null;
        }

        var value = devicePath.Substring(valueStart, hexDigitCount);
        return value.All(Uri.IsHexDigit) ? value.ToUpperInvariant() : null;
    }

    /// <summary>
    /// 中文：
    ///   尝试从设备路径中取出序列号。
    ///   输入：devicePath 设备路径。输出：序列号，无法判定时返回 null。
    ///
    ///   判据是 Windows 设备实例 ID 的既有约定：路径第三段若**不含 &amp;**，
    ///   它就是设备自己上报的序列号；含 &amp; 则是 Windows 按拓扑位置生成的
    ///   实例编号，与设备个体无关。
    ///
    ///   例：
    ///     HID\VID_05E0&amp;PID_1200\7&amp;2e0e0f3a&amp;0&amp;0000   含 &amp; → 位置编号，不是序列号
    ///     HID\VID_05E0&amp;PID_1200\S1234567             不含 &amp; → 序列号
    ///
    ///   这是启发式判断，不是保证。序列号只在**确实存在时**提高匹配可靠性，
    ///   拿不到也不影响绑定——设备路径始终是主要身份（规格 §6）。
    /// English:
    ///   Attempts to extract a serial number from a device path, returning null when
    ///   none can be determined.
    ///
    ///   The test follows Windows' device instance ID convention: if the path's third
    ///   segment contains no '&', it is the serial number the device reported itself;
    ///   with an '&' it is an instance number Windows generated from topology, which
    ///   says nothing about the individual device.
    ///
    ///   This is a heuristic rather than a guarantee. A serial number improves matching
    ///   reliability when it genuinely exists, and its absence does not affect binding
    ///   — the device path remains the primary identity (spec §6).
    /// </summary>
    private static string? ExtractSerialNumber(string? devicePath)
    {
        var instanceId = ToDeviceInstanceId(devicePath);
        if (instanceId is null)
        {
            return null;
        }

        var segments = instanceId.Split('\\');
        if (segments.Length < 3)
        {
            return null;
        }

        var instanceSegment = segments[^1];
        return instanceSegment.Contains('&', StringComparison.Ordinal) || instanceSegment.Length == 0
            ? null
            : instanceSegment;
    }

    /// <summary>
    /// 中文：
    ///   从注册表读设备友好名。
    ///   输入：devicePath 设备路径。输出：友好名，读不到时返回 null。
    ///   步骤：
    ///     1. 把设备接口路径转换成设备实例 ID；
    ///     2. 打开 HKLM\SYSTEM\CurrentControlSet\Enum\&lt;实例 ID&gt;；
    ///     3. 优先取 FriendlyName，没有则取 DeviceDesc；
    ///     4. DeviceDesc 常形如 "@input.inf,%...%;HID Keyboard Device"，
    ///        取最后一个分号之后的部分。
    ///
    ///   读注册表可能因权限或设备恰好被拔掉而失败。这里一律吞掉异常返回 null：
    ///   友好名只用于显示，取不到不影响任何判断，为它让诊断工具崩掉是荒谬的。
    /// English:
    ///   Reads a device's friendly name from the registry, or null.
    ///   Steps: (1) convert the interface path into a device instance ID; (2) open
    ///   HKLM\SYSTEM\CurrentControlSet\Enum\&lt;instance id&gt;; (3) prefer FriendlyName,
    ///   falling back to DeviceDesc; (4) DeviceDesc usually looks like
    ///   "@input.inf,%...%;HID Keyboard Device", so take what follows the last
    ///   semicolon.
    ///
    ///   The read can fail on permissions or because the device was just unplugged.
    ///   Exceptions are swallowed and null returned: the name is for display only,
    ///   nothing depends on it, and crashing a diagnostic tool over it would be absurd.
    /// </summary>
    private static string? ReadFriendlyName(string? devicePath)
    {
        // 步骤 1 / Step 1
        var instanceId = ToDeviceInstanceId(devicePath);
        if (instanceId is null)
        {
            return null;
        }

        try
        {
            // 步骤 2 / Step 2
            using var deviceKey = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Enum\{instanceId}");

            if (deviceKey is null)
            {
                return null;
            }

            // 步骤 3 / Step 3
            if (deviceKey.GetValue("FriendlyName") is string friendlyName
                && !string.IsNullOrWhiteSpace(friendlyName))
            {
                return friendlyName;
            }

            if (deviceKey.GetValue("DeviceDesc") is not string deviceDescription
                || string.IsNullOrWhiteSpace(deviceDescription))
            {
                return null;
            }

            // 步骤 4 / Step 4
            var lastSemicolon = deviceDescription.LastIndexOf(';');
            return lastSemicolon >= 0 && lastSemicolon < deviceDescription.Length - 1
                ? deviceDescription[(lastSemicolon + 1)..]
                : deviceDescription;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
                                              or System.Security.SecurityException
                                              or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// 中文：
    ///   把设备接口路径转换成注册表里用的设备实例 ID。
    ///   输入：devicePath 形如 <c>\\?\HID#VID_05E0&amp;PID_1200#7&amp;abc&amp;0&amp;0000#{guid}</c>。
    ///   输出：形如 <c>HID\VID_05E0&amp;PID_1200\7&amp;abc&amp;0&amp;0000</c>，无法转换时 null。
    ///   步骤：
    ///     1. 去掉前缀 <c>\\?\</c> 或 <c>\??\</c>——两种形式都可能出现，
    ///        取决于 Windows 版本；
    ///     2. 去掉结尾的接口类 GUID（<c>#{...}</c>）；
    ///     3. 把 <c>#</c> 换成 <c>\</c>。
    /// English:
    ///   Converts a device interface path into the device instance ID the registry uses.
    ///   Steps: (1) strip the leading <c>\\?\</c> or <c>\??\</c> — both occur, depending
    ///   on the Windows version; (2) strip the trailing interface class GUID
    ///   (<c>#{...}</c>); (3) replace <c>#</c> with <c>\</c>.
    /// </summary>
    private static string? ToDeviceInstanceId(string? devicePath)
    {
        if (string.IsNullOrWhiteSpace(devicePath))
        {
            return null;
        }

        var working = devicePath;

        // 步骤 1 / Step 1
        if (working.StartsWith(@"\\?\", StringComparison.Ordinal)
            || working.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            working = working[4..];
        }

        // 步骤 2 / Step 2
        var guidStart = working.IndexOf("#{", StringComparison.Ordinal);
        if (guidStart >= 0)
        {
            working = working[..guidStart];
        }

        // 步骤 3 / Step 3
        working = working.Replace('#', '\\');

        return string.IsNullOrWhiteSpace(working) ? null : working;
    }
}
