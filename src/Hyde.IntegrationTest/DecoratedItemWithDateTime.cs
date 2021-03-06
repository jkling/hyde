﻿using System;
using TechSmith.Hyde.Common.DataAnnotations;

namespace TechSmith.Hyde.IntegrationTest
{
   internal class DecoratedItemWithDateTime
   {
      [PartitionKey]
      public string Id
      {
         get;
         set;
      }

      [RowKey]
      public string Name
      {
         get;
         set;
      }

      public DateTime CreationDate
      {
         get;
         set;
      }
   }
}
