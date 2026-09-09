using System;
using System.Linq;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models.Conditions;
using ProxyDivert.Wpf.Bindings.Enums;
using ProxyDivert.Wpf.ViewModels.Conditions;
using Xunit;

namespace ProxyDivert.Core.Tests;

// The things you can do to a condition row rewrite the filter, and the two that change its shape —
// dragging a row somewhere else, dissolving a bracket — are the ones that can change what it MEANS
// without saying so. These pin down what each of them is allowed to do.
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

    // A row dropped below another one lands under it, in that row's group. Re-ordering is the
    // whole of what a drag does now: the arrows still work, they are just no longer the only way.
    [Fact]
    public void A_row_dropped_below_another_lands_under_it()
    {
        ConditionGroupViewModel root = Tree(ConditionOperator.All, ConditionOperator.Any);
        ConditionNodeViewModel first = root.Children[0];
        ConditionGroupViewModel inner = InnerOf(root);

        Assert.True(inner.CanAccept(first, DropWhere.After));
        inner.Accept(first, DropWhere.After);

        Assert.Same(inner, root.Children[0]);
        Assert.Same(first, root.Children[1]);
    }

    // The lower half of a group row is the bracket itself, and this is now the only way a row gets
    // INTO one: there is no "tick two rows and press group" any more.
    [Fact]
    public void A_row_dropped_on_a_group_row_goes_inside_the_bracket()
    {
        ConditionGroupViewModel root = Tree(ConditionOperator.All, ConditionOperator.Any);
        ConditionNodeViewModel first = root.Children[0];
        ConditionGroupViewModel inner = InnerOf(root);

        inner.Accept(first, DropWhere.Inside);

        Assert.Same(first, inner.Children[0]);
        Assert.Equal(3, inner.Children.Count);
        Assert.Single(root.Children);
    }

    // Dropping a bracket into itself cuts a branch out of the tree and pastes it into itself,
    // which leaves a loop hanging off nothing. Refused rather than tidied up afterwards.
    [Fact]
    public void A_bracket_cannot_be_dropped_inside_itself()
    {
        ConditionGroupViewModel root = Tree(ConditionOperator.All, ConditionOperator.Any);
        ConditionGroupViewModel inner = InnerOf(root);
        ConditionNodeViewModel insideIt = inner.Children[0];

        Assert.False(insideIt.CanAccept(inner, DropWhere.Before));
        Assert.False(inner.CanAccept(inner, DropWhere.Inside));

        // Asked again by the move itself, not only by the offer: the view is what draws the line,
        // and a view that got it wrong would take the tree apart.
        insideIt.Accept(inner, DropWhere.Before);

        Assert.Same(inner, root.Children[1]);
        Assert.Equal(2, inner.Children.Count);
    }

    // Nothing sits beside the outermost group — but the strip under its last row still has to mean
    // something, and the only thing it can sensibly mean is the end of the tree.
    [Fact]
    public void The_outermost_group_takes_a_row_at_its_end_but_not_beside_itself()
    {
        ConditionGroupViewModel root = Tree(ConditionOperator.All, ConditionOperator.Any);
        ConditionNodeViewModel first = root.Children[0];

        Assert.False(root.CanAccept(first, DropWhere.Before));
        Assert.True(root.CanAccept(first, DropWhere.After));

        root.Accept(first, DropWhere.After);

        Assert.Same(first, root.Children[1]);
    }

    // Dragging the last row out of a bracket leaves a bracket around nothing, which is not a
    // filter any more. It goes with the row rather than being left behind to be saved.
    [Fact]
    public void Dragging_the_last_row_out_of_a_bracket_takes_the_bracket_with_it()
    {
        ConditionGroupViewModel root = Tree(ConditionOperator.All, ConditionOperator.Any);
        ConditionGroupViewModel inner = InnerOf(root);
        ConditionNodeViewModel first = inner.Children[0];
        ConditionNodeViewModel second = inner.Children[1];

        root.Accept(first, DropWhere.Inside);
        root.Accept(second, DropWhere.Inside);

        Assert.Empty(root.Children.OfType<ConditionGroupViewModel>());
        Assert.Equal(3, root.Children.Count);
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
