using System;
using System.Linq;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models.Conditions;
using ProxyDivert.Wpf.ViewModels.Conditions;
using Xunit;

namespace ProxyDivert.Core.Tests;

// The buttons on a condition row rewrite the filter, and the two that change its shape — group the
// ticked rows, dissolve this bracket — are the ones that can change what it MEANS without saying
// so. These pin down when each is offered.
public class ConditionEditorTests
{
    private static ConditionGroupViewModel Tree(ConditionOperator outer, ConditionOperator inner)
    {
        var model = new ConditionGroup
        {
            Operator = outer,
            Children =
            {
                new ProcessNameCondition { Matcher = ProcessMatcherType.ExeName, Pattern = "x.exe" },
                new ConditionGroup
                {
                    Operator = inner,
                    Children =
                    {
                        new ProcessNameCondition { Matcher = ProcessMatcherType.ExeName, Pattern = "a.exe" },
                        new ProcessNameCondition { Matcher = ProcessMatcherType.ExeName, Pattern = "b.exe" },
                    },
                },
            },
        };

        return new ConditionGroupViewModel(model);
    }

    private static ConditionGroupViewModel InnerOf(ConditionGroupViewModel root)
        => root.Children.OfType<ConditionGroupViewModel>().Single();

    // Ticking rows is how you pick what to put in a bracket together, so the button has to come
    // alive the moment the second row is ticked. The tick is deliberately NOT an edit of the
    // filter, which is why it needs a signal of its own rather than riding on the edit one.
    [Fact]
    public void Group_selected_becomes_available_as_soon_as_two_rows_are_ticked()
    {
        ConditionGroupViewModel root = Tree(ConditionOperator.All, ConditionOperator.Any);
        int raised = 0;
        root.GroupSelectedCommand.CanExecuteChanged += (_, _) => raised++;

        Assert.False(root.GroupSelectedCommand.CanExecute(null));

        root.Children[0].IsSelected = true;
        root.Children[1].IsSelected = true;

        Assert.True(root.GroupSelectedCommand.CanExecute(null));
        Assert.NotEqual(0, raised);
    }

    // "x AND (a OR b)" is not "x AND a AND b". Dissolving the bracket throws the inner operator
    // away, so the filter would quietly start matching something else — the rows stay on screen
    // looking the same, which is what makes it worse than refusing.
    [Fact]
    public void A_bracket_that_joins_its_rows_differently_from_its_parent_cannot_be_dissolved()
    {
        ConditionGroupViewModel root = Tree(ConditionOperator.All, ConditionOperator.Any);
        ConditionGroupViewModel inner = InnerOf(root);

        Assert.False(inner.UngroupCommand.CanExecute(null));

        // Not merely greyed: the command runs when it is invoked directly, and a button whose
        // enabled state has not caught up yet is exactly how that happens.
        inner.UngroupCommand.Execute(null);

        Assert.Same(inner, InnerOf(root));
    }

    // Same operator on both sides, so the brackets are noise and removing them changes nothing.
    [Fact]
    public void A_bracket_that_joins_its_rows_the_same_way_as_its_parent_can_be_dissolved()
    {
        ConditionGroupViewModel root = Tree(ConditionOperator.All, ConditionOperator.All);

        Assert.True(InnerOf(root).UngroupCommand.CanExecute(null));

        InnerOf(root).UngroupCommand.Execute(null);

        Assert.Empty(root.Children.OfType<ConditionGroupViewModel>());
        Assert.Equal(3, root.Children.Count);
    }

    // One row in a bracket joins with nothing, so there is no operator to lose.
    [Fact]
    public void A_bracket_around_a_single_row_can_always_be_dissolved()
    {
        var model = new ConditionGroup
        {
            Operator = ConditionOperator.All,
            Children =
            {
                new ProcessNameCondition { Matcher = ProcessMatcherType.ExeName, Pattern = "x.exe" },
                new ConditionGroup
                {
                    Operator = ConditionOperator.Any,
                    Children = { new ProcessNameCondition { Matcher = ProcessMatcherType.ExeName, Pattern = "a.exe" } },
                },
            },
        };
        var root = new ConditionGroupViewModel(model);

        Assert.True(InnerOf(root).UngroupCommand.CanExecute(null));
    }

    // Changing the operator on either side is what decides the answer, so the button has to be
    // re-asked when it happens rather than staying as it was when the window opened.
    [Fact]
    public void Changing_an_operator_re_asks_whether_the_bracket_can_be_dissolved()
    {
        ConditionGroupViewModel root = Tree(ConditionOperator.All, ConditionOperator.Any);
        ConditionGroupViewModel inner = InnerOf(root);

        // Matching the parent makes the brackets noise, so they may go.
        inner.Operator = ConditionOperator.All;
        Assert.True(inner.UngroupCommand.CanExecute(null));

        // The parent moving away is just as much of a change as the child moving: the offer has to
        // be withdrawn even though nothing on the inner bracket was touched.
        root.Operator = ConditionOperator.Any;
        Assert.False(inner.UngroupCommand.CanExecute(null));

        inner.Operator = ConditionOperator.Any;
        Assert.True(inner.UngroupCommand.CanExecute(null));
    }
}
