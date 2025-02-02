﻿using Dotmim.Sync.Batch;
using Dotmim.Sync.Builders;
using Dotmim.Sync.Enumerations;
using Dotmim.Sync.Manager;
using Dotmim.Sync.Serialization;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;


namespace Dotmim.Sync
{
    public partial class LocalOrchestrator
    {
        /// <summary>
        /// Upgrade the database structure to reach the last DMS version
        /// </summary>
        public virtual async Task<ScopeInfo> UpgradeAsync(ScopeInfo scopeInfo = null, DbConnection connection = default, DbTransaction transaction = default, CancellationToken cancellationToken = default, IProgress<ProgressArgs> progress = null)
        {
            try
            {
                await using var runner = await this.GetConnectionAsync(SyncMode.Writing, SyncStage.Provisioning, connection, transaction, cancellationToken, progress).ConfigureAwait(false);
                // get Database builder
                var dbBuilder = this.Provider.GetDatabaseBuilder();

                // Initialize database if needed
                await dbBuilder.EnsureDatabaseAsync(runner.Connection, runner.Transaction).ConfigureAwait(false);

                var builder = this.GetScopeBuilder(this.Options.ScopeInfoTableName);

                if (scopeInfo == null || !scopeInfo.Schema.HasTables)
                {
                    var exists = await this.InternalExistsScopeInfoTableAsync(this.GetContext(), DbScopeType.Client, builder, runner.Connection, runner.Transaction, cancellationToken, progress).ConfigureAwait(false);

                    if (exists)
                        scopeInfo = await this.InternalGetScopeAsync<ScopeInfo>(this.GetContext(), DbScopeType.Client, this.ScopeName, builder, runner.Connection, runner.Transaction, cancellationToken, progress).ConfigureAwait(false);

                    if (scopeInfo == null)
                        throw new MissingClientScopeInfoException();
                }

                var setup = scopeInfo.Setup ?? this.Setup;
                // Get schema
                var schema = scopeInfo.Schema ?? await this.InternalGetSchemaAsync(this.GetContext(), setup, runner.Connection, runner.Transaction, cancellationToken, progress).ConfigureAwait(false);

                // If schema does not have any table, raise an exception
                if (schema == null || schema.Tables == null || !schema.HasTables)
                    throw new MissingTablesException();

                var r = await this.InternalUpgradeAsync(this.GetContext(), schema, setup, scopeInfo, builder, runner.Connection, runner.Transaction, cancellationToken, progress).ConfigureAwait(false);

                await runner.CommitAsync().ConfigureAwait(false);

                return r;
            }
            catch (Exception ex)
            {
                throw GetSyncError(ex);
            }
        }


        /// <summary>
        /// Check if we need to upgrade the Database Structure
        /// </summary>
        public virtual async Task<bool> NeedsToUpgradeAsync(ScopeInfo scopeInfo = null, DbConnection connection = default, DbTransaction transaction = default, CancellationToken cancellationToken = default, IProgress<ProgressArgs> progress = null)
        {
            try
            {
                await using var runner = await this.GetConnectionAsync(SyncMode.Reading, SyncStage.Provisioning, connection, transaction, cancellationToken, progress).ConfigureAwait(false);
                var builder = this.GetScopeBuilder(this.Options.ScopeInfoTableName);

                if (scopeInfo == null || !scopeInfo.Schema.HasTables)
                {
                    var exists = await this.InternalExistsScopeInfoTableAsync(this.GetContext(), DbScopeType.Client, builder, runner.Connection, runner.Transaction, cancellationToken, progress).ConfigureAwait(false);

                    if (exists)
                        scopeInfo = await this.InternalGetScopeAsync<ScopeInfo>(this.GetContext(), DbScopeType.Client, this.ScopeName, builder, runner.Connection, runner.Transaction, cancellationToken, progress).ConfigureAwait(false);

                    if (scopeInfo == null)
                        throw new MissingClientScopeInfoException();
                }

                return InternalNeedsToUpgrade(scopeInfo);
            }
            catch (Exception ex)
            {
                throw GetSyncError(ex);
            }
        }

