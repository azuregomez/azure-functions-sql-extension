// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Globalization;
using Microsoft.Azure.WebJobs.Host.Executors;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Text;

namespace Microsoft.Azure.WebJobs.Extensions.Sql
{
    /// <summary>
    /// Periodically polls SQL's change table to determine if any new changes have occurred to a user's table.
    /// </summary>
    /// <remarks>
    /// Note that there is no possiblity of SQL injection in the raw queries we generate. All parameters that involve
    /// inserting data from a user table are sanitized. All other parameters are generated exclusively using information
    /// about the user table's schema (such as primary key column names), data stored in SQL's internal change table, or
    /// data stored in our own worker table.
    /// </remarks>
    /// <typeparam name="T">A user-defined POCO that represents a row of the user's table</typeparam>
    internal sealed class SqlTableChangeMonitor<T> : IDisposable
    {
        public const int BatchSize = 10;
        public const int MaxAttemptCount = 5;
        public const int MaxLeaseRenewalCount = 5;
        public const int LeaseIntervalInSeconds = 30;
        public const int PollingIntervalInSeconds = 5;

        private readonly string _connectionString;
        private readonly int _userTableId;
        private readonly string _userTableName;
        private readonly string _userFunctionId;
        private readonly string _workerTableName;
        private readonly IReadOnlyList<string> _userTableColumns;
        private readonly IReadOnlyList<string> _primaryKeyColumns;
        private readonly IReadOnlyList<string> _rowMatchConditions;
        private readonly ITriggeredFunctionExecutor _executor;
        private readonly ILogger _logger;

        private readonly CancellationTokenSource _cancellationTokenSourceCheckForChanges;
        private readonly CancellationTokenSource _cancellationTokenSourceRenewLeases;
        private CancellationTokenSource _cancellationTokenSourceExecutor;

        // It should be impossible for multiple threads to access these at the same time because of the semaphore we use.
        private readonly SemaphoreSlim _rowsLock;
        private IReadOnlyList<IReadOnlyDictionary<string, string>> _rows;
        private int _leaseRenewalCount;
        private State _state = State.CheckingForChanges;

        /// <summary>
        /// Initializes a new instance of the <see cref="SqlTableChangeMonitor{T}" />> class.
        /// </summary>
        /// <param name="connectionString">The SQL connection string used to connect to the user's database</param>
        /// <param name="userTableId">The OBJECT_ID of the user table whose changes are being tracked on</param>
        /// <param name="userTableName">The name of the user table</param>
        /// <param name="userFunctionId">The unique ID that identifies user function</param>
        /// <param name="workerTableName">The name of the worker table</param>
        /// <param name="userTableColumns">List of all column names in the user table</param>
        /// <param name="primaryKeyColumns">List of primary key column names in the user table</param>
        /// <param name="executor">Used to execute the user's function when changes are detected on "table"</param>
        /// <param name="logger">Ilogger used to log information and warnings</param>
        public SqlTableChangeMonitor(
            string connectionString,
            int userTableId,
            string userTableName,
            string userFunctionId,
            string workerTableName,
            IReadOnlyList<string> userTableColumns,
            IReadOnlyList<string> primaryKeyColumns,
            ITriggeredFunctionExecutor executor,
            ILogger logger)
        {
            _ = !string.IsNullOrEmpty(connectionString) ? true : throw new ArgumentNullException(nameof(connectionString));
            _ = !string.IsNullOrEmpty(userTableName) ? true : throw new ArgumentNullException(nameof(userTableName));
            _ = !string.IsNullOrEmpty(userFunctionId) ? true : throw new ArgumentNullException(nameof(userFunctionId));
            _ = !string.IsNullOrEmpty(workerTableName) ? true : throw new ArgumentNullException(nameof(workerTableName));
            _ = userTableColumns ?? throw new ArgumentNullException(nameof(userTableColumns));
            _ = primaryKeyColumns ?? throw new ArgumentNullException(nameof(primaryKeyColumns));
            _ = executor ?? throw new ArgumentNullException(nameof(executor));
            _ = logger ?? throw new ArgumentNullException(nameof(logger));

            this._connectionString = connectionString;
            this._userTableId = userTableId;
            this._userTableName = userTableName;
            this._userFunctionId = userFunctionId;
            this._workerTableName = workerTableName;
            this._userTableColumns = primaryKeyColumns.Concat(userTableColumns.Except(primaryKeyColumns)).ToList();
            this._primaryKeyColumns = primaryKeyColumns;

            // Prep search-conditions that will be used besides WHERE clause to match table rows.
            this._rowMatchConditions = Enumerable.Range(0, BatchSize)
                .Select(index => string.Join(" AND ", primaryKeyColumns.Select(col => $"{col} = @{col}_{index}")))
                .ToList();

            this._executor = executor;
            this._logger = logger;

            this._cancellationTokenSourceCheckForChanges = new CancellationTokenSource();
            this._cancellationTokenSourceRenewLeases = new CancellationTokenSource();
            this._cancellationTokenSourceExecutor = new CancellationTokenSource();

            this._rowsLock = new SemaphoreSlim(1);
            this._rows = new List<IReadOnlyDictionary<string, string>>();
            this._leaseRenewalCount = 0;
            this._state = State.CheckingForChanges;

#pragma warning disable CS4014 // Queue the below tasks and exit. Do not wait for their completion.
            _ = Task.Run(() =>
            {
                this.RunChangeConsumptionLoopAsync();
                this.RunLeaseRenewalLoopAsync();
            });
#pragma warning restore CS4014
        }

