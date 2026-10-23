using System.Text.RegularExpressions;

namespace Leaf.Plugins.LeafDiscord;

public sealed record InjectionAssessment(int Score, IReadOnlyList<string> Signals)
{
    public bool Suspicious => Score >= 30;
}

public sealed partial class PromptInjectionDetector
{
    public InjectionAssessment Assess(string text, string? ownerDisplayName = null)
    {
        var signals = new List<string>();
        var score = 0;
        Add(OverridePattern(), "instruction-override", 35);
        Add(SecretPattern(), "secret-exfiltration", 35);
        Add(ToolEscalationPattern(), "capability-escalation", 30);
        Add(EncodingPattern(), "encoded-instructions", 15);
        if (AuthorityPattern().IsMatch(text)
            || IsConfiguredOwnerSpoof(text, ownerDisplayName))
        {
            signals.Add("authority-spoofing");
            score += 35;
        }
        return new InjectionAssessment(Math.Min(100, score), signals);

        void Add(Regex pattern, string signal, int weight)
        {
            if (!pattern.IsMatch(text)) return;
            signals.Add(signal);
            score += weight;
        }
    }

    [GeneratedRegex(@"(?is)\b(ignore|forget|override|bypass)\b.{0,80}\b(previous|prior|system|developer|rules?|instructions?|policy)\b")]
    private static partial Regex OverridePattern();

    [GeneratedRegex(@"(?is)\b(reveal|print|dump|send|exfiltrate|show)\b.{0,100}\b(prompt|token|secret|credential|memory|private|environment|filesystem)\b")]
    private static partial Regex SecretPattern();

    [GeneratedRegex(@"(?is)\b(enable|unlock|grant|call|use)\b.{0,100}\b(shell|terminal|powershell|cmd|localhost|private network|admin|tool|mcp)\b")]
    private static partial Regex ToolEscalationPattern();

    [GeneratedRegex(@"(?is)\b(base64|rot13|decode|encoded payload|unicode escape)\b")]
    private static partial Regex EncodingPattern();

    [GeneratedRegex(@"(?is)\b(i am|this is|authorized by|speaking for)\b.{0,80}\b(owner|administrator|system)\b")]
    private static partial Regex AuthorityPattern();

    private static bool IsConfiguredOwnerSpoof(string text, string? ownerDisplayName)
    {
        var name = ownerDisplayName?.Trim();
        if (string.IsNullOrWhiteSpace(name)) return false;
        var pattern = $@"(?is)\b(i am|this is|authorized by|speaking for)\b.{{0,80}}(?<![\p{{L}}\p{{N}}]){Regex.Escape(name)}(?![\p{{L}}\p{{N}}])";
        return Regex.IsMatch(text, pattern, RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100));
    }
}

public sealed record EgressAssessment(bool Allowed, string Content, IReadOnlyList<string> Findings);

public sealed partial class DiscordEgressGuard
{
    public EgressAssessment Inspect(string content)
    {
        var findings = new List<string>();
        var output = StripHiddenCharacters(content);
        if (!ReferenceEquals(output, content)) findings.Add(HiddenCharactersFinding);
        output = Replace(CredentialPattern(), "[redacted credential]", "credential");
        output = Replace(JwtPattern(), "[redacted token]", "token");
        output = Replace(PrivatePathPattern(), "[redacted private path]", "private-path");
        output = Replace(ExecutionMetadataPattern(), "[redacted execution metadata]", "execution-metadata");
        if (output.Contains("@everyone", StringComparison.OrdinalIgnoreCase)
            || output.Contains("@here", StringComparison.OrdinalIgnoreCase))
        {
            output = output.Replace("@everyone", "@ everyone", StringComparison.OrdinalIgnoreCase)
                .Replace("@here", "@ here", StringComparison.OrdinalIgnoreCase);
            findings.Add("mass-mention");
        }

        if (output.Length > 32_000)
            return new EgressAssessment(false, "", [.. findings, "oversized-output"]);
        return new EgressAssessment(true, output, findings);

        string Replace(Regex regex, string replacement, string finding)
        {
            if (!regex.IsMatch(output)) return output;
            findings.Add(finding);
            return regex.Replace(output, replacement);
        }
    }

    public const string HiddenCharactersFinding = "hidden-characters";

