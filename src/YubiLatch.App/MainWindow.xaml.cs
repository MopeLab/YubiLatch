using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Win32;
using YubiLatch.Core;

namespace YubiLatch.App;

public sealed record KeyItem(Guid Id, string Name, string Detail, Visibility PendingVisibility);

public sealed partial class MainWindow : Window
{
    private readonly ObservableCollection<KeyItem> _keys = [];
    private AccountStatus? _account;
    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();
        if (MicaController.IsSupported()) SystemBackdrop = new MicaBackdrop();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1140, 850));
        KeysList.ItemsSource = _keys;
        Root.Loaded += async (_, _) => await RefreshState();
    }

    private void NavigationChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (OverviewPage == null || args.SelectedItem is not NavigationViewItem item) return;
        var tag = item.Tag as string;
        OverviewPage.Visibility = tag == "overview" ? Visibility.Visible : Visibility.Collapsed;
        KeysPage.Visibility = tag == "keys" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = tag == "settings" ? Visibility.Visible : Visibility.Collapsed;
        RecoveryPage.Visibility = tag == "recovery" ? Visibility.Visible : Visibility.Collapsed;
        PageTitle.Text = tag == "overview" ? "把钥匙，带在身边。" : item.Content.ToString();
        PageSubtitle.Text = tag switch
        {
            "keys" => "每把钥匙独立绑定，每次触摸真实验证。",
            "settings" => "管理你的账户凭据与本机钥匙登录。",
            "recovery" => "给每一种意外，留一条回家的路。",
            _ => "通过 YubiKey 的物理触摸，连接到你的 Windows。"
        };
    }

    private void Show(string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        Notice.Message = message;
        Notice.Severity = severity;
        Notice.IsOpen = true;
    }

    private void Busy(bool value)
    {
        _busy = value;
        Progress.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        RefreshButton.IsEnabled = !value;
        Nav.IsEnabled = !value;
    }

    private async Task<ManagementReply?> Request(ManagementRequest request)
    {
        try
        {
            var reply = await BrokerClient.Send(request);
            if (!reply.Success) Show(reply.Message, InfoBarSeverity.Warning);
            return reply;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Show(L.T("ServiceUnavailable"), InfoBarSeverity.Warning);
            return null;
        }
    }

    private async Task RefreshState()
    {
        if (_busy) return;
        Busy(true);
        try
        {
            var reply = await Request(new());
            if (reply is { Success: true, Account: not null })
            {
                Apply(reply.Account);
                LastChecked.Text = L.Format("LastChecked", DateTime.Now);
            }
            else Readiness.Text = L.T("Unavailable");
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers\{D53C2D8E-51CC-4F5D-88E6-A0CDF5B39C54}");
            ProviderState.Text = L.T(key == null ? "UnregisteredProvider" : "InstalledProvider");
        }
        finally { Busy(false); }
    }

    private void Apply(AccountStatus status)
    {
        _account = status;
        var active = status.Keys.Count(k => k.State == BindingState.Active);
        Readiness.Text = L.T(!status.Enrolled ? "NotEnrolled" : !status.Enabled ? "Paused" : active == 0 ? "NoActiveKey" : "Ready");
        ReadinessDetail.Text = status.Enrolled ? status.UserName : "绑定后，账户密码由本地 SYSTEM 服务加密保存。";
        KeyCount.Text = active.ToString();
        BackupState.Text = L.T(active > 1 ? "BackupReady" : "BackupRecommended");
        EnabledText.Text = L.T(status.Enabled ? "Ready" : "Paused");
        AccountText.Text = status.Enrolled ? status.UserName : L.T("NotEnrolled");
        _keys.Clear();
        foreach (var key in status.Keys)
            _keys.Add(new(key.Id, key.Name,
                $"{key.Serial} · {L.T(key.State == BindingState.Active ? "Active" : "Pending")} · {key.CreatedUtc.ToLocalTime():yyyy-MM-dd}",
                key.State == BindingState.Pending ? Visibility.Visible : Visibility.Collapsed));
    }

    private async void RefreshClick(object sender, RoutedEventArgs args) => await RefreshState();

    private async void DevicesClick(object sender, RoutedEventArgs args)
    {
        if (_busy) return;
        Busy(true);
        try
        {
            var reply = await Request(new() { Operation = "Devices" });
            if (reply is not { Success: true }) return;
            if (reply?.Devices is { Count: > 0 } devices)
                DevicesText.Text = string.Join(Environment.NewLine, devices.Select(d => $"{d.Name} · {d.Serial} · {L.T(d.Slot2Occupied ? "OccupiedSlot" : "EmptySlot")}"));
            else DevicesText.Text = L.T("NoDevices");
        }
        finally { Busy(false); }
    }

    private ContentDialog Dialog(string title, object content, string primary) => new()
    {
        XamlRoot = Root.XamlRoot, Title = title, Content = content,
        PrimaryButtonText = primary, CloseButtonText = L.T("Cancel"), DefaultButton = ContentDialogButton.Close
    };

    private async void EnrollClick(object sender, RoutedEventArgs args)
    {
        if (_busy) return;
        Busy(true);
        var devices = await Request(new() { Operation = "Devices" });
        Busy(false);
        if (devices is not { Success: true }) return;
        if (devices?.Devices is not { Count: > 0 }) { Show(L.T("NoDevices"), InfoBarSeverity.Warning); return; }
        var eligible = devices.Devices.Where(d => !d.Slot2Occupied).ToArray();
        if (eligible.Length == 0) { Show(L.T("OccupiedSlot"), InfoBarSeverity.Warning); return; }

        var panel = new StackPanel { Spacing = 16, MaxWidth = 440 };
        var chooser = new ComboBox { Header = L.T("Device"), HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var device in eligible) chooser.Items.Add(new ComboBoxItem { Content = $"{device.Name} · {device.Serial}", Tag = device });
        chooser.SelectedIndex = eligible.Length == 1 ? 0 : -1;
        var name = new TextBox { Header = L.T("KeyName"), MaxLength = 64, Text = "我的 YubiKey" };
        var email = new TextBox { Header = L.T("AccountEmail"), MaxLength = 255, Text = _account?.UserName.Replace("MicrosoftAccount\\", "", StringComparison.OrdinalIgnoreCase) ?? "", IsReadOnly = _account?.Enrolled == true };
        var password = new PasswordBox { Header = L.T("Password"), MaxLength = 512 };
        var recovery = new CheckBox { Content = new TextBlock { Text = L.T("RecoveryConsent"), TextWrapping = TextWrapping.Wrap } };
        var consent = new CheckBox { Content = new TextBlock { Text = L.T("SlotConsent"), TextWrapping = TextWrapping.Wrap } };
        panel.Children.Add(chooser); panel.Children.Add(name); panel.Children.Add(email); panel.Children.Add(password); panel.Children.Add(recovery); panel.Children.Add(consent);
        var dialog = Dialog(L.T("BindTitle"), new ScrollViewer { Content = panel, MaxHeight = 540 }, L.T("BindAction"));
        ManagementRequest? request = null;
        dialog.PrimaryButtonClick += (_, e) =>
        {
            if (chooser.SelectedItem is not ComboBoxItem { Tag: DeviceStatus device } || string.IsNullOrWhiteSpace(email.Text)
                || password.Password.Length == 0 || string.IsNullOrWhiteSpace(name.Text) || recovery.IsChecked != true || consent.IsChecked != true)
            { e.Cancel = true; Show(L.T("MissingFields"), InfoBarSeverity.Warning); return; }
            request = new() { Operation = "Enroll", UserName = email.Text, Password = password.Password, KeyName = name.Text, Serial = device.Serial, RecoveryConfirmed = true, EmptySlotConsent = true };
        };
        await dialog.ShowAsync();
        password.Password = "";
        if (request == null) return;
        Show(L.T("TouchNow"));
        await Mutate(request);
    }

    private async Task<ManagementRequest?> PasswordPrompt(string operation, string title, string description, Guid keyId = default, bool enabled = false)
    {
        var panel = new StackPanel { Spacing = 16, MaxWidth = 440 };
        panel.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap });
        var email = new TextBox { Header = L.T("AccountEmail"), Text = _account?.UserName ?? "", MaxLength = 272, IsReadOnly = _account?.Enrolled == true };
        var password = new PasswordBox { Header = L.T("Password"), MaxLength = 512 };
        panel.Children.Add(email); panel.Children.Add(password);
        var recovery = new CheckBox { Content = new TextBlock { Text = L.T("RecoveryConsent"), TextWrapping = TextWrapping.Wrap } };
        if (enabled) panel.Children.Add(recovery);
        ManagementRequest? request = null;
        var dialog = Dialog(title, panel, L.T("Continue"));
        dialog.PrimaryButtonClick += (_, e) =>
        {
            if (password.Password.Length == 0 || email.Text.Length == 0 || enabled && recovery.IsChecked != true) { e.Cancel = true; return; }
            request = new() { Operation = operation, UserName = email.Text, Password = password.Password, KeyId = keyId, Enabled = enabled, RecoveryConfirmed = recovery.IsChecked == true };
        };
        await dialog.ShowAsync();
        password.Password = "";
        return request;
    }

    private async Task Mutate(ManagementRequest request)
    {
        if (_busy) return;
        Busy(true);
        try
        {
            var reply = await Request(request);
            if (reply is { Success: true })
            {
                if (reply.Account != null) Apply(reply.Account);
                Show(reply.Message, InfoBarSeverity.Success);
                LastChecked.Text = L.Format("LastChecked", DateTime.Now);
                if (reply.SlotManagementCode != null)
                {
                    var explanation = new StackPanel { Spacing = 16, MaxWidth = 400 };
                    explanation.Children.Add(new TextBlock { Text = "请将此码保存到密码管理器。它只管理此钥匙的 OTP 槽位 2，不能登录 Windows。撤销绑定、卸载清除数据或重装系统前务必保存。", TextWrapping = TextWrapping.Wrap });
                    explanation.Children.Add(new TextBox { Text = reply.SlotManagementCode, IsReadOnly = true, FontSize = 24, FontFamily = new FontFamily("Cascadia Mono") });
                    await new ContentDialog { XamlRoot = Root.XamlRoot, Title = "槽位管理码", Content = explanation, CloseButtonText = L.T("Close") }.ShowAsync();
                }
            }
        }
        finally { Busy(false); }
    }

    private async void ResumeClick(object sender, RoutedEventArgs args)
    {
        if (_busy || sender is not Button { Tag: Guid id }) return;
        var request = await PasswordPrompt("ResumePending", "恢复待验证绑定", L.T("TouchNow"), id);
        if (request != null) { Show(L.T("TouchNow")); await Mutate(request); }
    }
    private async void RemoveClick(object sender, RoutedEventArgs args)
    {
        if (_busy || sender is not Button { Tag: Guid id }) return;
        var request = await PasswordPrompt("RemoveKey", L.T("RemoveTitle"), L.T("RemoveBody"), id);
        if (request != null) await Mutate(request);
    }
    private async void ManagementCodeClick(object sender, RoutedEventArgs args)
    {
        if (_busy || sender is not Button { Tag: Guid id }) return;
        var request = await PasswordPrompt("GetSlotManagementCode", "查看槽位管理码", "管理码用于恢复或重新配置 OTP 槽位，不是 Windows 登录凭据。", id);
        if (request != null) await Mutate(request);
    }
    private async void EnableClick(object sender, RoutedEventArgs args)
    {
        if (_busy) return;
        var enable = (sender as Button)?.Tag as string == "enable";
        var request = await PasswordPrompt("SetEnabled", enable ? "启用钥匙登录" : "暂停钥匙登录", L.T("Reauthenticate"), enabled: enable);
        if (request != null) await Mutate(request);
    }
    private async void UpdatePasswordClick(object sender, RoutedEventArgs args)
    {
        if (_busy) return;
        var request = await PasswordPrompt("UpdatePassword", "更新账户密码", L.T("PasswordUpdated"));
        if (request != null) await Mutate(request);
    }
    private async void ForgetClick(object sender, RoutedEventArgs args)
    {
        if (_busy) return;
        var request = await PasswordPrompt("ForgetAll", L.T("ForgetTitle"), L.T("ForgetBody"));
        if (request != null) await Mutate(request);
    }
    private void OpenRecoveryClick(object sender, RoutedEventArgs args)
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "docs", "recovery.md"));
        if (File.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        else Show("请参阅仓库或安装包 docs/recovery.md。", InfoBarSeverity.Informational);
    }
}
