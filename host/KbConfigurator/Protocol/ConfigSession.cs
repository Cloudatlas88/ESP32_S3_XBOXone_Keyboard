namespace KbConfigurator.Protocol;

/// <summary>
/// 一个配置编辑页签需要实现的接口。
///
/// 界面上有【按键映射】和【宏编辑】两个页签，但它们编辑的是同一份
/// 396 字节配置。如果各自持有一份拷贝，就会出现
/// 「在宏页签改完保存 → 按键映射被重置」这类互相覆盖的事故。
/// 所以统一由 <see cref="ConfigSession"/> 持有一份 <see cref="KbConfig"/>，
/// 各页签只负责「把模型刷到界面」和「把界面写回模型」。
/// </summary>
public interface IConfigEditor
{
    /// <summary>把模型刷到界面控件（从设备读取 / 恢复默认后调用）</summary>
    void LoadFromModel(KbConfig cfg);

    /// <summary>把界面控件的值写回模型（保存到设备前调用）</summary>
    void WriteToModel(KbConfig cfg);
}

/// <summary>
/// 上位机内存里的「当前配置」—— 所有页签共享的唯一真源。
/// </summary>
public sealed class ConfigSession
{
    private readonly List<IConfigEditor> _editors = new();

    /// <summary>当前配置（共享实例；页签只改字段，不要整体替换）</summary>
    public KbConfig Config { get; private set; } = KbConfig.CreateDefault();

    /// <summary>注册一个页签</summary>
    public void Register(IConfigEditor e)
    {
        _editors.Add(e);
        e.LoadFromModel(Config);
    }

    /// <summary>整体替换配置（读取 / 恢复默认），并刷新所有页签</summary>
    public void Replace(KbConfig cfg)
    {
        Config = cfg;
        Normalize();
        foreach (var e in _editors) e.LoadFromModel(Config);
    }

    /// <summary>让所有页签把界面上的值写回 <see cref="Config"/>（保存前调用）</summary>
    public void CollectFromUi()
    {
        foreach (var e in _editors) e.WriteToModel(Config);
        Normalize();
    }

    /// <summary>
    /// 把废弃字段固化。
    ///
    /// ★ 放在**会话层**而不是某个页签里：以前是「按键映射」页的 WriteToModel 顺手做的，
    ///   于是"这个字段会不会被规范化"取决于那一页在不在 ——
    ///   合并页签时很容易把它一起弄丢。放到会话层就与界面无关了。
    /// </summary>
    private void Normalize() => SlotMap.NormalizeDeprecatedFields(Config);
}
