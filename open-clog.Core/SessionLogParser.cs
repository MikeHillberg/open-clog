using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace open_clog;

/// <summary>
/// Parsed metadata for a single line in the JSONL file.
/// </summary>
public struct LineMeta
{
    public long Offset;
    public int Length;
    public bool IsUserMessage;
    public string Timestamp;
    public string UserPreview;
}

/// <summary>
/// A user message iteration: the user message plus all subsequent entries until the next user message.
/// </summary>
public class IterationInfo
{
    public string Id { get; set; }
    public string Timestamp { get; set; }
    public string Preview { get; set; }
    public long SubtreeOffset { get; set; }
    public int SubtreeLength { get; set; }
}

/// <summary>
/// Parsed detail for a single JSONL entry.
/// </summary>
public class DetailInfo
{
    public string TypeAndId { get; set; }
    public string Role { get; set; }
    public string ContentPreview { get; set; }
    public string ToolCallPreview { get; set; }
    public string ThinkingPreview { get; set; }
    public string TokenIn { get; set; }
    public string TokenCacheRead { get; set; }
    public string TokenCacheWrite { get; set; }
    public string TokenOut { get; set; }
    public long ByteOffset { get; set; }
    public int ByteLength { get; set; }
}

/// <summary>
/// Session label info parsed from sessions.json.
/// </summary>
public class SessionLabel
{
    public string SessionId { get; set; }
    public string DisplayName { get; set; }
}

