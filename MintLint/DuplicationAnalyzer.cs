using System;
using System.Collections.Generic;

namespace MintLint;

internal static class DuplicationAnalyzer
{
    private const int WindowSize = 5;

    public static double[] ComputeRatios(IReadOnlyList<ParsedSource> sources)
    {
        if (sources.Count == 0)
        {
            return [];
        }

        List<List<string>> perFileLines = [];
        for (int i = 0; i < sources.Count; i++)
        {
            perFileLines.Add(NormalizeLines(sources[i].Text));
        }

        Dictionary<string, List<(int FileIndex, int Start)>> locations = new(StringComparer.Ordinal);
        for (int fileIndex = 0; fileIndex < perFileLines.Count; fileIndex++)
        {
            List<string> lines = perFileLines[fileIndex];
            if (lines.Count < WindowSize)
            {
                continue;
            }

            for (int start = 0; start <= lines.Count - WindowSize; start++)
            {
                bool hasMeaningfulLine = false;
                for (int offset = 0; offset < WindowSize; offset++)
                {
                    if (lines[start + offset].Length > 0)
                    {
                        hasMeaningfulLine = true;
                        break;
                    }
                }

                if (!hasMeaningfulLine)
                {
                    continue;
                }

                string key = string.Join('\n', lines.GetRange(start, WindowSize));
                if (!locations.TryGetValue(key, out List<(int FileIndex, int Start)>? keyLocations))
                {
                    keyLocations = [];
                    locations[key] = keyLocations;
                }

                keyLocations.Add((fileIndex, start));
            }
        }

        List<HashSet<int>> duplicateLines = [];
        for (int i = 0; i < perFileLines.Count; i++)
        {
            duplicateLines.Add([]);
        }

        foreach (List<(int FileIndex, int Start)> matches in locations.Values)
        {
            if (matches.Count < 2)
            {
                continue;
            }

            foreach ((int fileIndex, int start) in matches)
            {
                for (int offset = 0; offset < WindowSize; offset++)
                {
                    duplicateLines[fileIndex].Add(start + offset);
                }
            }
        }

        double[] ratios = new double[perFileLines.Count];
        for (int fileIndex = 0; fileIndex < perFileLines.Count; fileIndex++)
        {
            List<string> lines = perFileLines[fileIndex];
            int meaningful = 0;
            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i].Length > 0)
                {
                    meaningful++;
                }
            }

            if (meaningful == 0)
            {
                ratios[fileIndex] = 0.0;
                continue;
            }

            int duplicated = 0;
            foreach (int lineIndex in duplicateLines[fileIndex])
            {
                if (lineIndex < lines.Count && lines[lineIndex].Length > 0)
                {
                    duplicated++;
                }
            }

            ratios[fileIndex] = duplicated / (double)meaningful;
        }

        return ratios;
    }

    private static List<string> NormalizeLines(string source)
    {
        List<string> normalized = [];
        int start = 0;
        for (int i = 0; i <= source.Length; i++)
        {
            if (i == source.Length || source[i] == '\n')
            {
                int end = i;
                if (end > start && source[end - 1] == '\r')
                {
                    end--;
                }

                normalized.Add(NormalizeLine(source[start..end]));
                start = i + 1;
            }
        }

        if (source.Length > 0 && (source[^1] == '\n' || source[^1] == '\r'))
        {
            normalized.RemoveAt(normalized.Count - 1);
        }

        return normalized;
    }

    private static string NormalizeLine(string raw)
    {
        string stripped = raw.Trim();
        if (stripped.Length == 0 ||
            stripped.StartsWith("//", StringComparison.Ordinal) ||
            stripped.StartsWith("#", StringComparison.Ordinal) ||
            stripped.StartsWith("/*", StringComparison.Ordinal) ||
            stripped.StartsWith("*", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        string[] parts = stripped.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', parts);
    }
}