        /// <summary>
        /// Stops the change monitor which stops polling for changes on the user's table. If the change monitor is
        /// currently executing a set of changes, it is only stopped once execution is finished and the user's function
        /// is triggered (whether or not the trigger is successful).
        /// </summary>
        public void Stop()
        {
            this._cancellationTokenSourceCheckForChanges.Cancel();
        }

        public void Dispose()
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Executed once every <see cref="PollingIntervalInSeconds"/> period. If the state of the change monitor is
        /// <see cref="State.CheckingForChanges"/>, then the method query the change/worker tables for changes on the
        /// user's table. If any are found, the state of the change monitor is transitioned to
        /// <see cref="State.ProcessingChanges"/> and the user's function is executed with the found changes. If the
        /// execution is successful, the leases on "_rows" are released and the state transitions to
        /// <see cref="State.CheckingForChanges"/> once again.
        /// </summary>
        private async Task RunChangeConsumptionLoopAsync()
        {
            try
            {
                CancellationToken token = this._cancellationTokenSourceCheckForChanges.Token;

                using var connection = new SqlConnection(this._connectionString);
                await connection.OpenAsync(token);

                while (!token.IsCancellationRequested)
                {
                    if (this._state == State.CheckingForChanges)
                    {
                        // What should we do if this call gets stuck?
                        await this.GetChangesAsync(token);
                        await this.ProcessChangesAsync(token);
                    }

                    await Task.Delay(TimeSpan.FromSeconds(PollingIntervalInSeconds), token);
                }
            }
            catch (Exception e)
            {
                // Only want to log the exception if it wasn't caused by StopAsync being called, since Task.Delay
                // throws an exception if it's cancelled.
                if (e.GetType() != typeof(TaskCanceledException))
                {
                    this._logger.LogError(e.Message);
                }
            }
            finally
            {
                // If this thread exits due to any reason, then the lease renewal thread should exit as well. Otherwise,
                // it will keep looping perpetually.
                this._cancellationTokenSourceRenewLeases.Cancel();
                this._cancellationTokenSourceCheckForChanges.Dispose();
                this._cancellationTokenSourceExecutor.Dispose();
            }
        }