/// <summary>
/// Pure logic for parsing OpenClaw session JSONL files. No XAML dependencies.
/// </summary>
public static class SessionLogParser
{
    /// <summary>
    /// Parse a JSONL line to extract metadata (is it a user message, timestamp, offset info).
    /// Returns null if the line can't be parsed (e.g. partial write).
    /// </summary>
    public static LineMeta? ParseLineMeta(string line, long offset)
    {
        if (string.IsNullOrEmpty(line)) return null;

        bool isUser = false;
        string ts = null;

        string userPreview = null;

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            if (root.TryGetProperty("timestamp", out var tsProp))
                ts = tsProp.GetString();

            var type = root.TryGetProperty("type", out var tp) ? tp.GetString() : null;

            if (type == "message"
                && root.TryGetProperty("message", out var msgProp)
                && msgProp.TryGetProperty("role", out var roleProp)
                && roleProp.GetString() == "user")
            {
                isUser = true;
                if (msgProp.TryGetProperty("content", out var content))
                {
                    string rawText = null;
                    if (content.ValueKind == JsonValueKind.String)
                        rawText = content.GetString();
                    else if (content.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var part in content.EnumerateArray())
                        {
                            if (part.TryGetProperty("type", out var ct) && ct.GetString() == "text"
                                && part.TryGetProperty("text", out var txt))
                            {
                                rawText = txt.GetString();
                                break;
                            }
                        }
                    }
                    if (rawText != null)
                        userPreview = Truncate(ExtractUserText(rawText), 200);
                }
            }
        }
        catch { return null; }

        return new LineMeta
        {
            Offset = offset,
            Length = Encoding.UTF8.GetByteCount(line),
            IsUserMessage = isUser,
            Timestamp = ts,
            UserPreview = userPreview
        };
    }

    /// <summary>
    /// Given a list of line metas, build the iteration list (user messages with their subtree ranges).
    /// Returns most-recent-first order.
    /// </summary>
    public static List<IterationInfo> BuildIterations(List<LineMeta> lineMetas)
    {
        var results = new List<IterationInfo>();
        var lineCount = lineMetas.Count;

        for (int i = 0; i < lineCount; i++)
        {
            if (!lineMetas[i].IsUserMessage) continue;

            var rangeStart = lineMetas[i].Offset;

            int endLineExclusive = lineCount;
            for (int j = i + 1; j < lineCount; j++)
            {
                if (lineMetas[j].IsUserMessage)
                {
                    endLineExclusive = j;
                    break;
                }
            }

            var lastLineIdx = endLineExclusive - 1;
            var rangeEnd = lineMetas[lastLineIdx].Offset + lineMetas[lastLineIdx].Length;

            results.Add(new IterationInfo
            {
                Id = i.ToString(),
                Timestamp = lineMetas[i].Timestamp,
                Preview = lineMetas[i].UserPreview,
                SubtreeOffset = rangeStart,
                SubtreeLength = (int)(rangeEnd - rangeStart)
            });
        }

        results.Reverse();
        return results;
    }

    /// <summary>
    /// Parse a byte buffer (a subtree region from the JSONL file) into detail items.
    /// </summary>
    public static List<DetailInfo> ParseDetails(byte[] buffer, long baseOffset)
    {
        var results = new List<DetailInfo>();
        int start = 0;

        for (int i = 0; i <= buffer.Length; i++)
        {
            if (i == buffer.Length || buffer[i] == (byte)'\n')
            {
                int len = i - start;
                int trimmedLen = (len > 0 && buffer[start + len - 1] == (byte)'\r') ? len - 1 : len;
                if (trimmedLen > 0)
                {
                    var lineText = Encoding.UTF8.GetString(buffer, start, trimmedLen);
                    var lineFileOffset = baseOffset + start;
                    var detail = ParseDetailLine(lineText, lineFileOffset, trimmedLen);
                    if (detail != null)
                        results.Add(detail);
                }
                start = i + 1;
            }
        }

        return results;
    }

    /// <summary>
    /// Parse a single JSONL line into a DetailInfo.
    /// </summary>
    public static DetailInfo ParseDetailLine(string lineText, long fileOffset, int byteLength)
    {
        try
        {
            using var doc = JsonDocument.Parse(lineText);
            var root = doc.RootElement;

            var type = root.TryGetProperty("type", out var tp) ? tp.GetString() : "?";

            string customType = null;
            if (type == "custom" && root.TryGetProperty("customType", out var ctp))
                customType = ctp.GetString();

            string role = null;
            string contentPreview = null;
            string toolCallPreview = null;
            string thinkingPreview = null;

            if (root.TryGetProperty("message", out var msg))
            {
                if (msg.TryGetProperty("role", out var rp))
                    role = rp.GetString();

                if (msg.TryGetProperty("content", out var content))
                {
                    if (content.ValueKind == JsonValueKind.String)
                    {
                        var text = content.GetString();
                        contentPreview = Truncate(role == "user" ? ExtractUserText(text) : text, 200);
                    }
                    else if (content.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var part in content.EnumerateArray())
                        {
                            var partType = part.TryGetProperty("type", out var ptv) ? ptv.GetString() : null;

                            if (partType == "text" && part.TryGetProperty("text", out var txt))
                            {
                                var text = txt.GetString();
                                contentPreview = Truncate(role == "user" ? ExtractUserText(text) : text, 200);
                            }
                            else if (partType == "tool_call" || partType == "toolCall" || partType == "tool_use")
                            {
                                var name = part.TryGetProperty("name", out var np) ? np.GetString() : "";
                                var args = part.TryGetProperty("arguments", out var ap) ? Truncate(ap.GetRawText(), 150) : "";
                                toolCallPreview = $"{name}, {args}";
                            }
                            else if (partType == "thinking" && part.TryGetProperty("text", out var thinkTxt))
                            {
                                thinkingPreview = Truncate(thinkTxt.GetString(), 150);
                            }
                        }
                    }
                }
            }

            if (customType != null && contentPreview == null)
                contentPreview = customType;

            string tokenIn = null, tokenCacheRead = null, tokenCacheWrite = null, tokenOut = null;
            if (role == "assistant" && root.TryGetProperty("message", out var msgForUsage)
                && msgForUsage.TryGetProperty("usage", out var usage))
            {
                tokenIn = usage.TryGetProperty("input", out var inp) ? inp.GetInt64().ToString("N0") : "";
                tokenCacheRead = usage.TryGetProperty("cacheRead", out var cr) ? cr.GetInt64().ToString("N0") : "";
                tokenCacheWrite = usage.TryGetProperty("cacheWrite", out var cw) ? cw.GetInt64().ToString("N0") : "";
                tokenOut = usage.TryGetProperty("output", out var outp) ? outp.GetInt64().ToString("N0") : "";
            }

            var typeAndId = (type == "message" && role != null) ? role : type;

            return new DetailInfo
            {
                TypeAndId = typeAndId,
                Role = role,
                ContentPreview = contentPreview,
                ToolCallPreview = toolCallPreview,
                ThinkingPreview = thinkingPreview,
                TokenIn = tokenIn,
                TokenCacheRead = tokenCacheRead,
                TokenCacheWrite = tokenCacheWrite,
                TokenOut = tokenOut,
                ByteOffset = fileOffset,
                ByteLength = byteLength
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Format a date as short month/day without year, using current locale.
    /// e.g. "4/18" (US), "18/4" (UK), "18.4" (DE)
    /// </summary>
    public static string FormatShortDate(DateTimeOffset dto)
    {
        // Get the short date pattern and strip the year component
        var pattern = System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.ShortDatePattern;
        // Remove year parts like yyyy, yy and surrounding separators
        pattern = System.Text.RegularExpressions.Regex.Replace(pattern, @"[/\-. ]*y+[/\-. ]*", "");
        // Remove leading/trailing separators
        pattern = pattern.Trim('/', '-', '.', ' ');
        return dto.ToString(pattern);
    }

    /// <summary>
    /// For user messages with OpenClaw envelope metadata, extract just the human-written text.
    /// The envelope pattern is: metadata blocks with ```json fences, then the actual message at the end.
    /// </summary>
    public static string ExtractUserText(string text)
    {
        if (text == null) return null;

        int lastFenceEnd = -1;
        int idx = 0;
        while (idx < text.Length)
        {
            var fencePos = text.IndexOf("```", idx);
            if (fencePos < 0) break;

            var afterFence = fencePos + 3;
            if (afterFence >= text.Length || text[afterFence] == '\n' || text[afterFence] == '\r')
            {
                lastFenceEnd = afterFence;
                if (lastFenceEnd < text.Length && text[lastFenceEnd] == '\r') lastFenceEnd++;
                if (lastFenceEnd < text.Length && text[lastFenceEnd] == '\n') lastFenceEnd++;
            }
            idx = afterFence;
        }

        if (lastFenceEnd > 0 && lastFenceEnd < text.Length)
        {
            var remainder = text.Substring(lastFenceEnd).TrimStart('\r', '\n');
            if (remainder.Length > 0)
                return remainder;
        }

        return text;
    }

    /// <summary>
    /// Truncate a string to the first non-empty line, capped at max characters.
    /// </summary>
    public static string Truncate(string s, int max)
    {
        if (s == null) return null;
        var firstLine = s.Split('\n').FirstOrDefault(l => l.Trim().Length > 0) ?? s;
        return firstLine.Length <= max ? firstLine : firstLine[..max] + "\u2026";
    }

    /// <summary>
    /// Format an ISO timestamp for display.
    /// </summary>
    public static string FormatTimestamp(string iso, DateTimeOffset? now = null)
    {
        if (DateTimeOffset.TryParse(iso, out var dto))
        {
            var local = dto.ToLocalTime();
            var today = (now ?? DateTimeOffset.Now).Date;
            if (local.Date == today)
                return local.ToString("t");
            else
                return local.ToString("t") + ", " + FormatShortDate(local);
        }
        return iso ?? "";
    }

    /// <summary>
    /// Parse sessions.json to extract session labels.
    /// </summary>
    public static Dictionary<string, string> ParseSessionLabels(string json)
    {
        var labels = new Dictionary<string, string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                var entry = prop.Value;
                var sessionId = entry.TryGetProperty("sessionId", out var sid) ? sid.GetString() : null;
                if (sessionId == null) continue;

                var label = "";
                if (entry.TryGetProperty("origin", out var origin) && origin.TryGetProperty("label", out var lbl))
                {
                    label = lbl.GetString() ?? "";
                    var idIdx = label.IndexOf(" id:");
                    if (idIdx >= 0) label = label.Substring(0, idIdx);
                }

                var channel = entry.TryGetProperty("lastChannel", out var ch) ? ch.GetString() ?? "" : "";

                var friendly = !string.IsNullOrEmpty(label) ? label : "";
                if (!string.IsNullOrEmpty(channel))
                    friendly = !string.IsNullOrEmpty(friendly) ? $"{friendly} ({channel})" : channel;

                if (!string.IsNullOrEmpty(friendly))
                    labels[sessionId] = friendly;
            }
        }
        catch { }
        return labels;
    }

    /// <summary>
    /// Process new bytes from a JSONL file incrementally.
    /// Returns parsed LineMetas for complete lines, and any leftover text from an incomplete final line.
    /// </summary>
    public static (List<LineMeta> newLines, string leftover) ParseIncrementalBytes(
        byte[] newBytes, long readStartPos, string previousLeftover)
    {
        var newText = previousLeftover + Encoding.UTF8.GetString(newBytes);

        var leftoverByteLen = Encoding.UTF8.GetByteCount(previousLeftover);
        long textFileOffset = readStartPos - leftoverByteLen;

        var parts = newText.Split('\n');
        bool lastIsComplete = newText.EndsWith('\n');
        int completeCount = lastIsComplete ? parts.Length : parts.Length - 1;

        var leftover = lastIsComplete ? "" : (parts.Length > 0 ? parts[^1] : "");

        var results = new List<LineMeta>();
        long pos = textFileOffset;
        for (int i = 0; i < completeCount; i++)
        {
            var line = parts[i].TrimEnd('\r');
            if (line.Length > 0)
            {
                var meta = ParseLineMeta(line, pos);
                if (meta.HasValue)
                    results.Add(meta.Value);
            }
            pos += Encoding.UTF8.GetByteCount(parts[i]) + 1;
        }

        return (results, leftover);
    }
}
