using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Input;
using Polyline = Microsoft.UI.Xaml.Shapes.Polyline;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using QRCoder;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.Graphics.Imaging;
using Windows.System;
using Windows.UI;
using Windows.UI.ViewManagement;
using Microsoft.Win32;
using WinRT.Interop;

namespace Nearlink.Native;

public sealed partial class MainWindow : Window
{
    private readonly TransferHost _host = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher;
    private readonly UISettings _uiSettings = new();
    private readonly List<string> _pageHistory = ["chat"];
    private readonly HashSet<string> _messageIds = [];
    private readonly SemaphoreSlim _incomingApprovalDialog = new(1, 1);
    private AppNotificationManager? _notificationManager;
    private DesktopSyncClient? _syncClient;
    private readonly LanDiscoveryService _discovery;
    private readonly Dictionary<string, RemoteComputer> _pairedComputers;
    private readonly Dictionary<string, RemoteDesktopSession> _remoteSessions = new(StringComparer.Ordinal);
    private readonly HashSet<string> _onlineRemoteIds = new(StringComparer.Ordinal);
    private IReadOnlyList<LanDiscoveryService.NearbyComputer> _discoveredComputers = [];
    private string? _activeRemoteId;
    private bool _updatingConversationSelector;
    private AppWindow? _appWindow;
    private ThemePreference _themePreference;
    private int _pageHistoryIndex;
    private string _currentPage = "chat";
    private bool _started;
    private bool _deviceEditMode;
    private bool _isQrExpanded = true;
    private bool _suppressThemeControls;
    private bool _suppressExplorerToggle;
    private bool _hostReady;
    private bool _explorerSendInProgress;
    private readonly List<string> _queuedExplorerPaths = [];
    private Storyboard? _qrStoryboard;
    private Storyboard? _radarPulseStoryboard;

    public ObservableCollection<DeviceView> Devices { get; } = [];
    public ObservableCollection<PairedDeviceView> PairedDevices { get; } = [];
    public ObservableCollection<NearbyDeviceView> NearbyDevices { get; } = [];
    public ObservableCollection<MessageView> Messages { get; } = [];
    public ObservableCollection<MessageView> Files { get; } = [];

    public MainWindow()
    {
        InitializeComponent();
        _radarPulseStoryboard = RadarStage.Resources["RadarPulseStoryboard"] as Storyboard;
        _suppressExplorerToggle = true;
        ExplorerContextMenuToggle.IsOn = ExplorerShellIntegration.IsEnabled;
        ExplorerContextMenuStatus.Text = ExplorerShellIntegration.IsEnabled
            ? "已为当前 Windows 用户添加右键菜单。Windows 11 可能需要在“显示更多选项”中打开。"
            : "未在资源管理器中显示发送入口。";
        _suppressExplorerToggle = false;
        SettingsNavigation.SelectedIndex = 0;
        _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        try
        {
            _notificationManager = AppNotificationManager.Default;
            _notificationManager.NotificationInvoked += (_, args) =>
            {
                var argument = args.Argument;
                _dispatcher.TryEnqueue(() => OpenMessageNotification(argument));
            };
            _notificationManager.Register();
        }
        catch { _notificationManager = null; }
        _pairedComputers = LoadPairedComputers();
        RefreshConversationSelector();
        _discovery = new LanDiscoveryService(_host.DeviceId, _host.DeviceName, () => _host.BaseUrl);
        _discovery.DevicesChanged += devices => _dispatcher.TryEnqueue(() => UpdateNearbyDevices(devices));
        _host.PairApprovalRequested = ApprovePairRequestAsync;
        // Incoming files are accepted silently; messages are surfaced by OS notifications.
        _host.IncomingTransferApprovalRequested = (_, _) => Task.FromResult(true);
        _host.PeerSessionEvent += (peerId, message) => _dispatcher.TryEnqueue(() => HandleServerEvent(message, peerId, localPeerStore: true));
        _host.PhoneSessionEvent += (sessionId, message) => _dispatcher.TryEnqueue(() => HandleServerEvent(message, sessionId, localPhoneStore: true));
        _host.ConnectedDevicesChanged += devices => _dispatcher.TryEnqueue(() => UpdateConnectedDevices(devices));
        SystemBackdrop = new MicaBackdrop();
        ConfigureTitleBar();
        _themePreference = LoadThemePreference();
        ApplyTheme(_themePreference, persist: false);
        UpdatePageNavigation();
        Messages.CollectionChanged += (_, _) =>
        {
            if (Messages.Count > 0) MessagesList.ScrollIntoView(Messages[^1]);
        };
        Files.CollectionChanged += (_, _) => UpdateFileSelectionUi();
        _uiSettings.ColorValuesChanged += SystemColorsChanged;
        RootGrid.Loaded += Window_Loaded;
        Closed += Window_Closed;
    }

