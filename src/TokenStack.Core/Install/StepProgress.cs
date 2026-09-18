using System.Text.RegularExpressions;

namespace TokenStack.Core.Install;

/// <summary>Reads the `[3/8] ...` prefix the install pipeline already prints, so a front-end can
/// drive a progress bar without the pipeline having to grow a second reporting channel.
/// Deliberately fails soft: a line that does not match returns null and the caller just leaves
/// the bar where it is — a renamed step must never crash the installer it is reporting on.</summary>
public static partial class StepProgress
{
    [GeneratedRegex(@"^\s*\[(\d{1,3})/(\d{1,3})\]", RegexOptions.CultureInvariant)]
    private static partial Regex StepPrefix();

    public static (int Step, int Total)? Parse(string? line)
    {
        if (string.IsNullOrEmpty(line)) return null;
        var m = StepPrefix().Match(line);
        if (!m.Success) return null;

        var step = int.Parse(m.Groups[1].ValueSpan);
        var total = int.Parse(m.Groups[2].ValueSpan);
        // A zero total, or a step past the end, means we misread the line — not progress.
        return total > 0 && step >= 0 && step <= total ? (step, total) : null;
    }

    /// <summary>Percent complete for a parsed step, clamped to 0..100.</summary>
    public static int Percent(int step, int total) =>
        total <= 0 ? 0 : Math.Clamp((int)(step * 100L / total), 0, 100);
}
