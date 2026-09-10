using System;
using System.Collections.Generic;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models;
using ProxyDivert.Core.Routing.Models.Conditions;
using ProxyDivert.Wpf.ViewModels;
using Xunit;

namespace ProxyDivert.Core.Tests;

// A filter is a name, a condition tree and the policies to apply, and the tree is edited in a
// window of its own. A ProcessRule raises nothing, so an edit made behind that dialog left the cell
// showing the sentence the tree used to read as — and the way round it was to take the row out of
// the collection and put it back, which unselected it on the way past.
public class ProcessFilterRowViewModelTests
{
    private static ProcessRule Filter(string pattern) => new ProcessRule
    {
        Id = Guid.NewGuid(),
        Name = pattern,
        Condition = ConditionGroup.CreateDefault(pattern),
    };

    [Fact]
    public void WhatTheDialogChangedBehindTheGrid_IsReadAgainWhenTheRowIsAsked()
    {
        ProcessRule model = Filter("one.exe");
        var row = new ProcessFilterRowViewModel(model);
        var changed = new List<string>();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? string.Empty);

        // What the filter window does on save: it writes into the same rule.
        model.Condition = new ConditionGroup
        {
            Children = { new ProcessNameCondition { Matcher = ProcessMatcherType.ExeName, Pattern = "two.exe" } },
        };
        model.PolicyIds.Add(Guid.NewGuid());

        row.Refresh();

        // An empty name is WPF's "everything changed", which is what a cell bound to Condition or
        // to PolicyIds needs — neither is a property the grid can set, so neither raises on its own.
        Assert.Contains(string.Empty, changed);
        Assert.Same(model.Condition, row.Condition);
        Assert.Single(row.PolicyIds);
    }

    [Fact]
    public void EditingACell_ReachesTheFilterTheEngineWillBeGivenACopyOf()
    {
        ProcessRule model = Filter("one.exe");
        var row = new ProcessFilterRowViewModel(model);

        row.Name = "renamed";
        row.IsEnabled = false;
        row.IncludeChildren = false;

        Assert.Equal("renamed", model.Name);
        Assert.False(model.IsEnabled);
        Assert.False(model.IncludeChildren);
    }

    // The name is the only part of a filter that reads at a glance, the condition tree being one
    // click away. A blank one leaves a row nobody can identify.
    [Fact]
    public void ANameLeftBlank_IsDroppedRatherThanStored()
    {
        ProcessRule model = Filter("one.exe");
        var row = new ProcessFilterRowViewModel(model);

        row.Name = "  ";

        Assert.Equal("one.exe", model.Name);
    }
}
