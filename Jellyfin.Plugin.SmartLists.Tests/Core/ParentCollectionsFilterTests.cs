using Jellyfin.Plugin.SmartLists.Core;
using Jellyfin.Plugin.SmartLists.Core.Models;
using Jellyfin.Plugin.SmartLists.Core.QueryEngine;
using Jellyfin.Plugin.SmartLists.Services.Shared;
using Jellyfin.Plugin.SmartLists.Tests.Support;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using Expression = Jellyfin.Plugin.SmartLists.Core.QueryEngine.Expression;
using MediaTypeConstants = Jellyfin.Plugin.SmartLists.Core.Constants.MediaTypes;

namespace Jellyfin.Plugin.SmartLists.Tests.Core;

/// <summary>
/// Covers the "Include parent collections" option of the Collections rule: collections are
/// usually assigned at series (or album) level, so an episode is never a direct member of one.
/// Without the option "Collections is not in X" matches every episode - even those of a series
/// that sits in X. With it, the collections of the episode's ancestors (season, series) count too.
/// </summary>
public class ParentCollectionsFilterTests
{
    private static BoxSet CollectionNamed(string name)
    {
        var boxSet = new BoxSet { Id = Guid.NewGuid(), Name = name };
        boxSet.SortName = name;
        return boxSet;
    }

    /// <summary>
    /// Registers a collection and its DIRECT children, which keeps ExtractCollections off the
    /// library manager (membership is derived from CollectionDirectChildren alone).
    /// </summary>
    private static void SeedCollection(RefreshQueueService.RefreshCache cache, BoxSet boxSet, params BaseItem[] directChildren)
    {
        cache.AllCollections = [.. cache.AllCollections ?? [], boxSet];
        cache.CollectionDirectChildren[boxSet.Id] = directChildren;
    }

    private sealed record Library(BaseItem[] Pool, RefreshQueueService.RefreshCache Cache);

    /// <summary>
    /// Three series: "Solo" in no collection, "Shared" in "Family" and "Cartoon" in "Kids".
    /// One episode of "Solo" is itself a direct member of "Kids".
    /// </summary>
    private static Library BuildLibrary()
    {
        var top = TestItems.PhysicalFolder("shows");
        var cache = new RefreshQueueService.RefreshCache();
        var pool = new List<BaseItem>();

        Series AddShow(string name)
        {
            var show = TestItems.Under(TestItems.Show(name), top);
            var season = TestItems.Under(TestItems.SeasonOf(name + " S01"), show);
            pool.Add(TestItems.Under(TestItems.Ep(name, 1, 1, show: show), season));
            pool.Add(TestItems.Under(TestItems.Ep(name, 1, 2, show: show), season));
            return show;
        }

        AddShow("Solo");
        var shared = AddShow("Shared");
        var cartoon = AddShow("Cartoon");

        // "Solo S01E02" is in "Kids" on its own, without its series.
        var soloE02 = pool.Single(i => i.Name == "Solo S01E02");
        SeedCollection(cache, CollectionNamed("Family"), shared);
        SeedCollection(cache, CollectionNamed("Kids"), cartoon, soloE02);

        foreach (var item in pool.Append(top))
        {
            TestItems.SeedNoUserData(cache, item, TestItems.User);
        }

        return new Library([.. pool], cache);
    }

    private static string[] FilterNames(Library library, params Expression[] rules)
    {
        var dto = new SmartPlaylistDto
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Parent collections test",
            MediaTypes = [MediaTypeConstants.Episode],
            ExpressionSets = [new ExpressionSet { Expressions = [.. rules] }],
        };

        var byId = library.Pool.ToDictionary(i => i.Id);
        var ids = new SmartList(dto).FilterPlaylistItems(
            library.Pool, BaseItem.LibraryManager, TestItems.User, library.Cache, TestItems.ThrowingUserData());

        return [.. ids.Select(id => byId[id].Name).Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// The reported case: episodes of series in "Family" or "Kids" are excluded, and so is an
    /// episode that is a direct member itself.
    /// </summary>
    [Fact]
    public void FilterPlaylistItems_IncludeParentCollectionsIsNotIn_ExcludesEpisodesOfSeriesInTheCollections()
    {
        var names = FilterNames(
            BuildLibrary(),
            new Expression("Collections", "IsNotIn", "Family; Kids") { IncludeParentCollections = true });

        Assert.Equal(["Solo S01E01"], names);
    }

    /// <summary>
    /// Positive operators OR the item's own collections with its ancestors' collections.
    /// </summary>
    [Fact]
    public void FilterPlaylistItems_IncludeParentCollectionsEqual_MatchesEpisodesOfSeriesInTheCollection()
    {
        var names = FilterNames(
            BuildLibrary(),
            new Expression("Collections", "Equal", "Kids") { IncludeParentCollections = true });

        Assert.Equal(["Cartoon S01E01", "Cartoon S01E02", "Solo S01E02"], names);
    }

    /// <summary>
    /// With a cheap rule in the same set the list takes the real Phase 1 -> Phase 2 path.
    /// </summary>
    [Fact]
    public void FilterPlaylistItems_ParentCollectionsWithACheapRule_RunsThroughBothPhases()
    {
        var names = FilterNames(
            BuildLibrary(),
            new Expression("Name", "Contains", "E02"),
            new Expression("Collections", "IsNotIn", "Family") { IncludeParentCollections = true });

        Assert.Equal(["Cartoon S01E02", "Solo S01E02"], names);
    }

    /// <summary>
    /// Default off keeps today's behavior: only direct membership counts, so every episode except
    /// the directly added one passes "is not in".
    /// </summary>
    [Fact]
    public void FilterPlaylistItems_PlainCollectionsIsNotIn_OnlySeesDirectMembership()
    {
        var names = FilterNames(BuildLibrary(), new Expression("Collections", "IsNotIn", "Family; Kids"));

        Assert.Equal(["Cartoon S01E01", "Cartoon S01E02", "Shared S01E01", "Shared S01E02", "Solo S01E01"], names);
    }

    [Fact]
    public void FieldRequirements_IncludeParentCollections_RequestsTheParentCollectionsGroup()
    {
        static bool Requests(Expression expression)
            => FieldRequirements.Analyze([new ExpressionSet { Expressions = [expression] }])
                .RequiredGroups.HasFlag(ExtractionGroup.ParentCollections);

        Assert.True(Requests(new Expression("Collections", "Equal", "X") { IncludeParentCollections = true }));
        Assert.False(Requests(new Expression("Collections", "Equal", "X")));
        Assert.False(Requests(new Expression("Tags", "Equal", "X") { IncludeParentCollections = true }));
    }
}
