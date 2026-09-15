# =============================================================================
# package.ps1
#
# 中文:
#   打出发到工位上的正式包(规格 §16)。
#
#   ★ 自包含(--self-contained true)。
#
#     不这样的话,目标机器要先装 .NET 8 桌面运行时——而装运行时**需要管理员
#     权限**,仓库工位的账号未必有。真正的失败方式还更难看一点:程序双击之后
#     弹一个说不清楚的框然后什么也不发生,而去现场装的人手里没有任何线索。
#
#     代价是 138 MB。装在 U 盘上带过去,这个代价等于零。
#
#   ★ 单文件。工位上要复制的东西只有一个,没有"少拷了一个 DLL"这种失败方式。
#
#   ★ 不带 pdb、不带 .NET 自带的几十种语言的附属资源。发到工位上的东西里,
#     每一样都应该有它非在那儿不可的理由。
#
# English:
#   Builds the artifact that goes to a workstation (spec §16).
#
#   Self-contained, because otherwise the target machine needs the .NET 8 desktop runtime installed
#   first — and installing a runtime needs administrator rights, which a warehouse account may not
#   have. The real failure mode is worse than the inconvenience: a double-click produces an
#   unexplanatory dialog and nothing else, and whoever went on site has nothing to go on. The price
#   is 138 MB, which on a USB stick is no price at all.
#
#   Single-file, so there is exactly one thing to copy and no "a DLL was left behind" failure.
#
#   No pdb and no satellite resources for the framework's dozens of languages: everything shipped to
#   a workstation should have a reason to be there.
#
# 用法 / Usage:
#   pwsh -File package.ps1            打到 release\ScannerHelper-<版本>\
# =============================================================================

$ErrorActionPreference = 'Stop'

$root    = $PSScriptRoot
$project = Join-Path $root 'src\ScannerHelper.App\ScannerHelper.App.csproj'
$readme  = Join-Path $root 'docs\使用说明.txt'

# 版本号从工程文件里取,不在这里再写一遍——两处各写一遍迟早对不上,
# 而对不上的那一次,日志里的版本号会指向一个并不存在的构建。
# The version comes from the project file rather than being repeated here: two copies eventually
# disagree, and the time they do, the version in the log names a build that never existed.
$version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ }
$outDir  = Join-Path $root "release\ScannerHelper-$version"

Write-Host "打包 Scanner Helper $version" -ForegroundColor Cyan

# 程序占着自己的文件时 publish 会失败,而错误信息("文件被占用")跟真正的原因
# 隔着一层。先关掉。
# publish fails while the program holds its own file, and the error ("file in use") sits one step
# away from the cause. Close it first.
Get-Process ScannerHelper -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1

if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }

dotnet publish $project `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=none `
    -o $outDir --nologo

if ($LASTEXITCODE -ne 0) { throw "publish 失败 / publish failed" }

Copy-Item $readme $outDir

# 现场探测脚本一起发。2.4G 接收器在 Windows 眼里是什么设备,这件事只能在工位那台
# 机器上量,而人到了现场才发现工具没带是最浪费的一种往返。
# The probe ships alongside: what a 2.4G receiver is in Windows' eyes can only be measured on the
# workstation, and discovering on site that the tool was left behind is the most wasteful of trips.
$tools = Join-Path $outDir '工具'
New-Item -ItemType Directory -Path $tools -Force | Out-Null
Copy-Item (Join-Path $root 'tools\检查扫码枪.ps1') $tools
Copy-Item (Join-Path $root 'tools\检查扫码枪.bat') $tools

$exe  = Join-Path $outDir 'ScannerHelper.exe'

# ---------------------------------------------------------------- 条码 PDF
# 中文:
#   把条码页转成随包发的 PDF。
#
#   ★ 由**程序本身**产出,不是谁手工导出一次然后提交上去。
#
#     条码的内容(#SH:SN# 这些)由代码定义。手工导出的 PDF 会和代码各自演化——
#     某天前缀改了,随包的 PDF 还是旧的,印出来贴到墙上扫下去什么都不会发生,
#     而没有人知道该更新它。所以这一步每次打包都重跑。
#
#   ★ 用 Edge 无头打印,不引第三方 PDF 库。Windows 上 Edge 一定在,而发布件
#     的依赖越少越好(规格 §22.1:仓库 IT 按哈希加白名单)。
#     Edge 找不到就跳过并警告——PDF 是方便打印用的,不该让打包失败。
# English:
#   Renders the sheet to the PDF that ships with the release, produced by the program rather than
#   exported once by hand: the barcode content is defined in code, and a hand-made PDF drifts from
#   it — the day the prefix changes, the shipped PDF is the old one, and a sheet that does nothing
#   when scanned goes onto a wall with nobody knowing it needs replacing. So this reruns every build.
#
#   Edge prints it headlessly rather than pulling in a PDF library: Edge is always present on
#   Windows and the artifact is better off with fewer dependencies (spec §22.1, where warehouse IT
#   allow-lists by hash). A missing Edge warns and skips — the PDF is a printing convenience and
#   must not fail the build.
$sheetHtml = Join-Path $env:TEMP 'scannerhelper-sheets.html'
$sheetPdf  = Join-Path $outDir '模式切换与配置条码.pdf'

