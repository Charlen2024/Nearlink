using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Win32;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;

namespace Nearlink.Native;

public partial class App : Application
{
    private static readonly string UserKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.UserName)))[..16];
    private static readonly string MutexName = $"Local\\Nearlink.Desktop.{UserKey}";
    private static readonly string PipeName = $"Nearlink.Desktop.Commands.{UserKey}";
    private readonly Mutex _instanceMutex;
    private readonly bool _isPrimaryInstance;
    private readonly DispatcherQueue _dispatcher;
    private readonly CancellationTokenSource _pipeLifetime = new();
    private readonly object _pendingLock = new();
    private readonly List<string> _pendingExplorerPaths = [];
    private Timer? _explorerDebounce;
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _instanceMutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        _isPrimaryInstance = createdNew;
        if (_isPrimaryInstance)
        {
            ExplorerShellIntegration.EnsureRegistered();
            _ = RunCommandPipeAsync(_pipeLifetime.Token);
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var command = ParseExplorerCommand(Environment.GetCommandLineArgs().Skip(1).ToArray());
        if (!_isPrimaryInstance)
        {
            _ = RelayToPrimaryAndExitAsync(command);
            return;
        }

        _window = new MainWindow();
        _window.Closed += (_, _) =>
        {
            _pipeLifetime.Cancel();
            _explorerDebounce?.Dispose();
            try { _instanceMutex.ReleaseMutex(); } catch { }
            _instanceMutex.Dispose();
        };
        _window.Activate();
        if (command.Paths.Count > 0) QueueExplorerPaths(command.Paths);
    }

    private static ExplorerCommand ParseExplorerCommand(string[] args)
    {
        var marker = Array.FindIndex(args, arg => string.Equals(arg, "--nearlink-send", StringComparison.OrdinalIgnoreCase));
        return marker < 0
            ? new ExplorerCommand("activate", [])
            : new ExplorerCommand("send", args.Skip(marker + 1).Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private async Task RelayToPrimaryAndExitAsync(ExplorerCommand command)
    {
        var delivered = false;
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.Asynchronous);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            await client.ConnectAsync(timeout.Token);
            await using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true);
            await writer.WriteLineAsync(JsonSerializer.Serialize(command));
            await writer.FlushAsync(timeout.Token);
            delivered = true;
        }
        catch { }
        if (!delivered) MessageBoxW(IntPtr.Zero, "Nearlink 暂时无法接收资源管理器的发送请求。请确认应用已启动后重试。", "Nearlink", 0x00000010);
        _dispatcher.TryEnqueue(Exit);
    }

    private async Task RunCommandPipeAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 8,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(cancellationToken);
                using var reader = new StreamReader(server, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
                var json = await reader.ReadLineAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(json)) continue;
                var command = JsonSerializer.Deserialize<ExplorerCommand>(json);
                if (command is null) continue;
                _dispatcher.TryEnqueue(() =>
                {
                    if (command.Action == "activate") _window?.Activate();
                    else if (command.Paths is { Count: > 0 }) QueueExplorerPaths(command.Paths);
                });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch { if (!cancellationToken.IsCancellationRequested) await Task.Delay(200, cancellationToken); }
        }
    }

    private void QueueExplorerPaths(IEnumerable<string> paths)
    {
        lock (_pendingLock)
        {
            _pendingExplorerPaths.AddRange(paths.Where(File.Exists));
            _explorerDebounce?.Dispose();
            _explorerDebounce = new Timer(_ => FlushExplorerPaths(), null, 450, Timeout.Infinite);
        }
    }

    private void FlushExplorerPaths()
    {
        string[] paths;
        lock (_pendingLock)
        {
            paths = _pendingExplorerPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            _pendingExplorerPaths.Clear();
            _explorerDebounce?.Dispose();
            _explorerDebounce = null;
        }
        if (paths.Length == 0) return;
        _dispatcher.TryEnqueue(() =>
        {
            if (_window is not null) _window.HandleExplorerFiles(paths);
            else QueueExplorerPaths(paths);
        });
    }

    private sealed record ExplorerCommand(string Action, IReadOnlyList<string> Paths);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr window, string text, string caption, uint type);
}

public static class ExplorerShellIntegration
{
    private const string VerbPath = @"Software\Classes\*\shell\Nearlink.Send";
    private static readonly string PreferencePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nearlink", "explorer-context-menu.txt");

    public static bool IsEnabled
    {
        get
        {
            try { return !File.Exists(PreferencePath) || !string.Equals(File.ReadAllText(PreferencePath).Trim(), "off", StringComparison.OrdinalIgnoreCase); }
            catch { return true; }
        }
    }

    public static void EnsureRegistered()
    {
        if (IsEnabled) Register();
        else Remove();
    }

    public static void SetEnabled(bool enabled)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PreferencePath)!);
        File.WriteAllText(PreferencePath, enabled ? "on" : "off");
        if (enabled) Register(); else Remove();
    }

    private static void Register()
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable)) return;
        using var verb = Registry.CurrentUser.CreateSubKey(VerbPath, writable: true);
        if (verb is null) return;
        verb.SetValue(null, "发送到 Nearlink", RegistryValueKind.String);
        verb.SetValue("Icon", $"{executable},0", RegistryValueKind.String);
        verb.SetValue("MultiSelectModel", "Document", RegistryValueKind.String);
        using var command = verb.CreateSubKey("command", writable: true);
        command?.SetValue(null, $"\"{executable}\" --nearlink-send \"%1\"", RegistryValueKind.String);
        RefreshShellAssociations();
    }

    private static void Remove()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(VerbPath, throwOnMissingSubKey: false); } catch { }
        RefreshShellAssociations();
    }

    private static void RefreshShellAssociations() => SHChangeNotify(0x08000000, 0x0000, IntPtr.Zero, IntPtr.Zero);

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);
}
