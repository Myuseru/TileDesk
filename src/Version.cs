namespace TileDesk
{
    /// <summary>
    /// 代码里用的短版本号（日志首行、托盘气泡、齿轮菜单里显示的那行）。
    ///
    /// **注意**：版本号的事实来源是 <c>src\AssemblyInfo.cs</c>（文件属性里显示的那个）。
    /// 改版本时两处都要改，build.ps1 编译后会校验一致性并给出警告，不会悄悄放过。
    ///
    /// **递增规则（用户定）：一轮成功才刷一个号** —— "一轮"= 一个需求或一次报 bug，
    /// 改完并验证通过后 +1；同一轮里反复调试只算一轮，不要连着刷几个号。
    /// </summary>
    internal static class AppVersion
    {
        public const string Text = "2.1.2.1";
    }
}
