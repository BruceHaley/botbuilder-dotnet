﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Protocol;
using Microsoft.Bot.Protocol.NamedPipes;
using Microsoft.Bot.Schema;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;

namespace Microsoft.Bot.Builder.Integration.AspNet.Core.StreamingExtensions
{
    public class BotFrameworkNamedPipeAdapter : BotAdapter, IBotFrameworkStreamingExtensionsAdapter
    {
        private const string InvokeReponseKey = "BotFrameworkAdapter.InvokeResponse";
        private readonly ILogger _logger;
        private NamedPipeServer _server;

        public BotFrameworkNamedPipeAdapter(
            ILogger logger = null)
        {
            _logger = logger ?? NullLogger.Instance;
        }

        public void Initialize(IBot bot)
        {
            _server = new NamedPipeServer("bfv4.pipes", new NamedPipeRequestHandler(this, bot));
            Task.Run(() => _server.StartAsync());
        }

        /// <summary>
        /// Creates a turn context and runs the middleware pipeline for an incoming activity.
        /// </summary>
        /// <param name="authHeader">The HTTP authentication header of the request.</param>
        /// <param name="activity">The incoming activity.</param>
        /// <param name="callback">The code to run at the end of the adapter's middleware pipeline.</param>
        /// <param name="cancellationToken">A cancellation token that can be used by other objects
        /// or threads to receive notice of cancellation.</param>
        /// <returns>A task that represents the work queued to execute. If the activity type
        /// was 'Invoke' and the corresponding key (channelId + activityId) was found
        /// then an InvokeResponse is returned, otherwise null is returned.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="activity"/> is <c>null</c>.</exception>
        /// <exception cref="UnauthorizedAccessException">authentication failed.</exception>
        /// <remarks>Call this method to reactively send a message to a conversation.
        /// If the task completes successfully, then if the activity's <see cref="Activity.Type"/>
        /// is <see cref="ActivityTypes.Invoke"/> and the corresponding key
        /// (<see cref="Activity.ChannelId"/> + <see cref="Activity.Id"/>) is found
        /// then an <see cref="InvokeResponse"/> is returned, otherwise null is returned.
        /// <para>This method registers the following services for the turn.<list type="bullet">
        /// <item><see cref="IIdentity"/> (key = "BotIdentity"), a claims identity for the bot.</item>
        /// <item><see cref="IConnectorClient"/>, the channel connector client to use this turn.</item>
        /// </list></para>
        /// </remarks>
        /// <seealso cref="ContinueConversationAsync(string, ConversationReference, BotCallbackHandler, CancellationToken)"/>
        /// <seealso cref="BotAdapter.RunPipelineAsync(ITurnContext, BotCallbackHandler, CancellationToken)"/>
        public async Task<InvokeResponse> ProcessActivityAsync(string authHeader, Activity activity, BotCallbackHandler callback, CancellationToken cancellationToken)
        {
            BotAssert.ActivityNotNull(activity);

            return await ProcessActivityAsync(activity, callback, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Creates a turn context and runs the middleware pipeline for an incoming activity.
        /// </summary>
        /// <param name="activity">The incoming activity.</param>
        /// <param name="callback">The code to run at the end of the adapter's middleware pipeline.</param>
        /// <param name="cancellationToken">A cancellation token that can be used by other objects
        /// or threads to receive notice of cancellation.</param>
        /// <returns>A task that represents the work queued to execute.</returns>
        public async Task<InvokeResponse> ProcessActivityAsync(Activity activity, BotCallbackHandler callback, CancellationToken cancellationToken)
        {
            BotAssert.ActivityNotNull(activity);

            using (var context = new TurnContext(this, activity))
            {
                await RunPipelineAsync(context, callback, cancellationToken).ConfigureAwait(false);

                // Handle Invoke scenarios, which deviate from the request/response model in that
                // the Bot will return a specific body and return code.
                if (activity.Type == ActivityTypes.Invoke)
                {
                    var activityInvokeResponse = context.TurnState.Get<Activity>(InvokeReponseKey);
                    if (activityInvokeResponse == null)
                    {
                        return new InvokeResponse { Status = (int)HttpStatusCode.NotImplemented };
                    }
                    else
                    {
                        return (InvokeResponse)activityInvokeResponse.Value;
                    }
                }

                // For all non-invoke scenarios, the HTTP layers above don't have to mess
                // withthe Body and return codes.
                return null;
            }
        }

        public override async Task<ResourceResponse[]> SendActivitiesAsync(ITurnContext turnContext, Activity[] activities, CancellationToken cancellationToken)
        {
            if (turnContext == null)
            {
                throw new ArgumentNullException(nameof(turnContext));
            }

            if (activities == null)
            {
                throw new ArgumentNullException(nameof(activities));
            }

            if (activities.Length == 0)
            {
                throw new ArgumentException("Expecting one or more activities, but the array was empty.", nameof(activities));
            }

            var responses = new ResourceResponse[activities.Length];

            /*
             * NOTE: we're using for here (vs. foreach) because we want to simultaneously index into the
             * activities array to get the activity to process as well as use that index to assign
             * the response to the responses array and this is the most cost effective way to do that.
             */
            for (var index = 0; index < activities.Length; index++)
            {
                var activity = activities[index];
                var response = default(ResourceResponse);

                if (activity.Type == ActivityTypesEx.Delay)
                {
                    // The Activity Schema doesn't have a delay type build in, so it's simulated
                    // here in the Bot. This matches the behavior in the Node connector.
                    var delayMs = (int)activity.Value;
                    await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);

                    // No need to create a response. One will be created below.
                }
                else if (activity.Type == ActivityTypesEx.InvokeResponse)
                {
                    turnContext.TurnState.Add(InvokeReponseKey, activity);

                    // No need to create a response. One will be created below.
                }
                else if (activity.Type == ActivityTypes.Trace && activity.ChannelId != "emulator")
                {
                    // if it is a Trace activity we only send to the channel if it's the emulator.
                }
                else if (!string.IsNullOrWhiteSpace(activity.ReplyToId))
                {
                    var conversationId = activity.Conversation.Id;
                    var activityId = activity.ReplyToId;

                    var requestPath = $"/v3/conversations/{conversationId}/activities/{activityId}";

                    var requestContent = JsonConvert.SerializeObject(activity, SerializationSettings.BotSchemaSerializationSettings);
                    var stringContent = new StringContent(requestContent, Encoding.UTF8, "application/json");

                    var request = Request.CreatePost(requestPath, stringContent);
                    var socketResponse = await _server.SendAsync(request).ConfigureAwait(false);

                    response = socketResponse.ReadBodyAsJson<ResourceResponse>();
                }
                else
                {
                    var conversationId = activity.Conversation.Id;
                    var requestPath = $"/v3/conversations/{conversationId}/activities";

                    var requestContent = JsonConvert.SerializeObject(activity, SerializationSettings.BotSchemaSerializationSettings);
                    var stringContent = new StringContent(requestContent, Encoding.UTF8, "application/json");

                    var request = Request.CreatePost(requestPath, stringContent);
                    var socketResponse = await _server.SendAsync(request).ConfigureAwait(false);

                    response = socketResponse.ReadBodyAsJson<ResourceResponse>();
                }

                // If No response is set, then defult to a "simple" response. This can't really be done
                // above, as there are cases where the ReplyTo/SendTo methods will also return null
                // (See below) so the check has to happen here.

                // Note: In addition to the Invoke / Delay / Activity cases, this code also applies
                // with Skype and Teams with regards to typing events.  When sending a typing event in
                // these _channels they do not return a RequestResponse which causes the bot to blow up.
                // https://github.com/Microsoft/botbuilder-dotnet/issues/460
                // bug report : https://github.com/Microsoft/botbuilder-dotnet/issues/465
                if (response == null)
                {
                    response = new ResourceResponse(activity.Id ?? string.Empty);
                }

                responses[index] = response;
            }

            return responses;
        }

        public override Task<ResourceResponse> UpdateActivityAsync(ITurnContext turnContext, Activity activity, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public override Task DeleteActivityAsync(ITurnContext turnContext, ConversationReference reference, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public async Task<ConversationsResult> GetConversationsAsync(string continuationToken = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            var route = "/v3/conversations/";
            var request = Request.CreateGet(route);

            return await SendRequestAsync<ConversationsResult>(request, cancellationToken).ConfigureAwait(false);
        }

        public async Task<ConversationResourceResponse> PostConversationAsync(ConversationParameters parameters, CancellationToken cancellationToken = default(CancellationToken))
        {
            var route = "/v3/conversations/";
            var request = Request.CreatePost(route);
            request.SetBody(parameters);

            return await SendRequestAsync<ConversationResourceResponse>(request, cancellationToken).ConfigureAwait(false);
        }

        public async Task<ResourceResponse> PostToConversationAsync(string conversationId, Activity activity, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (activity == null)
            {
                throw new ArgumentNullException(nameof(activity));
            }

            var route = string.Format("/v3/conversations/{0}/activities", conversationId);
            var request = Request.CreatePost(route);
            request.SetBody(activity);

            return await SendRequestAsync<ResourceResponse>(request, cancellationToken).ConfigureAwait(false);
        }

        public async Task<ResourceResponse> PostConversationHistoryAsync(string conversationId, Transcript transcript, CancellationToken cancellationToken = default(CancellationToken))
        {
            var route = string.Format("/v3/conversations/{0}/activities/history", conversationId);
            var request = Request.CreatePost(route);
            request.SetBody(transcript);

            return await SendRequestAsync<ResourceResponse>(request, cancellationToken).ConfigureAwait(false);
        }

        public async Task<ResourceResponse> UpdateActivityAsync(string conversationId, string activityId, Activity activity, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (activity == null)
            {
                throw new ArgumentNullException(nameof(activity));
            }

            var route = string.Format("/v3/conversations/{0}/activities/{1}", conversationId, activity.Id);
            var request = Request.CreatePut(route);
            request.SetBody(activity);

            return await SendRequestAsync<ResourceResponse>(request, cancellationToken).ConfigureAwait(false);
        }

        public async Task<ResourceResponse> PostToActivityAsync(string conversationId, string activityId, Activity activity, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (activity == null)
            {
                throw new ArgumentNullException(nameof(activity));
            }

            var route = string.Format("/v3/conversations/{0}/activities/{1}", conversationId, activity.Id);
            var request = Request.CreatePost(route);
            request.SetBody(activity);

            return await SendRequestAsync<ResourceResponse>(request, cancellationToken).ConfigureAwait(false);
        }

        public async Task<ResourceResponse> DeleteActivityAsync(string conversationId, string activityId, CancellationToken cancellationToken = default(CancellationToken))
        {
            var route = string.Format("/v3/conversations/{0}/activities/{1}", conversationId, activityId);
            var request = Request.CreateDelete(route);

            return await SendRequestAsync<ResourceResponse>(request, cancellationToken).ConfigureAwait(false);
        }

        public async Task<IList<ChannelAccount>> GetConversationMembersAsync(string conversationId, CancellationToken cancellationToken = default(CancellationToken))
        {
            var route = string.Format("/v3/conversations/{0}/members", conversationId);
            var request = Request.CreateGet(route);

            return await SendRequestAsync<IList<ChannelAccount>>(request, cancellationToken).ConfigureAwait(false);
        }

        public async Task<PagedMembersResult> GetConversationPagedMembersAsync(string conversationId, int? pageSize = null, string continuationToken = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            var route = string.Format("/v3/conversations/{0}/pagedmembers", conversationId);
            var request = Request.CreateGet(route);

            return await SendRequestAsync<PagedMembersResult>(request, cancellationToken).ConfigureAwait(false);
        }

        public async Task<ResourceResponse> DeleteConversationMemberAsync(string conversationId, string memberId, CancellationToken cancellationToken = default(CancellationToken))
        {
            var route = string.Format("/v3/conversations/{0}/members/{1}", conversationId, memberId);
            var request = Request.CreateDelete(route);

            return await SendRequestAsync<ResourceResponse>(request, cancellationToken).ConfigureAwait(false);
        }

        public async Task<IList<ChannelAccount>> GetActivityMembersAsync(string conversationId, string activityId, CancellationToken cancellationToken = default(CancellationToken))
        {
            var route = string.Format("/v3/conversations/{0}/activities/{1}/members", conversationId, activityId);
            var request = Request.CreateGet(route);

            return await SendRequestAsync<IList<ChannelAccount>>(request, cancellationToken).ConfigureAwait(false);
        }

        private async Task<T> SendRequestAsync<T>(Request request, CancellationToken cancellation = default(CancellationToken))
        {
            try
            {
                var serverResponse = await _server.SendAsync(request, cancellation).ConfigureAwait(false);

                if (serverResponse.StatusCode == (int)HttpStatusCode.OK)
                {
                    return serverResponse.ReadBodyAsJson<T>();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.Message);
            }

            return default(T);
        }
    }
}
