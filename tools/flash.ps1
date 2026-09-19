<#
.SYNOPSIS
    ESP32-S3 固件烧录留档工具 —— 保证「每一次烧录的代码都被永久保存」。

.DESCRIPTION
    一条命令完成完整闭环：
        构建 → 归档固件二进制 → Git 提交+打标签 → 烧录 → 写烧录台账

    为什么需要它：
      1. 每次烧录的源码必须可回溯（Git commit + tag: flash-NNN）
      2. 每次烧录的**二进制**必须留存（代码一样但配置不同，产物就不同）
      3. 每次烧录的**结果**必须记录（成功/失败、Windows 如何识别、有无异常）

    归档产物结构：
        flash_archive/001_20260913-143000_smoke-tusb-hid/
            ├── manifest.json          本次烧录的完整元数据
            ├── sdkconfig              精确配置快照（复现的关键）
            ├── bootloader.bin
            ├── partition-table.bin
            └── tusb_hid.bin

.PARAMETER Project
    工程目录，相对工作区根。例如 firmware/smoke_tusb_hid

.PARAMETER Message
    本次烧录说明。会写进 git commit 与烧录台账。

.PARAMETER Port
    烧录串口。默认 COM3（CH343 UART 口）。
    注意：当固件启用了 TinyUSB 时，原生 USB 口的 COM4 会被 HID 设备取代而消失，
    此时只能走 UART 口 COM3，或按住 BOOT+点 RST 进 ROM 下载模式再用 COM4。

.PARAMETER Result
    烧录后的验证结论（可选，写入台账）。

.PARAMETER SkipFlash
    只归档 + 提交，不烧录。用于回溯登记历史烧录。

.PARAMETER SkipBuild
    跳过构建，直接用现有的 build 产物归档。

.EXAMPLE
    .\tools\flash.ps1 -Project firmware/smoke_tusb_hid -Message "官方示例冒烟测试" -Port COM4

.EXAMPLE
    .\tools\flash.ps1 -Project firmware/main_fw -Message "阶段一：摇杆+按钮映射" -Result "WASD 与四键正常"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Project,
    [Parameter(Mandatory = $true)][string]$Message,
    [string]$Port   = "COM3",
    [int]$Baud      = 460800,
    [string]$Result = "",
    [switch]$SkipBuild,
    [switch]$SkipFlash
)

$ErrorActionPreference = 'Continue'
$EIM = "C:\Program Files\eim\eim.exe"
$WS  = Split-Path $PSScriptRoot -Parent

function Step($m) { Write-Host ""; Write-Host "==> $m" -ForegroundColor Cyan }
function Ok($m)   { Write-Host "    [OK] $m" -ForegroundColor Green }
function Warn($m) { Write-Host "    [!]  $m" -ForegroundColor Yellow }
function Fail($m) { Write-Host "    [X]  $m" -ForegroundColor Red; exit 1 }

# ★ 绝不要把 git 包装成带 ValueFromRemainingArguments 的 PowerShell 函数：
#   `-A` / `-m` / `-l` 会被 PowerShell 当成函数形参吞掉，导致 `git add -A` 静默不执行、
#   仓库零提交而脚本仍报成功。一律用 `& git -C $WS ...` 直接调用（& 不会解析 - 开头的参数）。

# UTF-8 无 BOM 写入（5.1 的 Set-Content -Encoding utf8 会加 BOM，破坏 JSON）
function Write-Utf8NoBom {
    param([string]$Path, [string]$Text)
    [System.IO.File]::WriteAllText($Path, $Text, (New-Object System.Text.UTF8Encoding($false)))
}
function Append-Utf8NoBom {
    param([string]$Path, [string]$Text)
    [System.IO.File]::AppendAllText($Path, $Text + "`r`n", (New-Object System.Text.UTF8Encoding($false)))
}

# ───────────────────────── 0. 前置校验 ─────────────────────────
Step "前置校验"
$projAbs = Join-Path $WS $Project
if (-not (Test-Path (Join-Path $projAbs 'CMakeLists.txt'))) { Fail "找不到工程（缺少 CMakeLists.txt）: $projAbs" }
$projFwd  = $projAbs -replace '\\', '/'
$buildDir = Join-Path $projAbs 'build'
if (-not (Test-Path (Join-Path $WS '.git'))) { Fail "工作区不是 Git 仓库: $WS" }
Ok "工程: $Project"

$chipInfoPath = Join-Path $PSScriptRoot 'chipinfo.json'
# 必须显式 -Encoding UTF8：PS 5.1 的 Get-Content 默认按 ANSI 读，中文会变乱码导致 JSON 解析失败
$chip = if (Test-Path $chipInfoPath) { Get-Content $chipInfoPath -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null }

