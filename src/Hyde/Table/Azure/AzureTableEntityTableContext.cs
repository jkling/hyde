using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.RetryPolicies;
using Microsoft.WindowsAzure.Storage.Table;
using TechSmith.Hyde.Common;

namespace TechSmith.Hyde.Table.Azure
{
   internal class AzureTableEntityTableContext : ITableContext
   {
      private readonly ICloudStorageAccount _storageAccount;
      private readonly Queue<ExecutableTableOperation> _operations = new Queue<ExecutableTableOperation>();
      private readonly TableRequestOptions _retriableTableRequest = new TableRequestOptions
      {
         RetryPolicy = new ExponentialRetry( TimeSpan.FromSeconds( 1 ), 4 )
      };

      public AzureTableEntityTableContext( ICloudStorageAccount storageAccount )
      {
         _storageAccount = storageAccount;
      }

      public T GetItem<T>( string tableName, string partitionKey, string rowKey ) where T : new()
      {
         TableResult result = Get( tableName, partitionKey, rowKey );

         if ( result.Result == null )
         {
            throw new EntityDoesNotExistException( partitionKey, rowKey, null );
         }

         return ( (GenericTableEntity) result.Result ).ConvertTo<T>();
      }

      public IEnumerable<T> GetCollection<T>( string tableName ) where T : new()
      {
         string allPartitionAndRowsFilter = string.Empty;
         return ExecuteFilterOnTable<T>( tableName, allPartitionAndRowsFilter );
      }

      public IEnumerable<T> GetCollection<T>( string tableName, string partitionKey ) where T : new()
      {
         var allRowsInPartitonFilter = TableQuery.GenerateFilterCondition( "PartitionKey", QueryComparisons.Equal, partitionKey );
         return ExecuteFilterOnTable<T>( tableName, allRowsInPartitonFilter );
      }

      public IEnumerable<T> GetRangeByPartitionKey<T>( string tableName, string partitionKeyLow, string partitionKeyHigh ) where T : new()
      {
         var lowerRangePartitionFilter = TableQuery.GenerateFilterCondition( "PartitionKey", QueryComparisons.GreaterThanOrEqual, partitionKeyLow );
         var higherRangePartitionFilter = TableQuery.GenerateFilterCondition( "PartitionKey", QueryComparisons.LessThanOrEqual, partitionKeyHigh );

         var rangePartitionFilter = TableQuery.CombineFilters( lowerRangePartitionFilter, TableOperators.And, higherRangePartitionFilter );

         return ExecuteFilterOnTable<T>( tableName, rangePartitionFilter );
      }

      public IEnumerable<T> GetRangeByRowKey<T>( string tableName, string partitionKey, string rowKeyLow, string rowKeyHigh ) where T : new()
      {
         var partitionFilter = TableQuery.GenerateFilterCondition( "PartitionKey", QueryComparisons.Equal, partitionKey );
         var lowerRangeRowFilter = TableQuery.GenerateFilterCondition( "RowKey", QueryComparisons.GreaterThanOrEqual, rowKeyLow );
         var higherRangeRowFilter = TableQuery.GenerateFilterCondition( "RowKey", QueryComparisons.LessThanOrEqual, rowKeyHigh );

         var rangeRowFilter = TableQuery.CombineFilters( lowerRangeRowFilter, TableOperators.And, higherRangeRowFilter );

         var fullRangeFilter = TableQuery.CombineFilters( partitionFilter, TableOperators.And, rangeRowFilter );

         return ExecuteFilterOnTable<T>( tableName, fullRangeFilter );
      }

      public dynamic GetItem( string tableName, string partitionKey, string rowKey )
      {
         var retrieveOperation = TableOperation.Retrieve<GenericTableEntity>( partitionKey, rowKey );

         TableResult result = Table( tableName ).Execute( retrieveOperation, _retriableTableRequest );

         return ( (GenericTableEntity) result.Result ).ConvertToDynamic();
      }

      public IEnumerable<dynamic> GetCollection( string tableName )
      {
         var allRowsFilter = TableQuery.GenerateFilterCondition( "PartitionKey", QueryComparisons.GreaterThanOrEqual, "a" );
         return ExecuteFilterOnTable( tableName, allRowsFilter );
      }

      public IEnumerable<dynamic> GetCollection( string tableName, string partitionKey )
      {
         var allRowsInPartitonFilter = TableQuery.GenerateFilterCondition( "PartitionKey", QueryComparisons.Equal, partitionKey );
         return ExecuteFilterOnTable( tableName, allRowsInPartitonFilter );
      }

      public IEnumerable<dynamic> GetRangeByPartitionKey( string tableName, string partitionKeyLow, string partitionKeyHigh )
      {
         var lowerRangePartitionFilter = TableQuery.GenerateFilterCondition( "PartitionKey", QueryComparisons.GreaterThanOrEqual, partitionKeyLow );
         var higherRangePartitionFilter = TableQuery.GenerateFilterCondition( "PartitionKey", QueryComparisons.LessThanOrEqual, partitionKeyHigh );

         var rangePartitionFilter = TableQuery.CombineFilters( lowerRangePartitionFilter, TableOperators.And, higherRangePartitionFilter );

         return ExecuteFilterOnTable( tableName, rangePartitionFilter );
      }

