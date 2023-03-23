using System.ComponentModel;
using Raven.Server.Config.Attributes;
using Raven.Server.Config.Settings;
using Sparrow;
using Sparrow.LowMemory;
using Sparrow.Platform;

namespace Raven.Server.Config.Categories
{
    [ConfigurationCategory(ConfigurationCategoryType.PerformanceHints)]
    public class PerformanceHintsConfiguration : ConfigurationCategory
    {
        public PerformanceHintsConfiguration()
        {
            if (PlatformDetails.RunningOnLinux)
            {
                var threshold = new Size(8, SizeUnit.Gigabytes);
                MinSwapSize = MemoryInformation.TotalPhysicalMemory < threshold 
                    ? new Size(1, SizeUnit.Gigabytes) 
                    : Size.Min(MemoryInformation.TotalPhysicalMemory / 2, threshold);
            }
        }
        
        [Description("The size of a document after which it will get into the huge documents collection")]
        [DefaultValue(5)]
        [SizeUnit(SizeUnit.Megabytes)]
        [ConfigurationEntry("PerformanceHints.Documents.HugeDocumentSizeInMb", ConfigurationEntryScope.ServerWideOrPerDatabase)]
        public Size HugeDocumentSize { get; set; }

        [Description("The maximum size of the huge documents collection")]
        [DefaultValue(100)]
        [ConfigurationEntry("PerformanceHints.Documents.HugeDocumentsCollectionSize", ConfigurationEntryScope.ServerWideOrPerDatabase)]
        public int HugeDocumentsCollectionSize { get; set; }

        [Description("The maximum number of index outputs per document after which we will create a performance hint")]
        [DefaultValue(1024)]
        [ConfigurationEntry("PerformanceHints.Indexing.MaxIndexOutputsPerDocument", ConfigurationEntryScope.ServerWideOrPerDatabase)]
        public int MaxWarnIndexOutputsPerDocument { get; set; }

        [Description("The maximum number of query results after which we will create a performance hint")]
        [DefaultValue(2048)]
        [ConfigurationEntry("PerformanceHints.MaxNumberOfResults", ConfigurationEntryScope.ServerWideOrPerDatabase)]
        public int MaxNumberOfResults { get; set; }

        [Description("Request latency threshold before the server would issue a performance hint")]
        [ConfigurationEntry("PerformanceHints.TooLongRequestThresholdInSec", ConfigurationEntryScope.ServerWideOrPerDatabase)]
        [DefaultValue(30)]
        [TimeUnit(TimeUnit.Seconds)]
        public TimeSetting TooLongRequestThreshold { get; set; }
        
        [Description("The minimum swap size (for Linux only). If the swap size is lower a notification will arise")]
        [DefaultValue(DefaultValueSetInConstructor)]
        [SizeUnit(SizeUnit.Megabytes)]
        [ConfigurationEntry("PerformanceHints.Memory.MinSwapSizeInMb", ConfigurationEntryScope.ServerWideOnly)]
        public Size MinSwapSize { get; set; }

        [Description("The maximum number of LoadDocument()/LoadCompareExchangeValue() calls per a reference document/compare exchange value after which we will create a performance hint")]
        [DefaultValue(1024)]
        [ConfigurationEntry("PerformanceHints.Indexing.MaxNumberOfLoadsPerReference", ConfigurationEntryScope.ServerWideOrPerDatabase)]
        public int MaxNumberOfLoadsPerReference { get; set; }
        
        [Description("Alert when source document in indexed as field.")]
        [DefaultValue(true)]
        [ConfigurationEntry("PerformanceHints.Indexing.AlertWhenSourceDocumentIncludedInOutput", ConfigurationEntryScope.ServerWideOrPerDatabase)]
        public bool AlertWhenSourceDocumentIncludedInOutput { get; set; }
        
        [Description("Maximum depth of recursion in LINQ Select clause.")]
        [DefaultValue(32)]
        [ConfigurationEntry("PerformanceHints.Indexing.MaxDepthOfRecursionInLinqSelect", ConfigurationEntryScope.ServerWideOrPerDatabase)]
        public int MaxDepthOfRecursionInLinqSelect { get; set; }
    }
}
