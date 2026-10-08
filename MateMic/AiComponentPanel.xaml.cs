using System.Windows.Controls;

namespace MateMic;

/// <summary>
/// 「安装 AI 变声组件」的进度对话框内容（由 <see cref="Ui.DialogHost.CreateCustom"/> 承载）。
///
/// 用户把组件包（.zip）拖到主界面后弹出，一边解压一边更新进度。
/// 解压由调用方放在后台线程，这里只负责显示 —— 所以界面不会卡。
/// </summary>
public partial class AiComponentPanel : UserControl
{
    public AiComponentPanel()
    {
        InitializeComponent();
    }

    /// <summary>更新进度（由后台线程经 Dispatcher 调到 UI 线程）。</summary>
    public void SetProgress(int percent, string detail)
    {
        Bar.Value = Math.Clamp(percent, 0, 100);
        DetailText.Text = detail;
    }

    /// <summary>标记完成。</summary>
    public void SetDone(string detail)
    {
        Bar.Value = 100;
        TitleText.Text = "安装完成";
        DetailText.Text = detail;
    }

    /// <summary>标记失败。</summary>
    public void SetFailed(string detail)
    {
        TitleText.Text = "安装失败";
        DetailText.Text = detail;
    }
}
