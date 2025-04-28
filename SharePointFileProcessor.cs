using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Graph;
using Azure.Identity;

namespace AI_RAG_Examples_KM
{
    public class SharePointFileProcessor
    {
        private readonly string _tenantId;
        private readonly string _clientId;
        private readonly string _clientSecret;
        private readonly string _siteId;
        private readonly string _driveId;
        private readonly string _localDownloadPath;
        private readonly CustomTabularIngestion _customTabularIngestion;
        private GraphServiceClient? _graphClient;

        public SharePointFileProcessor(
            string tenantId,
            string clientId,
            string clientSecret,
            string siteId,
            string driveId,
            string localDownloadPath,
            CustomTabularIngestion customTabularIngestion)
        {
            _tenantId = tenantId;
            _clientId = clientId;
            _clientSecret = clientSecret;
            _siteId = siteId;
            _driveId = driveId;
            _localDownloadPath = localDownloadPath;
            _customTabularIngestion = customTabularIngestion;
        }

        // Authenticate and create the Graph client using Azure.Identity
        private void EnsureGraphClient()
        {
            if (_graphClient != null) return;

            var credential = new ClientSecretCredential(_tenantId, _clientId, _clientSecret);
            _graphClient = new GraphServiceClient(credential, new[] { "https://graph.microsoft.com/.default" });
        }

        // Download all files from the SharePoint document library (drive)
        public async Task ProcessFilesFromSharePointAsync(string fileExtensionPattern = "*")
        {
            EnsureGraphClient();

            // List all files in the drive root (or you can specify a folder)
            var files = await _graphClient
                .Drives[_driveId]
                .Items["root"]
                .Children
                .GetAsync();

            if (!Directory.Exists(_localDownloadPath))
            {
                Directory.CreateDirectory(_localDownloadPath);
            }

            foreach (var item in files.Value)
            {
                if (item.File == null) continue; // Skip folders

                string extension = Path.GetExtension(item.Name).ToLowerInvariant();
                if (fileExtensionPattern != "*" && !extension.Equals(fileExtensionPattern, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string localFilePath = Path.Combine(_localDownloadPath, item.Name);

                // Download the file content
                var stream = await _graphClient
                    .Drives[_driveId]
                    .Items[item.Id]
                    .Content
                    .GetAsync();

                using (var fileStream = new FileStream(localFilePath, FileMode.Create, FileAccess.Write))
                {
                    await stream.CopyToAsync(fileStream);
                }

                Console.WriteLine($"Downloaded SharePoint file: {item.Name}");

                // Ingest the file using the existing custom ingestion logic
                await _customTabularIngestion.ImportTabularDocumentCustomAsync(localFilePath, _driveId);
            }
        }
    }
}
