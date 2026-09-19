using KbConfigurator.Model;

namespace KbConfigurator.Ui;

/// <summary>
/// 可搜索的按键下拉。
///
/// ★ 这段原本长在 <see cref="Views.KeymapPanel"/> 里。做「按键布局」页的映射弹面板时
///   要复用同一套行为（尤其是"找不到时不能静默归零"那条），
///   所以抽出来 —— 而不是复制一份，否则以后修了一处漏另一处。
/// </summary>
internal static class KeyCombo
{
    /// <summary>
    /// 造一个可搜索的按键下拉。
    ///
    /// 做法：DropDownStyle = DropDown（可输入）+ 文本变化时重建 Items。
    /// 用重建 Items 而不是隐藏项，是因为 ComboBox 没有"隐藏某一项"的能力。
    /// </summary>
    public static ComboBox Make()
    {
        var cb = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDown,
            AutoCompleteMode = AutoCompleteMode.None,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            Margin = new Padding(0, 2, 6, 2),
        };
        Fill(cb, "");
        cb.SelectedIndex = 0;

        // ★★ 必须挂上自绘处理器，否则下拉列表**整个是黑的**。
        //
        //   原因：DarkTheme.Apply()/Style() 会把 ComboBox 设成
        //   `DrawMode = OwnerDrawFixed`（因为默认样式下选中项文字看不见），
        //   而**真正负责画每一项的 DrawItem 处理器是 DarkTheme.HookCombo() 挂的**。
        //   HookAllTabs(窗体) 只扫**那个窗体自己的**控件树 ——
        //   而映射弹面板（SlotMapPopup）是之后新建的独立窗体，
        //   它里面的下拉框没人给挂 DrawItem，于是每一项都不画，只剩一片黑底。
        //
        //   在这里挂最保险：**谁造下拉框谁负责**，不依赖调用方记得再调一次。
        DarkTheme.HookCombo(cb);

        bool updating = false;

        cb.TextUpdate += (_, _) =>
        {
            if (updating) return;
            updating = true;
            try
            {
                var typed = cb.Text;
                Fill(cb, typed);
                if (!cb.DroppedDown) cb.DroppedDown = true;

                cb.Text = typed;
                cb.SelectionStart = typed.Length;
                cb.SelectionLength = 0;
            }
            finally { updating = false; }
        };

        // 敲回车就选中当前高亮项
        cb.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            if (cb.SelectedIndex >= 0) { e.SuppressKeyPress = true; cb.DroppedDown = false; }
        };

        return cb;
    }

    /// <summary>按关键字填下拉项；空关键字就是全部</summary>
    public static void Fill(ComboBox cb, string filter)
    {
        var keep = cb.Text;

        cb.BeginUpdate();
        cb.Items.Clear();
        cb.Items.AddRange(KeyCodes.Search(filter).Cast<object>().ToArray());
        cb.EndUpdate();

        if (cb.Items.Count > 0) cb.SelectedIndex = 0;
        cb.Text = keep;
    }

    public static KeyOption Selected(ComboBox cb) => cb.SelectedItem as KeyOption ?? KeyCodes.None;

    public static byte SelectedCode(ComboBox cb) => Selected(cb).Code;

    public static byte SelectedMod(ComboBox cb) => Selected(cb).Modifier;

    /// <summary>
    /// 在下拉里选中 (code, mod)。
    ///
    /// ★ 关键：**找不到时绝不能静默归零**。
    ///   如果配置里的 combo 在当前列表里没有对应项（比如以后加/减了组合键、
    ///   或有人手工改过配置文件），直接 `SelectedIndex = 0`（不映射）
    ///   会让用户一点保存就把原值抹掉 —— 和"保存一次宏就清零"是同一类数据损失。
    ///
    ///   所以退回顺序是：
    ///     1. 精确匹配 (code, mod)
    ///     2. 只按 code 匹配（保住键码，修饰键对不上就先丢修饰键）
    ///     3. 都不行就**临时插一个"未知"项**，原值照样显示出来、也能原样写回去
    /// </summary>
    public static void SelectByKey(ComboBox cb, byte code, byte mod = 0)
    {
        SelectByKeyCore(cb, code, mod);

        // ★ 收尾必须取消文本选中：给 ComboBox.Text 赋值会顺手**全选**，
        //   于是下拉里的当前值显示成整条蓝色高亮，看着像"这一项被选中了"，
        //   而不是"当前值是这个"。多个下拉并排时尤其乱。
        cb.SelectionStart = cb.Text.Length;
        cb.SelectionLength = 0;
    }

    private static void SelectByKeyCore(ComboBox cb, byte code, byte mod)
    {
        if (code == 0 && mod == 0)
        {
            var none = KeyCodes.Find(0, 0);
            EnsureItem(cb, none);
            cb.SelectedItem = none;
            cb.Text = none.ToString();
            return;
        }

        var exact = KeyCodes.Find(code, mod);
        if (exact.Code == code && exact.Modifier == mod)
        {
            EnsureItem(cb, exact);
            cb.SelectedItem = exact;
            cb.Text = exact.ToString();
            return;
        }

        // 只按键码找
        var byCode = KeyCodes.All.FirstOrDefault(k => k.Code == code && k.Modifier == 0)
                  ?? KeyCodes.All.FirstOrDefault(k => k.Code == code);
        if (byCode is not null)
        {
            EnsureItem(cb, byCode);
            cb.SelectedItem = byCode;
            cb.Text = byCode.ToString();
            return;
        }

        // 完全不认识这个键码：插一个"未知"项把它保住
        var unknown = new KeyOption($"未知(0x{code:X2}/mod 0x{mod:X2})", code, mod);
        EnsureItem(cb, unknown);
        cb.SelectedItem = unknown;
        cb.Text = unknown.ToString();
    }

    /// <summary>确保下拉里有这一项（过滤后可能被筛掉了）</summary>
    public static void EnsureItem(ComboBox cb, KeyOption k)
    {
        if (!cb.Items.Contains(k)) cb.Items.Insert(0, k);
    }
}