        internal virtual bool InternalNeedsToUpgrade(ScopeInfo scopeInfo)
        {
            var version = SyncVersion.EnsureVersion(scopeInfo.Version);
            return version < SyncVersion.Current;
        }

        internal virtual async Task<ScopeInfo> InternalUpgradeAsync(SyncContext context, SyncSet schema, SyncSetup setup, ScopeInfo scopeInfo, DbScopeBuilder builder, DbConnection connection, DbTransaction transaction,
                        CancellationToken cancellationToken, IProgress<ProgressArgs> progress)
        {

            var version = SyncVersion.EnsureVersion(scopeInfo.Version);
            var oldVersion = version.Clone() as Version;

            // beta version
            if (version.Major == 0)
            {
                if (version.Minor <= 5)
                    version = await AutoUpgdrateToNewVersionAsync(context, new Version(0, 6, 0), connection, transaction, cancellationToken, progress).ConfigureAwait(false);

                if (version.Minor == 6 && version.Build == 0)
                    version = await UpgdrateTo601Async(context, schema, setup, connection, transaction, cancellationToken, progress).ConfigureAwait(false);

                if (version.Minor == 6 && version.Build == 1)
                    version = await UpgdrateTo602Async(context, schema, setup, connection, transaction, cancellationToken, progress).ConfigureAwait(false);

                if (version.Minor == 6 && version.Build >= 2)
                    version = await UpgdrateTo700Async(context, schema, setup, connection, transaction, cancellationToken, progress).ConfigureAwait(false);

                if (version.Minor == 7 && version.Build == 0)
                    version = await AutoUpgdrateToNewVersionAsync(context, new Version(0, 7, 1), connection, transaction, cancellationToken, progress).ConfigureAwait(false);

                if (version.Minor == 7 && version.Build == 1)
                    version = await AutoUpgdrateToNewVersionAsync(context, new Version(0, 7, 2), connection, transaction, cancellationToken, progress).ConfigureAwait(false);

                if (version.Minor == 7 && version.Build == 2)
                    version = await AutoUpgdrateToNewVersionAsync(context, new Version(0, 7, 3), connection, transaction, cancellationToken, progress).ConfigureAwait(false);

                if (version.Minor == 7 && version.Build >= 3)
                    version = await AutoUpgdrateToNewVersionAsync(context, new Version(0, 8, 0), connection, transaction, cancellationToken, progress).ConfigureAwait(false);

                if (version.Minor == 8 && version.Build == 0)
                    version = await UpgdrateTo801Async(context, schema, setup, connection, transaction, cancellationToken, progress).ConfigureAwait(false);

                if (version.Minor == 8 && version.Build == 1)
                    version = await AutoUpgdrateToNewVersionAsync(context, new Version(0, 9, 0), connection, transaction, cancellationToken, progress).ConfigureAwait(false);

                if (version.Minor == 9 && version.Build == 0)
                    version = await AutoUpgdrateToNewVersionAsync(context, new Version(0, 9, 1), connection, transaction, cancellationToken, progress).ConfigureAwait(false);

                if (version.Minor == 9 && version.Build == 1)
                    version = await UpgdrateTo093Async(context, schema, connection, transaction, cancellationToken, progress).ConfigureAwait(false);

                if (version.Minor == 9 && version.Build == 2)
                    version = await UpgdrateTo093Async(context, schema, connection, transaction, cancellationToken, progress).ConfigureAwait(false);

                if (version.Minor == 9 && version.Build == 3)
                    version = await UpgdrateTo094Async(context, schema, connection, transaction, cancellationToken, progress).ConfigureAwait(false);
            }

            if (oldVersion != version)
            {
                scopeInfo.Version = version.ToString();
                scopeInfo = await this.InternalSaveScopeAsync(context, DbScopeType.Client, scopeInfo, builder, connection, transaction, cancellationToken, progress).ConfigureAwait(false);
            }
            return scopeInfo;

        }

