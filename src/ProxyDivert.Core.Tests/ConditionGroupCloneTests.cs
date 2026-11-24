using System.Linq;
using ProxyDivert.Core.Routing.Models.Conditions;
using Xunit;

namespace ProxyDivert.Core.Tests;

public class ConditionGroupCloneTests
{
    // A "null" in the list comes from a hand-edited file or a model built in memory; Answer already
    // skips it, so cloning the same group must not be the thing that throws.
    [Fact]
    public void Cloning_a_group_skips_a_null_child()
    {
        var group = new ConditionGroup();
        group.Children.Add(new ProcessNameCondition { Pattern = "a.exe" });
        group.Children.Add(null!);

        var copy = Assert.IsType<ConditionGroup>(group.Clone());

        Assert.Single(copy.Children);
        Assert.Equal("a.exe", Assert.IsType<ProcessNameCondition>(copy.Children.Single()).Pattern);
    }
}
