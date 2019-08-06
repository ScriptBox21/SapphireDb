﻿using RealtimeDatabase.Internal;
using RealtimeDatabase.Models;
using RealtimeDatabase.Models.Prefilter;
using RealtimeDatabase.Models.Responses;
using RealtimeDatabase.Websocket.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using RealtimeDatabase.Helper;
using RealtimeDatabase.Models.Commands;

namespace RealtimeDatabase.Websocket
{
    class SubscriptionWebsocketMapping
    {
        public WebsocketConnection Websocket { get; set; }

        public CollectionSubscription Subscription { get; set; } 
    }

    public class WebsocketChangeNotifier
    {
        private readonly WebsocketConnectionManager connectionManager;
        private readonly DbContextAccesor dbContextAccessor;
        private readonly IServiceProvider serviceProvider;
        private readonly ILogger<WebsocketConnection> logger;
        private readonly DbContextTypeContainer contextTypeContainer;

        public WebsocketChangeNotifier(
            WebsocketConnectionManager connectionManager,
            DbContextAccesor dbContextAccessor,
            IServiceProvider serviceProvider,
            ILogger<WebsocketConnection> logger,
            DbContextTypeContainer contextTypeContainer)
        {
            this.connectionManager = connectionManager;
            this.dbContextAccessor = dbContextAccessor;
            this.serviceProvider = serviceProvider;
            this.logger = logger;
            this.contextTypeContainer = contextTypeContainer;
        }

        public void HandleChanges(List<ChangeResponse> changes, Type dbContextType)
        {
            IEnumerable<SubscriptionWebsocketMapping> subscriptions = connectionManager.connections
                .SelectMany(c => c.Subscriptions.Select(s => new SubscriptionWebsocketMapping() { Subscription = s, Websocket = c}));

            KeyValuePair<string, Type> dbKeyValuePair =
                contextTypeContainer.DbContextTypes.FirstOrDefault(v => v.Value == dbContextType);

            IEnumerable<IGrouping<string, SubscriptionWebsocketMapping>> subscriptionGroupings =
                subscriptions
                    .Where(s => s.Subscription.ContextName.ToLowerInvariant() == dbKeyValuePair.Key.ToLowerInvariant())
                    .GroupBy(s => s.Subscription.CollectionName);

            foreach (IGrouping<string, SubscriptionWebsocketMapping> subscriptionGrouping in subscriptionGroupings)
            {
                List<ChangeResponse> relevantChanges = changes.Where(c => c.CollectionName == subscriptionGrouping.Key).ToList();

                if (!relevantChanges.Any())
                {
                    continue;
                }

                Task.Run(() =>
                {
                    RealtimeDbContext db = dbContextAccessor.GetContext(dbContextType);
                    KeyValuePair<Type, string> property = db.sets
                        .FirstOrDefault(v => v.Value.ToLowerInvariant() == subscriptionGrouping.Key);
                    List<object> collectionSet = db.GetValues(property).ToList();

                    foreach (IGrouping<WebsocketConnection, SubscriptionWebsocketMapping> websocketGrouping in subscriptionGrouping.GroupBy(s => s.Websocket))
                    {
                        List<ChangeResponse> changesForConnection = relevantChanges.Where(rc => property.Key.CanQuery(websocketGrouping.Key.HttpContext, rc.Value, serviceProvider)).ToList();

                        foreach (SubscriptionWebsocketMapping mapping in websocketGrouping)
                        {
                            try
                            {
                                HandleSubscription(mapping, changesForConnection, db, property.Key, collectionSet);
                            }
                            catch (Exception ex)
                            {
                                SubscribeCommand tempErrorCommand = new SubscribeCommand()
                                {
                                    CollectionName = subscriptionGrouping.Key,
                                    ReferenceId = mapping.Subscription.ReferenceId,
                                    Prefilters = mapping.Subscription.Prefilters
                                };

                                _ = mapping.Websocket.Send(tempErrorCommand.CreateExceptionResponse<ResponseBase>(ex));
                                logger.LogError($"Error handling subscription '{mapping.Subscription.ReferenceId}' of {subscriptionGrouping.Key}");
                                logger.LogError(ex.Message);
                            }
                        }
                    }
                });
            }
        }

