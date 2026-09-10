// =============================================================================
// TrayIcon.cs
//
// 中文：
//   托盘图标（规格 §11.9）。
//
//   ★ 规格给了它一条硬性限制：**绝不能成为"程序完全隐形"的入口**。
//
//     §11.9 原文写着它"不得提供一条让运行中的程序变得完全不可见的路"。所以这里
//     的右键菜单里**没有**"最小化到托盘"那一项，而且关掉窗口走的仍然是那条带
//     确认框的退出路。这不是保守：本程序会把扫码枪的内容拦下来再发出去，一个
//     看不见却在工作的程序，工人无法判断"现在到底是不是它在起作用"。
//
//     它的定位是**第二块状态显示**，不是第二个隐藏处。
//
//   ★ 图标本身带状态，而且不只靠颜色。
//
//     与主界面同一套色板（规格 §11.2），并且每种状态另有一个形状：
//       SN   实心圆      SKU  实心方
//       暂停 两道竖杠    错误 感叹号
//       未连接 空心圆加一道斜杠
//
//     16 像素的托盘里，颜色最先被认出来，但红绿色觉障碍约占男性 8%，而 SN 的绿
//     和错误的红会先后出现在同一个位置上。形状让这个区分在没有颜色时依然成立
//     ——这与主界面上那道白色斜条纹是同一条原则。
//
//   ★ 用 WinForms 的 NotifyIcon，而不是自己 P/Invoke Shell_NotifyIcon。
//
//     WinForms 是 .NET 桌面运行时自带的，不引入任何**外部**依赖——仓库 IT 按哈希
//     把可执行文件加进白名单（规格 §22.1），关键是不新增要下载的东西，而不是
//     不引用框架里已有的程序集。自己写 Shell_NotifyIcon 还要额外处理图标句柄、
//     任务栏重建消息（explorer.exe 崩溃重启之后托盘图标会消失）等一堆细节，
//     那些 NotifyIcon 已经处理好了。
//
// English:
//   The tray icon (spec §11.9).
//
//   The spec sets one hard limit: it must never become a way for the running application to be
//   completely invisible. §11.9 says it "must not provide a way for the running application to
//   become completely invisible", so the context menu has no "minimize to tray" and closing the
//   window still goes through the confirmed exit. Not conservatism: this program intercepts scanner
//   content and re-emits it, and an invisible but working program leaves the operator unable to
//   tell whether it is the thing acting. The icon is a second status display, not a second place to
//   hide.
//
//   The icon carries state, and not by color alone. It uses the main window's palette (spec §11.2)
//   with a distinct shape per state: a filled circle for SN, a filled square for SKU, two bars for
//   paused, an exclamation for an error, a slashed hollow circle for disconnected. At sixteen
//   pixels color registers first, but red-green color-vision deficiency affects about 8% of men and
//   SN's green and the error's red appear one after another in the same place. Shape keeps the
//   distinction alive without color — the same principle as the white hazard stripe in the main
//   window.
//
//   WinForms' NotifyIcon rather than hand-written Shell_NotifyIcon: WinForms ships with the .NET
//   desktop runtime and adds no external dependency — warehouse IT allow-lists by hash (spec
//   §22.1), and what matters is adding nothing to download, not avoiding assemblies already in the
//   framework. Writing Shell_NotifyIcon directly would also mean handling icon handles and the
//   taskbar-recreated message (tray icons vanish when explorer.exe restarts), which NotifyIcon
//   already handles.
//
// 包含的成员 / Members in this file:
//   Update                   按状态刷新图标与提示
//   ShowRequested 等事件     菜单项
// =============================================================================

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ScannerHelper.App.Localization;
using ScannerHelper.Core.Domain;

namespace ScannerHelper.App;

/// <summary>
/// 中文：托盘图标（规格 §11.9）。
/// English: The tray icon (spec §11.9).
/// </summary>
public sealed class TrayIcon : IDisposable
{
    /// <summary>
    /// 中文：画布尺寸。画大再交给 Windows 缩，比直接画 16 像素在高分屏上清楚得多。
    /// English: The canvas size. Drawing large and letting Windows scale down is far crisper on a
    ///          high-DPI display than drawing sixteen pixels directly.
    /// </summary>
    private const int IconSize = 32;

    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _showItem;
    private readonly ToolStripMenuItem _pauseItem;
    private readonly ToolStripMenuItem _settingsItem;
    private readonly ToolStripMenuItem _exitItem;

    private Icon? _currentIcon;
    private string? _currentSignature;
    private bool _isDisposed;