        private async Task<Version> UpgdrateTo601Async(SyncContext context, SyncSet schema, SyncSetup setup, DbConnection connection, DbTransaction transaction,
                        CancellationToken cancellationToken, IProgress<ProgressArgs> progress)
        {

            var newVersion = new Version(0, 6, 1);
            // Sorting tables based on dependencies between them
            var schemaTables = schema.Tables
                .SortByDependencies(tab => tab.GetRelations()
                    .Select(r => r.GetParentTable()));

            var message = $"Upgrade to {newVersion}:";
            await this.InterceptAsync(new UpgradeProgressArgs(context, message, newVersion, connection, transaction), progress, cancellationToken).ConfigureAwait(false);
            
            foreach (var schemaTable in schemaTables)
            {
                var tableBuilder = this.GetTableBuilder(schemaTable, setup);


                // Upgrade Select Initial Changes
                var exists = await InternalExistsStoredProcedureAsync(context, tableBuilder, DbStoredProcedureType.SelectInitializedChanges, connection, transaction, cancellationToken, progress).ConfigureAwait(false);
                if (exists)
                    await InternalDropStoredProcedureAsync(context, tableBuilder, DbStoredProcedureType.SelectInitializedChanges, connection, transaction, cancellationToken, progress).ConfigureAwait(false);
                await InternalCreateStoredProcedureAsync(context, tableBuilder, DbStoredProcedureType.SelectInitializedChanges, connection, transaction, cancellationToken, progress).ConfigureAwait(false);

                await this.InterceptAsync(new UpgradeProgressArgs(context, $"SelectInitializedChanges stored procedure for table {tableBuilder.TableDescription.GetFullName()} updated", newVersion, connection, transaction), progress, cancellationToken).ConfigureAwait(false);

                // Upgrade Select Initial Changes With Filter
                if (tableBuilder.TableDescription.GetFilter() != null)
                {
                    var existsWithFilter = await InternalExistsStoredProcedureAsync(context, tableBuilder, DbStoredProcedureType.SelectInitializedChangesWithFilters, connection, transaction, cancellationToken, progress).ConfigureAwait(false);
                    if (existsWithFilter)
                        await InternalDropStoredProcedureAsync(context, tableBuilder, DbStoredProcedureType.SelectInitializedChangesWithFilters, connection, transaction, cancellationToken, progress).ConfigureAwait(false);
                    await InternalCreateStoredProcedureAsync(context, tableBuilder, DbStoredProcedureType.SelectInitializedChangesWithFilters, connection, transaction, cancellationToken, progress).ConfigureAwait(false);

                    await this.InterceptAsync(new UpgradeProgressArgs(context, $"SelectInitializedChangesWithFilters stored procedure for table {tableBuilder.TableDescription.GetFullName()} updated", newVersion, connection, transaction), progress, cancellationToken).ConfigureAwait(false);
                }

            }

            return newVersion;
        }

        private async Task<Version> UpgdrateTo602Async(SyncContext context, SyncSet schema, SyncSetup setup, DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken, IProgress<ProgressArgs> progress)
        {

            var newVersion = new Version(0, 6, 2);

            var message = $"Upgrade to {newVersion}:";
            await this.InterceptAsync(new UpgradeProgressArgs(context, message, newVersion, connection, transaction), progress, cancellationToken).ConfigureAwait(false);

            // Update the "Update trigger" for all tables

            // Sorting tables based on dependencies between them
            var schemaTables = schema.Tables
                .SortByDependencies(tab => tab.GetRelations()
                    .Select(r => r.GetParentTable()));

            foreach (var schemaTable in schemaTables)
            {
                var tableBuilder = this.GetTableBuilder(schemaTable, setup);

                // Upgrade Select Initial Changes
                var exists = await InternalExistsTriggerAsync(context, tableBuilder, DbTriggerType.Update, connection, transaction, cancellationToken, progress).ConfigureAwait(false);
                if (exists)
                    await InternalDropTriggerAsync(context, tableBuilder, DbTriggerType.Update, connection, transaction, cancellationToken, progress).ConfigureAwait(false);
                await InternalCreateTriggerAsync(context, tableBuilder, DbTriggerType.Update, connection, transaction, cancellationToken, progress).ConfigureAwait(false);

                await this.InterceptAsync(new UpgradeProgressArgs(context, $"Update Trigger for table {tableBuilder.TableDescription.GetFullName()} updated", newVersion, connection, transaction), progress, cancellationToken).ConfigureAwait(false);
            }

            message = $"Upgrade to {newVersion} done.";
            await this.InterceptAsync(new UpgradeProgressArgs(context, message, newVersion, connection, transaction), progress, cancellationToken).ConfigureAwait(false);

            return newVersion;
        }

