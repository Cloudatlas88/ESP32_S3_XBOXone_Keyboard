using System.Text;

namespace KbConfigurator;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // 全局异常兜底：任何未捕获异常都写日志 + 弹提示，
        // 而不是让进程静默退出、用户只看到"程序开了就没了"。
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportFatal(e.ExceptionObject as Exception);
        Application.ThreadException += (_, e) => ReportFatal(e.Exception);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        // 无界面自检：KbConfigurator.exe --selftest [秒数]
        if (args.Length > 0 && args[0] is "--selftest" or "-t")
        {
            int sec = args.Length > 1 && int.TryParse(args[1], out var s) ? s : 3;
            return SelfTest.Run(sec);
        }

        // 界面往返自检（不开窗口）：KbConfigurator.exe --guiroundtrip
        if (args.Length > 0 && args[0] is "--guiroundtrip" or "-g")
            return GuiRoundTripTest.Run();

        // 句柄失效检测 + 自动重连验收：KbConfigurator.exe --reconnect [等待秒数]
        if (args.Length > 0 && args[0] is "--reconnect" or "-r")
        {
            int t = args.Length > 1 && int.TryParse(args[1], out var ts) ? ts : 60;
            return ReconnectTest.Run(t);
        }

        // 软重启 + NVS 持久化验收：KbConfigurator.exe --reboot
        if (args.Length > 0 && args[0] is "--reboot" or "-b")
            return RebootTest.Run();

        // 界面按键行为验收：KbConfigurator.exe --uitest
        if (args.Length > 0 && args[0] is "--uitest" or "-u")
            return UiKeyTest.Run();

        // 布局自适应验收：KbConfigurator.exe --layout
        if (args.Length > 0 && args[0] is "--layout" or "-l")
            return LayoutTest.Run();

        // 界面交互逻辑验收（宏步骤增删排序/按键搜索/触发方式）：--uilogic
        if (args.Length > 0 && args[0] is "--uilogic" or "-ul")
            return UiLogicTest.Run();

        // 「按键布局」页验收（布局数据 / 坐标映射 / 命中判定 / 绘制）：--xlayout
        if (args.Length > 0 && args[0] is "--xlayout")
            return XboxLayoutTest.Run();

        // 把主窗体渲染成 PNG（UI 改动留档 / 发给用户确认）：--shot [路径] [页签] [宽] [高]
        if (args.Length > 0 && args[0] is "--shot")
            return ShotTest.Run(args);

        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.Run(new MainForm());
            return 0;
        }
        catch (Exception ex)
        {
            ReportFatal(ex);
            return 1;
        }
    }

    /// <summary>把崩溃信息写到文件并弹窗 —— 别让程序"开了就没"</summary>
    private static void ReportFatal(Exception? ex)
    {
        if (ex is null) return;

        var sb = new StringBuilder();
        sb.AppendLine($"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"类型: {ex.GetType().FullName}");
        sb.AppendLine($"消息: {ex.Message}");
        sb.AppendLine();
        sb.AppendLine(ex.ToString());

        string path;
        try
        {
            path = Path.Combine(AppContext.BaseDirectory, "crash.log");
            File.AppendAllText(path, sb.ToString() + Environment.NewLine + new string('=', 70) + Environment.NewLine);
        }
        catch
        {
            path = "(日志写入失败)";
        }

        try
        {
            Console.Error.WriteLine(sb.ToString());
            Console.Error.WriteLine($"已写入: {path}");
        }
        catch { }

        try
        {
            MessageBox.Show(
                $"程序遇到未处理的错误：\n\n{ex.GetType().Name}: {ex.Message}\n\n" +
                $"详细信息已写入：\n{path}",
                "程序错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch { }
    }
}