    /// <summary>
    /// 中文：建好托盘图标。
    /// English: Creates the tray icon.
    /// </summary>
    public TrayIcon()
    {
        _showItem = new ToolStripMenuItem();
        _showItem.Click += (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty);

        _pauseItem = new ToolStripMenuItem();
        _pauseItem.Click += (_, _) => PauseToggleRequested?.Invoke(this, EventArgs.Empty);

        _settingsItem = new ToolStripMenuItem();
        _settingsItem.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);

        _exitItem = new ToolStripMenuItem();
        _exitItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        var menu = new ContextMenuStrip();
        menu.Items.Add(_showItem);
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_settingsItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_exitItem);

        // ★ 菜单里**没有**"最小化到托盘"。规格 §11.9 不许托盘成为让程序完全
        //   隐形的入口，而那一项恰恰就是那条路。
        // There is deliberately no "minimize to tray": spec §11.9 forbids the tray from being a way
        // to make the program completely invisible, and that item is precisely that way.

        _notifyIcon = new NotifyIcon
        {
            ContextMenuStrip = menu,
            Visible = true,
        };

        _notifyIcon.DoubleClick += (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>中文：把窗口叫出来。 English: Bring the window forward.</summary>
    public event EventHandler? ShowRequested;

    /// <summary>中文：暂停或恢复。 English: Pause or resume.</summary>
    public event EventHandler? PauseToggleRequested;

    /// <summary>中文：打开设置。 English: Open settings.</summary>
    public event EventHandler? SettingsRequested;

    /// <summary>中文：退出程序。 English: Exit the application.</summary>
    public event EventHandler? ExitRequested;

    /// <summary>
    /// 中文：
    ///   按当前状态刷新图标与提示文字。
    ///
    ///   ★ 状态没变就什么都不做。这个方法由界面的秒级心跳调用，而每次都重画图标
    ///     意味着每秒创建并销毁一个 GDI 图标句柄——句柄泄漏在托盘这种"跑一整天"
    ///     的东西上会真的攒出问题，而且表现是几小时后图标变成空白，谁也想不到
    ///     是这里。
    /// English:
    ///   Refreshes the icon and tooltip from the current state.
    ///
    ///   Nothing happens when the state is unchanged. This is called by the UI's one-second
    ///   heartbeat, and redrawing every time would create and destroy a GDI icon handle every
    ///   second — a handle leak genuinely accumulates in something that runs all day, and it
    ///   presents as the icon going blank hours later, which nobody would trace back to here.
    /// </summary>
    public void Update(SessionSnapshot snapshot)
    {
        if (_isDisposed)
        {
            return;
        }

        var state = Classify(snapshot);
        var strings = Localizer.Strings;

        _showItem.Text = strings["Expand"];
        _pauseItem.Text = strings[snapshot.IsPaused ? "Resume" : "Pause"];
        _settingsItem.Text = strings["Settings"];
        _exitItem.Text = strings["ExitConfirm"];

        var signature = $"{state}|{Localizer.Current}|{snapshot.PortName}|{snapshot.LastScanRawCode}";

        if (signature == _currentSignature)
        {
            return;
        }

        _currentSignature = signature;
        _notifyIcon.Text = BuildTooltip(snapshot, state);

        var previous = _currentIcon;
        _currentIcon = CreateIcon(state);
        _notifyIcon.Icon = _currentIcon;

        // ★ 先换上新的再销毁旧的。反过来的话，两者之间那一瞬托盘指着一个已经
        //   释放的句柄，图标会闪一下空白。
        // Swap first, destroy second. The other order leaves the tray pointing at a freed handle
        // for an instant, and the icon flashes blank.
        previous?.Dispose();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;

        // ★ 必须显式隐藏再释放。只释放不隐藏的话，那个图标会**留在托盘里**
        //   直到鼠标碰它一下才消失——工人看到的是"关了程序但图标还在"，
        //   而这与规格 §19 要求的干净收尾正好相反。
        // Hide explicitly before disposing. Disposing alone leaves the icon sitting in the tray
        // until the mouse happens to pass over it, and what the operator sees is "I closed it but
        // the icon is still there" — the opposite of spec §19's clean shutdown.
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _currentIcon?.Dispose();
    }

    /// <summary>
    /// 中文：
    ///   状态的优先级与主界面**完全一致**：未连接 → 待决错误 → 暂停 → 模式。
    ///   两处显示同一件事，绝不能对"现在最要紧的是什么"给出不同答案。
    /// English:
    ///   The same priority as the main window: disconnected, pending error, paused, mode. Two
    ///   displays of one thing must never disagree about what matters most right now.
    /// </summary>
    private static TrayState Classify(SessionSnapshot snapshot)
        => !snapshot.IsConnected ? TrayState.Disconnected
            : snapshot.PendingErrorRawCode is not null ? TrayState.Error
            : snapshot.IsPaused ? TrayState.Paused
            : snapshot.Mode == ScanMode.Sku ? TrayState.Sku
            : TrayState.Sn;

    private static string BuildTooltip(SessionSnapshot snapshot, TrayState state)
    {
        var strings = Localizer.Strings;

        var headline = state switch
        {
            TrayState.Disconnected => strings["Disconnected"],
            TrayState.Error => strings["ErrorHeading"],
            TrayState.Paused => strings["PausedHeading"],
            TrayState.Sku => strings["ModeSku"],
            _ => strings["ModeSn"],
        };

        var detail = snapshot.LastScanRawCode ?? strings["NoScanYet"];

        // 托盘提示有 127 个字符的上限，超了整条都不显示——不是截断，是**什么都不显示**。
        // A tray tooltip is capped at 127 characters and an overlong one shows nothing at all
        // rather than being truncated.
        var text = $"{strings["AppTitle"]} — {headline}\n{detail}";

        return text.Length <= 127 ? text : text[..127];
    }

    /// <summary>
    /// 中文：
    ///   按状态画一个图标：底色 + 一个不依赖颜色的形状。
    ///
    ///   ★ GetHicon 拿到的是一个**非托管**图标句柄，Icon 包装它之后仍然要显式
    ///     DestroyIcon，否则每换一次状态就漏一个 GDI 句柄。这里先把位图画好、
    ///     复制成托管的 Icon，再立刻销毁那个句柄——之后的生命周期就归 GC 管了。
    /// English:
    ///   Draws a state icon: a background color plus a shape that does not depend on color.
    ///
    ///   GetHicon returns an unmanaged icon handle that must be destroyed explicitly even after
    ///   Icon wraps it, or every state change leaks a GDI handle. The bitmap is drawn, cloned into
    ///   a managed Icon, and the handle destroyed immediately; the lifetime after that belongs to
    ///   the GC.
    /// </summary>
    private static Icon CreateIcon(TrayState state)
    {
        using var bitmap = new Bitmap(IconSize, IconSize);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);

            using var background = new SolidBrush(BackgroundOf(state));
            graphics.FillEllipse(background, 0, 0, IconSize - 1, IconSize - 1);

            DrawShape(graphics, state);
        }

        var handle = bitmap.GetHicon();

        try
        {
            using var unmanaged = Icon.FromHandle(handle);
            return (Icon)unmanaged.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    /// <summary>
    /// 中文：与主界面同一套色板（规格 §11.2）。两处用不同的颜色表示同一个状态，
    ///       等于让工人记两套对应关系。
    /// English: The main window's palette (spec §11.2). Different colors for one state in two
    ///          places would make the operator learn two mappings.
    /// </summary>
    private static Color BackgroundOf(TrayState state)
        => state switch
        {
            TrayState.Sn => Color.FromArgb(0x1B, 0x7A, 0x3D),
            TrayState.Sku => Color.FromArgb(0x1B, 0x5F, 0xA8),
            TrayState.Error => Color.FromArgb(0xB3, 0x26, 0x1E),
            _ => Color.FromArgb(0x41, 0x4B, 0x56),
        };

    /// <summary>
    /// 中文：
    ///   每种状态一个形状——16 像素的托盘里，这是颜色之外的第二条线索。
    /// English:
    ///   One shape per state — in a sixteen-pixel tray this is the cue besides color.
    /// </summary>
    private static void DrawShape(Graphics graphics, TrayState state)
    {
        using var white = new SolidBrush(Color.White);
        using var pen = new Pen(Color.White, 3f);

        switch (state)
        {
            case TrayState.Sn:
                // 实心圆 / a filled circle
                graphics.FillEllipse(white, 10, 10, 12, 12);
                break;

            case TrayState.Sku:
                // 实心方 / a filled square
                graphics.FillRectangle(white, 10, 10, 12, 12);
                break;

            case TrayState.Paused:
                // 两道竖杠 / two bars
                graphics.FillRectangle(white, 10, 9, 4, 14);
                graphics.FillRectangle(white, 18, 9, 4, 14);
                break;

            case TrayState.Error:
                // 感叹号 / an exclamation mark
                graphics.FillRectangle(white, 14, 7, 4, 12);
                graphics.FillEllipse(white, 14, 21, 4, 4);
                break;

            default:
                // 空心圆加一道斜杠 / a slashed hollow circle
                graphics.DrawEllipse(pen, 8, 8, 16, 16);
                graphics.DrawLine(pen, 10, 22, 22, 10);
                break;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);

    /// <summary>
    /// 中文：托盘要显示的五种状态。与主界面的五种一一对应。
    /// English: The five states the tray shows, one for each of the main window's.
    /// </summary>
    private enum TrayState
    {
        Sn,
        Sku,
        Paused,
        Error,
        Disconnected,
    }
}
