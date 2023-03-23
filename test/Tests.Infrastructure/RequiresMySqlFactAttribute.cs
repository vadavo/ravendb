﻿using FastTests;
using Tests.Infrastructure.ConnectionString;
using Xunit;

namespace Tests.Infrastructure
{
    public class RequiresMySqlFactAttribute : FactAttribute
    {
        public RequiresMySqlFactAttribute()
        {
            if (RavenTestHelper.IsRunningOnCI)
                return;

            if (MySqlConnectionString.Instance.CanConnect == false)
                Skip = "Test requires MySQL database";
        }
    }
}
