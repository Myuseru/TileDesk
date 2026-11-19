# TileDesk 构建脚本
# 使用系统自带的 .NET Framework 编译器（优先 Visual Studio 的 Roslyn），无需安装任何依赖。
#
# ★ 本文件必须保存为 **UTF-8 with BOM**：Windows PowerShell 5.1 在没有 BOM 时
#   会按 ANSI 解析 .ps1，中文注释会被拆成乱码字节，进而把字符串引号吃掉、
#   整个脚本语法错误。改这个文件时请保留 BOM。

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src  = Join-Path $root 'src'
$out  = Join-Path $root 'build'
$exe  = Join-Path $out 'TileDesk.exe'

New-Item -ItemType Directory -Force -Path $out | Out-Null

$candidates = @()
Get-ChildItem 'C:\Program Files\Microsoft Visual Studio' -Directory -ErrorAction SilentlyContinue | ForEach-Object {
    $p = Join-Path $_.FullName 'Community\MSBuild\Current\Bin\Roslyn\csc.exe'
    if (Test-Path $p) { $candidates += $p }
}
$candidates += @(
    'C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\Roslyn\csc.exe',
    'C:\Program Files\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe',
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe')
)

$csc = $null
foreach ($c in $candidates) { if (Test-Path $c) { $csc = $c; break } }
if (-not $csc) { throw '找不到 csc.exe（.NET Framework 4.x 编译器）' }

Write-Host "编译器: $csc" -ForegroundColor Cyan

$sources = Get-ChildItem (Join-Path $src '*.cs') | ForEach-Object { $_.FullName }

$refs = @(
    'System.dll',
    'System.Core.dll',
    'System.Drawing.dll',
    'System.Windows.Forms.dll',
    'System.Runtime.Serialization.dll',
    # Windows 位置服务（右下角信息条的天气定位用）
    'System.Device.dll'
) | ForEach-Object { '/r:' + $_ }

# WPF：右键菜单的亚克力圆角外观（.NET Framework 自带，不需要额外运行时）
$wpfDir = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\WPF'
$refs += @(
    (Join-Path $wpfDir 'PresentationFramework.dll'),
    (Join-Path $wpfDir 'PresentationCore.dll'),
    (Join-Path $wpfDir 'WindowsBase.dll')
) | Where-Object { Test-Path $_ } | ForEach-Object { '/r:' + $_ }

# ---------- WinRT 引用（「正在播放」要走系统 SMTC）----------
$winrtRefs = @()
$unionCandidates = @()
$sdkPortable = 'C:\Program Files (x86)\Microsoft SDKs\Portable\v15.0'
if (Test-Path $sdkPortable) {
    $unionCandidates += Get-ChildItem $sdkPortable -Filter 'Windows.winmd' -Recurse -File -ErrorAction SilentlyContinue |
                        Sort-Object Length -Descending | ForEach-Object { $_.FullName }
}
$unionCandidates += @(
    'C:\Program Files (x86)\Windows Kits\10\UnionMetadata\*\Windows.winmd'
)
$union = $null
foreach ($c in $unionCandidates) {
    $hit = Get-Item $c -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($hit) { $union = $hit.FullName; break }
}
$winMeta = Join-Path $env:WINDIR 'System32\WinMetadata\Windows.Media.winmd'
$facades = 'C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8\Facades'
$winRtAsm = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\System.Runtime.WindowsRuntime.dll'

if ($union -and (Test-Path $winMeta) -and (Test-Path $winRtAsm)) {
    $winrtRefs += '/r:' + $union
    $winrtRefs += '/r:' + $winMeta
    if (Test-Path (Join-Path $facades 'System.Runtime.dll')) {
        $winrtRefs += '/r:' + (Join-Path $facades 'System.Runtime.dll')
        $winrtRefs += '/r:' + (Join-Path $facades 'System.Runtime.InteropServices.WindowsRuntime.dll')
    }
    $winrtRefs += '/r:' + $winRtAsm
    Write-Host "WinRT: $union" -ForegroundColor DarkGray
} else {
    Write-Host "警告：找不到 WinRT 元数据，「正在播放」功能会被编译掉" -ForegroundColor Yellow
    $winrtRefs += '/define:TILEDESK_NO_WINRT'
}

$manifestArg = '/win32manifest:' + (Join-Path $src 'app.manifest')
$outArg = '/out:' + $exe
$iconPath = Join-Path $src 'app.ico'

$cscArgs = @(
    '/nologo',
    '/target:winexe',
    '/platform:x64',
    '/optimize+',
    '/langversion:5',
    '/warn:2',
    '/nowarn:1591',
    $manifestArg,
    $outArg
)
if (Test-Path $iconPath) { $cscArgs += ('/win32icon:' + $iconPath) }
$cscArgs += $refs + $winrtRefs + $sources

& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw "编译失败，退出码 $LASTEXITCODE" }

Write-Host "构建成功 -> $exe" -ForegroundColor Green

# ---------- 打包 ----------
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force -Path $dist | Out-Null
Copy-Item $exe (Join-Path $dist 'TileDesk.exe') -Force

$verFile = Join-Path $root 'src\Version.cs'
$ver = '0.0.0'
if (Test-Path $verFile) {
    $m = [regex]::Match((Get-Content $verFile -Raw), 'AppVersion[\s\S]*?Text\s*=\s*"([^"]+)"')
    if ($m.Success) { $ver = $m.Groups[1].Value }
}
$verExe = Join-Path $dist ("TileDesk-v" + $ver + ".exe")
Copy-Item $exe $verExe -Force
Write-Host ("版本 v" + $ver + " -> " + (Split-Path $verExe -Leaf)) -ForegroundColor Cyan

$fileVer = (Get-Item $exe).VersionInfo.FileVersion
if ($fileVer -and -not $fileVer.StartsWith($ver)) {
    Write-Warning ("版本号不一致：AssemblyInfo.cs 是 " + $fileVer + "，Version.cs 里写的是 " + $ver)
} else {
    Write-Host ("版本核对通过：文件版本 " + $fileVer + " = AppVersion.Text " + $ver) -ForegroundColor DarkGray
}

Write-Host "发布目录 -> $dist" -ForegroundColor Green
Get-ChildItem $dist | Select-Object Name, Length | Format-Table -AutoSize

$readme = @'
TileDesk 3 — 桌面磁贴墙
=======================

这个 exe 就是完整程序，不需要安装任何东西、也不需要任何脚本。
运行环境：Windows 10 1809+ / Windows 11，系统自带 .NET Framework 4.x。

★★ 最重要的注意事项 ★★
------------------------
【不要把 TileDesk.exe 放在被沙箱 / 工作区管理的目录里运行】

Windows 有一条规则：从带「低完整性标签」的文件启动的进程，会被系统降权。
被降权的后果：打不开回收站、开机自启写不进去、封面缓存放不进 %LOCALAPPDATA%。

正确的做法：把 TileDesk.exe 复制到普通目录再运行，例如
  C:\Users\<你>\TileDesk\
  D:\Tools\TileDesk\
然后从齿轮菜单「安装到本机」装到 %LOCALAPPDATA%\Programs\TileDesk。


怎么用
------
1. 双击 TileDesk.exe
2. 磁贴墙右下角有【垃圾桶】和【齿轮】两个按钮：
     齿轮   → 设置 / 刷新 / 自动排列 / 隐藏原生图标 / 开机自启 / 退出
     垃圾桶 → 打开回收站；把卡片拖上去 = 把那个桌面图标丢进回收站
3. 想让它常驻：齿轮菜单 →「开机自动启动」
4. 想关掉：齿轮菜单 →「退出 TileDesk」

托盘图标里也有一份同样的菜单，但 Windows 11 默认把新图标收进
任务栏的「^」溢出区，所以【右下角的按钮才是最稳的入口】。

主要操作
--------
· 单击 / 双击卡片 = 打开
· 拖动卡片        = 重排（自动记住顺序）
· 拖到垃圾桶      = 删掉桌面图标（进回收站，可还原）
· Ctrl + 滚轮     = 缩放卡片大小
· 滚轮            = 上下滚动
· 右键卡片        = 打开位置 / 属性 / 自定义封面 / 重命名 / 删除 / 设置
· 右键空白处      = 桌面自己的菜单（空白处鼠标是穿透的）

自动同步
--------
程序会监视桌面文件夹，新建 / 删除 / 改名快捷方式后约 1 秒自动刷新。

3.0 改了什么
------------
· 渲染换成独立的帧时钟（高精度计时器），动画真正跑满 60fps
· 滚动不再重烤卡片、只重绘网格条带 —— 4K 下滚动从 ~20ms/帧降到 3~5ms
· 卡片投影改成「接触 + 环境」两层，描边、高光、悬停辉光全部重做
· 亚克力底修正了饱和度矩阵（原来会把浅色壁纸提亮成一片白）
· 设置界面与右键菜单按 Windows 11 的 Fluent 观感重做
· 空闲 CPU 约 0.05%（原来常驻十几毫秒一次的轮询）
'@
Set-Content -Path (Join-Path $dist '使用说明.txt') -Value $readme -Encoding UTF8
