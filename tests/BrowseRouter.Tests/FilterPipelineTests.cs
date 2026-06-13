using BrowseRouter.Core.Config;
using BrowseRouter.Core.Routing;
using System;
using System.Collections.Generic;

namespace BrowseRouter.Tests;

public class FilterPipelineTests
{
    [Fact]
    public void Returns_false_when_no_filter_matches()
    {
        var filters = new List<FilterDef>
        {
            new() { Name = "nope", Find = "doesnotmatch", Replace = "x", Priority = 1 }
        };
        var ok = FilterPipeline.TryApply(filters, "https://example.com/", out var output, out var applied);
        Assert.False(ok);
        Assert.Equal("https://example.com/", output);
        Assert.Null(applied);
    }

    [Fact]
    public void Returns_first_filter_that_changes_input_sorted_by_priority()
    {
        // FilterPipeline now relies on the caller to pre-sort by priority
        // (RootConfig does this in its JsonConstructor). The test mirrors
        // that contract: pass the filters in priority order, verify the
        // first one in iteration order is the one that fires.
        var filters = new List<FilterDef>
        {
            new() { Name = "first", Find = "^input$", Replace = "first", Priority = 1 },
            new() { Name = "second", Find = "^input$", Replace = "second", Priority = 2 },
            new() { Name = "third", Find = "^input$", Replace = "third", Priority = 3 }
        };
        var ok = FilterPipeline.TryApply(filters, "input", out var output, out var applied);
        Assert.True(ok);
        Assert.Equal("first", output);
        Assert.Equal("first", applied);
    }

    [Fact]
    public void Filter_that_does_not_alter_input_is_skipped()
    {
        var filters = new List<FilterDef>
        {
            new() { Name = "noop", Find = @"^https://example\.com/$", Replace = "https://example.com/", Priority = 1 },
            new() { Name = "rewrite", Find = "example", Replace = "rewritten", Priority = 2 }
        };
        var ok = FilterPipeline.TryApply(filters, "https://example.com/", out var output, out var applied);
        Assert.True(ok);
        Assert.Equal("https://rewritten.com/", output);
        Assert.Equal("rewrite", applied);
    }

    [Fact]
    public void DollarN_expands_capture_groups()
    {
        var filters = new List<FilterDef>
        {
            new() { Name = "strip", Find = @"(.*)[&?]utm_source=[^&]*(.*)", Replace = "$1$2", Priority = 1 }
        };
        var ok = FilterPipeline.TryApply(filters, "https://x.com/p?a=1&utm_source=email&b=2", out var output, out _);
        Assert.True(ok);
        Assert.Equal("https://x.com/p?a=1&b=2", output);
    }

    [Fact]
    public void Unescape_macro_url_decodes_capture_group()
    {
        var filters = new List<FilterDef>
        {
            new()
            {
                Name = "safelinks", Find = ".*safelinks.*[?&]url=([^&]+).*", Replace = "unescape($1)", Priority = 1
            }
        };
        var raw = "https://safelinks.example.com/?url=https%3A%2F%2Freal.example.com%2Fpage";
        var ok = FilterPipeline.TryApply(filters, raw, out var output, out _);
        Assert.True(ok);
        Assert.Equal("https://real.example.com/page", output);
    }

    [Fact]
    public void Broken_filter_does_not_block_subsequent_filters()
    {
        // A bad Find pattern is a CONFIG error, not a per-click error. The
        // filter is silently treated as a no-op (URL passes through unchanged)
        // and the next filter in priority order still gets a chance to run.
        // No onError is fired — the operator's signal for this is the warning
        // RootConfig.Validate() would emit at config load time.
        var errors = new List<(string, Exception)>();
        var filters = new List<FilterDef>
        {
            new() { Name = "bad", Find = "(", Replace = "x", Priority = 1 }, // invalid regex
            new() { Name = "rewrite", Find = "old", Replace = "new", Priority = 2 }
        };
        var ok = FilterPipeline.TryApply(filters, "old data", out var output, out var applied,
            onError: (n, e) => errors.Add((n, e)));
        Assert.True(ok);
        Assert.Equal("new data", output);
        Assert.Equal("rewrite", applied);
        Assert.Empty(errors);
    }

    [Fact]
    public void Find_regex_is_cached_on_the_filter_instance()
    {
        // The Find pattern is user-supplied, so we can't source-generate, but we
        // can avoid re-parsing it on every URL click. The first TryApply triggers
        // Lazy<Regex?> evaluation; subsequent calls against the same FilterDef
        // must reuse the very same Regex instance.
        var filter = new FilterDef { Name = "strip", Find = "utm_", Replace = "", Priority = 1 };
        Assert.False(filter.CompiledRegex.IsValueCreated);

        FilterPipeline.TryApply([filter], "https://x.com/?utm_a=1", out _, out _);
        var first = filter.CompiledRegex.Value;
        Assert.NotNull(first);

        FilterPipeline.TryApply([filter], "https://x.com/?utm_b=2", out _, out _);
        FilterPipeline.TryApply([filter], "https://x.com/?utm_c=3", out _, out _);

        Assert.Same(first, filter.CompiledRegex.Value);
    }

    [Fact]
    public void New_filter_instance_after_reload_does_not_share_cache()
    {
        // Config reload produces a fresh RootConfig (and therefore a fresh List
        // of fresh FilterDef instances). The new instance must start with an
        // unevaluated Lazy and build its own regex on first use — so the old
        // generation's Regex becomes eligible for GC along with its owner.
        var oldFilter = new FilterDef { Name = "strip", Find = "utm_", Replace = "", Priority = 1 };
        FilterPipeline.TryApply([oldFilter], "https://x.com/?utm_a=1", out _, out _);
        Assert.True(oldFilter.CompiledRegex.IsValueCreated);

        // Simulate a config reload: deserialisation yields a brand-new FilterDef.
        var newFilter = new FilterDef { Name = "strip", Find = "utm_", Replace = "", Priority = 1 };
        Assert.False(newFilter.CompiledRegex.IsValueCreated);
        Assert.NotSame(oldFilter, newFilter);

        FilterPipeline.TryApply([newFilter], "https://x.com/?utm_b=2", out _, out _);
        Assert.True(newFilter.CompiledRegex.IsValueCreated);
        Assert.NotSame(oldFilter.CompiledRegex.Value, newFilter.CompiledRegex.Value);
    }
}