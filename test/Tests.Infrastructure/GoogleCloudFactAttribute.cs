﻿using System;
using System.Runtime.CompilerServices;
using FastTests;
using Raven.Client.Documents.Operations.Backups;
using Xunit;

namespace Tests.Infrastructure
{
    public class GoogleCloudFactAttribute : FactAttribute
    {
        private const string BucketNameEnvironmentVariable = "GOOGLE_CLOUD_BUCKET_NAME";
        private const string GoogleCloudCredentialEnvironmentVariable = "GOOGLE_CLOUD_CREDENTIAL";

        public static GoogleCloudSettings GoogleCloudSettings { get; }

        static GoogleCloudFactAttribute()
        {
            GoogleCloudSettings = new GoogleCloudSettings
            {
                BucketName = Environment.GetEnvironmentVariable(BucketNameEnvironmentVariable),
                GoogleCredentialsJson = Environment.GetEnvironmentVariable(GoogleCloudCredentialEnvironmentVariable)
            };
        }

        public GoogleCloudFactAttribute([CallerMemberName] string memberName = "")
        {
            if (RavenTestHelper.IsRunningOnCI)
                return;

            if (string.IsNullOrWhiteSpace(GoogleCloudSettings.BucketName))
            {
                Skip = $"Google cloud {memberName} tests missing {BucketNameEnvironmentVariable} environment variable.";
                return;
            }

            if (string.IsNullOrWhiteSpace(GoogleCloudSettings.GoogleCredentialsJson))
            {
                Skip = $"Google cloud {memberName} tests missing {BucketNameEnvironmentVariable} environment variable.";
                return;
            }
        }
    }
}
