﻿using System;
using System.Runtime.CompilerServices;
using FastTests;
using Newtonsoft.Json;
using Raven.Client.Documents.Operations.Backups;
using Xunit;

namespace Tests.Infrastructure
{
    public class AzureSasTokenFactAttribute : FactAttribute
    {
        private const string AzureCredentialEnvironmentVariable = "AZURE_SAS_TOKEN_CREDENTIAL";

        private static readonly AzureSettings _azureSettings;

        public static AzureSettings AzureSettings => new AzureSettings(_azureSettings);

        private static readonly string ParsingError;

        static AzureSasTokenFactAttribute()
        {
            var azureSettingsString = Environment.GetEnvironmentVariable(AzureCredentialEnvironmentVariable);
            if (azureSettingsString == null)
                return;

            try
            {
                _azureSettings = JsonConvert.DeserializeObject<AzureSettings>(azureSettingsString);
            }
            catch (Exception e)
            {
                ParsingError = e.ToString();
            }
        }

        public AzureSasTokenFactAttribute([CallerMemberName] string memberName = "")
        {
            if (RavenTestHelper.IsRunningOnCI)
                return;

            if (string.IsNullOrEmpty(ParsingError) == false)
            {
                Skip = $"Failed to parse the Azure settings, error: {ParsingError}";
                return;
            }

            if (_azureSettings == null)
            {
                Skip = $"S3 {memberName} tests missing Azure settings.";
                return;
            }
        }
    }
}