        private async Task<Version> UpgdrateTo700Async(SyncContext context, SyncSet schema, SyncSetup setup, DbConnection connection, DbTransaction transaction,
                             CancellationToken cancellationToken, IProgress<ProgressArgs> progress)
        {

            var newVersion = new Version(0, 7, 0);
            // Sorting tables based on dependencies between them

            var schemaTables = schema.Tables
                .SortByDependencies(tab => tab.GetRelations()
                    .Select(r => r.GetParentTable()));

            var message = $"Upgrade to {newVersion}:";
            await this.InterceptAsync(new UpgradeProgressArgs(context, message, newVersion, connection, transaction), progress, cancellationToken).ConfigureAwait(false);

            foreach (var schemaTable in schemaTables)
            {
                var tableBuilder = this.GetTableBuilder(schemaTable, setup);

                // Upgrade Reset stored procedure
                var exists = await InternalExistsStoredProcedureAsync(context, tableBuilder,
                    DbStoredProcedureType.Reset, connection, transaction, cancellationToken, progress).ConfigureAwait(false);
                if (exists)
                    await InternalDropStoredProcedureAsync(context, tableBuilder, DbStoredProcedureType.Reset, connection, transaction, cancellationToken, progress).ConfigureAwait(false);
                await InternalCreateStoredProcedureAsync(context, tableBuilder, DbStoredProcedureType.Reset, connection, transaction, cancellationToken, progress).ConfigureAwait(false);

                await this.InterceptAsync(new UpgradeProgressArgs(context, $"Reset stored procedure for table {tableBuilder.TableDescription.GetFullName()} updated", newVersion, connection, transaction), progress, cancellationToken).ConfigureAwait(false);

                // Upgrade Update stored procedure
                var existsUpdateSP = await InternalExistsStoredProcedureAsync(context, tableBuilder,
                    DbStoredProcedureType.UpdateRow, connection, transaction, cancellationToken, progress).ConfigureAwait(false);
                if (existsUpdateSP)
                    await InternalDropStoredProcedureAsync(context, tableBuilder, DbStoredProcedureType.UpdateRow, connection, transaction, cancellationToken, progress).ConfigureAwait(false);
                await InternalCreateStoredProcedureAsync(context, tableBuilder, DbStoredProcedureType.UpdateRow, connection, transaction, cancellationToken, progress).ConfigureAwait(false);

                await this.InterceptAsync(new UpgradeProgressArgs(context, $"Update stored procedure for table {tableBuilder.TableDescription.GetFullName()} updated", newVersion, connection, transaction), progress, cancellationToken).ConfigureAwait(false);

                // Upgrade Bulk Update stored procedure
                var existsBulkUpdateSP = await InternalExistsStoredProcedureAsync(context, tableBuilder,
                    DbStoredProcedureType.BulkUpdateRows, connection, transaction, cancellationToken, progress).ConfigureAwait(false);

                if (existsBulkUpdateSP)
                {
                    await InternalDropStoredProcedureAsync(context, tableBuilder, DbStoredProcedureType.BulkUpdateRows, connection, transaction, cancellationToken, progress).ConfigureAwait(false);
                    await InternalCreateStoredProcedureAsync(context, tableBuilder, DbStoredProcedureType.BulkUpdateRows, connection, transaction, cancellationToken, progress).ConfigureAwait(false);

                    await this.InterceptAsync(new UpgradeProgressArgs(context, $"Bulk Update stored procedure for table {tableBuilder.TableDescription.GetFullName()} updated", newVersion, connection, transaction), progress, cancellationToken).ConfigureAwait(false);
                }
            }
            return newVersion;
        }


