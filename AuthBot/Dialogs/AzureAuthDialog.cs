// Copyright (c) Microsoft. All rights reserved. Licensed under the MIT license. See full license at the bottom of this file.
namespace AuthBot.Dialogs
{
    using System;
    using System.Threading.Tasks;
    using Helpers;
    using Microsoft.Bot.Builder.Dialogs;
    using Microsoft.Bot.Connector;
    using Models;
    using Microsoft.Bot.Builder.Dialogs.Internals;
    using Autofac;
    using System.Collections.Generic;
    using System.Text.RegularExpressions;
    
    [Serializable]
    public class AzureAuthDialog : IDialog<object>
    {
        protected string _resourceId { get; }
        protected string[] _scopes { get; }
        protected string _prompt { get; }
        string _authenticationId;

        public AzureAuthDialog(string resourceId, string prompt = "Please sign in to continue")
        {
            _resourceId = resourceId;
            _prompt = prompt;
        }

        public AzureAuthDialog(string[] scopes, string prompt = "Please sign in to continue")
        {
            _scopes = scopes;
            _prompt = prompt;
        }

        //HACK: Required to continue dialog from OAuthCallbackController
        public AzureAuthDialog() { }

#pragma warning disable CS1998 // Async method lacks 'await' operators and will run synchronously
        public async Task StartAsync(IDialogContext context)
#pragma warning restore CS1998 // Async method lacks 'await' operators and will run synchronously
        {
            context.Wait(MessageReceivedAsync);
        }

        public virtual async Task MessageReceivedAsync(IDialogContext context, IAwaitable<IMessageActivity> argument)
        {
            var msg = await argument;

            AuthResult authResult;
            if (AuthSettings.UseMagicNumber && context.UserData.TryGetValue(ContextConstants.AuthResultKey, out authResult))
            {
                string validated = "";
                int magicNumber = 0;

                try
                {
                    //IMPORTANT: DO NOT REMOVE THE MAGIC NUMBER CHECK THAT WE DO HERE. THIS IS AN ABSOLUTE SECURITY REQUIREMENT
                    //REMOVING THIS WILL REMOVE YOUR BOT AND YOUR USERS TO SECURITY VULNERABILITIES. 
                    //MAKE SURE YOU UNDERSTAND THE ATTACK VECTORS AND WHY THIS IS IN PLACE.
                    context.UserData.TryGetValue<string>(ContextConstants.MagicNumberValidated, out validated);
                    if (validated == "true")
                    {
                        await context.PostAsync($"Thank you {authResult.UserName}, you are now logged in. ");
                        context.Done(true);
                    }
                    else if (context.UserData.TryGetValue<int>(ContextConstants.MagicNumberKey, out magicNumber))
                    {
                        if (msg.Text.Length >= 6 && magicNumber.ToString() == msg.Text.Substring(0, 6))
                        {
                            context.UserData.SetValue<string>(ContextConstants.MagicNumberValidated, "true");

                            // Remove temp authentication conversation reverence
                            AuthenticationStorageHelper.Delete(_authenticationId);
                            _authenticationId = string.Empty;

                            await context.PostAsync($"Thank you {authResult.UserName}, you are now logged in. ");
                            context.Done(true);
                        }
                        else
                        {
                            context.UserData.RemoveValue(ContextConstants.AuthResultKey);
                            context.UserData.SetValue<string>(ContextConstants.MagicNumberValidated, "false");
                            context.UserData.RemoveValue(ContextConstants.MagicNumberKey);
                            await context.PostAsync($"I'm sorry but I couldn't validate your number. Please try authenticating once again. ");

                            context.Wait(MessageReceivedAsync);
                        }
                    }
                }
                catch
                {
                    context.UserData.RemoveValue(ContextConstants.AuthResultKey);
                    context.UserData.SetValue(ContextConstants.MagicNumberValidated, "false");
                    context.UserData.RemoveValue(ContextConstants.MagicNumberKey);
                    await context.PostAsync($"I'm sorry but something went wrong while authenticating.");
                    context.Done(false);
                }
            }
            else
                await CheckForLogin(context, msg);
        }

