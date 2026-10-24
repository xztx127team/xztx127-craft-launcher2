namespace XCL2.App.Models;

public partial class AppConfig
{
    [ExperiencePreference("输入与编辑", "首尾空格提示", Description = "只提示地址或路径中的多余空格，不自动修改内容。")]
    public bool Ux50InputWhitespaceHint { get; set; } = true;
    [ExperiencePreference("输入与编辑", "文件名非法字符定位")]
    public bool Ux50InputFilenameValidation { get; set; } = true;
    [ExperiencePreference("输入与编辑", "内存单位识别", Description = "内存框允许 2048MB、2GB；纯数字仍按 MB 解释。")]
    public bool Ux50InputMemoryUnits { get; set; } = true;
    [ExperiencePreference("输入与编辑", "滑块精确数值输入")]
    public bool Ux50InputPreciseSliders { get; set; } = true;
    [ExperiencePreference("输入与编辑", "拖入路径", Description = "拖入单个文件或文件夹只填写路径，不打开、导入或执行。")]
    public bool Ux50InputPathDrop { get; set; } = true;
    [ExperiencePreference("输入与编辑", "记住文件选择目录", Description = "按用途单独记住目录，不保存选中文件。可在下方清除。")]
    public bool Ux50InputRememberPickerDirectory { get; set; }
    [ExperiencePreference("输入与编辑", "就地复制反馈")]
    public bool Ux50InputLocalCopyFeedback { get; set; } = true;
    [ExperiencePreference("输入与编辑", "Esc 撤销当前文本编辑", Description = "仅恢复当前焦点框本次编辑前的内容，不撤销其他设置。")]
    public bool Ux50InputEscapeRevert { get; set; } = true;
    [ExperiencePreference("输入与编辑", "单行输入的多行粘贴预览", Description = "不处理密码框、命令、源码、密钥及多行编辑框。")]
    public bool Ux50InputMultilinePastePreview { get; set; } = true;
    [ExperiencePreference("隐私与输入", "允许按住查看密码或密钥", Description = "按住密码框的眼睛按钮临时显示，松开或失去焦点立即隐藏。")]
    public bool Ux50InputHoldRevealSecret { get; set; }
    public Dictionary<string, string> Ux50PickerDirectories { get; set; } = new(StringComparer.Ordinal);
}