# Start-Process -Wait 而不是 & 调用:见下面 Edge 那一步的说明,同一条理由。
# Start-Process -Wait rather than the call operator; see the note on the Edge step below.
Start-Process -FilePath $exe -ArgumentList '--sheets', $sheetHtml -Wait -NoNewWindow

$edge = @(
    "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe",
    "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not (Test-Path $sheetHtml)) {
    Write-Warning "条码页没有生成,跳过 PDF。 The sheet was not produced; skipping the PDF."
} elseif (-not $edge) {
    Write-Warning "找不到 Edge,跳过 PDF。 Edge was not found; skipping the PDF."
} else {
    # ★ 用 Start-Process,不要用 & 加 2>&1。
    #
    #   Edge 无头模式会往 stderr 写一行无害的诊断,而本脚本开头是
    #   $ErrorActionPreference = 'Stop'——Windows PowerShell 5.1 会把原生程序的
    #   stderr 包成 NativeCommandError 当成终止性错误,于是打包在"PDF 其实已经
    #   生成了"的情况下报错退出。
    # Start-Process rather than the call operator with 2>&1: headless Edge writes a harmless line to
    # stderr, and with $ErrorActionPreference = 'Stop' at the top of this script, Windows PowerShell
    # 5.1 wraps a native command's stderr in a NativeCommandError and treats it as terminating —
    # failing the build although the PDF was produced.
    $fileUrl = 'file:///' + $sheetHtml.Replace('\', '/')

    Start-Process -FilePath $edge -Wait -NoNewWindow -ArgumentList @(
        '--headless', '--disable-gpu', '--no-pdf-header-footer',
        "--print-to-pdf=$sheetPdf", $fileUrl
    )

    if (Test-Path $sheetPdf) {
        # docs 里也放一份,方便不装程序的人直接打印(用户要求)。
        # 同样每次打包覆盖,免得两处对不上——对不上的那份迟早会被印出来。
        # A copy lives in docs so it can be printed without installing the program. Overwritten each
        # build as well, or the two disagree and the wrong one eventually gets printed.
        # 复制失败不该让整个打包倒掉:最常见的原因是有人正开着这份 PDF 在看,
        # 而发布件里那一份已经好了。警告一声,让人知道 docs 里那份这次没更新。
        # A failed copy must not topple the build: the usual cause is someone reading the PDF, and
        # the one in the release is already correct. Warn instead, so it is known that the copy in
        # docs was not refreshed this time.
        try {
            Copy-Item $sheetPdf (Join-Path $root 'docs') -Force -ErrorAction Stop
        } catch {
            Write-Warning "docs 里那份 PDF 没能更新(多半正被打开着): $($_.Exception.Message)"
            Write-Warning "The copy in docs was not refreshed (likely open elsewhere)."
        }
    } else {
        Write-Warning "PDF 生成失败。 The PDF was not produced."
    }

    Remove-Item $sheetHtml -ErrorAction SilentlyContinue
}

$hash = (Get-FileHash $exe -Algorithm SHA256).Hash

Write-Host ""
Write-Host "输出 / Output: $outDir"
Get-ChildItem $outDir -Recurse | Format-Table FullName, Length -AutoSize
Write-Host "SHA256: $hash"
Write-Host ""

# ★ 哈希要对得上说明书里印的那一行。仓库 IT 就是照那一行加白名单的(规格 §22.1),
#   对不上的话,现场会在一个和程序毫无关系的地方卡住。
# The hash must match the line printed in the readme: warehouse IT allow-lists from exactly that
# line (spec §22.1), and a mismatch strands the rollout somewhere unrelated to the program.
if ((Get-Content (Join-Path $outDir '使用说明.txt') -Raw) -notmatch [regex]::Escape($hash)) {
    Write-Warning "使用说明.txt 里的 SHA256 与本次构建不一致,请更新 docs\使用说明.txt。"
    Write-Warning "The SHA256 in 使用说明.txt does not match this build; update docs\使用说明.txt."
}
