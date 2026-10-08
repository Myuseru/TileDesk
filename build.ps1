# TileDesk 构建脚本
# 使用系统自带的 .NET Framework 编译器（优先 Visual Studio 的 Roslyn），无需安装任何依赖。

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src  = Join-Path $root 'src'
$out  = Join-Path $root 'build'
$exe  = Join-Path $out 'TileDesk.exe'

New-Item -ItemType Directory -Force -Path $out | Out-Null

$candidates = @(
    'C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\Roslyn\csc.exe',
    'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\Roslyn\csc.exe',
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe')
)
# 再补充搜索 Visual Studio 的 Roslyn
Get-ChildItem 'C:\Program Files\Microsoft Visual Studio' -Directory -ErrorAction SilentlyContinue | ForEach-Object {
    $p = Join-Path $_.FullName 'MSBuild\Current\Bin\Roslyn\csc.exe'
    if (Test-Path $p) { $candidates = @($p) + $candidates }
}

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
    # Windows 位置服务（右下角信息条的天气定位用）。GeoCoordinateWatcher 内部走的就是
    # 系统位置接口，用户在「设置 → 隐私和安全性 → 位置」里能看到哪些程序在用。
    'System.Device.dll'
) | ForEach-Object { '/r:' + $_ }

# WPF：右键菜单的亚克力圆角外观（.NET Framework 自带，不需要额外运行时）。
# 菜单改用 WPF 是为了对上 TranslucentTB 那种 WinUI 观感 —— WinUI 需要 WinAppSDK
# 运行时，单文件程序背不动，WPF 能达到同样的视觉效果且零外部依赖。
# 这三个程序集不在 csc 的默认搜索路径里，必须给全路径。
$wpfDir = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\WPF'
$refs += @(
    (Join-Path $wpfDir 'PresentationFramework.dll'),
    (Join-Path $wpfDir 'PresentationCore.dll'),
    (Join-Path $wpfDir 'WindowsBase.dll')
) | Where-Object { Test-Path $_ } | ForEach-Object { '/r:' + $_ }

# ---------- WinRT 引用（「正在播放」要走系统 SMTC）----------
# .NET Framework 消费 WinRT 需要：
#   1) 联合元数据 Windows.winmd（提供 IAsyncOperation / TimeSpan 这些投影类型，
#      编译器把它当成名为 "Windows" 的程序集）
#   2) 具体命名空间的 winmd（这里只要 Windows.Media，SMTC 在里面）
#   3) Facades\System.Runtime.dll 与 System.Runtime.WindowsRuntime.dll
# 注意：不要再单独引 Windows.Storage.winmd —— 联合元数据里已经有 Storage 类型，
# 两个都引会让 Buffer / DataReader 之类报 CS0433「同时存在于两个程序集」。
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
    $manifestArg,
    $outArg
)
if (Test-Path $iconPath) { $cscArgs += ('/win32icon:' + $iconPath) }
$cscArgs += $refs + $winrtRefs + $sources

& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw "编译失败，退出码 $LASTEXITCODE" }

Write-Host "构建成功 -> $exe" -ForegroundColor Green

# ---------- 打包出一个干净的独立发布目录（只有一个 exe）----------
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force -Path $dist | Out-Null
Copy-Item $exe (Join-Path $dist 'TileDesk.exe') -Force

# 版本：从 src\Version.cs 里读，别在两处各写一份（写歪了就分不清别人测的是哪版）
$verFile = Join-Path $root 'src\Version.cs'
$ver = '0.0.0'
if (Test-Path $verFile) {
    $m = [regex]::Match((Get-Content $verFile -Raw), 'AppVersion[\s\S]*?Text\s*=\s*"([^"]+)"')
    if ($m.Success) { $ver = $m.Groups[1].Value }
}
# 再复制一份带版本号的：发给别人测试时一眼能分清
$verExe = Join-Path $dist ("TileDesk-v" + $ver + ".exe")
Copy-Item $exe $verExe -Force
Write-Host ("版本 v" + $ver + " -> " + (Split-Path $verExe -Leaf)) -ForegroundColor Cyan