        /// <summary>
        /// Queries the change/worker tables to check for new changes on the user's table. If any are found, stores the
        /// change along with the corresponding data from the user table in "_rows".
        /// </summary>
        private async Task GetChangesAsync(CancellationToken token)
        {
            try
            {
                using var connection = new SqlConnection(this._connectionString);
                await connection.OpenAsync(token);

                using SqlTransaction transaction = connection.BeginTransaction(System.Data.IsolationLevel.RepeatableRead);

                // Update the version number stored in the global state table if necessary before using it.
                using (SqlCommand updateTablesPreInvocationCommand = this.BuildUpdateTablesPreInvocation(connection, transaction))
                {
                    await updateTablesPreInvocationCommand.ExecuteNonQueryAsync(token);
                }

                // Use the version number to query for new changes.
                using (SqlCommand getChangesCommand = this.BuildGetChangesCommand(connection, transaction))
                {
                    var rows = new List<IReadOnlyDictionary<string, string>>();

                    using SqlDataReader reader = await getChangesCommand.ExecuteReaderAsync(token);
                    while (await reader.ReadAsync(token))
                    {
                        rows.Add(SqlBindingUtilities.BuildDictionaryFromSqlRow(reader));
                    }

                    this._rows = rows;
                }

                // If changes were found, acquire leases on them.
                if (this._rows.Count > 0)
                {
                    using SqlCommand acquireLeasesCommand = this.BuildAcquireLeasesCommand(connection, transaction);
                    await acquireLeasesCommand.ExecuteNonQueryAsync(token);
                }
                await transaction.CommitAsync(token);
            }
            catch (Exception e)
            {
                // If there's an exception in any part of the process, we want to clear all of our data in memory and
                // retry checking for changes again.
                this._rows = new List<IReadOnlyDictionary<string, string>>();
                this._logger.LogWarning($"Failed to check {this._userTableName} for new changes due to error: {e.Message}");
            }
        }

        private async Task ProcessChangesAsync(CancellationToken token)
        {
            if (this._rows.Count > 0)
            {
                this._state = State.ProcessingChanges;
                IReadOnlyList<SqlChange<T>> changes = null;

                try
                {
                    // What should we do if this fails? It doesn't make sense to retry since it's not a connection based
                    // thing. We could still try to trigger on the correctly processed changes, but that adds additional
                    // complication because we don't want to release the leases on the incorrectly processed changes.
                    // For now, just give up I guess?
                    changes = this.GetChanges();
                }
                catch (Exception e)
                {
                    await this.ClearRowsAsync(
                        $"Failed to extract user table data from table {this._userTableName} associated " +
                        $"with change metadata due to error: {e.Message}", true);
                }

                if (changes != null)
                {
                    FunctionResult result = await this._executor.TryExecuteAsync(
                        new TriggeredFunctionData() { TriggerValue = changes },
                        this._cancellationTokenSourceExecutor.Token);

                    if (result.Succeeded)
                    {
                        await this.ReleaseLeasesAsync(token);
                    }
                    else
                    {
                        // In the future might make sense to retry executing the function, but for now we just let
                        // another worker try.
                        await this.ClearRowsAsync(
                            $"Failed to trigger user's function for table {this._userTableName} due to " +
                            $"error: {result.Exception.Message}", true);
                    }
                }
            }
        }

        /// <summary>
        /// Executed once every <see cref="LeaseTime"/> period. If the state of the change monitor is
        /// <see cref="State.ProcessingChanges"/>, then we will renew the leases held by the change monitor on "_rows".
        /// </summary>
        private async void RunLeaseRenewalLoopAsync()
        {
            try
            {
                CancellationToken token = this._cancellationTokenSourceRenewLeases.Token;

                using var connection = new SqlConnection(this._connectionString);
                await connection.OpenAsync(token);

                while (!token.IsCancellationRequested)
                {
                    await this._rowsLock.WaitAsync(token);

                    await this.RenewLeasesAsync(connection, token);

                    // Want to make sure to renew the leases before they expire, so we renew them twice per lease period.
                    await Task.Delay(TimeSpan.FromSeconds(LeaseIntervalInSeconds / 2), token);
                }
            }
            catch (Exception e)
            {
                // Only want to log the exception if it wasn't caused by StopAsync being called, since Task.Delay throws
                // an exception if it's cancelled.
                if (e.GetType() != typeof(TaskCanceledException))
                {
                    this._logger.LogError(e.Message);
                }
            }
            finally
            {
                this._cancellationTokenSourceRenewLeases.Dispose();
            }
        }

