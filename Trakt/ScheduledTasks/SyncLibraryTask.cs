﻿using MediaBrowser.Model.Querying;
using Trakt.Api.DataContracts.Sync.Collection;
using TraktMovieCollected = Trakt.Api.DataContracts.Users.Collection.TraktMovieCollected;

namespace Trakt.ScheduledTasks
{
    using MediaBrowser.Common.Net;
    using MediaBrowser.Controller;
    using MediaBrowser.Controller.Entities;
    using MediaBrowser.Controller.Entities.Movies;
    using MediaBrowser.Controller.Entities.TV;
    using MediaBrowser.Controller.Library;
    using MediaBrowser.Model.Entities;
    using MediaBrowser.Model.IO;
    using MediaBrowser.Model.Logging;
    using MediaBrowser.Model.Serialization;
    using MediaBrowser.Model.Tasks;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Trakt.Api;
    using Trakt.Api.DataContracts.Sync;
    using Trakt.Helpers;
    using Trakt.Model;

    /// <summary>
    /// Task that will Sync each users local library with their respective trakt.tv profiles. This task will only include 
    /// titles, watched states will be synced in other tasks.
    /// </summary>
    public class SyncLibraryTask : IScheduledTask
    {
        //private readonly IHttpClient _httpClient;
        private readonly IJsonSerializer _jsonSerializer;

        private readonly IUserManager _userManager;

        private readonly ILogger _logger;

        private readonly TraktApi _traktApi;

        private readonly IUserDataManager _userDataManager;

        private readonly ILibraryManager _libraryManager;

        public SyncLibraryTask(
            ILogManager logger,
            IJsonSerializer jsonSerializer,
            IUserManager userManager,
            IUserDataManager userDataManager,
            IHttpClient httpClient,
            IServerApplicationHost appHost,
            IFileSystem fileSystem,
            ILibraryManager libraryManager)
        {
            _jsonSerializer = jsonSerializer;
            _userManager = userManager;
            _userDataManager = userDataManager;
            _libraryManager = libraryManager;
            _logger = logger.GetLogger("Trakt");
            _traktApi = new TraktApi(jsonSerializer, _logger, httpClient, appHost, userDataManager, fileSystem);
        }

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return new List<TaskTriggerInfo>();
        }

        public string Key
        {
            get
            {
                return "TraktSyncLibraryTask";
            }
        }

        /// <summary>
        /// Gather users and call <see cref="SyncUserLibrary"/>
        /// </summary>
        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            var users = _userManager.Users.Where(u => UserHelper.GetTraktUser(u) != null).ToList();

            // No point going further if we don't have users.
            if (users.Count == 0)
            {
                _logger.Info("No Users returned");
                return;
            }

