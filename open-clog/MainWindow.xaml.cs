using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Windows.Storage.Pickers;

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
                     && !f.Contains(".checkpoint.")
                     && !f.EndsWith(".trajectory.jsonl")
                     && !f.Contains(".reset."))
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
            SessionIdText.Text = Path.GetFileNameWithoutExtension(item.FileName);
            StopWatching();
            LoadSessionFile(fullPath);
            StartWatching(fullPath);
        }
        else
        {
            // No session selected — clear everything
            SessionIdText.Text = "";
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

        string firstTimestamp = null, lastTimestamp = null;
        foreach (var iter in SessionLogParser.BuildIterations(_lineMetas))
        {
            if (firstTimestamp == null) firstTimestamp = iter.Timestamp;
            lastTimestamp = iter.Timestamp;
            _messages.Add(new UserMessageItem
            {
                Id = iter.Id,
                Timestamp = SessionLogParser.FormatTimestamp(iter.Timestamp),
                Preview = iter.Preview ?? "",
                SubtreeOffset = iter.SubtreeOffset,
                SubtreeLength = iter.SubtreeLength
            });
        }

        UpdateMessagesHeader(null);

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

    private void UpdateMessagesHeader(UserMessageItem selectedItem)
    {
        var count = _messages.Count;
        if (count == 0)
        {
            MessagesHeader.Text = "Messages";
            return;
        }

        if (selectedItem != null)
        {
            var rangeStart = selectedItem.SubtreeOffset;
            var rangeEnd = rangeStart + selectedItem.SubtreeLength;
            string firstTs = null, lastTs = null;
            foreach (var lm in _lineMetas)
            {
                if (lm.Offset >= rangeStart && lm.Offset < rangeEnd && lm.Timestamp != null)
                {
                    if (firstTs == null) firstTs = lm.Timestamp;
                    lastTs = lm.Timestamp;
                }
            }

            if (firstTs != null && lastTs != null
                && DateTimeOffset.TryParse(firstTs, out var first)
                && DateTimeOffset.TryParse(lastTs, out var last))
            {
                var duration = last - first;
                if (duration < TimeSpan.Zero) duration = -duration;
                string timeStr;
                if (duration.TotalHours >= 1)
                    timeStr = $"{duration.TotalHours:F1}h";
                else if (duration.TotalMinutes >= 1)
                    timeStr = $"{duration.TotalMinutes:F1}m";
                else
                    timeStr = $"{duration.TotalSeconds:F1}s";
                MessagesHeader.Text = $"{count} Messages ({timeStr})";
                return;
            }
        }

        MessagesHeader.Text = $"{count} Messages";
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
            ExportLink.IsEnabled = true;
            PromptLink.IsEnabled = true;
            RefreshDetailList(item);
            UpdateMessagesHeader(item);
        }
        else
        {
            _detailItems.Clear();
            DetailList.Visibility = Visibility.Collapsed;
            NoTurnSelected.Visibility = Visibility.Visible;
            ExportLink.IsEnabled = false;
            PromptLink.IsEnabled = false;
            UpdateMessagesHeader(null);
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

    private async void ExportLink_Click(object sender, RoutedEventArgs e)
    {
        if (_currentFilePath == null || _detailItems.Count == 0) return;

        var picker = new FileSavePicker();
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.FileTypeChoices.Add("Text file", new List<string> { ".txt" });
        picker.SuggestedFileName = "turn-export";

        // Initialize picker with window handle
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSaveFileAsync();
        if (file == null) return;

        var sb = new StringBuilder();

        // Summary section
        sb.AppendLine("Summary");
        sb.AppendLine(new string('-', 100));
        sb.AppendLine();

        foreach (var detail in _detailItems.Reverse())
        {
            var line1 = detail.TypeAndId ?? "";

            // Add token counts right-aligned to 100 chars
            if (!string.IsNullOrEmpty(detail.TokenIn))
            {
                var tokens = $"in:{detail.TokenIn}  cacheRd:{detail.TokenCacheRead}  cacheWr:{detail.TokenCacheWrite}  out:{detail.TokenOut}";
                var padding = Math.Max(1, 100 - line1.Length - tokens.Length);
                line1 += new string(' ', padding) + tokens;
            }
            if (line1.Length > 100) line1 = line1[..100];
            sb.AppendLine(line1);

            if (!string.IsNullOrEmpty(detail.ContentPreview))
            {
                var preview = detail.ContentPreview;
                if (preview.Length > 100) preview = preview[..97] + "...";
                sb.AppendLine("  " + preview);
            }
            if (!string.IsNullOrEmpty(detail.ToolCallPreview))
            {
                foreach (var tc in detail.ToolCallPreview.Split('\n'))
                {
                    var tcLine = tc;
                    if (tcLine.Length > 98) tcLine = tcLine[..95] + "...";
                    sb.AppendLine("  " + tcLine);
                }
            }
            if (!string.IsNullOrEmpty(detail.ThinkingPreview))
            {
                var thinking = detail.ThinkingPreview;
                if (thinking.Length > 98) thinking = thinking[..95] + "...";
                sb.AppendLine("  [thinking] " + thinking);
            }
            sb.AppendLine();
        }

        // Details section
        sb.AppendLine();
        sb.AppendLine("Details");
        sb.AppendLine(new string('-', 100));
        sb.AppendLine();
        sb.AppendLine("[");

        bool first = true;
        foreach (var detail in _detailItems.Reverse())
        {
            if (!first) sb.AppendLine(",");
            first = false;

            try
            {
                var buffer = new byte[detail.ByteLength];
                using var fs = new FileStream(_currentFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                fs.Seek(detail.ByteOffset, SeekOrigin.Begin);
                fs.Read(buffer, 0, detail.ByteLength);
                var raw = Encoding.UTF8.GetString(buffer);

                using var doc = JsonDocument.Parse(raw);
                sb.Append(JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch
            {
                sb.Append("  { \"error\": \"could not read\" }");
            }
        }

        sb.AppendLine();
        sb.AppendLine("]");

        await Windows.Storage.FileIO.WriteTextAsync(file, sb.ToString());
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

    private void PromptLink_Click(object sender, RoutedEventArgs e)
    {
        if (_currentFilePath == null) return;
        if (MessageList.SelectedItem is not UserMessageItem selectedTurn) return;

        try
        {
            // Find the timestamp of the first line in this turn
            string turnTimestamp = null;
            var rangeStart = selectedTurn.SubtreeOffset;
            var rangeEnd = rangeStart + selectedTurn.SubtreeLength;
            foreach (var lm in _lineMetas)
            {
                if (lm.Offset >= rangeStart && lm.Offset < rangeEnd && lm.Timestamp != null)
                {
                    turnTimestamp = lm.Timestamp;
                    break;
                }
            }
            if (turnTimestamp == null) return;
            if (!DateTimeOffset.TryParse(turnTimestamp, out var turnTs)) return;

            // Find the trajectory sidecar file
            var dir = Path.GetDirectoryName(_currentFilePath);
            var baseName = Path.GetFileNameWithoutExtension(_currentFilePath);
            var trajectoryPath = Path.Combine(dir, baseName + ".trajectory.jsonl");
            if (!File.Exists(trajectoryPath)) return;

            // Read the trajectory file — it's JSONL (one JSON object per line)
            string systemPrompt = null;
            using (var fs = new FileStream(trajectoryPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(fs))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        using var lineDoc = JsonDocument.Parse(line);
                        var entry = lineDoc.RootElement;

                        var type = entry.TryGetProperty("type", out var tp) ? tp.GetString() : null;
                        if (type != "context.compiled") continue;

                        var ts = entry.TryGetProperty("ts", out var tsProp) ? tsProp.GetString() : null;
                        if (ts == null || !DateTimeOffset.TryParse(ts, out var entryTs)) continue;

                        if (entryTs <= turnTs)
                        {
                            if (entry.TryGetProperty("data", out var data)
                                && data.TryGetProperty("systemPrompt", out var sp))
                            {
                                systemPrompt = sp.GetString();
                            }
                        }
                        else
                        {
                            break; // past our turn, stop
                        }
                    }
                    catch { continue; }
                }
            }

            if (string.IsNullOrEmpty(systemPrompt))
            {
                systemPrompt = "(No system prompt found for this turn)";
            }

            var promptWindow = new Window();
            promptWindow.Title = "System Prompt";
            promptWindow.Content = new ScrollViewer
            {
                Content = BuildMarkdownBlock(systemPrompt),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            promptWindow.Activate();
        }
        catch { }
    }

    private static RichTextBlock BuildMarkdownBlock(string markdown)
    {
        var rtb = new RichTextBlock
        {
            TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            Padding = new Thickness(16),
            FontSize = 13
        };

        var lines = markdown.Split('\n');
        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');
            var para = new Paragraph();

            // Headings
            if (line.StartsWith("### "))
            {
                para.FontSize = 16;
                para.Margin = new Thickness(0, 12, 0, 4);
                AddInlineRuns(para.Inlines, line.Substring(4), isBold: true);
            }
            else if (line.StartsWith("## "))
            {
                para.FontSize = 18;
                para.Margin = new Thickness(0, 14, 0, 4);
                AddInlineRuns(para.Inlines, line.Substring(3), isBold: true);
            }
            else if (line.StartsWith("# "))
            {
                para.FontSize = 20;
                para.Margin = new Thickness(0, 16, 0, 4);
                AddInlineRuns(para.Inlines, line.Substring(2), isBold: true);
            }
            else
            {
                para.Margin = new Thickness(0, 4, 0, 4);
                AddInlineRuns(para.Inlines, line, isBold: false);
            }

            rtb.Blocks.Add(para);
        }

        return rtb;
    }

    private static void AddInlineRuns(InlineCollection inlines, string text, bool isBold)
    {
        // Process bold (**...**) and italic (*...* or _..._)
        int i = 0;
        while (i < text.Length)
        {
            // Bold: **...**
            if (i + 1 < text.Length && text[i] == '*' && text[i + 1] == '*')
            {
                var end = text.IndexOf("**", i + 2);
                if (end > i + 2)
                {
                    var run = new Run { Text = text.Substring(i + 2, end - i - 2) };
                    run.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
                    inlines.Add(run);
                    i = end + 2;
                    continue;
                }
            }

            // Italic: *...* (single)
            if (text[i] == '*' && (i + 1 >= text.Length || text[i + 1] != '*'))
            {
                var end = text.IndexOf('*', i + 1);
                if (end > i + 1)
                {
                    var run = new Run { Text = text.Substring(i + 1, end - i - 1) };
                    run.FontStyle = Windows.UI.Text.FontStyle.Italic;
                    inlines.Add(run);
                    i = end + 1;
                    continue;
                }
            }

            // Italic: _..._
            if (text[i] == '_')
            {
                var end = text.IndexOf('_', i + 1);
                if (end > i + 1)
                {
                    var run = new Run { Text = text.Substring(i + 1, end - i - 1) };
                    run.FontStyle = Windows.UI.Text.FontStyle.Italic;
                    inlines.Add(run);
                    i = end + 1;
                    continue;
                }
            }

            // Plain text — accumulate until next marker
            int next = i + 1;
            while (next < text.Length && text[next] != '*' && text[next] != '_')
                next++;

            var plainRun = new Run { Text = text.Substring(i, next - i) };
            if (isBold)
                plainRun.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
            inlines.Add(plainRun);
            i = next;
        }
    }
}