      public IEnumerable<dynamic> GetRangeByRowKey( string tableName, string partitionKey, string rowKeyLow, string rowKeyHigh )
      {
         var partitionFilter = TableQuery.GenerateFilterCondition( "PartitionKey", QueryComparisons.Equal, partitionKey );
         var lowerRangeRowFilter = TableQuery.GenerateFilterCondition( "RowKey", QueryComparisons.GreaterThanOrEqual, rowKeyLow );
         var higherRangeRowFilter = TableQuery.GenerateFilterCondition( "RowKey", QueryComparisons.LessThanOrEqual, rowKeyHigh );

         var rangeRowFilter = TableQuery.CombineFilters( lowerRangeRowFilter, TableOperators.And, higherRangeRowFilter );

         var fullRangeFilter = TableQuery.CombineFilters( partitionFilter, TableOperators.And, rangeRowFilter );

         return ExecuteFilterOnTable( tableName, fullRangeFilter );
      }

      public void AddNewItem( string tableName, dynamic itemToAdd, string partitionKey, string rowKey )
      {
         GenericTableEntity entity = GenericTableEntity.HydrateFrom( itemToAdd, partitionKey, rowKey );
         var operation = TableOperation.Insert( entity );
         _operations.Enqueue( new ExecutableTableOperation( tableName, operation, TableOperationType.Insert, partitionKey, rowKey ) );
      }

      public void Upsert( string tableName, dynamic itemToUpsert, string partitionKey, string rowKey )
      {
         // Upsert does not use an ETag (If-Match header) - http://msdn.microsoft.com/en-us/library/windowsazure/hh452242.aspx
         GenericTableEntity entity = GenericTableEntity.HydrateFrom( itemToUpsert, partitionKey, rowKey );
         var operation = TableOperation.InsertOrReplace( entity );
         _operations.Enqueue( new ExecutableTableOperation( tableName, operation, TableOperationType.InsertOrReplace, partitionKey, rowKey ) );
      }

      public void Update( string tableName, dynamic item, string partitionKey, string rowKey )
      {
         GenericTableEntity entity = GenericTableEntity.HydrateFrom( item, partitionKey, rowKey );
         entity.ETag = "*";

         var operation = TableOperation.Replace( entity );
         _operations.Enqueue( new ExecutableTableOperation( tableName, operation, TableOperationType.Replace, partitionKey, rowKey ) );
      }

      public void DeleteItem( string tableName, string partitionKey, string rowKey )
      {
         var operation = TableOperation.Delete( new GenericTableEntity
         {
            ETag = "*",
            PartitionKey = partitionKey,
            RowKey = rowKey
         } );
         _operations.Enqueue( new ExecutableTableOperation( tableName, operation, TableOperationType.Delete, partitionKey, rowKey ) );
      }

      public void DeleteCollection( string tableName, string partitionKey )
      {
         var allRowsInPartitonFilter = TableQuery.GenerateFilterCondition( "PartitionKey", QueryComparisons.Equal, partitionKey );
         var getAllInPartitionQuery = new TableQuery<TableEntity>().Where( allRowsInPartitonFilter );
         var entitiesToDelete = Table( tableName ).ExecuteQuery( getAllInPartitionQuery );
         foreach ( var entity in entitiesToDelete )
         {
            var operation = TableOperation.Delete( entity );
            _operations.Enqueue( new ExecutableTableOperation( tableName, operation, TableOperationType.Delete, partitionKey, entity.RowKey ) );
         }
      }

      public void Save( Execute executeMethod )
      {
         if ( !_operations.Any() )
         {
            return;
         }

         ExecutableTableOperation firstOperation = _operations.First();
         string partitionKey = firstOperation.PartitionKey;
         TableOperationType operationType = firstOperation.OperationType;
         string table = firstOperation.Table;

         bool canDoEntityGroupTransaction = !_operations.Any( o => o.PartitionKey != partitionKey || o.OperationType != operationType || o.Table != table );

         if ( executeMethod == Execute.InBatches && !canDoEntityGroupTransaction )
         {
            executeMethod = Execute.Individually;
         }

         try
         {
            switch ( executeMethod )
            {
               case Execute.Individually:
               {
                  SaveIndividual( new Queue<ExecutableTableOperation>( _operations ) );
                  break;
               }
               case Execute.InBatches:
               {
                  SaveBatch( new Queue<ExecutableTableOperation>( _operations ), table );
                  break;
               }
               case Execute.Atomically:
               {
                  throw new NotImplementedException();
               }
               default:
               {
                  throw new ArgumentException( "Unsupported execution method: " + executeMethod );
               }
            }
         }
         finally
         {
            _operations.Clear();
         }
      }