        private async Task RenewLeasesAsync(SqlConnection connection, CancellationToken token)
        {
            try
            {
                if (this._state == State.ProcessingChanges)
                {
                    // I don't think I need a transaction for renewing leases. If this worker reads in a row from the
                    // worker table and determines that it corresponds to its batch of changes, but then that row gets
                    // deleted by a cleanup task, it shouldn't renew the lease on it anyways.
                    using SqlCommand renewLeasesCommand = this.BuildRenewLeasesCommand(connection);
                    await renewLeasesCommand.ExecuteNonQueryAsync(token);
                }
            }
            catch (Exception e)
            {
                // This catch block is necessary so that the finally block is executed even in the case of an exception
                // (see https://docs.microsoft.com/dotnet/csharp/language-reference/keywords/try-finally, third
                // paragraph). If we fail to renew the leases, multiple workers could be processing the same change
                // data, but we have functionality in place to deal with this (see design doc).
                this._logger.LogError($"Failed to renew leases due to error: {e.Message}");
            }
            finally
            {
                if (this._state == State.ProcessingChanges)
                {
                    // Do we want to update this count even in the case of a failure to renew the leases? Probably,
                    // because the count is simply meant to indicate how much time the other thread has spent processing
                    // changes essentially.
                    this._leaseRenewalCount += 1;

                    // If this thread has been cancelled, then the _cancellationTokenSourceExecutor could have already
                    // been disposed so shouldn't cancel it.
                    if (this._leaseRenewalCount == MaxLeaseRenewalCount && !token.IsCancellationRequested)
                    {
                        this._logger.LogWarning("Call to execute the function (TryExecuteAsync) seems to be stuck, so it is being cancelled");

                        // If we keep renewing the leases, the thread responsible for processing the changes is stuck.
                        // If it's stuck, it has to be stuck in the function execution call (I think), so we should
                        // cancel the call.
                        this._cancellationTokenSourceExecutor.Cancel();
                        this._cancellationTokenSourceExecutor = new CancellationTokenSource();
                    }
                }

                // Want to always release the lock at the end, even if renewing the leases failed.
                this._rowsLock.Release();
            }
        }

        /// <summary>
        /// Resets the in-memory state of the change monitor and sets it to start polling for changes again.
        /// </summary>
        /// <param name="error">
        /// The error messages the logger will report describing the reason function execution failed (used only in the case of a failure).
        /// </param>
        /// <param name="acquireLock">True if ClearRowsAsync should acquire the "_rowsLock" (only true in the case of a failure)</param>
        private async Task ClearRowsAsync(string error, bool acquireLock)
        {
            if (acquireLock)
            {
                this._logger.LogError(error);
                await this._rowsLock.WaitAsync();
            }

            this._leaseRenewalCount = 0;
            this._state = State.CheckingForChanges;
            this._rows = new List<IReadOnlyDictionary<string, string>>();
            this._rowsLock.Release();
        }

        /// <summary>
        /// Releases the leases held on "_rows".
        /// </summary>
        /// <returns></returns>
        private async Task ReleaseLeasesAsync(CancellationToken token)
        {
            // Don't want to change the "_rows" while another thread is attempting to renew leases on them.
            await this._rowsLock.WaitAsync(token);
            long newLastSyncVersion = this.RecomputeLastSyncVersion();

            try
            {
                using var connection = new SqlConnection(this._connectionString);
                await connection.OpenAsync(token);
                using SqlTransaction transaction = connection.BeginTransaction(System.Data.IsolationLevel.RepeatableRead);

                // Release the leases held on "_rows".
                using (SqlCommand releaseLeasesCommand = this.BuildReleaseLeasesCommand(connection, transaction))
                {
                    await releaseLeasesCommand.ExecuteNonQueryAsync(token);
                }

                // Update the global state table if we have processed all changes with ChangeVersion <= newLastSyncVersion,
                // and clean up the worker table to remove all rows with ChangeVersion <= newLastSyncVersion.
                using (SqlCommand updateTablesPostInvocationCommand = this.BuildUpdateTablesPostInvocation(connection, transaction, newLastSyncVersion))
                {
                    await updateTablesPostInvocationCommand.ExecuteNonQueryAsync(token);
                }

                await transaction.CommitAsync(token);

            }
            catch (Exception e)
            {
                // What should we do if releasing the leases fails? We could try to release them again or just wait,
                // since eventually the lease time will expire. Then another thread will re-process the same changes
                // though, so less than ideal. But for now that's the functionality.
                this._logger.LogError($"Failed to release leases for user table {this._userTableName} due to error: {e.Message}");
            }
            finally
            {
                // Want to do this before releasing the lock in case the renew leases thread wakes up. It will see that
                // the state is checking for changes and not renew the (just released) leases.
                await this.ClearRowsAsync(string.Empty, false);
            }
        }

