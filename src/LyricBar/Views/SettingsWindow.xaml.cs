using System.Windows;
using LyricBar.Services;

namespace LyricBar.Views;

/// <summary>
/// 设置窗口：字号、歌词偏移、点击穿透、开机自启。
/// 字号是「实时预览」的——拖动滑块立刻生效，点保存才写入配置文件，
/// 点取消则把预览改回原值。
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly MainWindow _bar;
    private readonly AppSettings _settings;
    /// <summary>打开设置时的原字号，用于「取消」时还原预览。</summary>
    private readonly double _originalFontSize;

    public SettingsWindow(MainWindow bar, AppSettings settings)
    {
        InitializeComponent();
        _bar = bar;
        _settings = settings;
        _originalFontSize = settings.FontSize;

        // 先赋值再挂事件，避免初始化时就触发一次预览
        FontSizeSlider.Value = settings.FontSize;
        FontSizeSlider.ValueChanged += FontSizeSlider_ValueChanged;
        FontSizeLabel.Text = ((int)settings.FontSize).ToString();
        OffsetBox.Text = settings.OffsetMs.ToString();
        ClickThroughCheck.IsChecked = settings.ClickThrough;
        AutoStartCheck.IsChecked = SettingsService.IsAutoStartEnabled();
    }

    /// <summary>拖动字号滑块时，歌条上的文字立即跟着变大变小。</summary>
    private void FontSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        FontSizeLabel.Text = ((int)e.NewValue).ToString();
        _bar.PreviewFontSize(e.NewValue);
    }

    /// <summary>保存：写入设置、应用开机自启、让歌条立即套用新配置。</summary>
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _settings.FontSize = FontSizeSlider.Value;
        // 偏移限制在 ±60 秒，避免误输入导致歌词跑飞
        if (int.TryParse(OffsetBox.Text.Trim(), out var offset))
            _settings.OffsetMs = Math.Clamp(offset, -60000, 60000);
        _settings.ClickThrough = ClickThroughCheck.IsChecked == true;

        var autoStart = AutoStartCheck.IsChecked == true;
        try
        {
            SettingsService.ApplyAutoStart(autoStart);
            _settings.AutoStart = autoStart;
        }
        catch
        {
            // 写注册表可能因为权限失败，提示但不影响其它设置保存
            System.Windows.MessageBox.Show(this, "开机自启设置失败，请检查权限。", "LyricBar",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        _bar.ApplySettings(_settings);
        Close();
    }

    /// <summary>取消：把实时预览改回打开窗口时的字号，不保存任何改动。</summary>
    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _bar.PreviewFontSize(_originalFontSize);
        Close();
    }
}