      private void SaveIndividual( Queue<ExecutableTableOperation> operations )
      {
         while ( operations.Count > 0 )
         {
            ExecutableTableOperation operation = operations.Dequeue();

            try
            {
               Table( operation.Table ).Execute( operation.Operation, _retriableTableRequest );
            }
            catch ( StorageException ex )
            {
               if ( ex.RequestInformation.HttpStatusCode == (int) HttpStatusCode.NotFound && operation.OperationType == TableOperationType.Delete )
               {
                  continue;
               }

               _operations.Clear();
               if ( ex.RequestInformation.HttpStatusCode == (int) HttpStatusCode.BadRequest && 
                    operation.OperationType == TableOperationType.Delete &&
                    ex.RequestInformation.ExtendedErrorInformation.ErrorCode == "OutOfRangeInput" )
               {
                  // The table does not exist.
                  continue;
               }

               if ( ex.RequestInformation.HttpStatusCode == (int) HttpStatusCode.Conflict )
               {
                  throw new EntityAlreadyExistsException( "Entity already exists", ex );
               }
               if ( ex.RequestInformation.HttpStatusCode == (int) HttpStatusCode.NotFound )
               {
                  throw new EntityDoesNotExistException( "Entity does not exist", ex );
               }

               throw;
            }
         }
      }

      private void SaveBatch( Queue<ExecutableTableOperation> operations, string table )
      {
         int operationCounter = 0;
         var batchOperation = new TableBatchOperation();
         var opKeys = new HashSet<Tuple<string, string, TableOperationType>>();

         while ( operations.Count > 0 )
         {
            ExecutableTableOperation operation = operations.Dequeue();

            bool isAtMaxBatchSize = ( operationCounter == 100 );

            // Operations with the same partition key, row key, and operation type cannot be executed in the same
            // entity group transaction -- Table Storage will return a 400 bad request. We use the operation keys
            // below to detect conflicts and start a new batch if necessary.
            var opKey = new Tuple<string, string, TableOperationType>( operation.PartitionKey, operation.RowKey, operation.OperationType );
            bool nextOpConflictsWithBatch = ( opKeys.Contains( opKey ) );

            bool saveAndStartNewBatch = isAtMaxBatchSize || nextOpConflictsWithBatch;
            if ( saveAndStartNewBatch )
            {
               ExecuteBatchHandlingExceptions( table, batchOperation );
               batchOperation = new TableBatchOperation();
               operationCounter = 0;
               opKeys.Clear();
            }

            batchOperation.Add( operation.Operation );
            opKeys.Add( opKey );
            operationCounter++;
         }

         if ( batchOperation.Count > 0 )
         {
            ExecuteBatchHandlingExceptions( table, batchOperation );
         }
      }

      private void ExecuteBatchHandlingExceptions( string table, TableBatchOperation batchOperation )
      {
         try
         {
            Table( table ).ExecuteBatch( batchOperation, _retriableTableRequest );
         }
         catch ( StorageException ex )
         {
            if ( ex.RequestInformation.HttpStatusCode == (int) HttpStatusCode.Conflict )
            {
               throw new EntityAlreadyExistsException( "Entity already exists", ex );
            }
            if ( ex.RequestInformation.HttpStatusCode == (int) HttpStatusCode.NotFound )
            {
               throw new EntityDoesNotExistException( "Entity does not exist", ex );
            }
            if ( ex.RequestInformation.HttpStatusCode == (int) HttpStatusCode.BadRequest )
            {
               throw new InvalidOperationException( "Table storage returned 'Bad Request'", ex );
            }

            throw;
         }
      }

      [Obsolete( "Use GetRangeByPartitionKey instead." )]
      public IEnumerable<T> GetRange<T>( string tableName, string partitionKeyLow, string partitionKeyHigh ) where T : new()
      {
         return GetRangeByPartitionKey<T>( tableName, partitionKeyLow, partitionKeyHigh );
      }

      private TableResult Get( string tableName, string partitionKey, string rowKey )
      {
         var retrieveOperation = TableOperation.Retrieve<GenericTableEntity>( partitionKey, rowKey );

         return Table( tableName ).Execute( retrieveOperation, _retriableTableRequest );
      }

      private IEnumerable<T> ExecuteFilterOnTable<T>( string tableName, string filter ) where T : new()
      {
         var query = new TableQuery<GenericTableEntity>().Where( filter );
         return Table( tableName ).ExecuteQuery( query ).Select( e => e.ConvertTo<T>() );
      }

      private IEnumerable<dynamic> ExecuteFilterOnTable( string tableName, string filter )
      {
         var query = new TableQuery<GenericTableEntity>().Where( filter );
         return Table( tableName ).ExecuteQuery( query ).Select( e => e.ConvertToDynamic() );
      }

      private CloudTable Table( string tableName )
      {
         return new CloudTableClient( new Uri( _storageAccount.TableEndpoint ), _storageAccount.Credentials ).GetTableReference( tableName );
      }
   }
}