        private async Task<Version> UpgdrateTo801Async(SyncContext context, SyncSet schema, SyncSetup setup, DbConnection connection, DbTransaction transaction,
                       CancellationToken cancellationToken, IProgress<ProgressArgs> progress)
        {

            var newVersion = new Version(0, 8, 1);
            // Sorting tables based on dependencies between them

            var schemaTables = schema.Tables
                .SortByDependencies(tab => tab.GetRelations()
                    .Select(r => r.GetParentTable()));

            var message = $"Upgrade to {newVersion}:";
            await this.InterceptAsync(new UpgradeProgressArgs(context, message, newVersion, connection, transaction), progress, cancellationToken).ConfigureAwait(false);

            foreach (var schemaTable in schemaTables)
            {
                var tableBuilder = this.GetTableBuilder(schemaTable, setup);
                await InternalCreateStoredProceduresAsync(context, true, tableBuilder, connection, transaction, cancellationToken, progress).ConfigureAwait(false);

                await this.InterceptAsync(new UpgradeProgressArgs(context, $"ALL Stored procedures for table {tableBuilder.TableDescription.GetFullName()} updated", newVersion, connection, transaction), progress, cancellationToken).ConfigureAwait(false);
            }

            return newVersion;
        }

        private async Task<Version> UpgdrateTo093Async(SyncContext context, SyncSet schema, DbConnection connection, DbTransaction transaction,
                        CancellationToken cancellationToken, IProgress<ProgressArgs> progress)
        {
            var newVersion = new Version(0, 9, 3);
            // Sorting tables based on dependencies between them

            var schemaTables = schema.Tables
                .SortByDependencies(tab => tab.GetRelations()
                    .Select(r => r.GetParentTable()));

            var message = $"Upgrade to {newVersion}:";
            await this.InterceptAsync(new UpgradeProgressArgs(context, message, newVersion, connection, transaction), progress, cancellationToken).ConfigureAwait(false);

            var provision = SyncProvision.StoredProcedures | SyncProvision.Triggers;

            await this.DeprovisionAsync(provision, null, connection, transaction, cancellationToken, progress).ConfigureAwait(false);
            await this.ProvisionAsync(schema, provision, false, null, connection, transaction, cancellationToken, progress).ConfigureAwait(false);

            return newVersion;
        }
        private async Task<Version> UpgdrateTo094Async(SyncContext context, SyncSet schema, DbConnection connection, DbTransaction transaction,
                        CancellationToken cancellationToken, IProgress<ProgressArgs> progress)
        {
            var newVersion = new Version(0, 9, 4);
            // Sorting tables based on dependencies between them

            var schemaTables = schema.Tables
                .SortByDependencies(tab => tab.GetRelations()
                    .Select(r => r.GetParentTable()));

            var message = $"Upgrade to {newVersion}:";
            await this.InterceptAsync(new UpgradeProgressArgs(context, message, newVersion, connection, transaction), progress, cancellationToken).ConfigureAwait(false);

            var provision = SyncProvision.StoredProcedures | SyncProvision.Triggers;

            await this.DeprovisionAsync(provision, null, connection, transaction, cancellationToken, progress).ConfigureAwait(false);
            await this.ProvisionAsync(schema, provision, false, null, connection, transaction, cancellationToken, progress).ConfigureAwait(false);

            return newVersion;
        }
        private async Task<Version> AutoUpgdrateToNewVersionAsync(SyncContext context, Version newVersion, DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken, IProgress<ProgressArgs> progress)
        {
            var message = $"Upgrade to {newVersion}:";
            await this.InterceptAsync(new UpgradeProgressArgs(context, message, newVersion, connection, transaction), progress, cancellationToken).ConfigureAwait(false);
            return newVersion;
        }
    }
}