    /// <summary>
    /// Removes what a reader would not see but a model or client could: Unicode tag characters,
    /// bidi controls, zero-width and other invisible format characters, a byte-order mark, joiners
    /// that split ASCII (joiners inside emoji stay), and runs of two or more variation selectors.
    /// The same rules as cap-sentinel's hidden-content scanner. Returns the input instance when
    /// nothing was removed.
    /// </summary>
    internal static string StripHiddenCharacters(string content)
    {
        System.Text.StringBuilder? output = null;
        var previous = -1;
        for (var index = 0; index < content.Length;)
        {
            var codePoint = CodePointAt(content, index, out var width);
            var next = index + width < content.Length ? CodePointAt(content, index + width, out _) : -1;
            var remove = codePoint is 0xE0001 or (>= 0xE0020 and <= 0xE007F)
                    or (>= 0x202A and <= 0x202E) or (>= 0x2066 and <= 0x2069)
                    or 0x200B or 0x2060 or 0x2061 or 0x2062 or 0x2063 or 0x2064 or 0x180E or 0x00AD or 0xFEFF
                || codePoint is 0x200C or 0x200D && NeighboursAscii(content, index)
                || IsVariationSelector(codePoint) && (IsVariationSelector(next) || IsVariationSelector(previous));
            if (remove)
                output ??= new System.Text.StringBuilder(content, 0, index, content.Length);
            else
                output?.Append(content, index, width);
            previous = codePoint;
            index += width;
        }
        return output?.ToString() ?? content;
    }

    private static int CodePointAt(string text, int index, out int width)
    {
        if (char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
        {
            width = 2;
            return char.ConvertToUtf32(text[index], text[index + 1]);
        }
        width = 1;
        return text[index];
    }

    private static bool IsVariationSelector(int codePoint)
        => codePoint is (>= 0xFE00 and <= 0xFE0F) or (>= 0xE0100 and <= 0xE01EF);

    private static bool NeighboursAscii(string content, int index)
    {
        var before = index > 0 ? content[index - 1] : ' ';
        var after = index + 1 < content.Length ? content[index + 1] : ' ';
        return before < 0x80 || after < 0x80;
    }

    [GeneratedRegex(@"(?i)\b(api[_-]?key|access[_-]?token|client[_-]?secret|bot[_-]?token|password)\s*[:=]\s*[^\s,;]{8,}")]
    private static partial Regex CredentialPattern();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{12,}\.[A-Za-z0-9_-]{12,}\.[A-Za-z0-9_-]{8,}\b")]
    private static partial Regex JwtPattern();

    // Windows accepts either separator, and Agents often write forward slashes.
    [GeneratedRegex(@"(?i)(?:[A-Z]:[\\/](?:Users|Projects|Workspaces|ProgramData|Windows)[\\/][^\s<>]+|/(?:home|Users|etc|var|root)/[^\s<>]+)")]
    private static partial Regex PrivatePathPattern();

    [GeneratedRegex(@"(?i)\b(REDLEAF_EXECUTION_TOKEN|parentExecutionId|beneficiaryId|executionId)\b\s*[:=]\s*[^\s,;]+")]
    private static partial Regex ExecutionMetadataPattern();
}

public static class DiscordMessageChunker
{
    public static IReadOnlyList<string> Split(string content, int maxLength = 2_000)
    {
        if (string.IsNullOrEmpty(content)) return [];
        if (maxLength < 100) throw new ArgumentOutOfRangeException(nameof(maxLength));
        var chunks = new List<string>();
        var remaining = content;
        while (remaining.Length > maxLength)
        {
            // Reserve room to close a code fence when the split crosses one.
            var budget = maxLength - 4;
            var split = FindBoundary(remaining, budget);
            var chunk = remaining[..split].TrimEnd();
            remaining = remaining[split..].TrimStart();
            var fences = Regex.Matches(chunk, "```").Count;
            if (fences % 2 == 1)
            {
                chunk += "\n```";
                remaining = "```\n" + remaining;
            }
            chunks.Add(chunk);
        }
        if (remaining.Length > 0) chunks.Add(remaining);
        return chunks;
    }

    private static int FindBoundary(string value, int budget)
    {
        var newline = value.LastIndexOf('\n', budget - 1, budget);
        if (newline >= budget / 2) return newline + 1;
        var space = value.LastIndexOf(' ', budget - 1, budget);
        return space >= budget / 2 ? space + 1 : budget;
    }
}
