using System.Collections.Generic;
using System.Reflection;
using FastTests;
using Raven.Server.SqlMigration;
using Tests.Infrastructure.ConnectionString;
using Xunit.Sdk;

namespace Tests.Infrastructure
{
    public class RequiresOracleSqlInlineData : DataAttribute
    {
        public RequiresOracleSqlInlineData()
        {
            if (RavenTestHelper.IsRunningOnCI)
                return;

            if (OracleConnectionString.Instance.CanConnect == false)
                Skip = "Test requires Oracle database";
        }

        public override IEnumerable<object[]> GetData(MethodInfo testMethod)
        {
            return new[] { new object[] { MigrationProvider.Oracle } };
        }
    }
}
