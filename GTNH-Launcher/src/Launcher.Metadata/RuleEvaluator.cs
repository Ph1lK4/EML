using System.Text.RegularExpressions;
using Launcher.Contracts;

namespace Launcher.Metadata;

/// <summary>
/// Mojang 的 rules 语义：没有 rules = 允许；有 rules 时从 false 开始，
/// 按顺序命中就覆盖结果，最后一条命中的 action 说了算。
/// </summary>
public static class RuleEvaluator
{
    public static bool IsAllowed(IReadOnlyList<RuleJson>? rules, RuleContext context)
    {
        if (rules is null || rules.Count == 0) return true;

        var allowed = false;
        foreach (var rule in rules)
        {
            if (!Matches(rule, context)) continue;
            allowed = string.Equals(rule.Action, "allow", StringComparison.OrdinalIgnoreCase);
        }
        return allowed;
    }

    private static bool Matches(RuleJson rule, RuleContext context)
    {
        if (rule.Os is { } os)
        {
            if (os.Name is not null && !string.Equals(os.Name, context.OsName, StringComparison.OrdinalIgnoreCase))
                return false;
            if (os.Arch is not null && !string.Equals(os.Arch, context.Arch, StringComparison.OrdinalIgnoreCase))
                return false;
            if (os.Version is not null && !Regex.IsMatch(context.OsVersion, os.Version))
                return false;
        }

        if (rule.Features is { Count: > 0 } features)
        {
            foreach (var (key, expected) in features)
            {
                var actual = context.Features.TryGetValue(key, out var value) && value;
                if (actual != expected) return false;
            }
        }

        return true;
    }
}
