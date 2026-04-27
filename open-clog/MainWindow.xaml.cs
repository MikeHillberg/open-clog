using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace open_clog;

public class DetailLineItem : DetailInfo
{
    public Visibility HasContent => string.IsNullOrEmpty(ContentPreview) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility HasToolCall => string.IsNullOrEmpty(ToolCallPreview) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility HasThinking => string.IsNullOrEmpty(ThinkingPreview) ? Visibility.Collapsed : Visibility.Visible;
    public int ItemIndex { get; set; }
    public Microsoft.UI.Xaml.Media.Brush RowBackground => ItemIndex % 2 == 0
        ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent)
        : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray) { Opacity = 0.15 };
}

public class UserMessageItem
{
    public string Id { get; set; }
    public string Timestamp { get; set; }
    public string Preview { get; set; }
    public long SubtreeOffset { get; set; }
    public int SubtreeLength { get; set; }
}

public class SessionFileItem
{
    public string FileName { get; set; }
    public string DisplayName { get; set; }
    public override string ToString() => DisplayName;
}

public sealed partial class MainWindow : Window
{
    private ObservableCollection<UserMessageItem> _messages = new();
    private ObservableCollection<DetailLineItem> _detailItems = new();
    private string _currentFilePath;
    private FileSystemWatcher _watcher;
    private List<LineMeta> _lineMetas = new();
    private long _lastReadPosition;
    private string _leftoverText = "";
    private string _agentsDir;
    private string _sessionsDir;
    private Dictionary<string, string> _sessionLabels = new();
    private bool _suppressSelectionChanged;
    private bool _autoScroll = true;

