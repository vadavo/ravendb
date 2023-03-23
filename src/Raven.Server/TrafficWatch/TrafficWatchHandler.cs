﻿// ------------------------------------------------------------[-----------
//  <copyright file="ChangesHandler.cs" company="Hibernating Rhinos LTD">
//      Copyright (c) Hibernating Rhinos LTD. All rights reserved.
//  </copyright>
// -----------------------------------------------------------------------

using System;
using System.IO;
using System.Net.WebSockets;
using System.Threading.Tasks;
using Raven.Server.Json;
using Raven.Server.Routing;
using Raven.Server.Web;
using Sparrow.Json;
using Sparrow.Json.Parsing;
using Sparrow.Logging;

namespace Raven.Server.TrafficWatch
{
    public class TrafficWatchHandler : RequestHandler
    {
        private static readonly Logger _logger = LoggingSource.Instance.GetLogger<TrafficWatchHandler>("Server");

        [RavenAction("/admin/traffic-watch", "GET", AuthorizationStatus.Operator)]
        public async Task TrafficWatchWebsockets()
        {
            using (var webSocket = await HttpContext.WebSockets.AcceptWebSocketAsync())
            {
                using (ServerStore.ContextPool.AllocateOperationContext(out JsonOperationContext context))
                {
                    try
                    {
                        var resourceName = GetStringQueryString("resourceName", required: false);
                        var connection = new TrafficWatchConnection(webSocket, resourceName, context, ServerStore.ServerShutdown);
                        TrafficWatchManager.AddConnection(connection);
                        await connection.StartSendingNotifications();
                    }
                    catch (IOException)
                    {
                        // nothing to do - connection closed
                    }
                    catch (Exception ex)
                    {
                        if (_logger.IsInfoEnabled)
                            _logger.Info("Error encountered in TrafficWatch handler", ex);

                        try
                        {
                            await using (var ms = new MemoryStream())
                            {
                                await using (var writer = new AsyncBlittableJsonTextWriter(context, ms))
                                {
                                    context.Write(writer, new DynamicJsonValue
                                    {
                                        ["Exception"] = ex
                                    });
                                }

                                ms.TryGetBuffer(out ArraySegment<byte> bytes);
                                await webSocket.SendAsync(bytes, WebSocketMessageType.Text, true, ServerStore.ServerShutdown);
                            }
                        }
                        catch (Exception)
                        {
                            if (_logger.IsInfoEnabled)
                                _logger.Info("Failed to send the error in TrafficWatch handler to the client", ex);
                        }
                    }
                }
            }
        }

        [RavenAction("/admin/traffic-watch/configuration", "GET", AuthorizationStatus.Operator)]
        public async Task GetConfiguration()
        {
            using (ServerStore.ContextPool.AllocateOperationContext(out JsonOperationContext context))
            {
                await using (var writer = new AsyncBlittableJsonTextWriter(context, ResponseBodyStream()))
                {
                    var json = context.ReadObject(TrafficWatchToLog.Instance.ToJson(), "traffic-watch/configuration");
                    writer.WriteObject(json);
                }
            }
        }

        [RavenAction("/admin/traffic-watch/configuration", "POST", AuthorizationStatus.Operator)]
        public async Task SetConfiguration()
        {
            using (ServerStore.ContextPool.AllocateOperationContext(out JsonOperationContext context))
            {
                var json = await context.ReadForMemoryAsync(RequestBodyStream(), "traffic-watch/configuration");

                var configuration = JsonDeserializationServer.Parameters.PutTrafficWatchConfigurationParameters(json);
                
                TrafficWatchToLog.Instance.UpdateConfiguration(configuration);
            }

            NoContentStatus();
        }
    }
}