        /// <summary>
        /// Calculates the new version number to attempt to update LastSyncVersion in global state table to. If all
        /// version numbers in _rows are the same, use that version number. If they aren't, use the second largest
        /// version number. For an explanation as to why this method was chosen, see 9c in Steps of Operation in this
        /// design doc: https://microsoft-my.sharepoint.com/:w:/p/t-sotevo/EQdANWq9ZWpKm8e48TdzUwcBGZW07vJmLf8TL_rtEG8ixQ?e=owN2EX.
        /// </summary>
        private long RecomputeLastSyncVersion()
        {
            var changeVersionSet = new SortedSet<long>();
            foreach (Dictionary<string, string> row in this._rows)
            {
                string changeVersion = row["SYS_CHANGE_VERSION"];
                changeVersionSet.Add(long.Parse(changeVersion, CultureInfo.InvariantCulture));
            }

            // If there are at least two version numbers in this set, return the second highest one. Otherwise, return
            // the only version number in the set.
            return changeVersionSet.ElementAt(changeVersionSet.Count > 1 ? changeVersionSet.Count - 2 : 0);
        }

        /// <summary>
        /// Builds up the list of <see cref="SqlChange{T}"/> passed to the user's triggered function based on the data
        /// stored in "_rows". If any of the changes correspond to a deleted row, then the <see cref="SqlChange.Item">
        /// will be populated with only the primary key values of the deleted row.
        /// </summary>
        /// <returns>The list of changes</returns>
        private IReadOnlyList<SqlChange<T>> GetChanges()
        {
            var changes = new List<SqlChange<T>>();
            foreach (Dictionary<string, string> row in this._rows)
            {
                SqlChangeOperation operation = GetChangeOperation(row);

                // If the row has been deleted, there is no longer any data for it in the user table. The best we can do
                // is populate the row-item with the primary key values of the row.
                Dictionary<string, string> item = operation == SqlChangeOperation.Delete
                    ? this._primaryKeyColumns.ToDictionary(col => col, col => row[col])
                    : this._userTableColumns.ToDictionary(col => col, col => row[col]);

                changes.Add(new SqlChange<T>(operation, JsonConvert.DeserializeObject<T>(JsonConvert.SerializeObject(item))));
            }

            return changes;
        }

        /// <summary>
        /// Gets the change associated with this row (either an insert, update or delete).
        /// </summary>
        /// <param name="row">The (combined) row from the change table and worker table</param>
        /// <exception cref="InvalidDataException">Thrown if the value of the "SYS_CHANGE_OPERATION" column is none of "I", "U", or "D"</exception>
        /// <returns>SqlChangeOperation.Insert for an insert, SqlChangeOperation.Update for an update, and SqlChangeOperation.Delete for a delete</returns>
        private static SqlChangeOperation GetChangeOperation(Dictionary<string, string> row)
        {
            string operation = row["SYS_CHANGE_OPERATION"];

            return operation switch
            {
                "I" => SqlChangeOperation.Insert,
                "U" => SqlChangeOperation.Update,
                "D" => SqlChangeOperation.Delete,
                _ => throw new InvalidDataException($"Invalid change type encountered in change table row: {row}"),
            };
        }

