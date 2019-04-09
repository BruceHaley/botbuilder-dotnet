﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Bot.Connector.Authentication;
using Microsoft.Bot.Protocol.WebSockets;
using Microsoft.Extensions.Logging;

namespace Microsoft.Bot.Builder.Integration.AspNet.Core.StreamingExtensions
{
    public class BotFrameworkWebSocketAdapter : IBotFrameworkHttpAdapter
    {
        // These headers are used to send the required values for validation of an incoming connection request from an ABS channel.
        // TODO: We must document this somewhere, right? Find it and put a reference link here.
        private const string AuthHeaderName = "authorization";
        private const string ChannelIdHeaderName = "channelid";
        private readonly IChannelProvider _channelProvider;
        private readonly ICredentialProvider _credentialProvider;

        public BotFrameworkWebSocketAdapter(ICredentialProvider credentialProvider, IChannelProvider channelProvider = null, ILogger<BotFrameworkWebSocketAdapter> logger = null)
        {
            this._credentialProvider = credentialProvider;
            this._channelProvider = channelProvider;
        }

        public async Task ProcessAsync(HttpRequest httpRequest, HttpResponse httpResponse, IBot bot, CancellationToken cancellationToken = default(CancellationToken))
         {
            if (httpRequest == null)
            {
                throw new ArgumentNullException(nameof(httpRequest));
            }

            if (httpResponse == null)
            {
                throw new ArgumentNullException(nameof(httpResponse));
            }

            if (bot == null)
            {
                throw new ArgumentNullException(nameof(bot));
            }

            var authHeader = httpRequest.Headers.Where(x => x.Key.ToLower() == AuthHeaderName).FirstOrDefault().Value;
            var channelId = httpRequest.Headers.Where(x => x.Key.ToLower() == ChannelIdHeaderName).FirstOrDefault().Value;
            try
            {
                var claimsIdentity = await JwtTokenValidation.ValidateAuthHeader(authHeader, _credentialProvider, _channelProvider, channelId).ConfigureAwait(false);
                if (!claimsIdentity.IsAuthenticated)
                {
                    httpRequest.HttpContext.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
                    return;
                }
            }
            catch (Exception)
            {
                httpRequest.HttpContext.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
                return;
            }

            if (!httpRequest.HttpContext.WebSockets.IsWebSocketRequest)
            {
                httpRequest.HttpContext.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                await httpRequest.HttpContext.Response.WriteAsync("Upgrade to WebSocket required.").ConfigureAwait(false);
                return;
            }

            await CreateWebSocketConnectionAsync(httpRequest.HttpContext, bot).ConfigureAwait(false);
        }

        private async Task CreateWebSocketConnectionAsync(HttpContext httpContext, IBot bot)
        {
            var socket = await httpContext.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            var handler = new StreamingExtensionRequestHandler();
            var server = new WebSocketServer(socket, handler);
            handler.Server = server;
            handler.Bot = bot;
            var startListening = server.StartAsync();
            Task.WaitAll(startListening);
        }
    }
}
