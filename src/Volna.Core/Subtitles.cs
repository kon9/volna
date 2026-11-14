using System.Net;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Volna.Core;

public static partial class Subtitles
{
    [GeneratedRegex(@"^(\d{1,2}:)?\d{2}:\d{2}\.\d{3}\s+-->")] private static partial Regex CueTime();
    [GeneratedRegex(@"<[^>]*>")] private static partial Regex CueTag();
    [GeneratedRegex(@"^(?<start>(?:\d+:)?\d{2}:\d{2}[.,]\d{3})\s+-->\s+(?<end>(?:\d+:)?\d{2}:\d{2}[.,]\d{3})(?:\s|$)")] private static partial Regex CueRange();
    [GeneratedRegex(@"\s+")] private static partial Regex CueSpace();

    /// <summary>Timed, readable cues. Rolling automatic captions lose repeated leading lines, but later repetitions remain searchable.</summary>
    public static IReadOnlyList<SubtitleCue> ParseVtt(string vtt, bool rollingCaptions = false)
    {
        var cues = new List<SubtitleCue>();
        var lines = new List<string>();
        string[] previousLines = [];
        double start = -1, end = -1, previousEnd = -1;
        bool skipBlock = false;
        foreach (string raw in vtt.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            string line = raw.Trim().TrimStart('\uFEFF');
            if (line.Length == 0) { Flush(); skipBlock = false; continue; }
            if (line == "STYLE" || line == "REGION" || line == "NOTE" || line.StartsWith("NOTE ", StringComparison.Ordinal))
            {
                Flush();
                skipBlock = true;
            }
            if (skipBlock) continue;
            var match = CueRange().Match(line);
            if (match.Success)
            {
                Flush();
                start = Timestamp(match.Groups["start"].Value);
                end = Timestamp(match.Groups["end"].Value);
                if (end <= start) start = end = -1;
                continue;
            }
            // Malformed time rows must not become visible caption text or inherit the preceding cue's time.
            if (line.Contains("-->", StringComparison.Ordinal)) { Flush(); continue; }
            if (start < 0) continue;
            string text = CueSpace().Replace(WebUtility.HtmlDecode(CueTag().Replace(line, "")), " ").Trim();
            if (text.Length > 0) lines.Add(text);
        }
        Flush();
        return cues;

        void Flush()
        {
            if (start >= 0 && end > start && lines.Count > 0)
            {
                int overlap = 0;
                if (rollingCaptions && start <= previousEnd + 0.1)
                    for (int count = 1; count <= Math.Min(previousLines.Length, lines.Count); count++)
                        if (previousLines.AsSpan(previousLines.Length - count).SequenceEqual(lines.Take(count).ToArray())) overlap = count;
                string text = string.Join(" ", lines.Skip(overlap));
                if (text.Length > 0)
                {
                    // Word-by-word automatic captions grow the same line across overlapping cues.
                    if (rollingCaptions && overlap == 0 && cues.Count > 0 && start < cues[^1].End
                        && text.StartsWith(cues[^1].Text + " ", StringComparison.Ordinal))
                        cues[^1] = cues[^1] with { End = Math.Max(end, cues[^1].End), Text = text };
                    else cues.Add(new SubtitleCue(start, end, text));
                }
                else if (cues.Count > 0 && end > cues[^1].End) cues[^1] = cues[^1] with { End = end };
                previousLines = lines.ToArray();
                previousEnd = end;
            }
            lines.Clear();
            start = end = -1;
        }
    }

    static double Timestamp(string value)
    {
        string[] fields = value.Replace(',', '.').Split(':');
        if (fields.Length is not (2 or 3)) return -1;
        double result = 0;
        for (int i = 0; i < fields.Length; i++)
        {
            if (!double.TryParse(fields[i], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double number)
                || !double.IsFinite(number) || number < 0 || (i > 0 && number >= 60)) return -1;
            result = result * 60 + number;
        }
        return double.IsFinite(result) ? result : -1;
    }

    /// <summary>Plain text of a WebVTT file. Automatic captions repeat each line as it scrolls; only lines not just seen are kept.</summary>
    public static string VttToText(string vtt, bool timestamps)
    {
        var result = new StringBuilder();
        var recent = new Queue<string>();
        string? time = null;
        foreach (string raw in vtt.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line == "WEBVTT" || line.StartsWith("Kind:", StringComparison.Ordinal) || line.StartsWith("Language:", StringComparison.Ordinal) || line.StartsWith("NOTE", StringComparison.Ordinal)) continue;
            if (CueTime().IsMatch(line)) { time = line[..line.IndexOf(' ')]; continue; }
            if (time == null) continue;
            string text = WebUtility.HtmlDecode(CueTag().Replace(line, "")).Trim();
            if (text.Length == 0 || recent.Contains(text)) continue;
            recent.Enqueue(text);
            if (recent.Count > 4) recent.Dequeue();
            if (timestamps) result.Append('[').Append(time.Split('.')[0]).Append("] ");
            result.Append(text).Append('\n');
        }
        return result.ToString().TrimEnd();
    }
}