# 一致性校验：AssemblyInfo.cs（文件属性里的版本）必须和 Version.cs 的 AppVersion.Text 对得上，
# 否则"日志里写的版本"和"属性里的版本"会各说各话，追踪 bug 时最坑。
$fileVer = (Get-Item $exe).VersionInfo.FileVersion
if ($fileVer -and -not $fileVer.StartsWith($ver)) {
    Write-Warning ("版本号不一致：AssemblyInfo.cs 是 " + $fileVer + "，Version.cs 里写的是 " + $ver +
                   " —— 请把两处改成一样")
} else {
    Write-Host ("版本核对通过：文件版本 " + $fileVer + " = AppVersion.Text " + $ver) -ForegroundColor DarkGray
}

$readme = @'
TileDesk — 桌面磁贴墙
=====================

这个 exe 就是完整程序，不需要安装任何东西、也不需要任何脚本。

★★ 最重要的注意事项 ★★
------------------------
【不要把 TileDesk.exe 放在被沙箱 / 工作区管理的目录里运行】

Windows 有一条规则：从带「低完整性标签」的文件启动的进程，会被系统降权。
DSH 的工作区（...\Documents\deepseek-harness\default-workspace\）就带这个标签，
所以放在里面的 TileDesk.exe 一启动就是低权限（完整性 0x1000，正常应为 0x2000），
后果是：

  · 点回收站没反应，或者弹「explorer.exe 应用程序无法正常启动(0xc0000142)」
  · 「打开文件所在位置」失效
  · 开机自启写不进去
  · 封面缓存不能放在 %LOCALAPPDATA%，每次都要重新下载

判断方法：启动日志第一行会写「运行环境: 完整性 RID=0x????」，
0x2000 正常，0x1000 就是被降权了。
如果被降权，程序会主动弹提示并帮你复制到一个正常目录。

正确的做法：把 TileDesk.exe 复制到普通目录再运行，例如
  C:\Users\<你>\TileDesk\
  D:\Tools\TileDesk\
然后从那里用齿轮菜单的「安装到本机」装到 %LOCALAPPDATA%\Programs\TileDesk。


怎么用
------
1. 双击 TileDesk.exe
2. 磁贴墙右下角有【垃圾桶】和【齿轮】两个按钮：
     齿轮 → 设置… / 刷新 / 隐藏原生桌面图标 / 开机自启 / 安装到本机 / 卸载还原 / 退出
     垃圾桶 → 单击打开回收站；把卡片拖上去 = 把那个桌面图标丢进回收站
3. 想让它常驻：齿轮菜单 →「安装到本机（复制 + 开机自启）」
4. 想关掉：齿轮菜单 →「退出 TileDesk」
5. 想还原系统：齿轮菜单 →「卸载 / 还原系统…」

任务栏通知区域（托盘）里也有一份一模一样的菜单，
但 Windows 11 默认把新图标收进任务栏的「^」溢出区，
所以【右下角的按钮才是最稳的入口】。

主要操作
--------
· 双击卡片      = 打开
· 拖动卡片      = 重排（自动记住自定义顺序）
· 拖到垃圾桶    = 删掉桌面图标（进回收站，可还原）
· Ctrl + 滚轮   = 缩放卡片大小
· 滚轮          = 滚动
· 右键卡片      = 打开位置 / 属性 / 设置自定义封面 / 删除 / 设置 / 退出
· 右键空白处    = 桌面自己的菜单（空白处鼠标是穿透的）

自动同步
--------
程序会监视桌面文件夹，你在桌面新建 / 删除 / 改名快捷方式后约 1 秒，
磁贴墙会自动跟着增删并重新补位。

系统要求
--------
Windows 10 / 11，自带 .NET Framework 4.x 即可（系统自带，无需另外装）。
'@
Set-Content -Path (Join-Path $dist '使用说明.txt') -Value $readme -Encoding UTF8

Write-Host "发布目录 -> $dist" -ForegroundColor Green
Get-ChildItem $dist | Select-Object Name, Length | Format-Table -AutoSize
