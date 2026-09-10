// =============================================================================
// CommandSheet.cs
//
// 中文：
//   生成那两张贴在工位上的模式切换条码，用默认浏览器打开以便打印。
//
//   ★ 为什么是"生成一张网页让人打印"，而不是印在说明书里的一张图。
//
//     条码的内容（#SH:SN# / #SH:SKU#）由程序定义。印在文档里的图片会和代码
//     各自演化——某天前缀改了，墙上还贴着旧的那张，扫下去什么都不会发生，
//     而没有人知道该去更新墙上的纸。由程序**当场生成**，两者就不可能不一致。
//
//   ★ 为什么不做成程序里的一个打印对话框。
//
//     WPF 的打印栈要处理纸张、边距、预览，而这件事一年做一次。浏览器的打印
//     对话框已经把这些都做好了，且工人认得它。
//
//   ★ 这张纸是中英双语的，与界面语言无关。
//
//     它是个**实物**：印出来贴到墙上，之后可能被任何人看到——包括切换界面
//     语言的人早已不在场的时候。按当时的界面语言决定纸上印什么，等于让一张
//     长期存在的东西记住一个临时的选择。
//
//   ★ 条码下面必须印出人眼可读的内容。
//
//     纸会脏、会被磨花、会被撕掉一角。到那时唯一能判断"这张纸原本是哪一张"
//     的，就是印在下面的那行字。
//
// English:
//   Produces the two mode-switch sheets for the workstation and opens them in the default browser
//   for printing.
//
//   A generated page rather than an image in the manual, because the content (#SH:SN# / #SH:SKU#) is
//   defined by the program: a picture in a document evolves separately from the code, so the day the
//   prefix changes the wall still carries the old sheet, scanning it does nothing, and nobody knows
//   the paper needs updating. Generated on the spot, the two cannot disagree.
//
//   Not an in-app print dialog, because WPF's print stack means paper sizes, margins and previews
//   for something done once a year, while the browser's dialog already handles all of it and the
//   operator recognizes it.
//
//   The sheet is bilingual regardless of UI language: it is a physical object that goes on a wall
//   and may be read by anyone long after whoever picked the UI language has gone. Letting a
//   momentary setting decide what a long-lived object says is the wrong dependency.
//
//   The human-readable content is printed under each barcode, because paper gets dirty, scuffed and
//   torn — and that line is then the only way to tell which sheet this was.
//
// 包含的成员 / Members in this file:
//   Open   生成并打开
// =============================================================================

using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using ScannerHelper.Core.Input;
using ScannerHelper.Core.Printing;

namespace ScannerHelper.App;

/// <summary>
/// 中文：模式切换条码纸。
/// English: The mode-switch command sheets.
/// </summary>
public static class CommandSheet
{
    /// <summary>
    /// 中文：
    ///   一个模块印多宽，单位毫米。
    ///
    ///   ★ 0.4mm 是下限附近的安全值。再窄，普通仓库打印机的墨点扩散会把细条糊
    ///     在一起，条码在纸上看着好好的却扫不出来——而那时没有人会怀疑是打印机。
    /// English:
    ///   How wide one module prints, in millimetres. 0.4 mm sits safely near the lower limit: any
    ///   narrower and ink spread on an ordinary warehouse printer merges the thin bars, leaving a
    ///   barcode that looks fine on paper and does not scan — with nobody suspecting the printer.
    /// </summary>
    private const double ModuleMillimetres = 0.4;

    /// <summary>
    /// 中文：条码高度。够高，工人不用对得很准就能扫到。
    /// English: The bar height, tall enough that the operator need not aim precisely.
    /// </summary>
    private const double BarHeightMillimetres = 22;

    /// <summary>
    /// 中文：
    ///   两侧的静区宽度，单位是模块数。
    ///
    ///   ★ 静区不是留白好看。Code 128 规定两侧至少 10 个模块的空白，扫码枪靠它
    ///     判断条码从哪儿开始。省掉它，条码在屏幕上看着完整，扫起来时灵时不灵
    ///     ——取决于旁边正好有没有别的东西。
    /// English:
    ///   The quiet zone on each side, in modules. Not decorative margin: Code 128 requires at least
    ///   ten modules of blank on each side and the scanner uses it to find where the code begins.
    ///   Omit it and the barcode looks complete on screen while scanning intermittently, depending
    ///   on what happens to sit beside it.
    /// </summary>
    private const int QuietZoneModules = 12;