        /// <summary>
        /// Prompts the user to login. This can be overridden inorder to allow custom prompt messages or cards per channel.
        /// </summary>
        /// <param name="context">Chat context</param>
        /// <param name="msg">Chat message</param>
        /// <param name="authenticationUrl">OAuth URL for authenticating user</param>
        /// <returns>Task from Posting or prompt to the context.</returns>
        protected virtual Task PromptToLogin(IDialogContext context, IMessageActivity msg, string authenticationUrl)
        {
            Attachment plAttachment = null;
            switch (msg.ChannelId)
            {
                case "skypeforbusiness":
                    return context.PostAsync(_prompt + "[Click here](" + authenticationUrl + ")");
                case "emulator":
                case "skype":
                    {
                        SigninCard plCard = new SigninCard(_prompt, GetCardActions(authenticationUrl, "signin"));
                        plAttachment = plCard.ToAttachment();
                        break;
                    }
                // Teams does not yet support signin cards
                case "msteams":
                    {
                        ThumbnailCard plCard = new ThumbnailCard()
                        {
                            Title = _prompt,
                            Subtitle = "",
                            Images = new List<CardImage>(),
                            Buttons = GetCardActions(authenticationUrl, "openUrl")
                        };
                        plAttachment = plCard.ToAttachment();
                        break;
                    }
                default:
                    {
                        SigninCard plCard = new SigninCard(_prompt, GetCardActions(authenticationUrl, "signin"));
                        plAttachment = plCard.ToAttachment();
                        break;
                    }
//                    return context.PostAsync(this.prompt + "[Click here](" + authenticationUrl + ")");
            }

            IMessageActivity response = context.MakeMessage();
            response.Recipient = msg.From;
            response.Type = "message";

            response.Attachments = new List<Attachment>();
            response.Attachments.Add(plAttachment);

            return context.PostAsync(response);
        }

        private List<CardAction> GetCardActions(string authenticationUrl, string actionType)
        {
            List<CardAction> cardButtons = new List<CardAction>();
            CardAction plButton = new CardAction()
            {
                Value = authenticationUrl,
                Type = actionType,
                Title = "Authentication Required"
            };
            cardButtons.Add(plButton);
            return cardButtons;
        }

        /// <summary>
        /// Checks if we are able to get an access token. If not, we prompt for a login
        /// </summary>
        /// <param name="context"></param>
        /// <param name="msg"></param>
        /// <returns></returns>
        protected virtual async Task CheckForLogin(IDialogContext context, IMessageActivity msg)
        {
            try
            {
                string token;
                if (!string.IsNullOrEmpty(_resourceId))
                    token = await context.GetAccessToken(_resourceId);
                else
                    token = await context.GetAccessToken(_scopes);

                if (string.IsNullOrEmpty(token))
                {
                    if (msg.Text != null &&
                        CancellationWords.GetCancellationWords().Contains(msg.Text.ToUpper()))
                        context.Done(false);
                    else
                    {
                        // Save authentication conversation reference to temp storage
                        _authenticationId = Guid.NewGuid().ToString();
                        AuthenticationStorageHelper.UploadConversationReference(_authenticationId, 
                            new ConversationReference(msg.Id, msg.From, msg.Recipient, msg.Conversation, msg.ChannelId, msg.ServiceUrl));

                        string authenticationUrl;
                        if (!string.IsNullOrEmpty(_resourceId))
                            authenticationUrl = await AzureActiveDirectoryHelper.GetAuthUrlAsync(_authenticationId, _resourceId);
                        else
                            authenticationUrl = await AzureActiveDirectoryHelper.GetAuthUrlAsync(_authenticationId, _scopes);

                        await PromptToLogin(context, msg, authenticationUrl);
                        context.Wait(MessageReceivedAsync);
                    }
                }
                else
                    context.Done(true);
            }
            catch (Exception ex)
            {
                throw ex;
            }
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
