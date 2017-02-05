using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using MoreLinq;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;

namespace GamesCacheUpdater
{
    public class CacheUpdater
    {
        private const string GameDetailsFilename = "game-details.json";
        private const string PlaysFileName = "plays-{0}.json";
        private const string RecentPlaysFilename = "recent-plays-{0}.json";
        private const string CollectionFileName = "collection-{0}.json";

        private string _username;
        private string _password;
        private BggClient _client;
        CloudStorageAccount _storage;
        CloudBlobClient _blob;
        CloudBlobContainer _container;

        List<GameDetails> _games;
        Dictionary<string, GameDetails> _gamesById;

        List<PlayItem> _plays;

        List<CollectionItem> _collection;
        ILookup<string, CollectionItem> _collectionById;

        public CacheUpdater()
        {
        }

        public void Initialize()
        {
            _storage = CloudStorageAccount.Parse(ConfigurationManager.ConnectionStrings["CacheStorage"].ConnectionString);
            Console.WriteLine("Connecting to Azure Storage using {0}", _storage.Credentials.AccountName);
            _blob = _storage.CreateCloudBlobClient();
            _container = _blob.GetContainerReference("gamescache");
            _container.CreateIfNotExists();

            _username = ConfigurationManager.AppSettings["bgg_username"];
            _password = ConfigurationManager.AppSettings["bgg_password"];
            _client = new BggClient();
            if (string.IsNullOrWhiteSpace(_password))
            {
                Console.WriteLine("Using BGG Anonymously");
            }
            else
            {
                Console.WriteLine("Logging into BGG as {0}", _username);
                _client.Login(_username, _password);
            }

            JsonConvert.DefaultSettings = () => new JsonSerializerSettings
            {
                Formatting = Formatting.Indented,
                ContractResolver = new CamelCasePropertyNamesContractResolver(),
                NullValueHandling = NullValueHandling.Ignore
            };
        }

        public void DownloadPlays()
        {
            Console.WriteLine("Downloading plays for {0}", _username);
            _plays = _client.GetPlays(_username);
        }

        public void DownloadCollection()
        {
            Console.WriteLine("Downloading collection for {0}", _username);
            _collection = _client.GetCollection(_username, false)
                .Concat(_client.GetCollection(_username, true))
                .OrderBy(g => g.Name).ToList();

            _collectionById = _collection.ToLookup(g => g.GameId);
        }

        public void LoadCachedGameDetails()
        {
            Console.WriteLine("Loading cached game details");
            var blob = _container.GetBlockBlobReference(GameDetailsFilename);
            if (blob.Exists())
            {
                string json = blob.DownloadText();
                try
                {
                    _games = JsonConvert.DeserializeObject<List<GameDetails>>(json);
                }
                catch
                {
                    _games = new List<GameDetails>();
                }
            }
            else
            {
                _games = new List<GameDetails>();
            }
        }

        public void DownloadUpdatedGameDetails()
        {
            // collect the list of games that are in plays or in the collection
            var updateNeeded = new HashSet<string>();
            updateNeeded.UnionWith(_collection.Select(g => g.GameId));
            updateNeeded.UnionWith(_plays.Select(p => p.GameId));

            // filter down to just those we don't already have int the cache
            var available = new HashSet<string>(_games.Select(g => g.GameId));
            updateNeeded.ExceptWith(available);

            int newCount = updateNeeded.Count;

            // add back in ones that are outdated and need an update
            var cutoff = DateTimeOffset.UtcNow.AddHours(-6);
            var random = new Random();
            var outdated = _games.Where(g => g.Timestamp.AddHours(-1 * random.NextDouble()) < cutoff);
            updateNeeded.UnionWith(outdated.Select(g => g.GameId));

            Console.WriteLine("Getting updated details for {0} new games and {1} out-of-date games", newCount, updateNeeded.Count - newCount);
            _gamesById = _games.ToDictionary(g => g.GameId);
            foreach (var ids in updateNeeded.Batch(50))
            {
                var games = _client.GetGames(ids);
                foreach (var game in games)
                {
                    _gamesById[game.GameId] = game;
                }
            }
            _games = _gamesById.Values.ToList();
        }

        public void ProcessPlays()
        {
            Console.WriteLine("Processing {0} plays", _plays.Count);
            foreach (var play in _plays)
            {
                if (play.Duration.HasValue && play.Duration == 0)
                {
                    play.Duration = null;
                }

                if (_collectionById.Contains(play.GameId))
                {
                    var game = _collectionById[play.GameId].First();
                    play.Image = game.Image;
                    play.Thumbnail = game.Thumbnail;
                    if (play.Duration == null)
                    {
                        play.EstimatedDuration = game.PlayingTime;
                    }
                }
                else if (_gamesById.ContainsKey(play.GameId))
                {
                    var game = _gamesById[play.GameId];
                    play.Image = game.Image;
                    play.Thumbnail = game.Thumbnail;
                    if (play.Duration == null)
                    {
                        play.EstimatedDuration = game.PlayingTime;
                    }
                }
            }
        }

