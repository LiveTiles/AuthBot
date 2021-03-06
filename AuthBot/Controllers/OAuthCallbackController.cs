﻿// Copyright (c) Microsoft. All rights reserved. Licensed under the MIT license. See full license at the bottom of this file.
namespace AuthBot.Controllers
{
    using System;
    using System.Net;
    using System.Net.Http;
    using System.Security.Cryptography;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Web;
    using System.Web.Http;
    using Autofac;
    using Helpers;
    using Microsoft.Bot.Builder.Dialogs;
    using Microsoft.Bot.Builder.Dialogs.Internals;
    using Microsoft.Bot.Connector;
    using Microsoft.Rest;
    using Models;
    using Dialogs;

    public class OAuthCallbackController : ApiController
    {
        private static RNGCryptoServiceProvider rngCsp = new RNGCryptoServiceProvider();
        private static readonly uint MaxWriteAttempts = 5;

        [HttpGet]
        [Route("api/OAuthCallback")]
#pragma warning disable CS1998 // Async method lacks 'await' operators and will run synchronously
        public async Task<HttpResponseMessage> OAuthCallback()
#pragma warning restore CS1998 // Async method lacks 'await' operators and will run synchronously
        {
            try
            {
                var resp = new HttpResponseMessage(HttpStatusCode.OK);
                //resp.Content = new StringContent($"<html><body>You have been signed out. You can now close this window.</body></html>", System.Text.Encoding.UTF8, @"text/html");
                return resp;
            }
            catch (Exception ex)
            {
                // Callback is called with no pending message as a result the login flow cannot be resumed.
                return Request.CreateErrorResponse(HttpStatusCode.BadRequest, ex);
            }
        }

        [HttpGet]
        [Route("api/OAuthCallback")]
        public async Task<HttpResponseMessage> OAuthCallback(
            [FromUri] string code,
            [FromUri] string state,
            CancellationToken cancellationToken)
        {
            try
            {
                // Get the token
                AuthResult authResult = null;
                if (string.Equals(AuthSettings.Mode, "v1", StringComparison.OrdinalIgnoreCase))
                {
                    var tokenCache = new Microsoft.IdentityModel.Clients.ActiveDirectory.TokenCache();

                    // Exchange the Auth code with Access token
                    authResult = await AzureActiveDirectoryHelper.GetTokenByAuthCodeAsync(code, (Microsoft.IdentityModel.Clients.ActiveDirectory.TokenCache)tokenCache);
                }
                else if (string.Equals(AuthSettings.Mode, "v2", StringComparison.OrdinalIgnoreCase))
                {
                    var tokenCache = new Microsoft.Identity.Client.TokenCache();

                    // Exchange the Auth code with Access token
                    authResult = await AzureActiveDirectoryHelper.GetTokenByAuthCodeAsync(code, (Microsoft.Identity.Client.TokenCache)tokenCache, Models.AuthSettings.Scopes);
                }
                else if (string.Equals(AuthSettings.Mode, "b2c", StringComparison.OrdinalIgnoreCase))
                    throw new NotImplementedException("B2C has not been implemented.");

                // Get authentication conversation reference from temp storage
                var conversationReference = AuthenticationStorageHelper.GetConversationReference(state);

                // Create the message that is send to conversation to resume the login flow
                var message = conversationReference.GetPostToBotMessage();

                //IMPORTANT: DO NOT REMOVE THE MAGIC NUMBER CHECK THAT WE DO HERE. THIS IS AN ABSOLUTE SECURITY REQUIREMENT
                //REMOVING THIS WILL REMOVE YOUR BOT AND YOUR USERS TO SECURITY VULNERABILITIES. 
                //MAKE SURE YOU UNDERSTAND THE ATTACK VECTORS AND WHY THIS IS IN PLACE.
                int magicNumber = GenerateRandomNumber();

                // Save token and auth data to user bot data
                bool writeSuccessful = false;
                using (var scope = DialogModule.BeginLifetimeScope(Conversation.Container, message))
                {
                    IStateClient sc = scope.Resolve<IStateClient>();
                    uint writeAttempts = 0;
                    while (!writeSuccessful && writeAttempts++ < MaxWriteAttempts)
                    {
                        try
                        {
                            BotData userData = sc.BotState.GetUserData(message.ChannelId, message.From.Id);
                            userData.SetProperty(ContextConstants.AuthResultKey, authResult);

                            if (AuthSettings.UseMagicNumber)
                            {
                                userData.SetProperty(ContextConstants.MagicNumberKey, magicNumber);
                                userData.SetProperty(ContextConstants.MagicNumberValidated, "false");
                            }

                            sc.BotState.SetUserData(message.ChannelId, message.From.Id, userData);
                            writeSuccessful = true;   
                        }
                        catch (Exception)
                        {
                            writeSuccessful = false;
                        }
                    }
                }

                var reply = message.CreateReply();
                using (var scope = DialogModule.BeginLifetimeScope(Conversation.Container, reply))
                {
                    var client = scope.Resolve<IConnectorClient>();

                    if (!writeSuccessful)
                        reply.Text = "Could not log you in at this time, please try again later.";
                    else if (AuthSettings.UseMagicNumber)
                        reply.Text = "Please paste back the number you received in your authentication screen.";
                    else
                        reply.Text = $"Thank you {authResult.UserName}, you are now logged in.";

                    await client.Conversations.SendToConversationAsync(reply);
                }

                if (writeSuccessful && !AuthSettings.UseMagicNumber)
                {
                    // Remove temp authentication conversation reverence
                    AuthenticationStorageHelper.Delete(state);

                    // Re-direct to dialog
                    await Conversation.ResumeAsync(conversationReference, message);
                }

                var resp = Request.CreateResponse(HttpStatusCode.OK);
                if (!writeSuccessful)
                    resp.Content = new StringContent("<html><body>Could not log you in at this time, please try again later</body></html>", 
                        System.Text.Encoding.UTF8, @"text/html");
                else if (AuthSettings.UseMagicNumber)
                {
                    if (message.ChannelId == "skypeforbusiness")
                        resp.Content = new StringContent($"<html><body>Almost done! Please copy this number and paste it back to your chat so your authentication can complete:<br/> {magicNumber} </body></html>", 
                            System.Text.Encoding.UTF8, @"text/html");
                    else
                        resp.Content = new StringContent($"<html><body>Almost done! Please copy this number and paste it back to your chat so your authentication can complete:<br/> <h1>{magicNumber}</h1>.</body></html>", 
                            System.Text.Encoding.UTF8, @"text/html");
                }
                else
                    resp.Content = new StringContent(
                        "<html><body>You have successfully been authenticated and can now continue talking to your bot.</body></html>", 
                        System.Text.Encoding.UTF8, @"text/html");

                return resp;
            }
            catch (Exception ex)
            {
                // Callback is called with no pending message as a result the login flow cannot be resumed.
                return Request.CreateErrorResponse(HttpStatusCode.BadRequest, ex);
            }
        }

        private int GenerateRandomNumber()
        {
            int number = 0;
            byte[] randomNumber = new byte[1];
            do
            {
                rngCsp.GetBytes(randomNumber);
                var digit = randomNumber[0] % 10;
                number = number * 10 + digit;
            } while (number.ToString().Length < 6);
            return number;
        }
    }
}


//*********************************************************
//
//AuthBot, https://github.com/microsoftdx/AuthBot
//
//Copyright (c) Microsoft Corporation
//All rights reserved.
//
// MIT License:
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// ""Software""), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:




// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.




// THE SOFTWARE IS PROVIDED ""AS IS"", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//
//*********************************************************