    public MainWindow()
    {
        this.InitializeComponent();
        VerticalSplitter.InvertDirection = false;
        this.AppWindow.SetIcon(Path.Combine(Windows.ApplicationModel.Package.Current.InstalledLocation.Path, "Assets", "open-toed-clog.ico"));
        MessageList.ItemsSource = _messages;
        DetailList.ItemsSource = _detailItems;

        _agentsDir = App.TestInstall
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".openclaw-nonexistent", "agents")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".openclaw", "agents");

        PopulateAgentCombo();
    }

    private void PopulateAgentCombo()
    {
        var agents = new List<string>();
        if (Directory.Exists(_agentsDir))
        {
            agents = Directory.GetDirectories(_agentsDir)
                .Where(d => Directory.Exists(Path.Combine(d, "sessions")) && Directory.GetFiles(Path.Combine(d, "sessions"), "*.jsonl*").Any(f => !f.EndsWith(".lock") && !Path.GetFileName(f).StartsWith("sessions.json")))
                .Select(Path.GetFileName)
                .OrderBy(n => n == "main" ? 0 : 1)
                .ThenBy(n => n)
                .ToList();
        }

        if (agents.Count == 0)
        {
            NoInstallBar.IsOpen = true;
            return;
        }

        NoInstallBar.IsOpen = false;
        AgentCombo.ItemsSource = agents;
        AgentCombo.SelectedIndex = 0;
    }

    private void AgentCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AgentCombo.SelectedItem is string agentName)
        {
            _sessionsDir = Path.Combine(_agentsDir, agentName, "sessions");
            LoadSessionLabels();
            PopulateSessionCombo();
        }
    }

    private void LoadSessionLabels()
    {
        _sessionLabels.Clear();
        var sessionsJsonPath = Path.Combine(_sessionsDir, "sessions.json");
        if (!File.Exists(sessionsJsonPath))
            return;

        try
        {
            var json = File.ReadAllText(sessionsJsonPath);
            _sessionLabels = SessionLogParser.ParseSessionLabels(json);
        }
        catch { }
    }

    private void PopulateSessionCombo()
    {
        if (!Directory.Exists(_sessionsDir))
            return;

        var files = Directory.GetFiles(_sessionsDir, "*.jsonl*")
            .Where(f => !f.EndsWith(".lock")
                     && !Path.GetFileName(f).StartsWith("sessions.json")
                     && !f.Contains(".checkpoint."))
            .Select(f =>
            {
                var name = Path.GetFileName(f);
                var id = Path.GetFileNameWithoutExtension(name);
                var display = _sessionLabels.TryGetValue(id, out var label)
                    ? label
                    : name;
                var isHeartbeat = display.Contains("heartbeat", StringComparison.OrdinalIgnoreCase);
                return (Item: new SessionFileItem { FileName = name, DisplayName = display }, IsHeartbeat: isHeartbeat, LastWrite: File.GetLastWriteTime(f));
            })
            .OrderBy(x => x.IsHeartbeat ? 1 : 0)
            .ThenByDescending(x => x.LastWrite)
            .Select(x => x.Item)
            .ToList();

        SessionCombo.SelectedIndex = -1;
        SessionCombo.ItemsSource = files;
        if (files.Count > 0)
            SessionCombo.SelectedIndex = 0;
    }

    private void SessionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SessionCombo.SelectedItem is SessionFileItem item)
        {
            var fullPath = Path.Combine(_sessionsDir, item.FileName);
            StopWatching();
            LoadSessionFile(fullPath);
            StartWatching(fullPath);
        }
        else
        {
            // No session selected — clear everything
            StopWatching();
            _messages.Clear();
            _detailItems.Clear();
            _lineMetas.Clear();
            _currentFilePath = null;
            DetailJsonText.Text = "";
        }
    }

    private void StopWatching()
    {
        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= OnFileChanged;
            _watcher.Dispose();
            _watcher = null;
        }
    }

    private void StartWatching(string path)
    {
        _watcher = new FileSystemWatcher(Path.GetDirectoryName(path), Path.GetFileName(path))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true
        };
        _watcher.Changed += OnFileChanged;
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        try
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                try { ReadNewLines(); }
                catch { }
            });
        }
        catch { }
    }

    private void LoadSessionFile(string path)
    {
        _messages.Clear();
        _detailItems.Clear();
        _lineMetas.Clear();
        _lastReadPosition = 0;
        _leftoverText = "";
        _currentFilePath = path;

        ReadNewLines();
    }

    private void ReadNewLines()
    {
        if (_currentFilePath == null) return;

        byte[] newBytes;
        long readStartPos = _lastReadPosition;
        try
        {
            using var fs = new FileStream(_currentFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var fileLength = fs.Length;
            if (fileLength <= _lastReadPosition)
                return;

            fs.Seek(_lastReadPosition, SeekOrigin.Begin);
            var bytesToRead = (int)(fileLength - _lastReadPosition);
            newBytes = new byte[bytesToRead];
            var bytesRead = fs.Read(newBytes, 0, bytesToRead);
            if (bytesRead < bytesToRead)
                Array.Resize(ref newBytes, bytesRead);
            _lastReadPosition += bytesRead;
        }
        catch { return; }

        var (newLines, leftover) = SessionLogParser.ParseIncrementalBytes(newBytes, readStartPos, _leftoverText);
        _leftoverText = leftover;

        if (newLines.Count > 0)
        {
            _lineMetas.AddRange(newLines);
            RebuildMessageList();
        }
    }

    private void RebuildMessageList()
    {
        var selectedId = (MessageList.SelectedItem as UserMessageItem)?.Id;
        var selectedDetailOffset = (DetailList.SelectedItem as DetailLineItem)?.ByteOffset;

        _suppressSelectionChanged = true;
        _messages.Clear();

        foreach (var iter in SessionLogParser.BuildIterations(_lineMetas))
        {
            _messages.Add(new UserMessageItem
            {
                Id = iter.Id,
                Timestamp = SessionLogParser.FormatTimestamp(iter.Timestamp),
                Preview = iter.Preview ?? "",
                SubtreeOffset = iter.SubtreeOffset,
                SubtreeLength = iter.SubtreeLength
            });
        }

        if (selectedId != null)
        {
            var match = _messages.FirstOrDefault(m => m.Id == selectedId);
            if (match != null)
                MessageList.SelectedItem = match;
        }

        _suppressSelectionChanged = false;

        if (_autoScroll && _messages.Count > 0)
        {
            // Force select latest turn and refresh its details
            MessageList.SelectedItem = _messages[0];
            RefreshDetailList(_messages[0]);
        }
        // Only auto-refresh detail list if viewing the latest turn
        else if (MessageList.SelectedItem is UserMessageItem selected && _messages.Count > 0 && selected == _messages[0])
        {
            RefreshDetailList(selected);
        }
        else if (selectedDetailOffset.HasValue)
        {
            // Restore detail selection that was preserved across the rebuild
            var detailMatch = _detailItems.FirstOrDefault(d => d.ByteOffset == selectedDetailOffset.Value);
            if (detailMatch != null)
                DetailList.SelectedItem = detailMatch;
        }
    }

    private void RefreshDetailList(UserMessageItem item)
    {
        // Remember selected detail by byte offset (stable across refreshes)
        var selectedOffset = (DetailList.SelectedItem as DetailLineItem)?.ByteOffset;

        _detailItems.Clear();

        byte[] buffer;
        try
        {
            buffer = new byte[item.SubtreeLength];
            using var fs = new FileStream(_currentFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            fs.Seek(item.SubtreeOffset, SeekOrigin.Begin);
            fs.Read(buffer, 0, item.SubtreeLength);
        }
        catch { return; }

        var details = SessionLogParser.ParseDetails(buffer, item.SubtreeOffset);
        details.Reverse(); // most recent on top

        int idx = 0;
        foreach (var d in details)
        {
            _detailItems.Add(new DetailLineItem
            {
                TypeAndId = d.TypeAndId,
                Role = d.Role,
                ContentPreview = d.ContentPreview,
                ToolCallPreview = d.ToolCallPreview,
                ThinkingPreview = d.ThinkingPreview,
                TokenIn = d.TokenIn,
                TokenCacheRead = d.TokenCacheRead,
                TokenCacheWrite = d.TokenCacheWrite,
                TokenOut = d.TokenOut,
                ByteOffset = d.ByteOffset,
                ByteLength = d.ByteLength,
                ItemIndex = idx++
            });
        }

        // Restore detail selection
        if (_autoScroll && _detailItems.Count > 0)
        {
            DetailList.SelectedItem = _detailItems[0];
        }
        else if (selectedOffset.HasValue)
        {
            var match = _detailItems.FirstOrDefault(d => d.ByteOffset == selectedOffset.Value);
            if (match != null)
                DetailList.SelectedItem = match;
        }
    }

    private void AutoScrollToggle_Click(object sender, RoutedEventArgs e)
    {
        _autoScroll = AutoScrollToggle.IsChecked == true;
        if (_autoScroll)
            ApplyAutoScroll();
    }

    private void ApplyAutoScroll()
    {
        if (_messages.Count > 0 && MessageList.SelectedItem != _messages[0])
        {
            MessageList.SelectedItem = _messages[0];
        }
        if (_detailItems.Count > 0 && DetailList.SelectedItem != _detailItems[0])
        {
            DetailList.SelectedItem = _detailItems[0];
        }
    }

    private void MessageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionChanged) return;

        // If user manually selects a non-latest turn, disable auto-scroll
        if (_autoScroll && MessageList.SelectedItem is UserMessageItem sel
            && _messages.Count > 0 && sel != _messages[0])
        {
            _autoScroll = false;
            AutoScrollToggle.IsChecked = false;
        }

        if (MessageList.SelectedItem is UserMessageItem item)
        {
            NoTurnSelected.Visibility = Visibility.Collapsed;
            DetailList.Visibility = Visibility.Visible;
            RefreshDetailList(item);
        }
        else
        {
            _detailItems.Clear();
            DetailList.Visibility = Visibility.Collapsed;
            NoTurnSelected.Visibility = Visibility.Visible;
            DetailJsonText.Text = "";
            DetailJsonScroller.Visibility = Visibility.Collapsed;
            NoMessageSelected.Visibility = Visibility.Visible;
        }
    }

    private void DetailList_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        if (DetailList.SelectedItem is DetailLineItem item && item.ByteLength > 0)
        {
            var pretty = LoadPrettyJson(item);
            var window = new JsonDetailWindow(pretty);
            window.ActivateAndForeground();
        }
    }

    private void DetailList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // If user manually selects a non-latest message, disable auto-scroll
        if (_autoScroll && DetailList.SelectedItem is DetailLineItem sel
            && _detailItems.Count > 0 && sel != _detailItems[0])
        {
            _autoScroll = false;
            AutoScrollToggle.IsChecked = false;
        }

        if (DetailList.SelectedItem is DetailLineItem item && item.ByteLength > 0)
        {
            DetailJsonText.Text = LoadPrettyJson(item);
            DetailJsonScroller.Visibility = Visibility.Visible;
            NoMessageSelected.Visibility = Visibility.Collapsed;
        }
        else
        {
            DetailJsonText.Text = "";
            DetailJsonScroller.Visibility = Visibility.Collapsed;
            NoMessageSelected.Visibility = Visibility.Visible;
        }
    }

    private string LoadPrettyJson(DetailLineItem item)
    {
        try
        {
            var buffer = new byte[item.ByteLength];
            using var fs = new FileStream(_currentFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            fs.Seek(item.ByteOffset, SeekOrigin.Begin);
            fs.Read(buffer, 0, item.ByteLength);
            var raw = Encoding.UTF8.GetString(buffer);

            using var doc = JsonDocument.Parse(raw);
            return JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return "(error reading JSON)";
        }
    }
}
