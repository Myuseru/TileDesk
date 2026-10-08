using System.Reflection;
using System.Runtime.InteropServices;

[assembly: AssemblyTitle("TileDesk")]
[assembly: AssemblyDescription("桌面磁贴墙 — 隐藏 Windows 原生桌面图标，改用 Steam 风格的封面磁贴")]
[assembly: AssemblyProduct("TileDesk")]
[assembly: AssemblyCompany("TileDesk")]
[assembly: AssemblyCopyright("")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]
// 版本号：**这里是唯一的事实来源**（文件属性里显示的就是它）。
// 改版本时同时改 src\Version.cs 里的 AppVersion.Text —— build.ps1 会校验两者一致，
// 不一致会给出警告。
//
// ★ 递增规则（用户定）：**一轮成功才刷一个号**。
//   "一轮" = 用户提出的一个需求、或一次报 bug，改完并**验证通过**之后 +1 一次。
//   同一轮里反复调试（改了三次才对）**只算一轮**，不要连着刷几个号；没验证通过的不算。
//
//   修 bug / 小改动         -> 末位 +1   （2.0.4、2.0.5 …）
//   加功能 / 改行为         -> 中间位 +1 （2.1.0 …）
//   大改版 / 架构界面重做   -> 首位 +1   （3.0.0 …）
//
// 2.0.0 = 第一个对外发测试的版本；1.x 是内部迭代（卡片布局与拖拽动画、播放器控件、
// 右下角时钟天气、天气动画图标、定位与设置界面的修复都在其中）。
// 2.0.3 = 修好"菜单被时钟/播放器浮层盖住"那一轮（当轮连调三次，按规则只占一个号）。
[assembly: AssemblyVersion("2.1.2.0")]
[assembly: AssemblyFileVersion("2.1.2.0")]
[assembly: AssemblyInformationalVersion("2.1.2")]
[assembly: ComVisible(false)]