        private void HandleSubscription(SubscriptionWebsocketMapping mapping, List<ChangeResponse> changes, 
            RealtimeDbContext db, Type modelType, IEnumerable<object> collectionSet)
        {
            Task.Run(() =>
            {
                mapping.Subscription.Lock.Wait();

                try
                {
                    IEnumerable<object> currentCollectionSet = collectionSet;

                    foreach (IPrefilter prefilter in mapping.Subscription.Prefilters.OfType<IPrefilter>())
                    {
                        currentCollectionSet = prefilter.Execute(currentCollectionSet);
                    }

                    IAfterQueryPrefilter afterQueryPrefilter =
                        mapping.Subscription.Prefilters.OfType<IAfterQueryPrefilter>().FirstOrDefault();

                    if (afterQueryPrefilter != null)
                    {
                        List<object> result = currentCollectionSet.Where(v =>
                                modelType.CanQuery(mapping.Websocket.HttpContext, v, serviceProvider))
                            .Select(v => v.GetAuthenticatedQueryModel(mapping.Websocket.HttpContext, serviceProvider))
                            .ToList();

                        _ = mapping.Websocket.Send(new QueryResponse()
                        {
                            ReferenceId = mapping.Subscription.ReferenceId,
                            Result = afterQueryPrefilter.Execute(result)
                        });
                    }
                    else
                    {
                        SendDataToClient(currentCollectionSet.ToList(), modelType, db, mapping, changes);
                    }
                }
                finally
                {
                    mapping.Subscription.Lock.Release();
                }
            });
        }

        private void SendDataToClient(List<object> currentCollectionSetLoaded,
            Type modelType, RealtimeDbContext db, SubscriptionWebsocketMapping mapping, List<ChangeResponse> relevantChanges)
        {
            List<object[]> currentCollectionPrimaryValues = new List<object[]>();

            foreach (object obj in currentCollectionSetLoaded)
            {
                SendRelevantFilesToClient(modelType, db, obj, currentCollectionPrimaryValues, mapping, relevantChanges);
            }

            foreach (object[] transmittedObject in mapping.Subscription.TransmittedData)
            {
                if (currentCollectionPrimaryValues.All(pks => pks.Except(transmittedObject).Any()))
                {
                    _ = mapping.Websocket.Send(new UnloadResponse
                    {
                        PrimaryValues = transmittedObject,
                        ReferenceId = mapping.Subscription.ReferenceId
                    });
                }
            }

            mapping.Subscription.TransmittedData = currentCollectionPrimaryValues;
        }

        private void SendRelevantFilesToClient(Type modelType, RealtimeDbContext db, object obj,
            List<object[]> currentCollectionPrimaryValues, SubscriptionWebsocketMapping mapping, List<ChangeResponse> relevantChanges)
        {
            object[] primaryValues = modelType.GetPrimaryKeyValues(db, obj);
            currentCollectionPrimaryValues.Add(primaryValues);

            bool clientHasObject = mapping.Subscription.TransmittedData.Any(pks => !pks.Except(primaryValues).Any());

            if (clientHasObject)
            {
                ChangeResponse change = relevantChanges
                    .FirstOrDefault(c => !c.PrimaryValues.Except(primaryValues).Any());

                if (change != null)
                {
                    object value = change.Value.GetAuthenticatedQueryModel(mapping.Websocket.HttpContext, serviceProvider);
                    _ = mapping.Websocket.Send(change.CreateResponse(mapping.Subscription.ReferenceId, value));
                }
            }
            else
            {
                _ = mapping.Websocket.Send(new LoadResponse
                {
                    NewObject = obj.GetAuthenticatedQueryModel(mapping.Websocket.HttpContext, serviceProvider),
                    ReferenceId = mapping.Subscription.ReferenceId
                });
            }
        }
    }
}