            foreach (var user in users)
            {
                var traktUser = UserHelper.GetTraktUser(user);

                // I'll leave this in here for now, but in reality this continue should never be reached.
                if (string.IsNullOrEmpty(traktUser?.LinkedMbUserId))
                {
                    _logger.Error("traktUser is either null or has no linked MB account");
                    continue;
                }

                await
                    SyncUserLibrary(user, traktUser, progress.Split(users.Count), cancellationToken)
                        .ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Count media items and call <see cref="SyncMovies"/> and <see cref="SyncShows"/>
        /// </summary>
        /// <returns></returns>
        private async Task SyncUserLibrary(
            User user,
            TraktUser traktUser,
            ISplittableProgress<double> progress,
            CancellationToken cancellationToken)
        {
            await SyncMovies(user, traktUser, progress.Split(2), cancellationToken).ConfigureAwait(false);
            await SyncShows(user, traktUser, progress.Split(2), cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Sync watched and collected status of <see cref="Movie"/>s with trakt.
        /// </summary>
        private async Task SyncMovies(
            User user,
            TraktUser traktUser,
            ISplittableProgress<double> progress,
            CancellationToken cancellationToken)
        {
            /*
             * In order to sync watched status to trakt.tv we need to know what's been watched on Trakt already. This
             * will stop us from endlessly incrementing the watched values on the site.
             */
            var traktWatchedMovies = await _traktApi.SendGetAllWatchedMoviesRequest(traktUser).ConfigureAwait(false);
            var traktCollectedMovies = await _traktApi.SendGetAllCollectedMoviesRequest(traktUser).ConfigureAwait(false);
            var libraryMovies =
                _libraryManager.GetItemList(
                        new InternalItemsQuery(user)
                        {
                            IncludeItemTypes = new[] { typeof(Movie).Name },
                            IsVirtualItem = false,
                            OrderBy = new[]
                            {
                                new ValueTuple<string, SortOrder>(ItemSortBy.SortName, SortOrder.Ascending)
                            }
                        })
                    .Where(x => _traktApi.CanSync(x, traktUser))
                    .ToList();
            var collectedMovies = new List<Movie>();
            var uncollectedMovies = new List<TraktMovieCollected>();
            var playedMovies = new List<Movie>();
            var unplayedMovies = new List<Movie>();

            var decisionProgress = progress.Split(4).Split(libraryMovies.Count);
            foreach (var child in libraryMovies)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var libraryMovie = child as Movie;
                var userData = _userDataManager.GetUserData(user, child);

                // if movie is not collected, or (export media info setting is enabled and every collected matching movie has different metadata), collect it
                var collectedMathingMovies = Match.FindMatches(libraryMovie, traktCollectedMovies).ToList();
                if (!collectedMathingMovies.Any()
                    || (traktUser.ExportMediaInfo
                        && collectedMathingMovies.All(
                            collectedMovie => collectedMovie.MetadataIsDifferent(libraryMovie))))
                {
                    collectedMovies.Add(libraryMovie);
                }

                var movieWatched = Match.FindMatch(libraryMovie, traktWatchedMovies);

                // if the movie has been played locally and is unplayed on trakt.tv then add it to the list
                if (userData.Played)
                {
                    if (movieWatched == null)
                    {
                        if (traktUser.PostWatchedHistory)
                        {
                            playedMovies.Add(libraryMovie);
                        }
                        else if (!traktUser.SkipUnwatchedImportFromTrakt)
                        {
                            if (userData.Played)
                            {
                                userData.Played = false;

                                _userDataManager.SaveUserData(
                                    user.InternalId,
                                    libraryMovie,
                                    userData,
                                    UserDataSaveReason.Import,
                                    cancellationToken);
                            }
                        }
                    }
                }
                else
                {
                    // If the show has not been played locally but is played on trakt.tv then add it to the unplayed list
                    if (movieWatched != null)
                    {
                        unplayedMovies.Add(libraryMovie);
                    }
                }

                decisionProgress.Report(100);
            }

            foreach (var traktCollectedMovie in traktCollectedMovies)
            {
                if (!Match.FindMatches(traktCollectedMovie, libraryMovies).Any())
                {
                    _logger.Debug("No matches for {0}, will be uncollected on Trakt", _jsonSerializer.SerializeToString(traktCollectedMovie.movie));
                    uncollectedMovies.Add(traktCollectedMovie);
                }
            }

            if (traktUser.SyncCollection)
            {
                // send movies to mark collected
                await SendMovieCollectionAdds(traktUser, collectedMovies, progress.Split(4), cancellationToken).ConfigureAwait(false);

                // send movies to mark uncollected
                await SendMovieCollectionRemoves(traktUser, uncollectedMovies, progress.Split(4), cancellationToken).ConfigureAwait(false);
            }
            // send movies to mark watched
            await SendMoviePlaystateUpdates(true, traktUser, playedMovies, progress.Split(4), cancellationToken).ConfigureAwait(false);

            // send movies to mark unwatched
            await SendMoviePlaystateUpdates(false, traktUser, unplayedMovies, progress.Split(4), cancellationToken).ConfigureAwait(false);
        }

        private async Task SendMovieCollectionRemoves(
            TraktUser traktUser,
            List<TraktMovieCollected> movies,
            ISplittableProgress<double> progress,
            CancellationToken cancellationToken)
        {
            _logger.Info("Movies to remove from collection: " + movies.Count);
            if (movies.Count > 0)
            {
                try
                {
                    var dataContracts =
                        await
                            _traktApi.SendCollectionRemovalsAsync(
                                movies.Select(m => m.movie).ToList(),
                                traktUser,
                                cancellationToken).ConfigureAwait(false);
                    if (dataContracts != null)
                    {
                        foreach (var traktSyncResponse in dataContracts)
                        {
                            LogTraktResponseDataContract(traktSyncResponse);
                        }
                    }
                }
                catch (ArgumentNullException argNullEx)
                {
                    _logger.ErrorException("ArgumentNullException handled sending movies to trakt.tv", argNullEx);
                }
                catch (Exception e)
                {
                    _logger.ErrorException("Exception handled sending movies to trakt.tv", e);
                }

                progress.Report(100);
            }
        }

        private async Task SendMovieCollectionAdds(
            TraktUser traktUser,
            List<Movie> movies,
            ISplittableProgress<double> progress,
            CancellationToken cancellationToken)
        {
            _logger.Info("Movies to add to collection: " + movies.Count);
            if (movies.Count > 0)
            {
                try
                {
                    var dataContracts =
                        await
                            _traktApi.SendLibraryUpdateAsync(
                                movies,
                                traktUser,
                                cancellationToken,
                                EventType.Add).ConfigureAwait(false);
                    if (dataContracts != null)
                    {
                        foreach (var traktSyncResponse in dataContracts)
                        {
                            LogTraktResponseDataContract(traktSyncResponse);
                        }
                    }
                }
                catch (ArgumentNullException argNullEx)
                {
                    _logger.ErrorException("ArgumentNullException handled sending movies to trakt.tv", argNullEx);
                }
                catch (Exception e)
                {
                    _logger.ErrorException("Exception handled sending movies to trakt.tv", e);
                }

                progress.Report(100);
            }
        }

        private async Task SendMoviePlaystateUpdates(
            bool seen,
            TraktUser traktUser,
            List<Movie> playedMovies,
            ISplittableProgress<double> progress,
            CancellationToken cancellationToken)
        {
            _logger.Info("Movies to set " + (seen ? string.Empty : "un") + "watched: " + playedMovies.Count);
            if (playedMovies.Count > 0)
            {
                try
                {
                    var dataContracts =
                        await _traktApi.SendMoviePlaystateUpdates(playedMovies, traktUser, false, seen, cancellationToken).ConfigureAwait(false);
                    if (dataContracts != null)
                    {
                        foreach (var traktSyncResponse in dataContracts)
                        {
                            LogTraktResponseDataContract(traktSyncResponse);
                        }
                    }
                }
                catch (Exception e)
                {
                    _logger.ErrorException("Error updating movie play states", e);
                }

                progress.Report(100);
            }
        }

        /// <summary>
        /// Sync watched and collected status of <see cref="Movie"/>s with trakt.
        /// </summary>
        private async Task SyncShows(
            User user,
            TraktUser traktUser,
            ISplittableProgress<double> progress,
            CancellationToken cancellationToken)
        {
            var traktWatchedShows = await _traktApi.SendGetWatchedShowsRequest(traktUser).ConfigureAwait(false);
            var traktCollectedShows = await _traktApi.SendGetCollectedShowsRequest(traktUser).ConfigureAwait(false);
            var episodeItems =
                _libraryManager.GetItemList(
                        new InternalItemsQuery(user)
                        {
                            IncludeItemTypes = new[] { typeof(Episode).Name },
                            IsVirtualItem = false,
                            OrderBy = new[]
                            {
                                new ValueTuple<string, SortOrder>(ItemSortBy.SeriesSortName, SortOrder.Ascending)
                            }
                        })
                    .Where(x => _traktApi.CanSync(x, traktUser))
                    .ToList();

            var series =
                _libraryManager.GetItemList(
                        new InternalItemsQuery(user)
                        {
                            IncludeItemTypes = new[] { typeof(Series).Name },
                            IsVirtualItem = false
                        })
                    .Where(x => _traktApi.CanSync(x, traktUser))
                    .OfType<Series>()
                    .ToList();

            var collectedEpisodes = new List<Episode>();
            var uncollectedShows = new List<Api.DataContracts.Sync.Collection.TraktShowCollected>();
            var playedEpisodes = new List<Episode>();
            var unplayedEpisodes = new List<Episode>();


            var decisionProgress = progress.Split(4).Split(episodeItems.Count);
            foreach (var child in episodeItems)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var episode = child as Episode;
                var userData = _userDataManager.GetUserData(user, episode);
                var isPlayedTraktTv = false;
                var traktWatchedShow = Match.FindMatch(episode.Series, traktWatchedShows);

                if (traktWatchedShow?.seasons != null && traktWatchedShow.seasons.Count > 0)
                {
                    isPlayedTraktTv =
                        traktWatchedShow.seasons.Any(
                            season =>
                                season.number == episode.GetSeasonNumber() && season.episodes != null
                                && season.episodes.Any(te => te.number == episode.IndexNumber && te.plays > 0));
                }

                // if the show has been played locally and is unplayed on trakt.tv then add it to the list
                if (userData != null && userData.Played && !isPlayedTraktTv)
                {
                    if (traktUser.PostWatchedHistory)
                    {
                        playedEpisodes.Add(episode);
                    }
                    else if (!traktUser.SkipUnwatchedImportFromTrakt)
                    {
                        if (userData.Played)
                        {
                            userData.Played = false;

                            _userDataManager.SaveUserData(
                                user.InternalId,
                                episode,
                                userData,
                                UserDataSaveReason.Import,
                                cancellationToken);
                        }
                    }
                }
                else if (userData != null && !userData.Played && isPlayedTraktTv)
                {
                    // If the show has not been played locally but is played on trakt.tv then add it to the unplayed list
                    unplayedEpisodes.Add(episode);
                }

                var traktCollectedShow = Match.FindMatch(episode.Series, traktCollectedShows);
                if (traktCollectedShow?.seasons == null
                    || traktCollectedShow.seasons.All(x => x.number != episode.ParentIndexNumber)
                    || traktCollectedShow.seasons.First(x => x.number == episode.ParentIndexNumber)
                        .episodes.All(e => e.number != episode.IndexNumber))
                {
                    collectedEpisodes.Add(episode);
                }

                decisionProgress.Report(100);
            }
            // Check if we have all the collected items, add missing to uncollectedShows
            foreach (var traktShowCollected in traktCollectedShows)
            {
                _logger.Debug(_jsonSerializer.SerializeToString(series));
                var seriesMatch = Match.FindMatch(traktShowCollected.show, series);
                if (seriesMatch != null)
                {
                    var seriesEpisodes = episodeItems.OfType<Episode>().Where(e => e.Series.Id == seriesMatch.Id);

                    var uncollectedSeasons = new List<TraktShowCollected.TraktSeasonCollected>();
                    foreach (var traktSeasonCollected in traktShowCollected.seasons)
                    {
                        var uncollectedEpisodes =
                            new List<TraktEpisodeCollected>();
                        foreach (var traktEpisodeCollected in traktSeasonCollected.episodes)
                        {
                            if (seriesEpisodes.Any(e =>
                                e.ParentIndexNumber == traktSeasonCollected.number &&
                                e.IndexNumber == traktEpisodeCollected.number))
                            {

                            }
                            else
                            {
                                _logger.Debug("Could not match S{0}E{1} from {2} to any Emby episode, marking for collection removal", traktSeasonCollected.number, traktEpisodeCollected.number, _jsonSerializer.SerializeToString(traktShowCollected.show));
                                uncollectedEpisodes.Add(new TraktEpisodeCollected() { number = traktEpisodeCollected.number });
                            }
                        }

                        if (uncollectedEpisodes.Any())
                        {
                            uncollectedSeasons.Add(new TraktShowCollected.TraktSeasonCollected() { number = traktSeasonCollected.number, episodes = uncollectedEpisodes });
                        }
                    }

                    if (uncollectedSeasons.Any())
                    {
                        uncollectedShows.Add(new TraktShowCollected() { ids = traktShowCollected.show.ids, title = traktShowCollected.show.title, year = traktShowCollected.show.year, seasons = uncollectedSeasons });
                    }

                }
                else
                {
                    _logger.Debug("Could not match {0} to any Emby show, marking for collection removal", _jsonSerializer.SerializeToString(traktShowCollected.show));
                    uncollectedShows.Add(new TraktShowCollected() { ids = traktShowCollected.show.ids, title = traktShowCollected.show.title, year = traktShowCollected.show.year });
                }



            }

            if (traktUser.SyncCollection)
            {
                await SendEpisodeCollectionAdds(traktUser, collectedEpisodes, progress.Split(4), cancellationToken)
                    .ConfigureAwait(false);

                await SendEpisodeCollectionRemovals(traktUser, uncollectedShows, progress.Split(5), cancellationToken)
                    .ConfigureAwait(false);
            }

            await SendEpisodePlaystateUpdates(true, traktUser, playedEpisodes, progress.Split(4), cancellationToken).ConfigureAwait(false);

            await SendEpisodePlaystateUpdates(false, traktUser, unplayedEpisodes, progress.Split(4), cancellationToken).ConfigureAwait(false);
        }

        private async Task SendEpisodePlaystateUpdates(
            bool seen,
            TraktUser traktUser,
            List<Episode> playedEpisodes,
            ISplittableProgress<double> progress,
            CancellationToken cancellationToken)
        {
            _logger.Info("Episodes to set " + (seen ? string.Empty : "un") + "watched: " + playedEpisodes.Count);
            if (playedEpisodes.Count > 0)
            {
                try
                {
                    var dataContracts =
                        await _traktApi.SendEpisodePlaystateUpdates(playedEpisodes, traktUser, false, seen, cancellationToken).ConfigureAwait(false);
                    if (dataContracts != null)
                    {
                        foreach (var con in dataContracts)
                        {
                            LogTraktResponseDataContract(con);
                        }
                    }
                }
                catch (Exception e)
                {
                    _logger.ErrorException("Error updating episode play states", e);
                }

                progress.Report(100);
            }
        }

        private async Task SendEpisodeCollectionAdds(
            TraktUser traktUser,
            List<Episode> collectedEpisodes,
            ISplittableProgress<double> progress,
            CancellationToken cancellationToken)
        {
            _logger.Info("Episodes to add to Collection: " + collectedEpisodes.Count);
            if (collectedEpisodes.Count > 0)
            {
                try
                {
                    var dataContracts =
                        await
                            _traktApi.SendLibraryUpdateAsync(
                                collectedEpisodes,
                                traktUser,
                                cancellationToken,
                                EventType.Add).ConfigureAwait(false);
                    if (dataContracts != null)
                    {
                        foreach (var traktSyncResponse in dataContracts)
                        {
                            LogTraktResponseDataContract(traktSyncResponse);
                        }
                    }
                }
                catch (ArgumentNullException argNullEx)
                {
                    _logger.ErrorException("ArgumentNullException handled sending episodes to trakt.tv", argNullEx);
                }
                catch (Exception e)
                {
                    _logger.ErrorException("Exception handled sending episodes to trakt.tv", e);
                }

                progress.Report(100);
            }
        }

        private async Task SendEpisodeCollectionRemovals(
            TraktUser traktUser,
            List<Api.DataContracts.Sync.Collection.TraktShowCollected> uncollectedEpisodes,
            ISplittableProgress<double> progress,
            CancellationToken cancellationToken)
        {
            _logger.Info("Episodes to remove from Collection: " + uncollectedEpisodes.Count);
            if (uncollectedEpisodes.Count > 0)
            {
                try
                {
                    var dataContracts =
                        await
                            _traktApi.SendLibraryRemovalsAsync(
                                uncollectedEpisodes,
                                traktUser,
                                cancellationToken).ConfigureAwait(false);
                    if (dataContracts != null)
                    {
                        foreach (var traktSyncResponse in dataContracts)
                        {
                            LogTraktResponseDataContract(traktSyncResponse);
                        }
                    }
                }
                catch (ArgumentNullException argNullEx)
                {
                    _logger.ErrorException("ArgumentNullException handled sending episodes to trakt.tv", argNullEx);
                }
                catch (Exception e)
                {
                    _logger.ErrorException("Exception handled sending episodes to trakt.tv", e);
                }

                progress.Report(100);
            }
        }

        public string Name => "Sync library to trakt.tv";

        public string Category => "Trakt";

        public string Description
            => "Adds any media that is in each users trakt monitored locations to their trakt.tv profile";

        private void LogTraktResponseDataContract(TraktSyncResponse dataContract)
        {
            try
            {
                _logger.Debug("TraktResponse Added Movies: " + dataContract?.added?.movies);
                _logger.Debug("TraktResponse Added Shows: " + dataContract?.added?.shows);
                _logger.Debug("TraktResponse Added Seasons: " + dataContract?.added?.seasons);
                _logger.Debug("TraktResponse Added Episodes: " + dataContract?.added?.episodes);

                _logger.Debug("TraktResponse Deleted Movies: " + dataContract?.deleted?.movies);
                _logger.Debug("TraktResponse Deleted Shows: " + dataContract?.deleted?.shows);
                _logger.Debug("TraktResponse Deleted Seasons: " + dataContract?.deleted?.seasons);
                _logger.Debug("TraktResponse Deleted Episodes: " + dataContract?.deleted?.episodes);

                _logger.Debug("TraktResponse Existing Movies: " + dataContract?.existing?.movies);
                _logger.Debug("TraktResponse Existing Shows: " + dataContract?.existing?.shows);
                _logger.Debug("TraktResponse Existing Seasons: " + dataContract?.existing?.seasons);
                _logger.Debug("TraktResponse Existing Episodes: " + dataContract?.existing?.episodes);

                foreach (var traktMovie in dataContract.not_found.movies)
                {
                    _logger.Error("TraktResponse not Found:" + _jsonSerializer.SerializeToString(traktMovie));
                }

                foreach (var traktShow in dataContract.not_found.shows)
                {
                    _logger.Error("TraktResponse not Found:" + _jsonSerializer.SerializeToString(traktShow));
                }

                foreach (var traktSeason in dataContract.not_found.seasons)
                {
                    _logger.Error("TraktResponse not Found:" + _jsonSerializer.SerializeToString(traktSeason));
                }

                foreach (var traktEpisode in dataContract.not_found.episodes)
                {
                    _logger.Error("TraktResponse not Found:" + _jsonSerializer.SerializeToString(traktEpisode));
                }
            }
            catch (NullReferenceException e)
            {
                _logger.ErrorException("Couldn't decode trakt response", e);
                _logger.Debug("Response object: {0}", _jsonSerializer.SerializeToString(dataContract));
            }
        }
    }
}
