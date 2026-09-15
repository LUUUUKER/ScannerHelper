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
//   ★ 印成**两张卡**，分页。
//
//     第一张是工位卡：切换模式的两枚码，工人一天扫几十次。
//     第二张是装机卡：扫码枪自己的配置码，只有装机和排障时才用。
//
//     分开不是为了排版好看。装机卡上的「恢复出厂设置」会把枪打回 USB 键盘模式
//     ——那恰恰是最危险的那个状态：本程序的串口一片安静、不报错，而条码原封
//     不动地进了业务软件。把它和工人天天扫的码并排贴在工位上，早晚有人扫错一张，
//     而扫错之后现场看不出任何异常。
//
// English:
//   ...printed as two cards on separate pages. The first is for the workstation: the two
//   mode-switch codes, scanned dozens of times a day. The second is for setup: the scanner's own
//   configuration codes, used only when installing or recovering.
//
//   The separation is not for tidiness. The setup card's "restore factory defaults" puts the scanner
//   back into USB keyboard mode — precisely the most dangerous state, where this program's serial
//   port falls silent without error while barcodes go straight into the business application. Taped
//   beside codes an operator scans all day, it would eventually be scanned by mistake, and nothing
//   on site would look wrong afterwards.
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
    ///   扫码枪自己的配置码（**Tera D5100 专用**）。
    ///
    ///   ★ 这些内容是从随枪说明书上的条码里读出来的，不是本程序定义的。
    ///     换一把别的枪，这四个字符串就毫无意义——甚至可能有别的含义。
    ///     所以卡片上必须印明型号，代码里也记在这儿。
    ///
    ///   ★ 改动这里之后必须**打印出来实扫验证**。Code 128 的校验位只能保证
    ///     "印出来的就是这段字符串"，保证不了"这段字符串是对的那一条"——
    ///     后者只有枪本身能回答。
    /// English:
    ///   The scanner's own configuration codes, specific to the Tera D5100.
    ///
    ///   These strings were read out of the barcodes in the scanner's printed manual; they are not
    ///   defined by this program. On a different scanner they mean nothing, or something else, which
    ///   is why the card states the model and why this is recorded here.
    ///
    ///   Any change here must be verified by printing and scanning. Code 128's check digit only
    ///   guarantees that what was printed is this string; whether this string is the right one can
    ///   be answered only by the scanner itself.
    /// </summary>
    private static readonly (string Content, string Label, string Detail, bool IsHazard)[] SetupCodes =
    [
        ("%%SpecCode39", "① 恢复出厂设置 / Restore Defaults",
            "把枪清回出厂状态。★ 执行后枪会退回 USB 键盘模式，必须接着走完 ②③④。",
            true),
        ("%%SpecCodeA8", "② 切到 2.4G 无线 / 2.4GHz Mode",
            "让枪走无线，而不是蓝牙或有线。", false),
        ("%%SpecCode99", "③ 配对 / Pairing",
            "把枪和接收器配上。扫之前先拔下接收器，扫完再插回去。", false),
        ("%%SpecCodeAE", "④ 切到虚拟串口 / USB-COM Mode",
            "★ 最关键的一张。不扫它，枪就一直当键盘用——本程序收不到任何数据，"
            + "而条码会原封不动地进业务软件。扫完拔插一次接收器。", false),
    ];

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
        var path = Path.Combine(Path.GetTempPath(), "扫码助手-模式切换条码.html");

        if (Write(path) is { } failure)
        {
            return failure;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return null;
        }
        catch (Exception exception)
        {
            return exception.Message;
        }
    }

    /// <summary>
    /// 中文：
    ///   把条码页写到指定路径，不打开浏览器。
    ///   输出：出错时返回异常消息，成功返回 null。
    ///
    ///   ★ 打包脚本用它把同一份内容转成随包发的 PDF。
    ///
    ///     PDF 必须由**程序本身**产出，不能是谁手工导出一次然后提交上去。条码的
    ///     内容（#SH:SN# 这些）由代码定义，而一份手工导出的 PDF 会和代码各自
    ///     演化——某天前缀改了，随包发的 PDF 还是旧的，印出来贴到墙上扫下去
    ///     什么都不会发生，且没有人知道该更新它。这正是本文件开头拒绝"把条码
    ///     印进文档"的那条理由，对 PDF 同样适用。
    /// English:
    ///   Writes the sheet to a path without opening a browser.
    ///
    ///   The packaging script uses this to turn the same content into the PDF that ships with the
    ///   release. That PDF has to come from the program rather than from someone exporting it once
    ///   by hand: the barcode content (#SH:SN# and the rest) is defined in code, and a hand-exported
    ///   PDF evolves separately — the day the prefix changes, the shipped PDF is still the old one,
    ///   printing it puts a sheet on the wall that does nothing when scanned, and nobody knows it
    ///   needs updating. It is the reason this file opens with for not printing barcodes into
    ///   documents, and it applies to the PDF just the same.
    /// </summary>
    public static string? Write(string path)
    {
        try
        {
            File.WriteAllText(path, BuildHtml(), new UTF8Encoding(true));
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
              .pagebreak { page-break-before: always; }
              .setup { border: 0.8pt solid #444; border-radius: 2mm;
                       padding: 5mm 5mm 3mm 5mm; margin-bottom: 5mm;
                       page-break-inside: avoid; text-align: center; }
              .setup.hazard { border: 2pt solid #B3261E; background: #FDF2F1; }
              .setup .label { font-size: 13pt; font-weight: 700; margin: 0 0 1mm 0; }
              .setup.hazard .label { color: #B3261E; }
              .setup .detail { font-size: 9pt; color: #444; line-height: 1.6;
                               margin: 0 0 3mm 0; text-align: left; }
              .model { font-size: 9pt; color: #B3261E; font-weight: 700;
                       border: 1pt solid #B3261E; border-radius: 2mm;
                       padding: 3mm 4mm; margin: 0 0 6mm 0; line-height: 1.6; }
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
            <div class="pagebreak"></div>
            <h1>扫码枪配置码 / Scanner setup codes</h1>
            <p class="lede">
              装机、或者枪被误设置之后恢复用。平时用不到。<br>
              For installing a scanner, or recovering one whose settings were changed. Not used
              day to day.
            </p>
            <p class="model">
              ★ 这一页只适用于 <b>Tera D5100</b>。别的型号扫这些码没有意义，甚至可能设成别的东西。<br>
              This page applies to the <b>Tera D5100</b> only. On another model these codes do
              nothing, or something else.
              <br><br>
              ★ 这一页<b>不要贴在工位上</b>，跟装机工具放在一起。①「恢复出厂设置」会把枪打回键盘模式
              ——那时本程序收不到任何数据、也不会报错，而条码会原封不动地进业务软件。<br>
              Keep this page <b>away from the workstation</b>, with the setup kit. Code ① returns the
              scanner to keyboard mode, where this program receives nothing and reports nothing while
              barcodes go straight into the business application.
            </p>

            """);

        AppendSetupCards(html);

        html.Append("""
            </body>
            </html>
            """);

        return html.ToString();
    }

    /// <summary>
    /// 中文：把装机卡那几张码画出来。顺序就是说明书里的顺序，且编了号——
    ///       装机的人照着 ①②③④ 走一遍即可，不必回去翻说明书。
    /// English:
    ///   Draws the setup card's codes in the manual's own order, numbered so whoever installs a
    ///   scanner can follow ①②③④ without going back to the booklet.
    /// </summary>
    private static void AppendSetupCards(StringBuilder html)
    {
        foreach (var (content, label, detail, isHazard) in SetupCodes)
        {
            var hazardClass = isHazard ? " hazard" : string.Empty;

            html.Append(CultureInfo.InvariantCulture,
                $"<div class=\"setup{hazardClass}\">\n");
            html.Append(CultureInfo.InvariantCulture, $"  <p class=\"label\">{label}</p>\n");
            html.Append(CultureInfo.InvariantCulture, $"  <p class=\"detail\">{detail}</p>\n");
            html.Append(BuildSvg(content));
            html.Append(CultureInfo.InvariantCulture, $"  <p class=\"code\">{content}</p>\n");
            html.Append("</div>\n\n");
        }
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