        public void ProcessCollection()
        {
            Console.WriteLine("Processing {0} collection games", _collection.Count);
            IEnumerable<CollectionItem> games = _collection;
            var gamesById = _collectionById;

            foreach (var game in games)
            {
                // manually mark games as expansions if they are flagged as such in the comments
                if (!string.IsNullOrWhiteSpace(game.PrivateComment) && game.PrivateComment.Contains("%Expands:"))
                {
                    game.IsExpansion = true;
                }
            }

            var articles = "the,a,an,het,een,de,das,ein,der,le,la,il,el".Split(',');
            Regex removeArticles = new Regex("^(" + string.Join("|", articles.Select(a => a + @"\ ")) + ")");
            Regex descriptionRegEx = new Regex(@"%Description:(.*\w+.*)$");
            Regex playingTimeRegEx = new Regex(@"%PlayingTime:(.*\w+.*)$");
            Regex expansionCommentExpression = new Regex(@"%Expands:(.*\w+.*)\[(\d+)\]", RegexOptions.Compiled);

            foreach (var game in games)
            {
                game.SortableName = removeArticles.Replace(game.Name.Trim().ToLower(), "");
                if (game.MinPlayingTime.HasValue && game.MaxPlayingTime.HasValue)
                {
                    game.PlayingTime = (game.MinPlayingTime + game.MaxPlayingTime) / 2;
                }

                if (_gamesById.ContainsKey(game.GameId))
                {
                    var gameDetails = _gamesById[game.GameId];
                    game.Mechanics = gameDetails.Mechanics;
                    game.BGGRating = gameDetails.BggRating;
                    game.AverageWeight = gameDetails.AverageWeight;
                    game.Artists = gameDetails.Artists;
                    game.Publishers = gameDetails.Publishers;
                    game.Designers = gameDetails.Designers;
                    if (!game.IsExpansion)
                    {
                        //game.Description = HttpUtility.HtmlDecode(gameDetails.Description).Trim();
                    }
                }

                if (!string.IsNullOrWhiteSpace(game.PrivateComment))
                {
                    if (game.PrivateComment.Contains("%Description:"))
                    {
                        var match = descriptionRegEx.Match(game.PrivateComment);
                        if (match.Success)
                        {
                            game.Description = match.Groups[1].Value.Trim();
                        }
                    }

                    if (game.PrivateComment.Contains("%PlayingTime:"))
                    {
                        var match = playingTimeRegEx.Match(game.PrivateComment);
                        if (match.Success)
                        {
                            int playingTime;
                            if (int.TryParse(match.Groups[1].Value.Trim(), out playingTime))
                            {
                                game.PlayingTime = playingTime;
                            }
                        }

                    }
                }

            }

            // collect up all the expansions
            var expansions = from g in games
                             where g.IsExpansion
                             orderby g.Name
                             select g;

            foreach (var expansion in expansions)
            {
                if (_gamesById.ContainsKey(expansion.GameId))
                {
                    var expansionDetails = _gamesById[expansion.GameId];
                    if (expansionDetails != null)
                    {
                        var expandsLinks = new List<BoardGameLink>(expansionDetails.Expands ?? new List<BoardGameLink>());
                        if (!string.IsNullOrWhiteSpace(expansion.PrivateComment) && expansion.PrivateComment.Contains("%Expands:"))
                        {
                            var match = expansionCommentExpression.Match(expansion.PrivateComment);
                            if (match.Success)
                            {
                                var name = match.Groups[1].Value.Trim();
                                var id = match.Groups[2].Value.Trim();
                                expandsLinks.Add(new BoardGameLink
                                {
                                    GameId = id,
                                    Name = name
                                });
                            }
                        }
                        foreach (var link in expandsLinks)
                        {
                            var parentGames = gamesById[link.GameId];
                            foreach (var game in parentGames)
                            {
                                if (game.IsExpansion)
                                {
                                    continue;
                                }
                                if (game.Expansions == null)
                                {
                                    game.Expansions = new List<CollectionItem>();
                                }
                                game.Expansions.Add(expansion.Clone());
                            }
                        }
                    }
                }
            }

            games = from g in games
                    where !g.IsExpansion
                    orderby g.SortableName
                    select g;

            Regex startsWithAlpha = new Regex("^[a-z]");
            foreach (var game in games)
            {
                if (game.Expansions != null)
                {
                    foreach (var expansion in game.Expansions)
                    {
                        var parentName = game.Name.ToLower();
                        if (parentName.Length < expansion.Name.Length && expansion.Name.ToLower().Substring(0, parentName.Length) == parentName)
                        {
                            expansion.ShortName = expansion.Name.Substring(parentName.Length).TrimStart('–', '-', ':', ' ');
                        }
                        else
                        {
                            expansion.ShortName = expansion.Name.Trim();
                        }
                        expansion.SortableShortName = removeArticles.Replace(expansion.ShortName.ToLower(),"");
                    }
                }
            }

            _collection = games.ToList();
        }

        public void SaveEverything()
        {
            Console.WriteLine("Saving results to blob storage");
            var json = JsonConvert.SerializeObject(_games);
            var blob = _container.GetBlockBlobReference(GameDetailsFilename);
            blob.UploadText(json);

            json = JsonConvert.SerializeObject(_plays);
            blob = _container.GetBlockBlobReference(string.Format(PlaysFileName, _username));
            blob.UploadText(json);

            json = JsonConvert.SerializeObject(_plays.Take(100));
            blob = _container.GetBlockBlobReference(string.Format(RecentPlaysFilename, _username));
            blob.UploadText(json);

            json = JsonConvert.SerializeObject(_collection);
            blob = _container.GetBlockBlobReference(string.Format(CollectionFileName, _username));
            blob.UploadText(json);

        }

    }
}
