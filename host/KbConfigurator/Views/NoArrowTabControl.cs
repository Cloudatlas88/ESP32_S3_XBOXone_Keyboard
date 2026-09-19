namespace KbConfigurator.Views;

/// <summary>
/// 不响应方向键切换页签的 TabControl。
///
/// ★ 为什么需要这个：
///   摇杆映射成的就是方向键（上 0x52 / 下 0x51 / 左 0x50 / 右 0x4F）。
///   用户推着摇杆测试映射效果时，配置器窗口会收到这些方向键，
///   而原生 TabControl 把方向键当成"切换页签"的指令 —— 于是页签乱翻，
///   根本没法一边推摇杆一边看「模拟调试」页的数据。
///
/// ★ 只拦「用方向键切换页签」这一条路径，不吞掉所有方向键：
///   子控件（目标按键下拉框、延迟数字框）里的方向键仍然正常工作 ——
///   因为下拉框/NumericUpDown 自己就把方向键处理掉了，根本不会冒泡到这里。
///   所以这里拦不到它们，也不需要特殊照顾。
///
/// ★★ 拦截位置：必须在 **WndProc** 上拦，不能只重写 ProcessCmdKey/ProcessDialogKey。
///   这是实测踩出来的：写了那两个方法之后跑测试，往控件句柄发真实 WM_KEYDOWN，
///   页签照样被切换。原因是 TabControl 的方向键切换是它自己的窗口过程直接处理的，
///   根本不走 ProcessCmdKey / ProcessDialogKey 那两条路
///   （实测：对原生 TabControl 直接调这两个方法，页签一点都不动）。
///
///   所以真正起作用的是下面的 WndProc；另外两个保留作为次要路径的兜底。
/// </summary>
public sealed class NoArrowTabControl : TabControl
{
    private const int WM_KEYDOWN = 0x0100;

    private const int VK_LEFT  = 0x25;
    private const int VK_UP    = 0x26;
    private const int VK_RIGHT = 0x27;
    private const int VK_DOWN  = 0x28;

    /// <summary>判断是不是方向键（忽略修饰符位）</summary>
    private static bool IsArrow(Keys keyData) => (keyData & Keys.KeyCode) switch
    {
        Keys.Left or Keys.Right or Keys.Up or Keys.Down => true,
        _ => false,
    };

    private static bool IsArrowVk(IntPtr wParam) => wParam.ToInt32() switch
    {
        VK_LEFT or VK_RIGHT or VK_UP or VK_DOWN => true,
        _ => false,
    };

    /// <summary>★ 真正起作用的一层：方向键按下时不交给默认窗口过程</summary>
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_KEYDOWN && IsArrowVk(m.WParam))
        {
            // 直接丢msg：不调 base，TabControl 就没机会把它当成"切页签"的指令。
            // 注意只丢 KEYDOWN 就够 —— 页签切换是按下时触发的。
            return;
        }
        base.WndProc(ref m);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // 返回 true = 这条键我已经处理了（也就是"吞掉"），不往下传、不切页签
        if (IsArrow(keyData)) return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override bool ProcessDialogKey(Keys keyData)
    {
        if (IsArrow(keyData)) return true;
        return base.ProcessDialogKey(keyData);
    }
}
