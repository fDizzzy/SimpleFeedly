﻿namespace SimpleFeedly
{
    using Newtonsoft.Json;
    using NLog;
    using SimpleFeedly.Core.Utils;
    using SimpleFeedly.Rss;
    using SimpleFeedly.Rss.Entities;
    using SimpleFeedly.SettingParsers;
    using StackExchange.Exceptional;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Runtime.Caching;
    using System.Text.RegularExpressions;

    using System.Xml;

    public static partial class SiteInitialization
    {
        private static HashSet<string> _feedCache = new HashSet<string>();
        private static int _currentDate = DateTime.Now.Day;
        private static readonly Random _ran = new Random();

        public static void InitializeRssCrawler(ILogger logger, RandomTimeSpan channelFetchingDelay, TimeSpan channelErrorDelay, TimeSpan errorDelay, TimeSpan loopDelay)
        {
            System.Threading.Tasks.Task.Run(() =>
            {
                //var channelHubCtx = GlobalHost.ConnectionManager.GetHubContext<ChannelHub>();
                ObjectCache cache = MemoryCache.Default;

                while (true)
                {
                    if (_currentDate != DateTime.Now.Day)
                    {
                        _feedCache = new HashSet<string>();
                        _currentDate = DateTime.Now.Day;
                    }

                    var feedUrl = string.Empty;
                    try
                    {
                        List<RssChannelsRow> channels = channels = SimpleFeedlyDatabaseAccess.GetActiveChannels().OrderBy(x => x.Id).ToList();

                        var count = 0;

                        foreach (var channel in channels)
                        {
                            feedUrl = channel.Link;

                            count++;

                            logger.Info($"- [{count}/{channels.Count}] Working on channel: {channel.Id} | {feedUrl}");
                            //channelHubCtx.Clients.All.updateChannelProgress(new { Message = $"<strong>Fetching</strong> <a href='{channel.Link}' target='_blank'>{channel.Link}</a>", IsSleeping = false });

                            if (string.IsNullOrWhiteSpace(feedUrl))
                            {
                                logger.Warn($"=> Channel has empty link: {channel.Id}");
                                continue;
                            }

                            var channelSleepingCacheKey = "channel_is_sleeping|" + channel.Id;

                            var isSleeping = cache[channelSleepingCacheKey] as bool?;
                            if (isSleeping == null || isSleeping == false)
                            {
                                try
                                {
                                    RssCrawlerEngine usedEngine = RssCrawlerEngine.CodeHollowFeedReader;
                                    var feed = RssCrawler.GetFeedsFromChannel(feedUrl, channel.RssCrawlerEngine, false, out usedEngine, out Exception fetchFeedError);

                                    // update default engine for channel
                                    SimpleFeedlyDatabaseAccess.UpdateChannelDefaultEngine((long)channel.Id, feed == null ? (RssCrawlerEngine?)null : usedEngine);

                                    if (feed != null)
                                    {
                                        logger.Info($"  + Number of items: {feed.Items.Count}");

                                        var hasNew = false;
                                        foreach (var fItem in feed.Items)
                                        {
                                            if (!StringUtils.IsUrl(fItem.Link))
                                            {
                                                continue;
                                            }

                                            var feedItemKey = GenerateFeedItemKey(fItem);
                                            var feedCacheKey = GenerateFeedCacheKey((long)channel.Id, feedItemKey);

                                            if (string.IsNullOrWhiteSpace(feedItemKey) || string.IsNullOrWhiteSpace(fItem.Link))
                                            {
                                                logger.Info($"  + Skipped item: {JsonConvert.SerializeObject(fItem)}");
                                                continue;
                                            }

                                            if (!_feedCache.Contains(feedCacheKey))
                                            {
                                                if (!SimpleFeedlyDatabaseAccess.CheckExistFeedItem((long)channel.Id, feedItemKey))
                                                {
                                                    var feedItem = new RssFeedItemsRow
                                                    {
                                                        ChannelId = channel.Id,
                                                        FeedItemKey = feedItemKey,
                                                        Title = string.IsNullOrWhiteSpace(fItem.Title) ? fItem.Link : fItem.Title,
                                                        Link = fItem.Link,
                                                        Description = fItem.Description,
                                                        PublishingDate = fItem.PublishingDate,
                                                        Author = fItem.Author,
                                                        Content = fItem.Content
                                                    };

                                                    SimpleFeedlyDatabaseAccess.InsertFeedItem(feedItem);

                                                    hasNew = true;
                                                }

                                                _feedCache.Add(feedCacheKey);
                                            }
                                        }

                                        SimpleFeedlyDatabaseAccess.UpdateChannelErrorStatus((long)channel.Id, false, null);

                                        if (!hasNew)
                                        {
                                            var randomExpiryTime =
                                            cache.Add(channelSleepingCacheKey, true, DateTime.Now.Add(channelFetchingDelay.GenerateRamdomValue()));
                                        }
                                    }
                                    else
                                    {
                                        SimpleFeedlyDatabaseAccess.UpdateChannelErrorStatus((long)channel.Id, true, fetchFeedError == null ? null : JsonConvert.SerializeObject(fetchFeedError));

                                        if (fetchFeedError != null)
                                        {
                                            ErrorHandle(fetchFeedError, feedUrl);
                                        }
                                    }
                                }
                                catch (Exception err)
                                {
                                    SimpleFeedlyDatabaseAccess.UpdateChannelErrorStatus((long)channel.Id, true, JsonConvert.SerializeObject(err));

                                    cache.Add(channelSleepingCacheKey, true, DateTime.Now.Add(channelErrorDelay));
                                    logger.Error(err, $"An error occurred on channel: {channel.Id} | {feedUrl}");

                                    ErrorHandle(err, feedUrl);
                                }
                            }
                            else
                            {
                                logger.Info($"  + sleeping...");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.Error(ex, "An error occurred");

                        ErrorHandle(ex, feedUrl);

                        System.Threading.Thread.Sleep(errorDelay);
                    }

                    //channelHubCtx.Clients.All.updateChannelProgress(new { Message = "<span class='link-muted'>Crawler's sleeping...</span>", IsSleeping = true });

                    _currentDate = DateTime.Now.Day;

                    // we should delay a little bit, some seconds maybe
                    System.Threading.Thread.Sleep(loopDelay);
                }
            });
        }

        private static string GenerateFeedItemKey(SimpleFeedlyFeedItem item)
        {
            if (string.IsNullOrWhiteSpace(item.Id))
            {
                if (string.IsNullOrWhiteSpace(item.Link))
                {
                    return null;
                }
                else
                {
                    return item.Link;
                }
            }
            else
            {
                return item.Id;
            }
        }

        private static string GenerateFeedCacheKey(long channelId, string feedItemKey)
        {
            return StringUtils.MD5Hash($"{channelId}>{feedItemKey}");
        }

        private static void ErrorHandle(Exception ex, string feedUrl)
        {
            // we can send an email for warning right here if needed

            // or just log error into some error stores, it's up to you
            ErrorStore.LogExceptionWithoutContext(ex, false, false,
                                       new Dictionary<string, string>
                                       {
                                           {"feedUrl", feedUrl }
                                       });
        }
    }

    public class RssCrawler
    {
        /// <summary>
        /// GetFeedsFromChannel
        /// </summary>
        /// <param name="feedUrl">feed Url</param>
        /// <param name="crawlerEngine">crawler engine</param>
        /// <param name="isRest">
        /// Normally we call this method two times, first times with 'default' channel's crawler engine, and the last times for the rest crawler engine
        /// isRest = false: FIRST TIMES
        /// isRest = true: LAST TIMES
        /// </param>
        /// <param name="engine"></param>
        /// <param name="error"></param>
        /// <returns></returns>
        public static SimpleFeedlyFeed GetFeedsFromChannel(string feedUrl, RssCrawlerEngine? defaultCrawlerEngine, bool isRest, out RssCrawlerEngine engine, out Exception error)
        {
            error = null;
            engine = RssCrawlerEngine.CodeHollowFeedReader;
            SimpleFeedlyFeed result = new SimpleFeedlyFeed();
            var items = new List<SimpleFeedlyFeedItem>();

            var status = false;

            foreach (RssCrawlerEngine engineLoop in (RssCrawlerEngine[])Enum.GetValues(typeof(RssCrawlerEngine)))
            {
                if (status)
                {
                    break;
                }

                var canRun = false;

                if (defaultCrawlerEngine == null)
                {
                    canRun = true;
                }
                else
                {
                    if (!isRest) // first time
                    {
                        canRun = engineLoop == defaultCrawlerEngine;
                    }
                    else
                    {
                        canRun = engineLoop != defaultCrawlerEngine;
                    }
                }

                if (!canRun)
                {
                    continue;
                }

                if (engineLoop == RssCrawlerEngine.CodeHollowFeedReader && canRun)
                {
                    if (!status)
                    {
                        try
                        {
                            var feed = CodeHollow.FeedReader.FeedReader.ReadAsync(feedUrl).GetAwaiter().GetResult();

                            foreach (var item in feed.Items)
                            {
                                var feedItem = new SimpleFeedlyFeedItem
                                {
                                    Id = item.Id,
                                    Title = string.IsNullOrWhiteSpace(item.Title) ? item.Link : item.Title,
                                    Link = item.Link,
                                    Description = item.Description,
                                    PublishingDate = item.PublishingDate ?? DateTime.Now,
                                    Author = item.Author,
                                    Content = item.Content
                                };

                                items.Add(feedItem);
                            }

                            engine = RssCrawlerEngine.CodeHollowFeedReader;
                            status = true;
                        }
                        catch (Exception ex)
                        {
                            error = ex;
                        }
                    }
                }

                if (engineLoop == RssCrawlerEngine.SyndicationFeed && canRun)
                {
                    if (!status)
                    {
                        try
                        {
                            XmlReaderSettings settings = new XmlReaderSettings();
                            settings.DtdProcessing = DtdProcessing.Parse;

                            using (var reader = XmlReader.Create(feedUrl, settings))
                            {
                                var feed = System.ServiceModel.Syndication.SyndicationFeed.Load(reader);
                                reader.Close();

                                foreach (System.ServiceModel.Syndication.SyndicationItem item in feed.Items)
                                {
                                    var feedItem = new SimpleFeedlyFeedItem();

                                    var link = item.Links.FirstOrDefault()?.Uri.ToString();
                                    link = string.IsNullOrWhiteSpace(link) ? item.Id : link;

                                    feedItem.Id = item.Id;
                                    feedItem.Title = string.IsNullOrWhiteSpace(item.Title?.Text) ? link : item.Title.Text;
                                    feedItem.Link = link;
                                    feedItem.Description = item.Summary?.Text;
                                    feedItem.PublishingDate = item.PublishDate.UtcDateTime;
                                    feedItem.Author = item.Authors.FirstOrDefault()?.Name ?? string.Empty;
                                    feedItem.Content = item.Content?.ToString();

                                    items.Add(feedItem);
                                }
                            }

                            engine = RssCrawlerEngine.SyndicationFeed;
                            status = true;
                        }
                        catch (Exception ex)
                        {
                            error = ex;
                        }
                    }
                }

                if (engineLoop == RssCrawlerEngine.ParseRssByXml && canRun)
                {
                    if (!status)
                    {
                        try
                        {
                            var xmlString = string.Empty;
                            using (WebClient client = new WebClient())
                            {
                                var htmlData = client.DownloadData(feedUrl);
                                xmlString = System.Text.Encoding.UTF8.GetString(htmlData);

                                // ReplaceHexadecimalSymbols
                                string r = "[\x00-\x08\x0B\x0C\x0E-\x1F\x26]";
                                xmlString = Regex.Replace(xmlString, r, "", RegexOptions.Compiled);
                            }

                            XmlDocument rssXmlDoc = new XmlDocument();
                            rssXmlDoc.LoadXml(xmlString);

                            // Parse the Items in the RSS file
                            XmlNodeList rssNodes = rssXmlDoc.SelectNodes("rss/channel/item");

                            // Iterate through the items in the RSS file
                            foreach (XmlNode rssNode in rssNodes)
                            {
                                var feedItem = new SimpleFeedlyFeedItem();

                                XmlNode rssSubNode = rssNode.SelectSingleNode("link");
                                feedItem.Link = rssSubNode != null ? rssSubNode.InnerText : null;

                                rssSubNode = rssNode.SelectSingleNode("title");
                                feedItem.Title = rssSubNode != null ? rssSubNode.InnerText : null;
                                feedItem.Title = string.IsNullOrWhiteSpace(feedItem.Title) ? feedItem.Link : feedItem.Title;

                                rssSubNode = rssNode.SelectSingleNode("description");
                                feedItem.Description = rssSubNode != null ? rssSubNode.InnerText : null;

                                rssSubNode = rssNode.SelectSingleNode("pubDate");
                                DateTime pubDate = DateTime.Now;

                                if (rssSubNode != null)
                                {
                                    if (DateTime.TryParse(rssSubNode.InnerText, out DateTime tmpDate))
                                    {
                                        pubDate = tmpDate;
                                    }
                                }

                                feedItem.PublishingDate = pubDate;

                                if (!string.IsNullOrWhiteSpace(feedItem.Link))
                                {
                                    items.Add(feedItem);
                                }
                            }

                            engine = RssCrawlerEngine.ParseRssByXml;
                            status = true;
                        }
                        catch (Exception ex)
                        {
                            error = ex;
                        }
                    }
                }
            }

            // isRest == false => if it's the first time, we maybe need to call 2nd times
            // status == false => we maybe need to call 2nd times if current engines did not return anything
            // defaultCrawlerEngine = null will process rss with all engines, therefor we don't need to call 2nd times
            if (isRest == false && !status && defaultCrawlerEngine != null)
            {
                return GetFeedsFromChannel(feedUrl, defaultCrawlerEngine, true, out engine, out error);
            }

            if (!status)
            {
                return null;
            }
            else
            {
                error = null;
                result.Items = items;
                return result;
            }
        }
    }


    public class SimpleFeedlyFeed
    {
        public SimpleFeedlyFeed()
        {
            Items = new List<SimpleFeedlyFeedItem>();
        }

        public List<SimpleFeedlyFeedItem> Items { get; set; }
    }

    public class SimpleFeedlyFeedItem
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Link { get; set; }
        public string Description { get; set; }
        public DateTime PublishingDate { get; set; }
        public string Author { get; set; }
        public string Content { get; set; }
    }
}