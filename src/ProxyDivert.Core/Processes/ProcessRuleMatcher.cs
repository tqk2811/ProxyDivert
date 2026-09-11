using System;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models;
using ProxyDivert.Core.Routing.Models.Conditions;

namespace ProxyDivert.Core.Processes;

// Decides whether a process filter applies to a given process. Separate from the watcher so it can
// be tested without spawning anything.
//
// The conditions answer for themselves — see ProcessCondition.Evaluate — and they answer in four
// states, not two. What is left here is the rule that turns four into two: a filter applies only
// when the tree comes back Match. Ignored (nothing filled in) and Unknown (the data could not be
// read) both mean "leave this process alone", which is the direction the earlier two-slot version
// took as well, and the one that cannot redirect a process nobody named.
public static class ProcessRuleMatcher
{
    public static bool IsMatch(ProcessRule rule, string processName, string? executablePath, string? commandLine = null)
    {
        if (rule is null) throw new ArgumentNullException(nameof(rule));
        if (!rule.IsEnabled || rule.Condition is null) return false;

        var context = new ConditionContext(processName, executablePath, commandLine);
        return rule.Condition.Evaluate(context) == ConditionResult.Match;
    }
}
