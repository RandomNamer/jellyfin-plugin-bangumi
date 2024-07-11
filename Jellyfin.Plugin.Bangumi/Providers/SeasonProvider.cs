using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bangumi.Configuration;
using Jellyfin.Plugin.Bangumi.Model;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Bangumi.Providers;

public class SeasonProvider : IRemoteMetadataProvider<Season, SeasonInfo>, IHasOrder
{
    private readonly BangumiApi _api;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<EpisodeProvider> _log;

    public SeasonProvider(BangumiApi api, ILogger<EpisodeProvider> log, ILibraryManager libraryManager)
    {
        _api = api;
        _log = log;
        _libraryManager = libraryManager;
    }

    private static PluginConfiguration Configuration => Plugin.Instance!.Configuration;

    public int Order => -5;

    public string Name => Constants.ProviderName;

    public async Task<MetadataResult<Season>> GetMetadata(SeasonInfo info, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Subject? subject = null;
        var baseName = Path.GetFileName(info.Path);
        var result = new MetadataResult<Season>
        {
            ResultLanguage = Constants.Language
        };
        var localConfiguration = await LocalConfiguration.ForPath(info.Path);

        var parent = _libraryManager.FindByPath(Path.GetDirectoryName(info.Path) ?? "some", true);

        var subjectId = 0;
        if (localConfiguration.Id != 0)
        {
            subjectId = localConfiguration.Id;
        }
        else if (int.TryParse(baseName.GetAttributeValue("bangumi"), out var subjectIdFromAttribute))
        {
            subjectId = subjectIdFromAttribute;
        }
        else if (int.TryParse(info.ProviderIds.GetOrDefault(Constants.ProviderName), out var subjectIdFromInfo))
        {
            subjectId = subjectIdFromInfo;
        }
        else if (info.IndexNumber == 1 && int.TryParse(info.SeriesProviderIds.GetOrDefault(Constants.ProviderName), out var subjectIdFromParent))
        {
            subjectId = subjectIdFromParent;
        }
        else if (parent is Series series)
        {
            var previousSeason = series.Children
                // Search "Season 2" for "Season 1" and "Season 2 Part X" If this season is not yet identified we treat it as last
                .Where(x => x.IndexNumber == info.IndexNumber - 1 || x.IndexNumber == info.IndexNumber)
                .MaxBy(x => int.Parse(x.GetProviderId(Constants.ProviderName) ?? "0"));
            if (previousSeason?.Path == info.Path)
            {
                //Season 1 absent, search for id
                string[] searchNames = [$"{parent.Name} Season {info.IndexNumber}", $"{parent.Name} 第${info.IndexNumber}季"];
                foreach (var searchName in searchNames)
                {
                    _log.LogInformation($"Guessing season id by name:  {searchName}");
                    var searchResult = await _api.SearchSubject(searchName, token);
                    if (info.Year != null)
                        searchResult = searchResult.FindAll(x =>
                            x.ProductionYear == null || x.ProductionYear == info.Year.ToString());
                    if (searchResult.Count > 0)
                    {
                        subject = searchResult[0];
                        subjectId = subject.Id;
                        break;
                    }
                }
                _log.LogInformation("Guessed result: {Name} (#{ID})", subject?.Name, subject?.Id);
            }
            else if (int.TryParse(previousSeason?.GetProviderId(Constants.ProviderName), out var previousSeasonId) && previousSeasonId > 0)
            {
                _log.LogInformation("Guessing season id from previous season #{ID}", previousSeasonId);
                subject = await _api.SearchNextSubject(previousSeasonId, token);
                if (subject != null)
                {
                    _log.LogInformation("Guessed result: {Name} (#{ID})", subject.Name, subject.Id);
                    subjectId = subject.Id;
                }
            }
        }

        if (subjectId == 0)
            return result;

        subject ??= await _api.GetSubject(subjectId, token);
        if (subject == null)
            return result;

        result.Item = new Season();
        result.HasMetadata = true;

        result.Item.ProviderIds.Add(Constants.ProviderName, subject.Id.ToString());
        result.Item.CommunityRating = subject.Rating?.Score;
        if (Configuration.UseBangumiSeasonTitle)
        {
            result.Item.Name = subject.Name;
            result.Item.OriginalTitle = subject.OriginalName;
        }

        result.Item.Overview = string.IsNullOrEmpty(subject.Summary) ? null : subject.Summary;
        result.Item.Tags = subject.PopularTags;

        if (DateTime.TryParse(subject.AirDate, out var airDate))
        {
            result.Item.PremiereDate = airDate;
            result.Item.ProductionYear = airDate.Year;
        }

        if (subject.ProductionYear?.Length == 4)
            result.Item.ProductionYear = int.Parse(subject.ProductionYear);

        if (subject.IsNSFW)
            result.Item.OfficialRating = "X";

        (await _api.GetSubjectPersonInfos(subject.Id, token)).ForEach(result.AddPerson);
        (await _api.GetSubjectCharacters(subject.Id, token)).ForEach(result.AddPerson);

        return result;
    }

    public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(SeasonInfo info, CancellationToken token)
    {
        return Task.FromResult(Enumerable.Empty<RemoteSearchResult>());
    }

    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken token)
    {
        return _api.GetHttpClient().GetAsync(url, token);
    }
}

internal static class SeasonInfoExt
{
    public static string ToLogString(this SeasonInfo info)
    {
        return $"{info}" +
               $"\nName: {info.Name}" +
               $"\nOriginalTitle: {info.OriginalTitle}" +
               $"\nPath: {info.Path}" +
               $"\nProviderIds: {info.ProviderIds.ToLogString()}" +
               $"\nIndexNumber: {info.IndexNumber}, ParentIndexNumber: {info.ParentIndexNumber}" +
               $"\nSeriesProviderIds: {info.SeriesProviderIds.ToLogString()}";
    }
    
    public static string ToLogString<TKey, TValue> (this IDictionary<TKey, TValue> dictionary)
    {
        return "{" + string.Join(",", dictionary.Select(kv => kv.Key + "=" + kv.Value).ToArray()) + "}";
    }
    
    public static T GetItemAtPosition<T>(this IEnumerable<T> collection, int position)
    {
        if (collection == null)
        {
            throw new ArgumentNullException(nameof(collection), "Collection cannot be null.");
        }
        
        if (position < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(position), "Position must be non-negative.");
        }

        using (var enumerator = collection.GetEnumerator())
        {
            for (int i = 0; i <= position; i++)
            {
                if (!enumerator.MoveNext())
                {
                    return default; // Position is out of range
                }
            }
            return enumerator.Current;
        }
    }
}