        /// <summary>
        /// Builds the command to update the global state table in the case of a new minimum valid version number.
        /// Sets the LastSyncVersion for this _userTableName to be the new minimum valid version number.
        /// </summary>
        /// <param name="connection">The connection to add to the returned SqlCommand</param>
        /// <param name="transaction">The transaction to add to the returned SqlCommand</param>
        /// <returns>The SqlCommand populated with the query and appropriate parameters</returns>
        private SqlCommand BuildUpdateTablesPreInvocation(SqlConnection connection, SqlTransaction transaction)
        {
            string updateTablesPreInvocationQuery = $@"
                DECLARE @min_valid_version bigint;
                SET @min_valid_version = CHANGE_TRACKING_MIN_VALID_VERSION({this._userTableId});

                DECLARE @last_sync_version bigint;
                SELECT @last_sync_version = LastSyncVersion
                FROM {SqlTriggerConstants.GlobalStateTableName}
                WHERE UserFunctionID = '{this._userFunctionId}' AND UserTableID = {this._userTableId};
                
                IF @last_sync_version < @min_valid_version
                    UPDATE {SqlTriggerConstants.GlobalStateTableName}
                    SET LastSyncVersion = @min_valid_version
                    WHERE UserFunctionID = '{this._userFunctionId}' AND UserTableID = {this._userTableId};
            ";

            return new SqlCommand(updateTablesPreInvocationQuery, connection, transaction);
        }

        /// <summary>
        /// Builds the query to check for changes on the user's table (<see cref="RunChangeConsumptionLoopAsync()"/>).
        /// </summary>
        /// <param name="connection">The connection to add to the returned SqlCommand</param>
        /// <param name="transaction">The transaction to add to the returned SqlCommand</param>
        /// <returns>The SqlCommand populated with the query and appropriate parameters</returns>
        private SqlCommand BuildGetChangesCommand(SqlConnection connection, SqlTransaction transaction)
        {
            string selectList = string.Join(", ", this._userTableColumns.Select(col => $"u.{col}"));
            string userTableJoinCondition = string.Join(" AND ", this._primaryKeyColumns.Select(col => $"c.{col} = u.{col}"));
            string workerTableJoinCondition = string.Join(" AND ", this._primaryKeyColumns.Select(col => $"c.{col} = w.{col}"));

            string getChangesQuery = $@"
                DECLARE @last_sync_version bigint;
                SELECT @last_sync_version = LastSyncVersion
                FROM {SqlTriggerConstants.GlobalStateTableName}
                WHERE UserFunctionID = '{this._userFunctionId}' AND UserTableID = {this._userTableId};

                SELECT TOP {BatchSize}
                    {selectList},
                    c.SYS_CHANGE_VERSION, c.SYS_CHANGE_OPERATION,
                    w.ChangeVersion, w.AttemptCount, w.LeaseExpirationTime
                FROM CHANGETABLE (CHANGES {this._userTableName}, @last_sync_version) AS c
                LEFT OUTER JOIN {this._workerTableName} AS w WITH (TABLOCKX) ON {workerTableJoinCondition}
                LEFT OUTER JOIN {this._userTableName} AS u ON {userTableJoinCondition}
                WHERE
                    (w.LeaseExpirationTime IS NULL AND (w.ChangeVersion IS NULL OR w.ChangeVersion < c.SYS_CHANGE_VERSION) OR
                        w.LeaseExpirationTime < SYSDATETIME()) AND
                    (w.AttemptCount IS NULL OR w.AttemptCount < {MaxAttemptCount})
                ORDER BY c.SYS_CHANGE_VERSION ASC;
            ";

            return new SqlCommand(getChangesQuery, connection, transaction);
        }

