﻿using System;
using System.Net.Http;
using System.Threading.Tasks;
using Raven.Client.Http;
using Raven.Client.Util;
using Sparrow.Json;

namespace Raven.Client.Documents.Commands
{
    public class StreamCommand : RavenCommand<StreamResult>
    {
        private readonly string _url;

        public StreamCommand(string url)
        {
            _url = url ?? throw new ArgumentNullException(nameof(url));
            ResponseType = RavenCommandResponseType.Empty;
        }

        public override HttpRequestMessage CreateRequest(JsonOperationContext ctx, ServerNode node, out string url)
        {
            var request = new HttpRequestMessage
            {
                Method = HttpMethod.Get
            };
            url = $"{node.Url}/databases/{node.Database}/{_url}";
            return request;
        }

        public override async Task<ResponseDisposeHandling> ProcessResponse(JsonOperationContext context, HttpCache cache, HttpResponseMessage response, string url)
        {
            var responseStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);

            Result = new StreamResult
            {
                Response = response,
                Stream = new StreamWithTimeout(responseStream)
            };

            return ResponseDisposeHandling.Manually;
        }

        public override bool IsReadRequest => true;
    }
}