    /// <summary>
    /// 中文：
    ///   生成两张条码纸并用默认浏览器打开。
    ///   输出：出错时返回异常消息，成功返回 null。
    /// English:
    ///   Produces both sheets and opens them in the default browser, returning an error message on
    ///   failure and null on success.
    /// </summary>
    public static string? Open()
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "扫码助手-模式切换条码.html");
            File.WriteAllText(path, BuildHtml(), new UTF8Encoding(true));

            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return null;
        }
        catch (Exception exception)
        {
            return exception.Message;
        }
    }

    private static string BuildHtml()
    {
        var html = new StringBuilder(8192);

        html.Append("""
            <!doctype html>
            <html lang="zh-CN">
            <head>
            <meta charset="utf-8">
            <title>扫码助手 — 模式切换条码</title>
            <style>
              body { font-family: "Microsoft YaHei", "Segoe UI", sans-serif;
                     margin: 18mm 16mm; color: #111; }
              h1 { font-size: 16pt; margin: 0 0 2mm 0; }
              .lede { font-size: 10pt; color: #444; margin: 0 0 8mm 0; line-height: 1.6; }
              .sheet { border: 1.2pt solid #111; border-radius: 3mm;
                       padding: 7mm 6mm 5mm 6mm; margin-bottom: 8mm;
                       page-break-inside: avoid; text-align: center; }
              .label { font-size: 20pt; font-weight: 700; margin: 0 0 1mm 0; }
              .sub { font-size: 10pt; color: #444; margin: 0 0 5mm 0; }
              .code { font-family: Consolas, "Courier New", monospace;
                      font-size: 11pt; letter-spacing: 1px; margin-top: 2mm; }
              .note { font-size: 9pt; color: #555; line-height: 1.7;
                      border-top: 0.6pt solid #bbb; padding-top: 4mm; }
              @media print { .noprint { display: none; } }
              .noprint { background: #f2f2f2; border-radius: 2mm; padding: 4mm 5mm;
                         font-size: 10pt; margin-bottom: 8mm; }
            </style>
            </head>
            <body>
            <div class="noprint">按 Ctrl+P 打印这一页，剪开之后贴在工位上。
            Press Ctrl+P to print, then cut and tape these to the workstation.</div>
            <h1>扫码助手 · 模式切换条码 / Mode-switch sheets</h1>
            <p class="lede">
              扫一下就切换模式，不用碰键盘。这两枚条码不会被发送到业务软件里。<br>
              Scan one to switch mode without touching the keyboard.
              Neither code is ever sent to the business application.
            </p>

            """);

        AppendSheet(html, "切到 SN 模式", "SN MODE — 扫到的码原样发出 / send the code as-is",
            ScanCommand.SwitchToSnCode);

        AppendSheet(html, "切到 SKU 模式", "SKU MODE — 从码里截出 SKU / extract the SKU",
            ScanCommand.SwitchToSkuCode);

        html.Append("""
            <p class="note">
              ★ 暂停期间这两张纸不起作用，扫到的内容会原样发给业务软件。<br>
              &nbsp;&nbsp;&nbsp;While paused these sheets do nothing and their content is sent
              through unchanged — pausing means the program does not interfere, with no exceptions.
              <br><br>
              ★ 打印后请先扫一次试试。打印机墨点扩散可能让细条糊在一起，
              纸上看着好好的却扫不出来。<br>
              &nbsp;&nbsp;&nbsp;Test each sheet with the scanner after printing: ink spread can merge
              the thin bars, leaving a code that looks fine and does not scan.
            </p>
            </body>
            </html>
            """);

        return html.ToString();
    }

    private static void AppendSheet(StringBuilder html, string label, string subtitle, string content)
    {
        html.Append("<div class=\"sheet\">\n");
        html.Append(CultureInfo.InvariantCulture, $"  <p class=\"label\">{label}</p>\n");
        html.Append(CultureInfo.InvariantCulture, $"  <p class=\"sub\">{subtitle}</p>\n");
        html.Append(BuildSvg(content));
        html.Append(CultureInfo.InvariantCulture, $"  <p class=\"code\">{content}</p>\n");
        html.Append("</div>\n\n");
    }

    /// <summary>
    /// 中文：
    ///   把模块序列画成 SVG。宽度用毫米，因为这是要印在纸上的东西——
    ///   用像素的话，同一份文件在不同缩放、不同打印机上宽度都不一样，
    ///   而条码的物理宽度直接决定它扫不扫得出来。
    /// English:
    ///   Draws the module sequence as SVG, sized in millimetres because this is going onto paper:
    ///   in pixels the same file prints at different widths on different zoom levels and printers,
    ///   and a barcode's physical width decides whether it scans at all.
    /// </summary>
    private static string BuildSvg(string content)
    {
        var modules = Code128.Encode(content);
        var totalModules = modules.Sum() + (QuietZoneModules * 2);

        var width = totalModules * ModuleMillimetres;
        var svg = new StringBuilder(2048);

        svg.Append(CultureInfo.InvariantCulture,
            $"  <svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width:0.###}mm\" " +
            $"height=\"{BarHeightMillimetres:0.###}mm\" " +
            $"viewBox=\"0 0 {totalModules} 100\" preserveAspectRatio=\"none\">\n");

        // 背景必须显式画白。透明的 SVG 印在有底色的纸上，静区就不再是静区了。
        // The background is painted white explicitly: a transparent SVG on tinted paper leaves the
        // quiet zone no longer quiet.
        svg.Append(CultureInfo.InvariantCulture,
            $"    <rect x=\"0\" y=\"0\" width=\"{totalModules}\" height=\"100\" fill=\"#fff\"/>\n");

        var x = QuietZoneModules;
        var isBar = true;

        foreach (var moduleWidth in modules)
        {
            if (isBar)
            {
                svg.Append(CultureInfo.InvariantCulture,
                    $"    <rect x=\"{x}\" y=\"0\" width=\"{moduleWidth}\" height=\"100\" fill=\"#000\"/>\n");
            }

            x += moduleWidth;
            isBar = !isBar;
        }

        svg.Append("  </svg>\n");
        return svg.ToString();
    }
}
