namespace Piztki.Core;

/// <summary>
/// Estimates when each word of a lyric line is sung. The lyrics database only says when a *line*
/// starts, so the rest is worked out: how fast this particular song is sung, how long each line
/// therefore takes, and how that time divides between words of different lengths.
/// </summary>
public static class LyricTiming
{
    /// <summary>Roughly how many syllables a word or line has.</summary>
    public static int Syllables(string text)
    {
        int count = 0;
        bool inVowelGroup = false;
        int hebrewOrArabicLetters = 0;

        foreach (char raw in text)
        {
            char c = char.ToLowerInvariant(raw);

            if (c is >= '֐' and <= 'ࣿ')
            {
                // written without vowels: about one syllable for every two letters
                if (char.IsLetter(c)) hebrewOrArabicLetters++;
                inVowelGroup = false;
            }
            else if (c >= '⺀')
            {
                // Chinese, Japanese, Korean: each character is sung as its own beat
                if (char.IsLetter(c)) count++;
                inVowelGroup = false;
            }
            else if ("aeiouyáéíóúàèìòùâêîôûäëïöüãõå".Contains(c))
            {
                if (!inVowelGroup) count++;
                inVowelGroup = true;
            }
            else
            {
                inVowelGroup = false;
            }
        }

        count += (hebrewOrArabicLetters + 1) / 2;
        return Math.Max(1, count);
    }

    /// <summary>
    /// Seconds per syllable for this song. Lines that run straight into the next one show the
    /// singer's real pace (gap ÷ syllables); lines followed by a pause look slower than they are.
    /// So the pace is taken from the quicker third of the lines, which are the back-to-back ones.
    /// </summary>
    public static double Pace(IReadOnlyList<(TimeSpan time, string text)> lines)
    {
        var ratios = new List<double>();
        for (int i = 0; i + 1 < lines.Count; i++)
        {
            if (lines[i].text.Length == 0) continue;
            double gap = (lines[i + 1].time - lines[i].time).TotalSeconds;
            int syllables = Syllables(lines[i].text);
            if (gap is > 0.4 and < 14 && syllables >= 3) ratios.Add(gap / syllables);
        }
        if (ratios.Count < 4) return 0.3; // not enough to learn from: a middling pop pace

        ratios.Sort();
        return Math.Clamp(ratios[ratios.Count * 3 / 10], 0.11, 0.7);
    }

    /// <summary>
    /// The pace around each line, in seconds per syllable. A song changes speed between sections
    /// (a slow hook, a fast verse), so each line looks at its neighbours, four either side, rather
    /// than at the whole song.
    /// </summary>
    public static double[] LocalPaces(IReadOnlyList<(TimeSpan time, string text)> lines)
    {
        double overall = Pace(lines);

        // the ratio for each line on its own, or NaN where it can't be measured
        var ratio = new double[lines.Count];
        for (int i = 0; i < lines.Count; i++)
        {
            ratio[i] = double.NaN;
            if (i + 1 >= lines.Count || lines[i].text.Length == 0) continue;
            double gap = (lines[i + 1].time - lines[i].time).TotalSeconds;
            int syllables = Syllables(lines[i].text);
            if (gap is > 0.4 and < 14 && syllables >= 3) ratio[i] = gap / syllables;
        }

        var paces = new double[lines.Count];
        var nearby = new List<double>();
        for (int i = 0; i < lines.Count; i++)
        {
            nearby.Clear();
            for (int j = Math.Max(0, i - 4); j <= Math.Min(lines.Count - 1, i + 4); j++)
                if (!double.IsNaN(ratio[j])) nearby.Add(ratio[j]);

            if (nearby.Count < 3)
            {
                paces[i] = overall;
                continue;
            }

            // just under the middle: lines followed by a pause read as slower than they are
            nearby.Sort();
            paces[i] = Math.Clamp(nearby[nearby.Count * 4 / 10], 0.11, 0.7);
        }
        return paces;
    }

    /// <summary>How long a line takes to sing, given the song's pace and the time until the next line.</summary>
    public static TimeSpan Duration(string text, double gapSeconds, double pace)
    {
        // the last syllable is usually held a little
        double estimate = Syllables(text) * pace * 1.08 + 0.3;
        double seconds = Math.Min(gapSeconds * 0.97, Math.Max(0.9, estimate));
        return TimeSpan.FromSeconds(Math.Max(0.3, seconds));
    }

    /// <summary>
    /// Converts "how far through the line's time are we" into "how far through its letters":
    /// each word gets time in proportion to its syllables, the last word a bit extra.
    /// </summary>
    public sealed class WordMap
    {
        readonly double[] _timeEnd;   // cumulative share of the line's time when each word ends
        readonly double[] _charEnd;   // cumulative share of the line's characters at that point

        public WordMap(string text)
        {
            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) words = new[] { text.Length > 0 ? text : " " };

            var weights = new double[words.Length];
            double totalWeight = 0;
            for (int i = 0; i < words.Length; i++)
            {
                weights[i] = Syllables(words[i]) + (i == words.Length - 1 ? 0.7 : 0);
                totalWeight += weights[i];
            }

            double totalChars = Math.Max(1, text.Length);
            _timeEnd = new double[words.Length];
            _charEnd = new double[words.Length];
            double time = 0;
            int chars = 0;
            for (int i = 0; i < words.Length; i++)
            {
                time += weights[i] / totalWeight;
                chars += words[i].Length + 1; // the space after it lights up with the word
                _timeEnd[i] = time;
                _charEnd[i] = Math.Min(1, chars / totalChars);
            }
            _timeEnd[^1] = 1;
            _charEnd[^1] = 1;
        }

        public double CharsAt(double timeProgress)
        {
            if (timeProgress <= 0) return 0;
            if (timeProgress >= 1) return 1;

            double prevTime = 0, prevChars = 0;
            for (int i = 0; i < _timeEnd.Length; i++)
            {
                if (timeProgress <= _timeEnd[i])
                {
                    double span = _timeEnd[i] - prevTime;
                    double within = span <= 0 ? 1 : (timeProgress - prevTime) / span;
                    return prevChars + (_charEnd[i] - prevChars) * within;
                }
                prevTime = _timeEnd[i];
                prevChars = _charEnd[i];
            }
            return 1;
        }
    }
}