# ───────────────────────── 1. 构建 ─────────────────────────
if (-not $SkipBuild) {
    Step "构建 ($Project)"
    & $EIM run "idf.py -C $projFwd build" 2>&1 | Select-Object -Last 8
    if ($LASTEXITCODE -ne 0) { Fail "构建失败（exit=$LASTEXITCODE）" }
    Ok "构建成功"
} else {
    Step "跳过构建（-SkipBuild）"
}

$flasherArgs = Join-Path $buildDir 'flasher_args.json'
if (-not (Test-Path $flasherArgs)) { Fail "缺少 $flasherArgs，请先构建" }

# ───────────────────────── 2. 归档 ─────────────────────────
Step "归档固件二进制"
$archiveRoot = Join-Path $WS 'flash_archive'
New-Item -ItemType Directory -Force -Path $archiveRoot | Out-Null

$existing = Get-ChildItem $archiveRoot -Directory -ErrorAction SilentlyContinue |
            ForEach-Object { if ($_.Name -match '^(\d{3})_') { [int]$Matches[1] } }
# ★ Measure-Object 的 .Maximum 返回 Double，而 "D3" 格式符不支持浮点数，
#   必须显式 [int] 转换，否则 "flash-{0:D3}" -f 抛错 -> $tag/$archDirName 变空
#   -> 归档文件被误放进 flash_archive 根目录。（该 bug 只在第 2 次及以后运行才暴露）
$seq = if ($existing) { [int](($existing | Measure-Object -Maximum).Maximum) + 1 } else { 1 }
$tag = "flash-{0:D3}" -f $seq

$slug = ($Project -replace '^firmware/', '') -replace '[\\/]', '-' -replace '[^A-Za-z0-9\-]', '-'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$archDirName = "{0:D3}_{1}_{2}" -f $seq, $stamp, $slug
$archDir = Join-Path $archiveRoot $archDirName
if (-not $tag -or $archDirName -notmatch '^\d{3}_') {
    Fail "序号/归档目录名计算失败（seq=[$seq] dir=[$archDirName]），终止以免污染仓库"
}
New-Item -ItemType Directory -Force -Path $archDir | Out-Null

$fa = Get-Content $flasherArgs -Raw -Encoding UTF8 | ConvertFrom-Json
$fileRecords = @()
foreach ($p in $fa.flash_files.PSObject.Properties) {
    $offset = $p.Name
    $rel    = $p.Value
    $src    = Join-Path $buildDir $rel
    if (-not (Test-Path $src)) { Warn "缺文件: $rel"; continue }
    $leaf = Split-Path $src -Leaf
    $dst  = Join-Path $archDir $leaf
    Copy-Item $src $dst -Force
    $fileRecords += [ordered]@{
        file    = $leaf
        offset  = $offset
        bytes   = (Get-Item $dst).Length
        sha256  = (Get-FileHash $dst -Algorithm SHA256).Hash
    }
    Ok ("{0,-28} offset={1,-8} {2,8:N0} B  {3}" -f $leaf, $offset, (Get-Item $dst).Length, (Get-FileHash $dst -Algorithm SHA256).Hash.Substring(0,16))
}

# sdkconfig 快照（复现关键）
$cfgSrc = Join-Path $projAbs 'sdkconfig'
if (Test-Path $cfgSrc) { Copy-Item $cfgSrc (Join-Path $archDir 'sdkconfig') -Force; Ok "sdkconfig 快照已保存" }

# ───────────────────────── 3. Git 提交（烧录前，确保代码已落库）─────────────────────────
Step "Git 提交并打标签"
& git -C $WS add -A | Out-Null
if ($LASTEXITCODE -ne 0) { Fail "git add 失败" }

$commitMsg = "$tag`: $Message"
& git -C $WS commit --allow-empty -m $commitMsg | Out-Null
if ($LASTEXITCODE -ne 0) { Fail "git commit 失败" }

$head = (& git -C $WS rev-parse HEAD | Out-String).Trim()
if (-not $head) { Fail "无法读取 Git commit hash" }
$headShort = $head.Substring(0, [Math]::Min(12, $head.Length))
Ok "提交 $headShort  ($commitMsg)"

$tagExists = (& git -C $WS tag -l $tag | Out-String).Trim()
if ($tagExists) { Warn "标签 $tag 已存在，跳过打标签" }
else {
    & git -C $WS tag -a $tag -m $Message | Out-Null
    if ($LASTEXITCODE -ne 0) { Warn "打标签 $tag 失败" } else { Ok "标签 $tag 已创建" }
}