        /// <summary>
        /// Builds the query to acquire leases on the rows in "_rows" if changes are detected in the user's table
        /// (<see cref="RunChangeConsumptionLoopAsync()"/>).
        /// </summary>
        /// <param name="connection">The connection to add to the returned SqlCommand</param>
        /// <param name="transaction">The transaction to add to the returned SqlCommand</param>
        /// <returns>The SqlCommand populated with the query and appropriate parameters</returns>
        private SqlCommand BuildAcquireLeasesCommand(SqlConnection connection, SqlTransaction transaction)
        {
            var acquireLeasesQuery = new StringBuilder();

            for (int index = 0; index < this._rows.Count; index++)
            {
                string valuesList = string.Join(", ", this._primaryKeyColumns.Select(col => $"@{col}_{index}"));
                string changeVersion = this._rows[index]["SYS_CHANGE_VERSION"];

                acquireLeasesQuery.Append($@"
                    IF NOT EXISTS (SELECT * FROM {this._workerTableName} WITH (TABLOCKX) WHERE {this._rowMatchConditions[index]})
                        INSERT INTO {this._workerTableName} WITH (TABLOCKX)
                        VALUES ({valuesList}, {changeVersion}, 1, DATEADD(second, {LeaseIntervalInSeconds}, SYSDATETIME()));
                    ELSE
                        UPDATE {this._workerTableName} WITH (TABLOCKX)
                        SET
                            ChangeVersion = {changeVersion},
                            AttemptCount = AttemptCount + 1,
                            LeaseExpirationTime = DATEADD(second, {LeaseIntervalInSeconds}, SYSDATETIME())
                        WHERE {this._rowMatchConditions[index]};
                ");
            }

            return this.GetSqlCommandWithParameters(acquireLeasesQuery.ToString(), connection, transaction);
        }

        /// <summary>
        /// Builds the query to renew leases on the rows in "_rows" (<see cref="RenewLeasesAsync(CancellationToken)"/>).
        /// </summary>
        /// <param name="connection">The connection to add to the returned SqlCommand</param>
        /// <returns>The SqlCommand populated with the query and appropriate parameters</returns>
        private SqlCommand BuildRenewLeasesCommand(SqlConnection connection)
        {
            var renewLeasesQuery = new StringBuilder();

            for (int index = 0; index < this._rows.Count; index++)
            {
                renewLeasesQuery.Append($@"
                    UPDATE {this._workerTableName} WITH (TABLOCKX)
                    SET LeaseExpirationTime = DATEADD(second, {LeaseIntervalInSeconds}, SYSDATETIME())
                    WHERE {this._rowMatchConditions[index]};
                ");
            }

            return this.GetSqlCommandWithParameters(renewLeasesQuery.ToString(), connection, null);
        }

        /// <summary>
        /// Builds the query to release leases on the rows in "_rows" after successful invocation of the user's function
        /// (<see cref="RunChangeConsumptionLoopAsync()"/>).
        /// </summary>
        /// <param name="connection">The connection to add to the returned SqlCommand</param>
        /// <param name="transaction">The transaction to add to the returned SqlCommand</param>
        /// <returns>The SqlCommand populated with the query and appropriate parameters</returns>
        private SqlCommand BuildReleaseLeasesCommand(SqlConnection connection, SqlTransaction transaction)
        {
            var releaseLeasesQuery = new StringBuilder("DECLARE @current_change_version bigint;\n");

            for (int index = 0; index < this._rows.Count; index++)
            {
                string changeVersion = this._rows[index]["SYS_CHANGE_VERSION"];

                releaseLeasesQuery.Append($@"
                    SELECT @current_change_version = ChangeVersion
                    FROM {this._workerTableName} WITH (TABLOCKX)
                    WHERE {this._rowMatchConditions[index]};

                    IF @current_change_version <= {changeVersion}
                        UPDATE {this._workerTableName} WITH (TABLOCKX) 
                        SET ChangeVersion = {changeVersion}, AttemptCount = 0, LeaseExpirationTime = NULL
                        WHERE {this._rowMatchConditions[index]};
                ");
            }

            return this.GetSqlCommandWithParameters(releaseLeasesQuery.ToString(), connection, transaction);
        }

        /// <summary>
        /// Builds the command to update the global version number in _globalStateTable after successful invocation of
        /// the user's function. If the global version number is updated, also cleans the worker table and removes all
        /// rows for which ChangeVersion <= newLastSyncVersion.
        /// </summary>
        /// <param name="connection">The connection to add to the returned SqlCommand</param>
        /// <param name="transaction">The transaction to add to the returned SqlCommand</param>
        /// <param name="newLastSyncVersion">The new LastSyncVersion to store in the _globalStateTable for this _userTableName</param>
        /// <returns>The SqlCommand populated with the query and appropriate parameters</returns>
        private SqlCommand BuildUpdateTablesPostInvocation(SqlConnection connection, SqlTransaction transaction, long newLastSyncVersion)
        {
            string workerTableJoinCondition = string.Join(" AND ", this._primaryKeyColumns.Select(col => $"c.{col} = w.{col}"));

            // TODO: Need to think through all cases to ensure the query below is correct, especially with use of < vs <=.
            string updateTablesPostInvocationQuery = $@"
                DECLARE @current_last_sync_version bigint;
                SELECT @current_last_sync_version = LastSyncVersion
                FROM {SqlTriggerConstants.GlobalStateTableName}
                WHERE UserFunctionID = '{this._userFunctionId}' AND UserTableID = {this._userTableId};

                DECLARE @unprocessed_changes bigint;
                SELECT @unprocessed_changes = COUNT(*) FROM (
                    SELECT c.SYS_CHANGE_VERSION
                    FROM CHANGETABLE(CHANGES {this._userTableName}, @current_last_sync_version) AS c
                    LEFT OUTER JOIN {this._workerTableName} AS w WITH (TABLOCKX) ON {workerTableJoinCondition}
                    WHERE
                        c.SYS_CHANGE_VERSION <= {newLastSyncVersion} AND
                        ((w.ChangeVersion IS NULL OR w.ChangeVersion != c.SYS_CHANGE_VERSION OR w.LeaseExpirationTime IS NOT NULL) AND
                        (w.AttemptCount IS NULL OR w.AttemptCount < {MaxAttemptCount}))) AS Changes

                IF @unprocessed_changes = 0 AND @current_last_sync_version < {newLastSyncVersion}
                BEGIN
                    UPDATE {SqlTriggerConstants.GlobalStateTableName}
                    SET LastSyncVersion = {newLastSyncVersion}
                    WHERE UserFunctionID = '{this._userFunctionId}' AND UserTableID = {this._userTableId};

                    DELETE FROM {this._workerTableName} WITH (TABLOCKX) WHERE ChangeVersion <= {newLastSyncVersion};
                END
            ";

            return new SqlCommand(updateTablesPostInvocationQuery, connection, transaction);
        }

        /// <summary>
        /// Returns SqlCommand with SqlParameters added to it. Each parameter follows the format
        /// (@PrimaryKey_i, PrimaryKeyValue), where @PrimaryKey is the name of a primary key column, and PrimaryKeyValue
        /// is one of the row's value for that column. To distinguish between the parameters of different rows, each row
        /// will have a distinct value of i.
        /// </summary>
        /// <param name="commandText">SQL query string</param>
        /// <param name="connection">The connection to add to the returned SqlCommand</param>
        /// <param name="transaction">The transaction to add to the returned SqlCommand</param>
        /// <remarks>
        /// Ideally, we would have a map that maps from rows to a list of SqlCommands populated with their primary key
        /// values. The issue with this is that SQL doesn't seem to allow adding parameters to one collection when they
        /// are part of another. So, for example, since the SqlParameters are part of the list in the map, an exception
        /// is thrown if they are also added to the collection of a SqlCommand. The expected behavior seems to be to
        /// rebuild the SqlParameters each time.
        /// </remarks>
        private SqlCommand GetSqlCommandWithParameters(string commandText, SqlConnection connection, SqlTransaction transaction)
        {
            var command = new SqlCommand(commandText, connection, transaction);

            for (int index = 0; index < this._rows.Count; index++)
            {
                foreach (string col in this._primaryKeyColumns)
                {
                    command.Parameters.Add(new SqlParameter($"@{col}_{index}", this._rows[index][col]));
                }
            }

            return command;
        }

        private enum State
        {
            CheckingForChanges,
            ProcessingChanges,
        }
    }
}