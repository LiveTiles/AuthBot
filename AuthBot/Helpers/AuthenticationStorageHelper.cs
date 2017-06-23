using Microsoft.Azure; 
using Microsoft.WindowsAzure.Storage; 
using Microsoft.WindowsAzure.Storage.Blob;
using Microsoft.Bot.Connector;

using Newtonsoft.Json.Linq;

namespace AuthBot.Helpers
{
    // Reference: https://docs.microsoft.com/en-us/azure/storage/storage-dotnet-how-to-use-blobs

    public static class AuthenticationStorageHelper
    {
        const string _container = "bottempauthdata";

        public static CloudBlobClient GetClient()
        {
            CloudStorageAccount storageAccount;
            var dataStorageConnectionString = CloudConfigurationManager.GetSetting("Data.StorageConnectionString");
            if (string.IsNullOrEmpty(dataStorageConnectionString))
                storageAccount = CloudStorageAccount.DevelopmentStorageAccount;
            else
                storageAccount = CloudStorageAccount.Parse(dataStorageConnectionString);

            return storageAccount.CreateCloudBlobClient();
        }

        public static CloudBlobContainer GetContainer()
        {
            CloudBlobContainer container = GetClient().GetContainerReference(_container);
            container.CreateIfNotExists();
            return container;
        }
        
        public static CloudBlockBlob GetBlob(string blobName)
        {
            return GetContainer().GetBlockBlobReference(blobName);           
        }
        
        public static void UploadConversationReference(string blobName, ConversationReference conversation)
        {
            GetBlob(blobName).UploadText(JObject.FromObject(conversation).ToString());
        }

        public static ConversationReference GetConversationReference(string blobName)
        {
            return JObject.Parse(GetBlob(blobName).DownloadText()).ToObject<ConversationReference>();
        }

        public static void Delete(string blobName)
        {
            GetBlob(blobName).Delete();
        }
    }
}
