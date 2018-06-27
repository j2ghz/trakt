namespace Trakt.Api
{
    public static class TraktUris
    {
        public const string Id = "5c3fa60402300c958b9b3bb2bb69f9492bda87a51ee4f841162b1abff696015e";
        public const string Secret = "0e9a0a23bbd3557d9d2c8e5f815c93308ffa424ab8e673ad8d52c7e9fe974c06";

        #region POST URI's

        public const string Token = @"https://api.trakt.tv/oauth/token";

        public const string SyncCollectionAdd = @"https://api.trakt.tv/sync/collection";
        public const string SyncCollectionRemove = @"https://api.trakt.tv/sync/collection/remove";
        public const string SyncWatchedHistoryAdd = @"https://api.trakt.tv/sync/history";
        public const string SyncWatchedHistoryRemove = @"https://api.trakt.tv/sync/history/remove";
        public const string SyncRatingsAdd = @"https://api.trakt.tv/sync/ratings";

        public const string ScrobbleStart = @"https://api.trakt.tv/scrobble/start";
        public const string ScrobblePause = @"https://api.trakt.tv/scrobble/pause";
        public const string ScrobbleStop = @"https://api.trakt.tv/scrobble/stop";
        #endregion

        #region GET URI's

        public const string WatchedMovies = @"https://api.trakt.tv/sync/watched/movies";
        public const string WatchedShows = @"https://api.trakt.tv/sync/watched/shows";
        public const string CollectedMovies = @"https://api.trakt.tv/sync/collection/movies?extended=metadata";
        public const string CollectedShows = @"https://api.trakt.tv/sync/collection/shows?extended=metadata";

        // Recommendations
        public const string RecommendationsMovies = @"https://api.trakt.tv/recommendations/movies";
        public const string RecommendationsShows = @"https://api.trakt.tv/recommendations/shows";

        #endregion

        #region DELETE 

        // Recommendations
        public const string RecommendationsMoviesDismiss = @"https://api.trakt.tv/recommendations/movies/{0}";
        public const string RecommendationsShowsDismiss = @"https://api.trakt.tv/recommendations/shows/{0}";

        #endregion
    }
}

