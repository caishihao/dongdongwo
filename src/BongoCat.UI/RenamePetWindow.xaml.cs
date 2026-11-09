using System.Windows;
using BongoCat.Core.Save;

namespace BongoCat.UI;

/// <summary>
/// E9 重命名对话框：只收集文本，清洗与落盘统一由 <c>TrayCommandController.SetPetName</c>
/// 完成（单一写入路径，与首登选择器共用 <see cref="PetNameRules"/>）。
/// </summary>
public partial class RenamePetWindow : Window
{
    public RenamePetWindow(string currentName, string defaultName)
    {
        InitializeComponent();
        PetNameBox.Text = currentName;
        PetNameBox.CaretIndex = PetNameBox.Text.Length;
        HintText.Text = $"留空将恢复默认名「{defaultName}」";
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        PetNameBox.Focus();
        PetNameBox.SelectAll();
    }

    /// <summary>用户输入的原始文本（未清洗；调用方负责 Sanitize）。</summary>
    public string PetName => PetNameBox.Text;

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