# manifest（含烧录前状态）
$manifest = [ordered]@{
    seq            = $seq
    tag            = $tag
    timestamp      = (Get-Date).ToString('o')
    project        = $Project
    description    = $Message
    git_commit     = $head
    git_tag        = $tag
    idf_version    = 'v6.1'
    target         = 'esp32s3'
    port           = if ($SkipFlash) { $null } else { $Port }
    flashed        = (-not $SkipFlash)
    flash_result   = $null
    verification   = if ($Result) { $Result } else { $null }
    chip           = $chip
    files          = $fileRecords
}
$manifest | ConvertTo-Json -Depth 8 | ForEach-Object { Write-Utf8NoBom (Join-Path $archDir 'manifest.json') $_ }
Ok "manifest.json 已写入"

# ───────────────────────── 4. 烧录 ─────────────────────────
$flashOk = $false
$flashNote = ''
if (-not $SkipFlash) {
    Step "烧录到 $Port"
    Write-Host "    （首次烧录约 10-20 秒；TinyUSB 固件运行时原生口会消失，属正常）" -ForegroundColor DarkGray
    & $EIM run "idf.py -C $projFwd -p $Port -b $Baud flash" 2>&1 |
        Where-Object { $_ -match 'Writing|Wrote|Hash of data verified|Hard resetting|error|Error|Failed|失败|fatal' } |
        Select-Object -Last 20
    if ($LASTEXITCODE -eq 0) { $flashOk = $true; Ok "烧录成功" }
    else { $flashNote = "烧录失败 exit=$LASTEXITCODE"; Warn $flashNote }
} else {
    Step "跳过烧录（-SkipFlash）"
    $flashNote = '未烧录（回溯登记）'
}

# ───────────────────────── 5. 写台账 + 回填 manifest ─────────────────────────
Step "更新烧录台账"
$logPath = Join-Path $WS 'FLASH_LOG.md'
if (-not (Test-Path $logPath)) {
    $logHeader = @'
# 烧录台账 (FLASH_LOG)

> 每一次烧录在此登记一行。归档目录见 `flash_archive/`，源码见对应 Git 标签 `flash-NNN`。
>
> | 字段 | 含义 |
> |---|---|
> | # | 烧录序号，与 Git 标签 `flash-NNN`、归档目录前缀一致 |
> | 提交 | 本次烧录对应的 Git commit（前 12 位）|
> | 固件 | 归档目录中主应用的 SHA256 前 16 位，用于确认「跑的就是这份」|

| # | 时间 | 说明 | 工程 | 提交 | 端口 | 固件 (SHA256) | 结果 |
|---|---|---|---|---|---|---|---|
'@
    Write-Utf8NoBom $logPath ($logHeader + "`r`n")
}

$mainBin = $fileRecords | Where-Object { $_.offset -eq '0x10000' } | Select-Object -First 1
if (-not $mainBin) { $mainBin = $fileRecords | Select-Object -Last 1 }
$binShort = if ($mainBin) { $mainBin.sha256.Substring(0, 16) } else { 'n/a' }

if ($SkipFlash)  { $outcome = if ($Result) { "回溯登记 — $Result" } else { '未烧录（回溯登记）' } }
elseif ($flashOk) { $outcome = if ($Result) { "成功 — $Result" } else { '成功' } }
else              { $outcome = "失败 — $flashNote" }

$row = "| $seq | $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') | $Message | ``$Project`` | ``$headShort`` | $(if($SkipFlash){'-'}else{$Port}) | ``$binShort`` | $outcome |"
Append-Utf8NoBom $logPath $row
Ok "台账已登记第 $seq 条"

$manifest.flash_result = if ($SkipFlash) { 'skipped' } elseif ($flashOk) { 'success' } else { 'failed' }
$manifest | ConvertTo-Json -Depth 8 | ForEach-Object { Write-Utf8NoBom (Join-Path $archDir 'manifest.json') $_ }

& git -C $WS add -A | Out-Null
& git -C $WS commit --allow-empty -m "$tag-result: $outcome" | Out-Null

# ───────────────────────── 汇总 ─────────────────────────
Write-Host ""
Write-Host ("=" * 66) -ForegroundColor DarkGray
Write-Host "  烧录留档完成" -ForegroundColor Cyan
Write-Host ("=" * 66) -ForegroundColor DarkGray
Write-Host "  序号      : $seq"
Write-Host "  标签      : $tag"
Write-Host "  归档目录  : flash_archive/$archDirName"
Write-Host "  Git 提交  : $headShort"
Write-Host "  烧录结果  : $outcome"
Write-Host "  台账      : FLASH_LOG.md"
if (-not $SkipFlash -and $flashOk) {
    Write-Host ""
    Write-Host "  提示：若本次固件启用了 TinyUSB，原生 USB 口的 COM4 已消失。" -ForegroundColor DarkGray
    Write-Host "        下次烧录请用 UART 口 ($Port)，或按住 BOOT + 点 RST 进下载模式。" -ForegroundColor DarkGray
}
Write-Host ""
if (-not $SkipFlash -and -not $flashOk) { exit 1 }
