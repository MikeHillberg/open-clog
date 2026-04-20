using Microsoft.VisualStudio.TestTools.UnitTesting;
using open_clog;
using System.Collections.Generic;
using System.Text;

namespace open_clog.Tests;

[TestClass]
public class SessionLogParserTests
{
    [TestMethod]
    public void ExtractUserText_StripsFencedMetadata()
    {
        var input = "Conversation info (untrusted metadata):\n```json\n{\"message_id\": \"1\"}\n```\n\nSender:\n```json\n{\"name\": \"Mike\"}\n```\n\nHello world!";
        var result = SessionLogParser.ExtractUserText(input);
        Assert.AreEqual("Hello world!", result);
    }

    [TestMethod]
    public void ExtractUserText_NoFences_ReturnsOriginal()
    {
        var input = "Just a plain message";
        var result = SessionLogParser.ExtractUserText(input);
        Assert.AreEqual("Just a plain message", result);
    }

    [TestMethod]
    public void ExtractUserText_Null_ReturnsNull()
    {
        Assert.IsNull(SessionLogParser.ExtractUserText(null));
    }

    [TestMethod]
    public void ExtractUserText_OnlyFences_ReturnsOriginal()
    {
        var input = "```json\n{}\n```";
        var result = SessionLogParser.ExtractUserText(input);
        Assert.AreEqual(input, result);
    }

    [TestMethod]
    public void Truncate_ShortString_ReturnsAsIs()
    {
        Assert.AreEqual("hello", SessionLogParser.Truncate("hello", 10));
    }

    [TestMethod]
    public void Truncate_LongString_TruncatesWithEllipsis()
    {
        var result = SessionLogParser.Truncate("abcdefghij", 5);
        Assert.AreEqual("abcde\u2026", result);
    }

    [TestMethod]
    public void Truncate_MultiLine_UsesFirstNonEmptyLine()
    {
        var result = SessionLogParser.Truncate("\n\nactual line\nmore", 100);
        Assert.AreEqual("actual line", result);
    }

    [TestMethod]
    public void Truncate_Null_ReturnsNull()
    {
        Assert.IsNull(SessionLogParser.Truncate(null, 10));
    }

    [TestMethod]
    public void FormatTimestamp_Today_ShowsTimeOnly()
    {
        var now = new System.DateTimeOffset(2026, 4, 18, 12, 0, 0, System.TimeSpan.FromHours(-7));
        var iso = "2026-04-18T10:30:00-07:00";
        var result = SessionLogParser.FormatTimestamp(iso, now);
        Assert.IsTrue(result.Contains("10:30"), $"Expected time in: {result}");
    }

    [TestMethod]
    public void FormatTimestamp_OtherDay_ShowsTimeAndDate()
    {
        var now = new System.DateTimeOffset(2026, 4, 18, 12, 0, 0, System.TimeSpan.FromHours(-7));
        var iso = "2026-04-17T10:30:00-07:00";
        var result = SessionLogParser.FormatTimestamp(iso, now);
        Assert.IsTrue(result.Contains("4/17") || result.Contains("17/4") || result.Contains("17.4"), $"Expected date in: {result}");
    }

    [TestMethod]
    public void FormatTimestamp_InvalidString_ReturnsOriginal()
    {
        Assert.AreEqual("garbage", SessionLogParser.FormatTimestamp("garbage"));
    }

    [TestMethod]
    public void ParseLineMeta_UserMessage_DetectsCorrectly()
    {
        var json = "{\"type\":\"message\",\"timestamp\":\"2026-04-18T10:00:00Z\",\"message\":{\"role\":\"user\",\"content\":\"hi\"}}";
        var meta = SessionLogParser.ParseLineMeta(json, 0);
        Assert.IsNotNull(meta);
        Assert.IsTrue(meta.Value.IsUserMessage);
        Assert.AreEqual("2026-04-18T10:00:00Z", meta.Value.Timestamp);
    }

    [TestMethod]
    public void ParseLineMeta_AssistantMessage_NotUser()
    {
        var json = "{\"type\":\"message\",\"timestamp\":\"2026-04-18T10:00:00Z\",\"message\":{\"role\":\"assistant\",\"content\":\"hello\"}}";
        var meta = SessionLogParser.ParseLineMeta(json, 100);
        Assert.IsNotNull(meta);
        Assert.IsFalse(meta.Value.IsUserMessage);
        Assert.AreEqual(100L, meta.Value.Offset);
    }

    [TestMethod]
    public void ParseLineMeta_InvalidJson_ReturnsNull()
    {
        Assert.IsNull(SessionLogParser.ParseLineMeta("not json{{{", 0));
    }

    [TestMethod]
    public void BuildIterations_GroupsByUserMessage()
    {
        var metas = new List<LineMeta>
        {
            new() { Offset = 0, Length = 50, IsUserMessage = true, Timestamp = "t1" },
            new() { Offset = 51, Length = 100, IsUserMessage = false, Timestamp = "t2" },
            new() { Offset = 152, Length = 60, IsUserMessage = false, Timestamp = "t3" },
            new() { Offset = 213, Length = 40, IsUserMessage = true, Timestamp = "t4" },
            new() { Offset = 254, Length = 80, IsUserMessage = false, Timestamp = "t5" },
        };

        var iterations = SessionLogParser.BuildIterations(metas);

        // Most recent first
        Assert.AreEqual(2, iterations.Count);
        Assert.AreEqual("t4", iterations[0].Timestamp);
        Assert.AreEqual("t1", iterations[1].Timestamp);

        // First iteration (t4) spans lines 3-4 (offset 213 to 254+80)
        Assert.AreEqual(213L, iterations[0].SubtreeOffset);
        Assert.AreEqual(254 + 80 - 213, iterations[0].SubtreeLength);

        // Second iteration (t1) spans lines 0-2 (offset 0 to 152+60)
        Assert.AreEqual(0L, iterations[1].SubtreeOffset);
        Assert.AreEqual(152 + 60, iterations[1].SubtreeLength);
    }

