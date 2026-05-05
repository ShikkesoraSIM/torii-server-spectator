// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using osu.Game.Online.Metadata;
using osu.Server.QueueProcessor;
using osu.Server.Spectator.Database;
using StackExchange.Redis;
using ServerBeatmapUpdates = osu.Server.QueueProcessor.BeatmapUpdates;
using ClientBeatmapUpdates = osu.Game.Online.Metadata.BeatmapUpdates;

namespace osu.Server.Spectator.Hubs.Metadata
{
    /// <summary>
    /// A service which broadcasts any new metadata changes to <see cref="MetadataHub"/>.
    /// </summary>
    public class MetadataBroadcaster : IDisposable
    {
        // Redis pub/sub channel that g0v0-server publishes to whenever a
        // user's public-facing payload changes (equipped aura, group
        // membership, custom title, profile hue, ...). Payload is a
        // single integer user id encoded as ASCII; the spectator just
        // re-broadcasts that user id to every connected SignalR client.
        // Lightweight on purpose: receivers fetch fresh data themselves
        // if/when they're rendering that user.
        private const string user_updated_channel = "torii:user_updated";

        private readonly IDatabaseFactory databaseFactory;
        private readonly IHubContext<MetadataHub> metadataHubContext;
        private readonly IConnectionMultiplexer redis;

        private readonly ILogger logger;

        private readonly IDisposable? poller;
        private readonly ChannelMessageQueue? userUpdateQueue;

        public MetadataBroadcaster(
            ILoggerFactory loggerFactory,
            IDatabaseFactory databaseFactory,
            IHubContext<MetadataHub> metadataHubContext,
            IConnectionMultiplexer redis)
        {
            this.databaseFactory = databaseFactory;
            this.metadataHubContext = metadataHubContext;
            this.redis = redis;

            logger = loggerFactory.CreateLogger(nameof(MetadataBroadcaster));

            // Torii: BeatmapStatusWatcher polls osu-web's bss_process_queue table,
            // which g0v0 doesn't have. Gate the poller on AppSettings so vanilla
            // osu-web deploys keep working but Torii deploys don't crash on every
            // tick with "table 'bss_process_queue' doesn't exist". g0v0 publishes
            // beatmap updates via redis pub/sub instead — wired up separately if
            // we ever want client-broadcast on updates.
            if (AppSettings.EnableBeatmapStatusPolling)
                poller = BeatmapStatusWatcher.StartPollingAsync(handleUpdates, 5000).Result;

            // Subscribe to the cross-process user-update channel so that
            // a cosmetic mutation made on the g0v0-server side fans out
            // to every connected SignalR client without anyone having to
            // poll. Wrapped in try/catch so a missing Redis configuration
            // doesn't tank the whole spectator startup — without the
            // subscription, picker-driven changes still work locally for
            // the user who made them, only the cross-client notification
            // is lost.
            try
            {
                userUpdateQueue = redis.GetSubscriber().Subscribe(RedisChannel.Literal(user_updated_channel));
                userUpdateQueue.OnMessage(handleUserUpdated);
                logger.LogInformation("Subscribed to {channel} for cross-process UserUpdated broadcasts.", user_updated_channel);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to subscribe to {channel}; UserUpdated broadcasts disabled.", user_updated_channel);
                userUpdateQueue = null;
            }
        }

        private async void handleUserUpdated(ChannelMessage message)
        {
            // Payload is a single integer user id (ASCII). Anything else
            // is malformed publishing and we just ignore — broadcasting a
            // garbage id to every client would be worse than skipping.
            if (!int.TryParse(message.Message.ToString(), out int userId) || userId <= 0)
            {
                logger.LogWarning("Received unparseable UserUpdated payload: {payload}", message.Message);
                return;
            }

            try
            {
                // String literal (not nameof) because the spectator
                // references the UPSTREAM osu.Game NuGet package, which
                // doesn't yet have IMetadataClient.UserUpdated. The
                // torii-osu fork on the client side registers a handler
                // for this exact name, so the runtime dispatch lines up.
                // Same trick the existing UserClientNameUpdated event
                // uses for the verified-client-name broadcast.
                await metadataHubContext.Clients.All.SendAsync("UserUpdated", userId);
            }
            catch (Exception ex)
            {
                // SignalR send failures are non-fatal — clients will pick
                // up the change next time they refetch the user. Log and
                // move on so the subscription stays alive.
                logger.LogWarning(ex, "Failed to broadcast UserUpdated for {userId}", userId);
            }
        }

        // ReSharper disable once AsyncVoidMethod
        private async void handleUpdates(ServerBeatmapUpdates updates)
        {
            logger.LogInformation("Polled beatmap changes up to last queue id {lastProcessedQueueID}", updates.LastProcessedQueueID);

            if (updates.BeatmapSetIDs.Any())
            {
                logger.LogInformation("Broadcasting new beatmaps to client: {beatmapIds}", string.Join(',', updates.BeatmapSetIDs.Select(i => i.ToString())));
                await metadataHubContext.Clients.All.SendAsync(nameof(IMetadataClient.BeatmapSetsUpdated), new ClientBeatmapUpdates(updates.BeatmapSetIDs, updates.LastProcessedQueueID));
            }
        }

        public void Dispose()
        {
            poller?.Dispose();
            userUpdateQueue?.Unsubscribe();
        }
    }
}
