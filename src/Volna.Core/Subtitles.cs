using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Volna.Core;

public static partial class Subtitles
{
    [GeneratedRegex(@"^(\d{1,2}:)?\d{2}:\d{2}\.\d{3}\s+-->")] private static partial Regex CueTime();
    [GeneratedRegex(@"<[^>]*>")] private static partial Regex CueTag();

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
