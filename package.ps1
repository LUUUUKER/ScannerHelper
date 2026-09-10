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

$exe  = Join-Path $outDir 'ScannerHelper.exe'
$hash = (Get-FileHash $exe -Algorithm SHA256).Hash

Write-Host ""
Write-Host "输出 / Output: $outDir"
Get-ChildItem $outDir | Format-Table Name, Length -AutoSize
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