    private void SettingsNavigation_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var section = (SettingsNavigation.SelectedItem as ListViewItem)?.Tag?.ToString();
        AppearanceSettingsSection.Visibility = section == "appearance" ? Visibility.Visible : Visibility.Collapsed;
        DeviceIdentitySettingsSection.Visibility = section == "identity" ? Visibility.Visible : Visibility.Collapsed;
        TransferSettingsSection.Visibility = section == "transfer" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ExplorerContextMenuToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressExplorerToggle) return;
        try
        {
            ExplorerShellIntegration.SetEnabled(ExplorerContextMenuToggle.IsOn);
            ExplorerContextMenuStatus.Text = ExplorerContextMenuToggle.IsOn
                ? "右键菜单已启用。Windows 11 可能需要在“显示更多选项”中打开。"
                : "右键菜单已关闭。";
        }
        catch (Exception ex)
        {
            _suppressExplorerToggle = true;
            ExplorerContextMenuToggle.IsOn = !ExplorerContextMenuToggle.IsOn;
            _suppressExplorerToggle = false;
            ExplorerContextMenuStatus.Text = $"无法更新右键菜单：{ex.Message}";
        }
    }

    private void ConfigureTitleBar()
    {
        ExtendsContentIntoTitleBar = true;
        var hwnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        var iconPath = EnsureWindowIconFile();
        if (iconPath is not null)
        {
            _appWindow.SetIcon(iconPath);
            _appWindow.SetTaskbarIcon(iconPath);
        }
        var titleBar = _appWindow.TitleBar;
        titleBar.ExtendsContentIntoTitleBar = true;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonForegroundColor = ColorHelper.FromArgb(255, 190, 194, 198);
        titleBar.ButtonHoverBackgroundColor = ColorHelper.FromArgb(32, 128, 128, 128);
        titleBar.ButtonHoverForegroundColor = Colors.White;
        titleBar.ButtonPressedBackgroundColor = ColorHelper.FromArgb(56, 128, 128, 128);
        SetTitleBar(TitleBar);
    }

    private static string? EnsureWindowIconFile()
    {
        try
        {
            using var resource = typeof(MainWindow).Assembly.GetManifestResourceStream("Nearlink.Native.Assets.nearlink.ico");
            if (resource is null) return null;
            using var memory = new MemoryStream();
            resource.CopyTo(memory);
            var bytes = memory.ToArray();
            var stamp = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))[..12];
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nearlink", "icons");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"nearlink-{stamp}.ico");
            if (!File.Exists(path)) File.WriteAllBytes(path, bytes);
            return path;
        }
        catch { return null; }
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _radarPulseStoryboard?.Begin();
        if (_started) return;
        _started = true;
        try
        {
            await _host.StartAsync(_stop.Token);
            _hostReady = true;
            _discovery.Start();
            DeviceNameTextBox.Text = _host.DeviceName;
            PairCodeText.Text = _host.Code;
            LinkText.Text = _host.PairUrl;
            SettingsLinkText.Text = _host.PairUrl;
            QrImage.Source = await BuildQrImageAsync(_host.PairUrl);
            StatusText.Text = "等待手机或其他电脑连接";
            StartSyncClient();
            LoadLocalConversationHistory();
            _ = CheckForWindowsUpdatesPeriodicallyAsync();
            if (_queuedExplorerPaths.Count > 0)
            {
                var queued = _queuedExplorerPaths.ToArray();
                _queuedExplorerPaths.Clear();
                HandleExplorerFiles(queued);
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = "启动失败";
            LinkText.Text = ex.Message;
            await ShowDialogAsync("Nearlink 无法启动局域网服务", ex.Message);
        }
    }

    private void StartSyncClient()
    {
        _syncClient = new DesktopSyncClient(_host.LocalBaseUrl, _host.Code, _host.DesktopSessionId, _host.DeviceName);
        _syncClient.EventReceived += message => _dispatcher.TryEnqueue(() => HandleServerEvent(message));
        _syncClient.StatusChanged += status => _dispatcher.TryEnqueue(() => StatusText.Text = status);
        _syncClient.Start();
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e) => await CheckForWindowsUpdateAsync(showCurrent: true);

    private async Task CheckForWindowsUpdatesPeriodicallyAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(15), _stop.Token);
            while (!_stop.IsCancellationRequested)
            {
                await CheckForWindowsUpdateAsync(showCurrent: false);
                await Task.Delay(TimeSpan.FromMinutes(15), _stop.Token);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    private async Task CheckForWindowsUpdateAsync(bool showCurrent)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            var release = await client.GetFromJsonAsync<UpdateManifest>("https://charlen.top/api/v1/releases/windows", _stop.Token);
            if (release is null || !Version.TryParse(release.Version.TrimStart('v'), out var latest))
                throw new InvalidDataException("服务器返回的版本信息无效");
            var current = typeof(MainWindow).Assembly.GetName().Version ?? new Version(1, 0, 0);
            if (latest <= current)
            {
                if (showCurrent) await ShowDialogAsync("已是最新版本", $"Nearlink {current.ToString(3)} 已是最新版本。");
                return;
            }
            var dialog = new ContentDialog
            {
                Title = $"Nearlink 有新版本 {release.Version}",
                Content = string.IsNullOrWhiteSpace(release.Notes) ? "现在下载并安装更新？" : release.Notes,
                PrimaryButtonText = "下载并更新",
                CloseButtonText = "稍后",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = RootGrid.XamlRoot,
                RequestedTheme = RootGrid.ActualTheme
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                try { await DownloadAndInstallWindowsUpdateAsync(client, release); }
                catch (Exception ex) { await ShowDialogAsync("更新失败", $"Nearlink 更新未能安装：{ex.Message}"); }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (showCurrent) await ShowDialogAsync("检查更新失败", $"暂时无法检查更新：{ex.Message}");
        }
    }

    private async Task DownloadAndInstallWindowsUpdateAsync(HttpClient client, UpdateManifest release)
    {
        if (!Uri.TryCreate(new Uri("https://charlen.top"), release.DownloadUrl, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.Host != "charlen.top")
            throw new InvalidDataException("更新下载地址无效");
        var processPath = Environment.ProcessPath ?? throw new InvalidOperationException("无法定位 Nearlink 程序文件");
        var installDirectory = Path.GetDirectoryName(processPath)!;
        var updateDirectory = Path.Combine(Path.GetTempPath(), "Nearlink-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(updateDirectory);
        try
        {
            using var downloadClient = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            await using (var response = await downloadClient.GetStreamAsync(uri, _stop.Token))
            await using (var output = File.Create(Path.Combine(updateDirectory, "update.zip")))
                await response.CopyToAsync(output, _stop.Token);
            var archivePath = Path.Combine(updateDirectory, "update.zip");
            await using (var archive = File.OpenRead(archivePath))
            {
                var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(archive, _stop.Token));
                if (string.IsNullOrWhiteSpace(release.Sha256) || !CryptographicOperations.FixedTimeEquals(
                        Convert.FromHexString(actualHash), Convert.FromHexString(release.Sha256)))
                    throw new InvalidDataException("更新文件校验失败");
            }
            var extractionDirectory = Path.Combine(updateDirectory, "package");
            ZipFile.ExtractToDirectory(archivePath, extractionDirectory);
            var updatedExe = Path.Combine(extractionDirectory, "Nearlink.exe");
            if (!File.Exists(updatedExe)) throw new InvalidDataException("更新包中缺少 Nearlink.exe");
            var scriptPath = Path.Combine(updateDirectory, "install-update.ps1");
            var script = $"$ErrorActionPreference='Stop'\nwhile (Get-Process -Id {Environment.ProcessId} -ErrorAction SilentlyContinue) {{ Start-Sleep -Milliseconds 300 }}\ntry {{ Copy-Item -LiteralPath '{EscapePowerShell(updatedExe)}' -Destination '{EscapePowerShell(processPath)}' -Force; Start-Process -FilePath '{EscapePowerShell(processPath)}'; Remove-Item -LiteralPath '{EscapePowerShell(updateDirectory)}' -Recurse -Force }} catch {{ Start-Process -FilePath '{EscapePowerShell(processPath)}' }}\n";
            await File.WriteAllTextAsync(scriptPath, script, _stop.Token);
            Process.Start(new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                ArgumentList = { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scriptPath }
            });
            Application.Current.Exit();
        }
        catch
        {
            try { Directory.Delete(updateDirectory, recursive: true); } catch { }
            throw;
        }
    }

    private static string EscapePowerShell(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private sealed record UpdateManifest(string Platform, string Version, string Sha256, string DownloadUrl, string? Notes);

    private async void SaveDeviceName_Click(object sender, RoutedEventArgs e)
    {
        if (!_host.TrySetDeviceName(DeviceNameTextBox.Text, out var error))
        {
            DeviceNameSaveStatus.Text = error;
            return;
        }
        var name = _host.DeviceName;
        _discovery.SetDeviceName(name);
        if (_syncClient is not null) await _syncClient.UpdateDeviceNameAsync(name);
        foreach (var session in _remoteSessions.Values)
            await session.UpdateLocalDeviceNameAsync(name);
        DeviceNameSaveStatus.Text = "已保存；连接设备名称已更新";
    }

    private static async Task<BitmapImage> BuildQrImageAsync(string value)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(value, QRCodeGenerator.ECCLevel.Q);
        var bytes = new PngByteQRCode(data).GetGraphic(8);
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }
        stream.Seek(0);
        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(stream);
        return bitmap;
    }

    private void HandleServerEvent(TransferHost.ServerEvent message, string? sourcePeerId = null, bool localPeerStore = false, bool localPhoneStore = false)
    {
        if (message.Type == "message" && message.Item is { } incoming && incoming.FromId != _host.DeviceId)
            NotifyReceivedMessage(incoming, sourcePeerId, localPhoneStore);
        if (message.Type != "presence" && sourcePeerId != _activeRemoteId && !(localPhoneStore && _activeRemoteId is null)) return;
        if (message.Type == "presence" && sourcePeerId is not null) return;
        switch (message.Type)
        {
            case "presence":
                UpdateConnectedDevices(message.Devices ?? []);
                break;
            case "message":
            case "file":
                if (message.Item is not null) AddMessage(message.Item, sourcePeerId, localPeerStore, localPhoneStore);
                break;
            case "history":
                foreach (var item in message.Items ?? []) AddMessage(item, sourcePeerId, localPeerStore, localPhoneStore);
                break;
        }
    }

    private void NotifyReceivedMessage(TransferStore.Entry item, string? sourcePeerId, bool localPhoneStore)
    {
        try
        {
            var body = item.Text ?? string.Empty;
            if (string.IsNullOrWhiteSpace(body)) return;
            var notification = new AppNotificationBuilder()
                .AddArgument("action", "open-message")
                .AddArgument("peer", sourcePeerId ?? string.Empty)
                .AddArgument("phone", localPhoneStore ? "1" : "0")
                .AddText($"Nearlink · {item.From}")
                .AddText(body)
                .BuildNotification();
            _notificationManager?.Show(notification);
        }
        catch { }
    }

    private void OpenMessageNotification(string argument)
    {
        try
        {
            var values = argument.Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Split('=', 2))
                .Where(part => part.Length == 2)
                .ToDictionary(part => Uri.UnescapeDataString(part[0]), part => Uri.UnescapeDataString(part[1]), StringComparer.OrdinalIgnoreCase);
            if (values.TryGetValue("phone", out var phone) && phone == "1")
                OpenLocalSession();
            else if (values.TryGetValue("peer", out var peerId) && _pairedComputers.TryGetValue(peerId, out var computer))
                _ = ActivateRemoteAsync(computer);
            else NavigateToPage("chat");
            Activate();
        }
        catch { Activate(); }
    }

    private void UpdateConnectedDevices(IReadOnlyList<TransferHost.DeviceInfo> connected)
    {
        Devices.Clear();
        foreach (var device in connected)
            if (device.Id != _host.DesktopSessionId)
                Devices.Add(new DeviceView(device.Id, device.Name, device.Kind));
        RefreshNearbyDevices();
        if (_currentPage == "devices") RefreshPairedDevicesPage();
        if (_activeRemoteId is null)
            StatusText.Text = Devices.Count > 0 ? $"{Devices.Count} 台手机或电脑在线 · 局域网直连" : "等待手机或其他电脑连接";
        RefreshConversationSelector();
    }

    private async void ImagePreview_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not Image image || image.DataContext is not MessageView item || item.PreviewImage is not { } source) return;
        var preview = new Image { Source = source, Stretch = Stretch.Uniform, MaxWidth = 1100, MaxHeight = 760, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
        var ink = new Canvas();
        Polyline? activeStroke = null;
        ink.PointerPressed += (_, args) =>
        {
            var point = args.GetCurrentPoint(ink).Position;
            activeStroke = new Polyline
            {
                Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 64, 64)),
                StrokeThickness = 4,
                StrokeLineJoin = PenLineJoin.Round,
                Points = new PointCollection { point }
            };
            ink.Children.Add(activeStroke);
            ink.CapturePointer(args.Pointer);
            args.Handled = true;
        };
        ink.PointerMoved += (_, args) =>
        {
            if (activeStroke is null) return;
            activeStroke.Points.Add(args.GetCurrentPoint(ink).Position);
        };
        ink.PointerReleased += (_, args) => { activeStroke = null; ink.ReleasePointerCapture(args.Pointer); };
        ink.PointerCanceled += (_, args) => { activeStroke = null; ink.ReleasePointerCapture(args.Pointer); };
        var stage = new Grid { Width = 1100, Height = 700, Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 18, 20, 25)) };
        stage.Children.Add(preview);
        stage.Children.Add(ink);
        var dialog = new ContentDialog
        {
            Title = item.FileName,
            Content = stage,
            PrimaryButtonText = "保存涂鸦",
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = RootGrid.XamlRoot,
            RequestedTheme = RootGrid.ActualTheme
        };
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try { await SaveAnnotatedImageAsync(stage, item.FileName); }
            catch (Exception ex) { TransferStatusText.Text = $"保存图片失败：{ex.Message}"; args.Cancel = true; }
            finally { deferral.Complete(); }
        };
        await dialog.ShowAsync();
    }

    private async Task SaveAnnotatedImageAsync(FrameworkElement stage, string fileName)
    {
        var picker = new FileSavePicker { SuggestedFileName = Path.GetFileNameWithoutExtension(fileName) + "-标注.png" };
        picker.FileTypeChoices.Add("PNG 图片", [".png"]);
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        var bitmap = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
        await bitmap.RenderAsync(stage);
        var pixels = await bitmap.GetPixelsAsync();
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        var reader = DataReader.FromBuffer(pixels);
        var pixelBytes = new byte[pixels.Length];
        reader.ReadBytes(pixelBytes);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixelBytes);
        await encoder.FlushAsync();
    }

    private void RefreshConversationSelector()
    {
        if (ConversationSelector is null) return;
        var selectedKey = _activeRemoteId is null ? "local" : $"computer:{_activeRemoteId}";
        _updatingConversationSelector = true;
        try
        {
            ConversationSelector.Items.Clear();
            ConversationSelector.Items.Add(new ComboBoxItem { Content = "本机设备 · 手机会话", Tag = "local" });
            foreach (var peer in _pairedComputers.Values.Where(peer => _onlineRemoteIds.Contains(peer.DeviceId))
                         .OrderBy(peer => peer.Name, StringComparer.CurrentCultureIgnoreCase))
                ConversationSelector.Items.Add(new ComboBoxItem { Content = $"{peer.Name} · 电脑", Tag = $"computer:{peer.DeviceId}" });
            ConversationSelector.SelectedItem = ConversationSelector.Items
                .OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, selectedKey)) ?? ConversationSelector.Items[0];
        }
        finally { _updatingConversationSelector = false; }
    }

    private async void ConversationSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingConversationSelector || ConversationSelector.SelectedItem is not ComboBoxItem { Tag: string key }) return;
        if (key == "local") { OpenLocalSession(); return; }
        if (!key.StartsWith("computer:", StringComparison.Ordinal)) return;
        if (!_pairedComputers.TryGetValue(key[9..], out var computer)) return;
        try { await ActivateRemoteAsync(computer); }
        catch (Exception ex)
        {
            StatusText.Text = $"无法打开 {computer.Name}：{ex.Message}";
            if (_activeRemoteId == computer.DeviceId) OpenLocalSession();
        }
    }

    private void AddMessage(TransferStore.Entry item, string? sourcePeerId = null, bool localPeerStore = false, bool localPhoneStore = false)
    {
        if (!_messageIds.Add(item.Id)) return;
        ImageSource? preview = null;
        if (item.Kind == "file" && IsImage(item.Name ?? ""))
        {
            try
            {
                var image = new BitmapImage { DecodePixelWidth = 640 };
                if (localPeerStore && sourcePeerId is not null)
                    image.UriSource = new Uri(_host.GetPeerStoredFilePath(sourcePeerId, item.Id), UriKind.Absolute);
                else if (localPhoneStore && sourcePeerId is not null)
                    image.UriSource = new Uri(_host.GetPhoneStoredFilePath(sourcePeerId, item.Id), UriKind.Absolute);
                else if (sourcePeerId is not null && _remoteSessions.TryGetValue(sourcePeerId, out var remote))
                    image.UriSource = remote.GetPreviewUri(item.Id);
                else image.UriSource = new Uri(_host.GetStoredFilePath(item.Id), UriKind.Absolute);
                preview = image;
            }
            catch { }
        }
        var view = new MessageView(
            item.From,
            DateTimeOffset.FromUnixTimeMilliseconds(item.Time).ToLocalTime().ToString("HH:mm"),
            item.Text ?? "",
            item.Kind == "file",
            item.Name ?? "",
            FormatSize(item.Size),
            item.Id,
            item.FromId == _host.DeviceId,
            preview,
            sourcePeerId,
            localPeerStore,
            localPhoneStore);
        Messages.Add(view);
        if (view.IsFile) Files.Add(view);
    }

    private void UpdateNearbyDevices(IReadOnlyList<LanDiscoveryService.NearbyComputer> discovered)
    {
        _discoveredComputers = discovered;
        foreach (var device in discovered)
        {
            var paired = _pairedComputers.TryGetValue(device.DeviceId, out var saved);
            if (!paired) continue;
            if (!string.Equals(saved!.BaseUrl, device.BaseUrl, StringComparison.Ordinal))
            {
                _pairedComputers[device.DeviceId] = saved with { BaseUrl = device.BaseUrl, Name = device.Name };
                SavePairedComputers();
            }
            if (!_remoteSessions.ContainsKey(device.DeviceId)) StartRemoteSession(_pairedComputers[device.DeviceId]);
        }
        if (_activeRemoteId is not null && _pairedComputers.TryGetValue(_activeRemoteId, out var active) &&
            discovered.FirstOrDefault(device => device.DeviceId == _activeRemoteId) is { } activeBeacon &&
            !string.Equals(active.BaseUrl, activeBeacon.BaseUrl, StringComparison.Ordinal))
        {
            _pairedComputers[_activeRemoteId] = active with { BaseUrl = activeBeacon.BaseUrl, Name = activeBeacon.Name };
            SavePairedComputers();
            _ = ReconnectActiveRemoteAsync(_pairedComputers[_activeRemoteId]);
        }
        RefreshNearbyDevices();
        RefreshConversationSelector();
    }

    private void RefreshNearbyDevices()
    {
        NearbyDevices.Clear();
        foreach (var device in _discoveredComputers)
        {
            var paired = _pairedComputers.TryGetValue(device.DeviceId, out var saved);
            NearbyDevices.Add(new NearbyDeviceView(device.DeviceId, device.Name, device.BaseUrl,
                paired ? "已配对 · 点击打开传输" : "首次连接需对方确认", paired, "computer"));
        }
        foreach (var device in Devices.Where(device => string.Equals(device.Kind, "phone", StringComparison.OrdinalIgnoreCase)))
        {
            if (NearbyDevices.Any(nearby => nearby.DeviceId == device.Id)) continue;
            NearbyDevices.Add(new NearbyDeviceView(device.Id, device.Name, "", "已连接 · 点击打开传输", true, "phone-session"));
        }
        NearbyDeviceCountText.Text = NearbyDevices.Count == 0 ? "0 台" : $"{NearbyDevices.Count} 台";
        EmptyNearbyText.Visibility = NearbyDevices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (NearbyDevices.Count == 0)
            EmptyNearbyText.Text = "正在查找同一局域网内可连接的 Nearlink 设备…";
    }

    private async Task<bool> ApprovePairRequestAsync(TransferHost.PairRequest request, CancellationToken cancellationToken)
    {
        await _incomingApprovalDialog.WaitAsync(cancellationToken);
        try
        {
            var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_dispatcher.TryEnqueue(async () =>
            {
                try
                {
                    var dialog = new ContentDialog
                    {
                        Title = "允许电脑连接？",
                        Content = $"{request.Name}（{request.Address}）想与这台电脑建立 Nearlink 连接。允许后，双方可以互发消息和文件。",
                        PrimaryButtonText = "允许连接",
                        CloseButtonText = "拒绝",
                        DefaultButton = ContentDialogButton.Close,
                        XamlRoot = RootGrid.XamlRoot,
                        RequestedTheme = RootGrid.ActualTheme
                    };
                    var approved = await dialog.ShowAsync() == ContentDialogResult.Primary;
                    if (approved && request.BaseUrl is not null && request.Token is not null)
                    {
                        _pairedComputers[request.DeviceId] = new RemoteComputer(request.DeviceId, request.Name, request.BaseUrl, request.Token);
                        SavePairedComputers();
                    }
                    decision.TrySetResult(approved);
                }
                catch { decision.TrySetResult(false); }
            })) return false;
            try { return await decision.Task.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { return false; }
        }
        finally { _incomingApprovalDialog.Release(); }
    }

    private async void ConnectNearby_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: NearbyDeviceView item }) return;
        if (item.Kind == "phone-session")
        {
            OpenLocalSession();
            return;
        }
        try
        {
            var baseUrl = item.BaseUrl;
            if (!_pairedComputers.TryGetValue(item.DeviceId, out var paired))
            {
                StatusText.Text = $"正在请求 {item.Name} 确认连接…";
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
                var localPeerToken = _host.GetOrCreatePeerToken(item.DeviceId);
                using var response = await http.PostAsJsonAsync($"{baseUrl.TrimEnd('/')}/api/pair/request",
                    new TransferHost.PairRequestBody(_host.DeviceId, _host.DeviceName, localPeerToken, _host.BaseUrl), _stop.Token);
                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException(response.StatusCode == System.Net.HttpStatusCode.Forbidden
                        ? "对方拒绝了连接请求" : $"连接请求失败（{(int)response.StatusCode}）");
                var accepted = await response.Content.ReadFromJsonAsync<TransferHost.PairResponse>(cancellationToken: _stop.Token)
                    ?? throw new InvalidDataException("配对响应无效");
                if (accepted.DeviceId != item.DeviceId) throw new InvalidDataException("配对设备身份与发现结果不一致");
                paired = new RemoteComputer(accepted.DeviceId, accepted.Name, baseUrl, accepted.Token);
                _pairedComputers[paired.DeviceId] = paired;
                SavePairedComputers();
            }
            else if (!string.Equals(paired.BaseUrl, baseUrl, StringComparison.Ordinal))
            {
                paired = paired with { BaseUrl = baseUrl, Name = item.Name };
                _pairedComputers[paired.DeviceId] = paired;
                SavePairedComputers();
            }
            await ActivateRemoteAsync(paired);
        }
        catch (Exception ex)
        {
            StatusText.Text = "连接失败";
            await ShowDialogAsync("无法连接这台电脑", ex.Message);
        }
    }

    private async Task ActivateRemoteAsync(RemoteComputer computer)
    {
        _activeRemoteId = computer.DeviceId;
        RefreshConversationSelector();
        Messages.Clear(); Files.Clear(); _messageIds.Clear();
        StatusText.Text = $"正在连接 {computer.Name}…";
        var remote = StartRemoteSession(computer);
        await LoadPeerConversationAsync(computer, remote);
        if (_activeRemoteId != computer.DeviceId) return;
        NavigateToPage("chat");
    }

    private async Task LoadPeerConversationAsync(RemoteComputer computer, RemoteDesktopSession remote)
    {
        var history = new List<(TransferStore.Entry Item, bool LocalCopy)>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try { history.AddRange((await remote.GetHistoryAsync(timeout.Token)).Select(item => (item, false))); }
        catch (Exception ex) { StatusText.Text = $"连接中 · {ex.Message}"; }
        try { history.AddRange(_host.GetPeerHistory(computer.DeviceId).Select(item => (item, true))); }
        catch (Exception ex) { StatusText.Text = $"本机历史读取失败 · {ex.Message}"; }
        if (_activeRemoteId != computer.DeviceId) return;
        foreach (var (item, localCopy) in history.OrderBy(entry => entry.Item.Time).ThenBy(entry => entry.Item.Id, StringComparer.Ordinal))
            AddMessage(item, computer.DeviceId, localCopy);
    }

    private RemoteDesktopSession StartRemoteSession(RemoteComputer computer)
    {
        if (_remoteSessions.TryGetValue(computer.DeviceId, out var existing))
        {
            if (string.Equals(existing.BaseUrl, computer.BaseUrl, StringComparison.Ordinal)) return existing;
            _remoteSessions.Remove(computer.DeviceId);
            _ = existing.DisposeAsync();
        }
        var session = new RemoteDesktopSession(computer.DeviceId, computer.Name, computer.BaseUrl,
            computer.Token, _host.DeviceId, _host.DeviceName);
        session.EventReceived += message => _dispatcher.TryEnqueue(() => HandleServerEvent(message, computer.DeviceId));
        session.StatusChanged += status => _dispatcher.TryEnqueue(() =>
        {
            var online = status.StartsWith("已连接", StringComparison.Ordinal);
            if (online) _onlineRemoteIds.Add(computer.DeviceId);
            else _onlineRemoteIds.Remove(computer.DeviceId);
            var nearby = NearbyDevices.FirstOrDefault(device => device.DeviceId == computer.DeviceId);
            if (nearby is not null) nearby.Detail = online ? "已配对 · 在线" : "已配对 · 正在重连";
            if (!online && _activeRemoteId == computer.DeviceId)
            {
                OpenLocalSession();
                StatusText.Text = $"{computer.Name} 已断开，已切换到本机历史";
            }
            else if (_activeRemoteId == computer.DeviceId) StatusText.Text = $"{computer.Name} · {status}";
            RefreshConversationSelector();
        });
        _remoteSessions[computer.DeviceId] = session;
        session.Start();
        return session;
    }

    private async Task ReconnectActiveRemoteAsync(RemoteComputer computer)
    {
        _onlineRemoteIds.Remove(computer.DeviceId);
        if (_remoteSessions.Remove(computer.DeviceId, out var old)) await old.DisposeAsync();
        var session = StartRemoteSession(computer);
        Messages.Clear(); Files.Clear(); _messageIds.Clear();
        await LoadPeerConversationAsync(computer, session);
    }

    private void LocalSession_Click(object sender, RoutedEventArgs e)
    {
        OpenLocalSession();
    }

    private void OpenLocalSession()
    {
        _activeRemoteId = null;
        RefreshConversationSelector();
        Messages.Clear(); Files.Clear(); _messageIds.Clear();
        LoadLocalConversationHistory();
        StatusText.Text = Devices.Count > 0 ? $"{Devices.Count} 台其他设备在线 · 消息同步中" : "等待手机或其他电脑连接";
        NavigateToPage("chat");
    }

    private void LoadLocalConversationHistory()
    {
        var history = _host.GetHistory().Select(item => (Item: item, PhoneSessionId: (string?)null))
            .Concat(_host.GetAllPhoneHistory().Select(item => (Item: item, PhoneSessionId: (string?)item.FromId)))
            .OrderBy(entry => entry.Item.Time).ThenBy(entry => entry.Item.Id, StringComparer.Ordinal);
        foreach (var entry in history)
            AddMessage(entry.Item, entry.PhoneSessionId, localPhoneStore: entry.PhoneSessionId is not null);
    }

    private static string PairedComputersPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nearlink", "paired-computers.json");

    private Dictionary<string, RemoteComputer> LoadPairedComputers()
    {
        try
        {
            if (!File.Exists(PairedComputersPath)) return new(StringComparer.Ordinal);
            var list = JsonSerializer.Deserialize<List<RemoteComputer>>(File.ReadAllText(PairedComputersPath), new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
            return list.ToDictionary(peer => peer.DeviceId, StringComparer.Ordinal);
        }
        catch { return new(StringComparer.Ordinal); }
    }

    private void SavePairedComputers()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PairedComputersPath)!);
            var temp = PairedComputersPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_pairedComputers.Values, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            File.Move(temp, PairedComputersPath, overwrite: true);
        }
        catch { }
    }

    private void RefreshPairedDevicesPage()
    {
        if (PairedDevicesSubtitle is null) return;
        var selectedIds = PairedDevices.Where(device => device.IsSelected).Select(device => device.DeviceId).ToHashSet(StringComparer.Ordinal);
        var records = new Dictionary<string, (string Name, string Kind)>(StringComparer.Ordinal);
        foreach (var peer in _pairedComputers.Values)
        {
            var kind = Devices.FirstOrDefault(device => device.Id == peer.DeviceId)?.Kind ??
                (Uri.TryCreate(peer.BaseUrl, UriKind.Absolute, out var uri) && uri.Port == 51463 ? "phone" : "computer");
            records[peer.DeviceId] = (peer.Name, kind);
        }
        foreach (var device in Devices)
            records[device.Id] = (device.Name, device.Kind);
        foreach (var id in _host.GetTrustedPeerIds())
            if (!records.ContainsKey(id)) records[id] = ($"已配对设备 · {id[..Math.Min(8, id.Length)]}", "device");

        PairedDevices.Clear();
        foreach (var (id, record) in records.OrderBy(item => item.Value.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var online = Devices.Any(device => device.Id == id) || _onlineRemoteIds.Contains(id);
            var row = new PairedDeviceView(id, record.Name, record.Kind, online ? "在线" : "当前离线");
            row.SelectionChanged += UpdateDeviceSelectionUi;
            row.SetEditMode(_deviceEditMode);
            row.IsSelected = selectedIds.Contains(id);
            PairedDevices.Add(row);
        }
        EmptyPairedDevicesText.Visibility = PairedDevices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateDeviceSelectionUi();
    }

    private void ToggleDeviceEdit_Click(object sender, RoutedEventArgs e)
    {
        _deviceEditMode = !_deviceEditMode;
        ToggleDeviceEditButton.Content = _deviceEditMode ? "完成" : "编辑";
        DeviceEditActions.Visibility = _deviceEditMode ? Visibility.Visible : Visibility.Collapsed;
        PairedDevicesSubtitle.Text = _deviceEditMode ? "选择要移除的设备；移除后可再次配对" : "电脑和手机的配对记录";
        foreach (var device in PairedDevices)
        {
            device.SetEditMode(_deviceEditMode);
            if (!_deviceEditMode) device.IsSelected = false;
        }
        UpdateDeviceSelectionUi();
    }

    private void UpdateDeviceSelectionUi()
    {
        if (DeviceSelectionText is null) return;
        var count = PairedDevices.Count(device => device.IsSelected);
        DeviceSelectionText.Text = $"已选择 {count} 台";
        RemoveSelectedDevicesButton.Content = $"移除所选（{count}）";
        RemoveSelectedDevicesButton.IsEnabled = count > 0;
    }

    private async void RemoveSelectedDevices_Click(object sender, RoutedEventArgs e)
    {
        var selected = PairedDevices.Where(device => device.IsSelected).ToArray();
        if (selected.Length == 0) return;
        var names = string.Join("、", selected.Take(5).Select(device => device.Name));
        var extra = selected.Length > 5 ? $"等另外 {selected.Length - 5} 台设备" : "";
        var dialog = new ContentDialog
        {
            Title = $"移除 {selected.Length} 台设备？",
            Content = $"将断开并移除这些设备的配对：{names}{extra}。之后仍可重新配对。",
            PrimaryButtonText = "移除设备",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = RootGrid.XamlRoot,
            RequestedTheme = RootGrid.ActualTheme
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        foreach (var device in selected)
        {
            _onlineRemoteIds.Remove(device.DeviceId);
            _pairedComputers.Remove(device.DeviceId);
            if (_remoteSessions.Remove(device.DeviceId, out var session)) await session.DisposeAsync();
            await _host.ForgetConnectedDeviceAsync(device.DeviceId);
            if (_activeRemoteId == device.DeviceId)
            {
                _activeRemoteId = null;
                Messages.Clear(); Files.Clear(); _messageIds.Clear();
                LoadLocalConversationHistory();
                StatusText.Text = "设备已移除 · 等待手机或其他电脑连接";
            }
        }
        SavePairedComputers();
        _deviceEditMode = false;
        ToggleDeviceEditButton.Content = "编辑";
        DeviceEditActions.Visibility = Visibility.Collapsed;
        PairedDevicesSubtitle.Text = "电脑和手机的配对记录";
        RefreshConversationSelector();
        RefreshNearbyDevices();
        RefreshPairedDevicesPage();
    }

    private void NavChat_Click(object sender, RoutedEventArgs e) => NavigateToPage("chat");
    private void NavFiles_Click(object sender, RoutedEventArgs e) => NavigateToPage("files");
    private void NavDevices_Click(object sender, RoutedEventArgs e)
    {
        RefreshPairedDevicesPage();
        NavigateToPage("devices");
    }
    private void NavSettings_Click(object sender, RoutedEventArgs e) => NavigateToPage("settings");

    private void NavigateToPage(string page)
    {
        if (page == _currentPage) return;
        if (_pageHistoryIndex < _pageHistory.Count - 1)
            _pageHistory.RemoveRange(_pageHistoryIndex + 1, _pageHistory.Count - _pageHistoryIndex - 1);
        _pageHistory.Add(page);
        _pageHistoryIndex = _pageHistory.Count - 1;
        ShowPage(page);
        UpdatePageNavigation();
    }

    private void BackPage_Click(object sender, RoutedEventArgs e)
    {
        if (_pageHistoryIndex <= 0) return;
        _pageHistoryIndex--;
        ShowPage(_pageHistory[_pageHistoryIndex]);
        UpdatePageNavigation();
    }

    private void ForwardPage_Click(object sender, RoutedEventArgs e)
    {
        if (_pageHistoryIndex >= _pageHistory.Count - 1) return;
        _pageHistoryIndex++;
        ShowPage(_pageHistory[_pageHistoryIndex]);
        UpdatePageNavigation();
    }

    private void UpdatePageNavigation()
    {
        BackPageButton.IsEnabled = _pageHistoryIndex > 0;
        ForwardPageButton.IsEnabled = _pageHistoryIndex < _pageHistory.Count - 1;
    }

    private void ToggleSidebar_Click(object sender, RoutedEventArgs e)
    {
        AppSplitView.IsPaneOpen = !AppSplitView.IsPaneOpen;
        var isOpen = AppSplitView.IsPaneOpen;
        var label = isOpen ? "隐藏设备侧栏" : "显示设备侧栏";
        SidebarPaneIcon.Glyph = isOpen ? "\uE89F" : "\uE8A0";
        ToggleSidebarMenuItem.Text = label;
        ToolTipService.SetToolTip(ToggleSidebarButton, label);
    }

    private void ShowPage(string page)
    {
        if (_currentPage == "devices" && page != "devices" && _deviceEditMode)
            ToggleDeviceEdit_Click(this, new RoutedEventArgs());
        ChatPage.Visibility = page == "chat" ? Visibility.Visible : Visibility.Collapsed;
        FilesPage.Visibility = page == "files" ? Visibility.Visible : Visibility.Collapsed;
        DevicesPage.Visibility = page == "devices" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
        _currentPage = page;
        var target = page switch
        {
            "chat" => (FrameworkElement)ChatPage,
            "files" => FilesPage,
            "devices" => DevicesPage,
            _ => SettingsPage
        };
        target.Opacity = 0;
        var transition = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(180)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var storyboard = new Storyboard();
        Storyboard.SetTarget(transition, target);
        Storyboard.SetTargetProperty(transition, "Opacity");
        storyboard.Children.Add(transition);
        storyboard.Begin();
    }

    private void ToggleQr_Click(object sender, RoutedEventArgs e) => SetQrExpanded(!_isQrExpanded);

    private void SetQrExpanded(bool expanded)
    {
        if (_isQrExpanded == expanded) return;
        _isQrExpanded = expanded;
        _qrStoryboard?.Stop();
        var currentHeight = QrImagePanel.ActualHeight;
        if (expanded)
        {
            QrImagePanel.Visibility = Visibility.Visible;
            QrImagePanel.Height = 0;
            QrImagePanel.Opacity = 0;
            currentHeight = 0;
        }
        var height = new DoubleAnimation
        {
            From = currentHeight,
            To = expanded ? 168 : 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(260)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        var opacity = new DoubleAnimation
        {
            From = expanded ? 0 : QrImagePanel.Opacity,
            To = expanded ? 1 : 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(220)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        _qrStoryboard = new Storyboard();
        Storyboard.SetTarget(height, QrImagePanel);
        Storyboard.SetTargetProperty(height, "Height");
        Storyboard.SetTarget(opacity, QrImagePanel);
        Storyboard.SetTargetProperty(opacity, "Opacity");
        _qrStoryboard.Children.Add(height);
        _qrStoryboard.Children.Add(opacity);
        _qrStoryboard.Completed += (_, _) =>
        {
            if (!expanded) QrImagePanel.Visibility = Visibility.Collapsed;
            QrImagePanel.Height = 168;
            QrImagePanel.Opacity = 1;
        };
        _qrStoryboard.Begin();
        QrChevronIcon.Glyph = expanded ? "\uE70E" : "\uE70D";
        ToolTipService.SetToolTip(ToggleQrButton, expanded ? "收起二维码" : "展开二维码");
        ToggleQrMenuItem.Text = expanded ? "收起二维码" : "展开二维码";
    }

    private void ThemeMode_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressThemeControls || sender is not RadioButton radio || !Enum.TryParse<ThemePreference>(radio.Tag?.ToString(), true, out var mode)) return;
        ApplyTheme(mode, persist: true);
    }

    private void ApplyTheme(ThemePreference mode, bool persist)
    {
        _themePreference = mode;
        var isDark = mode switch
        {
            ThemePreference.Dark => true,
            ThemePreference.Light => false,
            _ => IsWindowsDarkMode()
        };
        RootGrid.RequestedTheme = mode == ThemePreference.System ? ElementTheme.Default : isDark ? ElementTheme.Dark : ElementTheme.Light;
        Application.Current.Resources["IncomingBubbleBrush"] = new SolidColorBrush(ColorHelper.FromArgb(255, isDark ? (byte)42 : (byte)241, isDark ? (byte)47 : (byte)243, isDark ? (byte)53 : (byte)245));
        Application.Current.Resources["OutgoingBubbleBrush"] = new SolidColorBrush(ColorHelper.FromArgb(255, isDark ? (byte)38 : (byte)234, isDark ? (byte)58 : (byte)241, isDark ? (byte)85 : (byte)254));
        foreach (var item in Messages) item.RefreshTheme();
        _suppressThemeControls = true;
        ThemeSystemRadio.IsChecked = mode == ThemePreference.System;
        ThemeLightRadio.IsChecked = mode == ThemePreference.Light;
        ThemeDarkRadio.IsChecked = mode == ThemePreference.Dark;
        _suppressThemeControls = false;
        if (persist)
        {
            try
            {
                var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nearlink", "theme.txt");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, mode.ToString().ToLowerInvariant());
            }
            catch { }
        }
    }

    private void SystemColorsChanged(UISettings sender, object args)
    {
        if (_themePreference == ThemePreference.System)
            _dispatcher.TryEnqueue(() => ApplyTheme(ThemePreference.System, persist: false));
    }

    private static ThemePreference LoadThemePreference()
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nearlink", "theme.txt");
            return File.Exists(path) && Enum.TryParse<ThemePreference>(File.ReadAllText(path).Trim(), true, out var mode) ? mode : ThemePreference.System;
        }
        catch { return ThemePreference.System; }
    }

    private static bool IsWindowsDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return Convert.ToInt32(key?.GetValue("AppsUseLightTheme", 1)) == 0;
        }
        catch { return false; }
    }

    private async void CopyLink_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_host.PairUrl)) return;
        var package = new DataPackage();
        package.SetText(_host.PairUrl);
        Clipboard.SetContent(package);
        CopyLinkButton.Content = "已复制";
        await Task.Delay(1300);
        _dispatcher.TryEnqueue(() => CopyLinkButton.Content = "复制");
    }

    private async void Attach_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var files = await picker.PickMultipleFilesAsync();
        await UploadFilesAsync(files);
    }

    private void ChatPage_DragEnter(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = e.DataView.Contains(StandardDataFormats.StorageItems) ? DataPackageOperation.Copy : DataPackageOperation.None;
        DropHintBorder.Visibility = e.AcceptedOperation == DataPackageOperation.Copy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ChatPage_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = e.DataView.Contains(StandardDataFormats.StorageItems) ? DataPackageOperation.Copy : DataPackageOperation.None;
        e.DragUIOverride.Caption = "发送到 Nearlink";
        e.DragUIOverride.IsCaptionVisible = true;
    }

    private void ChatPage_DragLeave(object sender, DragEventArgs e) => DropHintBorder.Visibility = Visibility.Collapsed;

    private async void ChatPage_Drop(object sender, DragEventArgs e)
    {
        DropHintBorder.Visibility = Visibility.Collapsed;
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        var files = items.OfType<StorageFile>().ToArray();
        await UploadFilesAsync(files);
    }

    private async Task UploadFilesAsync(IReadOnlyList<StorageFile> files)
    {
        if (files.Count == 0) return;
        for (var i = 0; i < files.Count; i++)
        {
            var file = files[i];
            try
            {
                TransferStatusText.Text = $"正在发送 {Path.GetFileName(file.Path)}…";
                var progress = new Progress<double>(value => _dispatcher.TryEnqueue(() => TransferStatusText.Text = $"正在发送 {Path.GetFileName(file.Path)} · {value:P0}"));
                if (_activeRemoteId is not null && _remoteSessions.TryGetValue(_activeRemoteId, out var remote))
                    await remote.UploadFileAsync(file.Path, progress, _stop.Token);
                else await _host.UploadFileAsync(file.Path, progress, _stop.Token);
            }
            catch (Exception ex)
            {
                TransferStatusText.Text = $"文件发送失败：{ex.Message}";
                return;
            }
        }
        TransferStatusText.Text = $"已发送 {files.Count} 个文件";
        _ = ClearTransferStatusAsync();
    }

    public void HandleExplorerFiles(IReadOnlyList<string> paths)
    {
        var files = paths.Where(File.Exists).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length == 0) return;
        if (!_hostReady || _explorerSendInProgress)
        {
            _queuedExplorerPaths.AddRange(files);
            return;
        }
        _ = SendExplorerFilesAsync(files);
    }

    private async Task SendExplorerFilesAsync(IReadOnlyList<string> files)
    {
        _explorerSendInProgress = true;
        try
        {
            Activate();
            NavigateToPage("chat");
            var targets = GetExplorerSendTargets();
            if (targets.Count == 0)
            {
                TransferStatusText.Text = "正在查找接收设备…";
                for (var attempt = 0; attempt < 10 && targets.Count == 0; attempt++)
                {
                    await Task.Delay(250, _stop.Token);
                    targets = GetExplorerSendTargets();
                }
            }

            if (targets.Count == 0)
            {
                TransferStatusText.Text = "还没有可用的接收设备";
                await ShowDialogAsync("没有可用设备", "先连接手机或配对一台 Nearlink 电脑，再从资源管理器发送文件。");
                return;
            }

            ExplorerSendTarget target;
            if (targets.Count == 1) target = targets[0];
            else
            {
                var selector = new ComboBox
                {
                    ItemsSource = targets,
                    DisplayMemberPath = nameof(ExplorerSendTarget.Name),
                    SelectedIndex = 0,
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };
                var details = new StackPanel { Spacing = 10, MinWidth = 360 };
                details.Children.Add(new TextBlock { Text = $"发送 {files.Count} 个文件到：" });
                details.Children.Add(selector);
                var dialog = new ContentDialog
                {
                    Title = "发送到 Nearlink",
                    Content = details,
                    PrimaryButtonText = "发送",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = RootGrid.XamlRoot,
                    RequestedTheme = RootGrid.ActualTheme
                };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary || selector.SelectedItem is not ExplorerSendTarget selectedTarget) return;
                target = selectedTarget;
            }

            await UploadExplorerFilesAsync(files, target);
        }
        finally
        {
            _explorerSendInProgress = false;
            if (_queuedExplorerPaths.Count > 0 && _hostReady)
            {
                var next = _queuedExplorerPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                _queuedExplorerPaths.Clear();
                HandleExplorerFiles(next);
            }
        }
    }

    private async Task UploadExplorerFilesAsync(IReadOnlyList<string> paths, ExplorerSendTarget target)
    {
        for (var index = 0; index < paths.Count; index++)
        {
            var path = paths[index];
            try
            {
                TransferStatusText.Text = $"正在发送 {index + 1}/{paths.Count} · {Path.GetFileName(path)}";
                var progress = new Progress<double>(value => _dispatcher.TryEnqueue(() => TransferStatusText.Text = $"正在发送 {index + 1}/{paths.Count} · {value:P0}"));
                if (target.IsPhoneGroup) await _host.UploadFileAsync(path, progress, _stop.Token);
                else
                {
                    if (!_pairedComputers.TryGetValue(target.Id, out var computer)) throw new InvalidOperationException("这台电脑已从配对列表移除");
                    var session = StartRemoteSession(computer);
                    await session.UploadFileAsync(path, progress, _stop.Token);
                }
            }
            catch (Exception ex)
            {
                TransferStatusText.Text = $"发送失败：{Path.GetFileName(path)}";
                await ShowDialogAsync("文件发送失败", $"{Path.GetFileName(path)}\n\n{ex.Message}");
                return;
            }
        }
        TransferStatusText.Text = string.Empty;
    }

    private List<ExplorerSendTarget> GetExplorerSendTargets()
    {
        var targets = new List<ExplorerSendTarget>();
        var phoneCount = Devices.Count(device => string.Equals(device.Kind, "phone", StringComparison.OrdinalIgnoreCase));
        if (phoneCount > 0) targets.Add(new ExplorerSendTarget("phones", $"已连接的手机群组（{phoneCount} 台）", true));
        var onlineComputerIds = NearbyDevices.Where(device => device.IsPaired).Select(device => device.DeviceId)
            .Concat(Devices.Where(device => string.Equals(device.Kind, "desktop", StringComparison.OrdinalIgnoreCase)).Select(device => device.Id))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var computer in _pairedComputers.Values.Where(item => onlineComputerIds.Contains(item.DeviceId)).OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
            targets.Add(new ExplorerSendTarget(computer.DeviceId, $"{computer.Name} · 电脑", false));
        return targets;
    }

    private sealed record ExplorerSendTarget(string Id, string Name, bool IsPhoneGroup);

    private async void Send_Click(object sender, RoutedEventArgs e) => await SendMessageAsync();

    private async Task SendMessageAsync()
    {
        var text = MessageInput.Text.Trim();
        if (text.Length == 0) return;
        try
        {
            if (_activeRemoteId is not null && _remoteSessions.TryGetValue(_activeRemoteId, out var remote))
                await remote.SendMessageAsync(text, _stop.Token);
            else await _host.SendMessageAsync(text, _stop.Token);
            MessageInput.Text = "";
        }
        catch (Exception ex) { TransferStatusText.Text = $"消息发送失败：{ex.Message}"; }
    }

    private async void MessageInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            await SendMessageAsync();
        }
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MessageView item }) return;
        var picker = new FileSavePicker { SuggestedFileName = item.FileName };
        var extension = Path.GetExtension(item.FileName);
        picker.FileTypeChoices.Add("文件", new List<string> { string.IsNullOrEmpty(extension) ? ".bin" : extension });
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        try
        {
            TransferStatusText.Text = $"正在保存 {item.FileName}…";
            await DownloadMessageFileAsync(item, file.Path);
            TransferStatusText.Text = "文件已保存";
            _ = ClearTransferStatusAsync();
        }
        catch (Exception ex) { TransferStatusText.Text = $"保存失败：{ex.Message}"; }
    }

    private Task DownloadMessageFileAsync(MessageView item, string destination)
    {
        if (item.IsLocalPeerCopy && item.SourceDeviceId is not null)
            return _host.DownloadPeerFileAsync(item.SourceDeviceId, item.FileId, destination, _stop.Token);
        if (item.IsLocalPhoneCopy && item.SourceDeviceId is not null)
            return _host.DownloadPhoneFileAsync(item.SourceDeviceId, item.FileId, destination, _stop.Token);
        if (item.SourceDeviceId is not null && _remoteSessions.TryGetValue(item.SourceDeviceId, out var remote))
            return remote.DownloadFileAsync(item.FileId, item.FileName, destination, _stop.Token);
        return _host.DownloadFileAsync(item.FileId, item.FileName, destination, _stop.Token);
    }

    private void SelectAllFiles_Click(object sender, RoutedEventArgs e)
    {
        foreach (var file in Files) file.IsSelected = true;
        UpdateFileSelectionUi();
    }

    private void ClearFileSelection_Click(object sender, RoutedEventArgs e)
    {
        foreach (var file in Files) file.IsSelected = false;
        UpdateFileSelectionUi();
    }

    private void RemoveSelectedFiles_Click(object sender, RoutedEventArgs e)
    {
        var selected = Files.Where(file => file.IsSelected).ToList();
        foreach (var item in selected) Files.Remove(item);
        FilesOperationStatusText.Text = selected.Count > 0 ? $"已从列表移除 {selected.Count} 项，文件仍保存在本机" : "";
        UpdateFileSelectionUi();
    }

    private async void SaveSelectedFiles_Click(object sender, RoutedEventArgs e)
    {
        var selected = Files.Where(file => file.IsSelected).ToList();
        if (selected.Count == 0) return;
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;

        SaveSelectedFilesButton.IsEnabled = false;
        var saved = 0;
        var failed = 0;
        for (var index = 0; index < selected.Count; index++)
        {
            var item = selected[index];
            var destination = GetAvailableFilePath(folder.Path, item.FileName);
            FilesOperationStatusText.Text = $"正在保存 {index + 1}/{selected.Count}：{item.FileName}";
            try
            {
                await DownloadMessageFileAsync(item, destination);
                saved++;
            }
            catch
            {
                failed++;
                try { if (File.Exists(destination)) File.Delete(destination); } catch { }
            }
        }
        FilesOperationStatusText.Text = failed == 0
            ? $"已保存 {saved} 个文件到所选文件夹"
            : $"保存完成：{saved} 个成功，{failed} 个失败";
        UpdateFileSelectionUi();
    }

    private void UpdateFileSelectionUi()
    {
        var selectedCount = Files.Count(item => item.IsSelected);
        FilesSelectionText.Text = $"已选 {selectedCount} 项 · 共 {Files.Count} 项";
        RemoveSelectedFilesButton.IsEnabled = selectedCount > 0;
        SaveSelectedFilesButton.IsEnabled = selectedCount > 0;
    }

    private void FilesList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateFileSelectionUi();

    private static string GetAvailableFilePath(string folder, string fileName)
    {
        var safeName = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(safeName)) safeName = "文件";
        var destination = Path.Combine(folder, safeName);
        if (!File.Exists(destination)) return destination;
        var name = Path.GetFileNameWithoutExtension(safeName);
        var extension = Path.GetExtension(safeName);
        for (var suffix = 2; ; suffix++)
        {
            destination = Path.Combine(folder, $"{name} ({suffix}){extension}");
            if (!File.Exists(destination)) return destination;
        }
    }

    private async void About_Click(object sender, RoutedEventArgs e) => await ShowAboutDialogAsync();

    private async Task ShowAboutDialogAsync()
    {
        var content = new StackPanel { Spacing = 18, MinWidth = 340 };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        var mark = new Border
        {
            Width = 54,
            Height = 54,
            CornerRadius = new CornerRadius(14),
            Background = new SolidColorBrush(Colors.Transparent),
            Child = new Image { Source = await LoadAboutIconAsync(), Width = 54, Height = 54, Stretch = Stretch.Uniform }
        };
        var titleBlock = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 3 };
        titleBlock.Children.Add(new TextBlock { Text = "Nearlink", FontSize = 22, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        titleBlock.Children.Add(new TextBlock { Text = "Windows 与手机之间的局域网互传", Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });
        header.Children.Add(mark);
        header.Children.Add(titleBlock);
        content.Children.Add(header);
        content.Children.Add(new Border
        {
            Height = 1,
            Background = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"]
        });
        content.Children.Add(new TextBlock
        {
            Text = "Designed by Chao",
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        var githubBlock = new StackPanel { Spacing = 2 };
        githubBlock.Children.Add(new TextBlock
        {
            Text = "GitHub",
            FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        });
        githubBlock.Children.Add(new HyperlinkButton
        {
            Content = "@charlen2024",
            NavigateUri = new Uri("https://github.com/charlen2024"),
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(0, 4, 8, 4)
        });
        content.Children.Add(githubBlock);

        var dialog = new ContentDialog
        {
            Title = "关于",
            Content = content,
            CloseButtonText = "关闭",
            XamlRoot = RootGrid.XamlRoot,
            RequestedTheme = RootGrid.ActualTheme
        };
        await dialog.ShowAsync();
    }

    private static async Task<BitmapImage?> LoadAboutIconAsync()
    {
        using var resource = typeof(MainWindow).Assembly.GetManifestResourceStream("Nearlink.Native.Assets.nearlink-about.png");
        if (resource is null) return null;
        using var bytes = new MemoryStream();
        await resource.CopyToAsync(bytes);
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(bytes.ToArray());
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }
        stream.Seek(0);
        var image = new BitmapImage { DecodePixelWidth = 128 };
        await image.SetSourceAsync(stream);
        return image;
    }

    private async Task ShowDialogAsync(string title, string content)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = content,
            CloseButtonText = "确定",
            XamlRoot = RootGrid.XamlRoot,
            RequestedTheme = RootGrid.ActualTheme
        };
        await dialog.ShowAsync();
    }

    private async Task ClearTransferStatusAsync()
    {
        await Task.Delay(2500);
        _dispatcher.TryEnqueue(() => TransferStatusText.Text = "");
    }

    private async void Window_Closed(object sender, WindowEventArgs args)
    {
        _radarPulseStoryboard?.Stop();
        _uiSettings.ColorValuesChanged -= SystemColorsChanged;
        _stop.Cancel();
        await _discovery.DisposeAsync();
        foreach (var remote in _remoteSessions.Values) await remote.DisposeAsync();
        if (_syncClient is not null) await _syncClient.DisposeAsync();
        await _host.DisposeAsync();
        _stop.Dispose();
        try { _notificationManager?.Unregister(); } catch { }
        _incomingApprovalDialog.Dispose();
    }

    private static bool IsImage(string name) => Path.GetExtension(name).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".tif" or ".tiff";
    private static string FormatSize(long bytes) => bytes < 1024 ? $"{bytes} B" : bytes < 1024 * 1024 ? $"{bytes / 1024d:0.0} KB" : $"{bytes / (1024d * 1024):0.0} MB";

    public sealed class DeviceView
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Kind { get; set; }
        public string DisplayName => $"{Name} · {(Kind == "phone" ? "手机" : Kind == "desktop" ? "电脑" : "设备")}";
        public DeviceView(string id, string name, string kind) { Id = id; Name = name; Kind = kind; }
    }

    public sealed class NearbyDeviceView : INotifyPropertyChanged
    {
        private string _detail;
        public string DeviceId { get; }
        public string Name { get; }
        public string BaseUrl { get; }
        public bool IsPaired { get; }
        public string Kind { get; }
        public string ActionText => IsPaired ? "打开传输" : "申请配对";
        public string Detail { get => _detail; set { if (_detail == value) return; _detail = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Detail))); } }
        public NearbyDeviceView(string deviceId, string name, string baseUrl, string detail, bool isPaired, string kind)
        { DeviceId = deviceId; Name = name; BaseUrl = baseUrl; _detail = detail; IsPaired = isPaired; Kind = kind; }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public sealed class PairedDeviceView : INotifyPropertyChanged
    {
        private bool _isSelected;
        private Visibility _editVisibility = Visibility.Collapsed;
        public string DeviceId { get; }
        public string Name { get; }
        public string Kind { get; }
        public string Detail { get; }
        public string DisplayName => $"{Name} · {(Kind == "phone" ? "手机" : Kind is "computer" or "desktop" ? "电脑" : "设备")}";
        public string IconGlyph => Kind == "phone" ? "\uE8EA" : "\uE7F4";
        public bool IsSelected { get => _isSelected; set { if (_isSelected == value) return; _isSelected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected))); SelectionChanged?.Invoke(); } }
        public Visibility EditVisibility { get => _editVisibility; private set { if (_editVisibility == value) return; _editVisibility = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EditVisibility))); } }
        public event Action? SelectionChanged;
        public event PropertyChangedEventHandler? PropertyChanged;
        public PairedDeviceView(string deviceId, string name, string kind, string detail) { DeviceId = deviceId; Name = name; Kind = kind; Detail = detail; }
        public void SetEditMode(bool enabled) => EditVisibility = enabled ? Visibility.Visible : Visibility.Collapsed;
    }

    public sealed record RemoteComputer(string DeviceId, string Name, string BaseUrl, string Token);
    private enum ThemePreference { System, Light, Dark }

    public sealed class MessageView : INotifyPropertyChanged
    {
        private bool _isSelected;
        public string From { get; }
        public string Time { get; }
        public string Text { get; }
        public bool IsFile { get; }
        public string FileName { get; }
        public string FileSize { get; }
        public string FileId { get; }
        public bool IsMine { get; }
        public string? SourceDeviceId { get; }
        public bool IsLocalPeerCopy { get; }
        public bool IsLocalPhoneCopy { get; }
        public HorizontalAlignment Alignment => IsMine ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        public ImageSource? PreviewImage { get; }
        public bool IsSelected
        {
            get => _isSelected;
            set { if (_isSelected == value) return; _isSelected = value; OnPropertyChanged(); }
        }
        public string Meta => $"{From} · {Time}";
        public string MetaWithSize => $"{Meta} · {FileSize}";
        public string BubbleBrushKey => IsMine ? "OutgoingBubbleBrush" : "IncomingBubbleBrush";
        public Brush BubbleBrush => (Brush)Application.Current.Resources[BubbleBrushKey];
        public Visibility TextVisibility => IsFile ? Visibility.Collapsed : Visibility.Visible;
        public Visibility FileVisibility => IsFile ? Visibility.Visible : Visibility.Collapsed;
        public Visibility ImageVisibility => PreviewImage is null ? Visibility.Collapsed : Visibility.Visible;
        public Visibility ThumbnailVisibility => PreviewImage is null ? Visibility.Collapsed : Visibility.Visible;
        public Visibility FileIconVisibility => PreviewImage is null ? Visibility.Visible : Visibility.Collapsed;

        public MessageView(string from, string time, string text, bool isFile, string fileName, string fileSize, string fileId, bool isMine, ImageSource? previewImage, string? sourceDeviceId, bool isLocalPeerCopy, bool isLocalPhoneCopy)
        {
            From = from;
            Time = time;
            Text = text;
            IsFile = isFile;
            FileName = fileName;
            FileSize = fileSize;
            FileId = fileId;
            IsMine = isMine;
            PreviewImage = previewImage;
            SourceDeviceId = sourceDeviceId;
            IsLocalPeerCopy = isLocalPeerCopy;
            IsLocalPhoneCopy = isLocalPhoneCopy;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        public void RefreshTheme() => OnPropertyChanged(nameof(BubbleBrush));
        private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
