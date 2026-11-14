using System.Text;
using System.Text.RegularExpressions;

namespace Volna.Core;

/// <summary>Finds spoken phrases across caption boundaries while keeping the complete matching cues and their times.</summary>
public static partial class TranscriptSearch
{
    const double MaximumGap = 5;

    [GeneratedRegex(@"\s+")] private static partial Regex Whitespace();

    readonly record struct IndexedCue(SubtitleCue Cue, int From, int To);

    public static IReadOnlyList<SubtitleCue> Find(IReadOnlyList<SubtitleCue> cues, string? query)
    {
        string wanted = Whitespace().Replace(query ?? "", " ").Trim();
        if (wanted.Length == 0) return cues;
        var results = new List<SubtitleCue>();
        var group = new List<IndexedCue>();
        var text = new StringBuilder();
        var seen = new HashSet<(double Start, double End)>();
        foreach (var cue in cues)
        {
            // A long silence or a new, out-of-order caption block is not a continuation of the previous phrase.
            if (group.Count > 0 && (cue.Start - group[^1].Cue.End > MaximumGap || cue.Start < group[^1].Cue.Start)) Flush();
            string words = Whitespace().Replace(cue.Text, " ").Trim();
            if (words.Length == 0) continue;
            if (text.Length > 0) text.Append(' ');
            int from = text.Length;
            text.Append(words);
            group.Add(new IndexedCue(cue, from, text.Length));
        }
        Flush();
        return results;

        void Flush()
        {
            if (group.Count == 0) return;
            string passage = text.ToString();
            int offset = 0;
            while (offset < passage.Length)
            {
                int found = passage.IndexOf(wanted, offset, StringComparison.OrdinalIgnoreCase);
                if (found < 0) break;
                int first = At(found), last = At(found + wanted.Length - 1);
                double end = group[first].Cue.End;
                for (int i = first + 1; i <= last; i++) end = Math.Max(end, group[i].Cue.End);
                if (seen.Add((group[first].Cue.Start, end)))
                {
                    string context = passage[group[first].From..group[last].To];
                    results.Add(new SubtitleCue(group[first].Cue.Start, end, context));
                }
                offset = found + 1;
            }
            group.Clear();
            text.Clear();
        }

        int At(int position)
        {
            int low = 0, high = group.Count - 1;
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                if (group[middle].To <= position) low = middle + 1;
                else high = middle;
            }
            return low;
        }
    }
}
