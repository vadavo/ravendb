// -----------------------------------------------------------------------
//  <copyright file="EndOfDiskSpaceEvent.cs" company="Hibernating Rhinos LTD">
//      Copyright (c) Hibernating Rhinos LTD. All rights reserved.
//  </copyright>
// -----------------------------------------------------------------------

using System.Runtime.ExceptionServices;
using Sparrow;
using Sparrow.Server.Utils;

namespace Voron.Util
{
    public class EndOfDiskSpaceEvent
    {
        private readonly long _availableSpaceWhenEventOccurred;
        private readonly string _path;
        private readonly ExceptionDispatchInfo _edi;

        public EndOfDiskSpaceEvent(string path, long availableSpaceWhenEventOccurred, ExceptionDispatchInfo edi)
        {
            _availableSpaceWhenEventOccurred = availableSpaceWhenEventOccurred;
            _path = path;
            _edi = edi;
        }

        public void AssertCanContinueWriting()
        {
            var diskInfoResult = DiskUtils.GetDiskSpaceInfo(_path);
            if (diskInfoResult == null)
                return;

            var freeSpaceInBytes = diskInfoResult.TotalFreeSpace.GetValue(SizeUnit.Bytes);
            if (freeSpaceInBytes > _availableSpaceWhenEventOccurred)
                return;

            _edi.Throw();
        }
    }
}