    [TestMethod]
    public void ParseDetails_ParsesAssistantWithTokens()
    {
        var line = "{\"type\":\"message\",\"message\":{\"role\":\"assistant\",\"content\":\"hi\",\"usage\":{\"input\":1000,\"cacheRead\":500,\"cacheWrite\":200,\"output\":50}}}";
        var buffer = Encoding.UTF8.GetBytes(line);
        var details = SessionLogParser.ParseDetails(buffer, 0);

        Assert.AreEqual(1, details.Count);
        Assert.AreEqual("assistant", details[0].TypeAndId);
        Assert.AreEqual("1,000", details[0].TokenIn);
        Assert.AreEqual("500", details[0].TokenCacheRead);
        Assert.AreEqual("200", details[0].TokenCacheWrite);
        Assert.AreEqual("50", details[0].TokenOut);
        Assert.AreEqual("hi", details[0].ContentPreview);
    }

    [TestMethod]
    public void ParseDetails_SkipsPartialJson()
    {
        var good = "{\"type\":\"message\",\"message\":{\"role\":\"user\",\"content\":\"hello\"}}";
        var partial = "{\"type\":\"mess";
        var buffer = Encoding.UTF8.GetBytes(good + "\n" + partial);
        var details = SessionLogParser.ParseDetails(buffer, 0);

        Assert.AreEqual(1, details.Count);
        Assert.AreEqual("user", details[0].TypeAndId);
    }

    [TestMethod]
    public void ParseDetails_CustomType()
    {
        var line = "{\"type\":\"custom\",\"customType\":\"openclaw:bootstrap-context:full\"}";
        var buffer = Encoding.UTF8.GetBytes(line);
        var details = SessionLogParser.ParseDetails(buffer, 0);

        Assert.AreEqual(1, details.Count);
        Assert.AreEqual("custom", details[0].TypeAndId);
        Assert.AreEqual("openclaw:bootstrap-context:full", details[0].ContentPreview);
    }

    [TestMethod]
    public void ParseDetails_ToolCall()
    {
        var line = "{\"type\":\"message\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"tool_call\",\"name\":\"read\",\"arguments\":\"{\\\"path\\\":\\\"foo.txt\\\"}\"}]}}";
        var buffer = Encoding.UTF8.GetBytes(line);
        var details = SessionLogParser.ParseDetails(buffer, 0);

        Assert.AreEqual(1, details.Count);
        Assert.IsTrue(details[0].ToolCallPreview.StartsWith("read,"));
    }

    [TestMethod]
    public void ParseSessionLabels_ParsesCorrectly()
    {
        var json = @"{
            ""agent:main:telegram:direct:123"": {
                ""sessionId"": ""abc-123"",
                ""origin"": { ""label"": ""MikeChat id:8651962616"" },
                ""lastChannel"": ""telegram""
            },
            ""agent:main:main"": {
                ""sessionId"": ""def-456"",
                ""origin"": { ""label"": ""heartbeat"" },
                ""lastChannel"": ""webchat""
            }
        }";

        var labels = SessionLogParser.ParseSessionLabels(json);

        Assert.AreEqual(2, labels.Count);
        Assert.AreEqual("MikeChat (telegram)", labels["abc-123"]);
        Assert.AreEqual("heartbeat (webchat)", labels["def-456"]);
    }

    [TestMethod]
    public void ParseIncrementalBytes_HandlesPartialLines()
    {
        var line1 = "{\"type\":\"message\",\"message\":{\"role\":\"user\",\"content\":\"hi\"}}";
        var partial = "{\"type\":\"mess";
        var bytes = Encoding.UTF8.GetBytes(line1 + "\n" + partial);

        var (newLines, leftover) = SessionLogParser.ParseIncrementalBytes(bytes, 0, "");

        Assert.AreEqual(1, newLines.Count);
        Assert.IsTrue(newLines[0].IsUserMessage);
        Assert.AreEqual(partial, leftover);
    }

    [TestMethod]
    public void ParseIncrementalBytes_CompletesLeftover()
    {
        var line = "{\"type\":\"message\",\"message\":{\"role\":\"user\",\"content\":\"hi\"}}";
        var previousLeftover = "{\"type\":\"message\",\"message\":{\"role\":\"";
        var remaining = "user\",\"content\":\"hi\"}}\n";
        var bytes = Encoding.UTF8.GetBytes(remaining);

        var (newLines, leftover) = SessionLogParser.ParseIncrementalBytes(
            bytes, 
            Encoding.UTF8.GetByteCount(previousLeftover), 
            previousLeftover);

        Assert.AreEqual(1, newLines.Count);
        Assert.IsTrue(newLines[0].IsUserMessage);
        Assert.AreEqual("", leftover);
    }
}
