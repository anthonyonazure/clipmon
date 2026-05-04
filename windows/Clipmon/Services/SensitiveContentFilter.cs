using System.Text.RegularExpressions;

namespace Clipmon.Services;

public sealed class SensitiveContentFilter
{
    private readonly SettingsService _settings;
    private List<Regex> _compiled = new();
    private int _patternsRevision;

    public SensitiveContentFilter(SettingsService settings)
    {
        _settings = settings;
        _settings.Changed += (_, _) => InvalidateCache();
        Recompile();
    }

    /// <summary>Returns true when content should be EXCLUDED from the clipboard history.</summary>
    public bool ShouldSkip(string? text, string? sourceApplication, out string? reason)
    {
        reason = null;
        var settings = _settings.Current;

        // App skip list
        if (!string.IsNullOrEmpty(sourceApplication) && settings.SkipList.Apps.Count > 0)
        {
            foreach (var app in settings.SkipList.Apps)
            {
                if (string.IsNullOrWhiteSpace(app)) continue;
                if (sourceApplication.Contains(app, StringComparison.OrdinalIgnoreCase))
                {
                    reason = $"Skipped clipboard from {app}";
                    return true;
                }
            }
        }

        if (string.IsNullOrEmpty(text)) return false;

        // Keyword skip list
        if (settings.SkipList.Keywords.Count > 0)
        {
            foreach (var keyword in settings.SkipList.Keywords)
            {
                if (string.IsNullOrWhiteSpace(keyword)) continue;
                if (text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    reason = "Matched skip keyword";
                    return true;
                }
            }
        }

        if (!settings.SensitiveFilter.Enabled) return false;

        EnsureCompiled();

        foreach (var rx in _compiled)
        {
            if (rx.IsMatch(text))
            {
                reason = "Matched sensitive pattern (likely credential)";
                return true;
            }
        }

        // Entropy heuristic for long, single-token, high-entropy strings.
        if (text.Length >= settings.SensitiveFilter.LongHighEntropyThreshold)
        {
            var trimmed = text.Trim();
            if (!trimmed.Contains(' ') && !trimmed.Contains('\n'))
            {
                if (ShannonEntropy(trimmed) >= settings.SensitiveFilter.EntropyBitsPerChar)
                {
                    reason = "High-entropy single token (likely token/secret)";
                    return true;
                }
            }
        }

        return false;
    }

    private void InvalidateCache() => _patternsRevision++;

    private void EnsureCompiled()
    {
        if (_compiled.Count > 0 && _patternsRevision == _compiledRevision) return;
        Recompile();
    }

    private int _compiledRevision;

    private void Recompile()
    {
        var patterns = _settings.Current.SensitiveFilter.Patterns;
        var compiled = new List<Regex>(patterns.Count);
        foreach (var pattern in patterns)
        {
            if (string.IsNullOrWhiteSpace(pattern)) continue;
            try
            {
                compiled.Add(new Regex(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(150)));
            }
            catch
            {
                // Skip invalid user-supplied patterns silently.
            }
        }
        _compiled = compiled;
        _compiledRevision = _patternsRevision;
    }

    private static double ShannonEntropy(string s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        var counts = new Dictionary<char, int>();
        foreach (var c in s)
        {
            counts[c] = counts.TryGetValue(c, out var n) ? n + 1 : 1;
        }
        double entropy = 0;
        var len = (double)s.Length;
        foreach (var count in counts.Values)
        {
            var p = count / len;
            entropy -= p * Math.Log2(p);
        }
        return entropy;
    }
}
