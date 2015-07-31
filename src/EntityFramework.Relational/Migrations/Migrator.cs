// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using JetBrains.Annotations;
using Microsoft.Data.Entity.Internal;
using Microsoft.Data.Entity.Metadata;
using Microsoft.Data.Entity.Migrations.Builders;
using Microsoft.Data.Entity.Migrations.History;
using Microsoft.Data.Entity.Migrations.Infrastructure;
using Microsoft.Data.Entity.Migrations.Operations;
using Microsoft.Data.Entity.Migrations.Sql;
using Microsoft.Data.Entity.Storage;
using Microsoft.Data.Entity.Storage.Commands;
using Microsoft.Data.Entity.Update;
using Microsoft.Data.Entity.Utilities;
using Microsoft.Framework.Logging;
using Strings = Microsoft.Data.Entity.Relational.Internal.Strings;

namespace Microsoft.Data.Entity.Migrations
{
    public class Migrator : IMigrator
    {
        public const string InitialDatabase = "0";

        private readonly IMigrationAssembly _migrationAssembly;
        private readonly IHistoryRepository _historyRepository;
        private readonly IRelationalDatabaseCreator _databaseCreator;
        private readonly IMigrationSqlGenerator _migrationSqlGenerator;
        private readonly ISqlStatementExecutor _executor;
        private readonly IRelationalConnection _connection;
        private readonly IModelDiffer _modelDiffer;
        private readonly IModel _model;
        private readonly IMigrationIdGenerator _idGenerator;
        private readonly IUpdateSqlGenerator _sqlGenerator;
        private readonly LazyRef<ILogger> _logger;
        private readonly IMigrationModelFactory _modelFactory;

        public Migrator(
            [NotNull] IMigrationAssembly migrationAssembly,
            [NotNull] IHistoryRepository historyRepository,
            [NotNull] IDatabaseCreator databaseCreator,
            [NotNull] IMigrationSqlGenerator migrationSqlGenerator,
            [NotNull] ISqlStatementExecutor executor,
            [NotNull] IRelationalConnection connection,
            [NotNull] IModelDiffer modelDiffer,
            [NotNull] IModel model,
            [NotNull] IMigrationIdGenerator idGenerator,
            [NotNull] IUpdateSqlGenerator sqlGenerator,
            [NotNull] ILoggerFactory loggerFactory,
            [NotNull] IMigrationModelFactory modelFactory)
        {
            Check.NotNull(migrationAssembly, nameof(migrationAssembly));
            Check.NotNull(historyRepository, nameof(historyRepository));
            Check.NotNull(databaseCreator, nameof(databaseCreator));
            Check.NotNull(migrationSqlGenerator, nameof(migrationSqlGenerator));
            Check.NotNull(executor, nameof(executor));
            Check.NotNull(connection, nameof(connection));
            Check.NotNull(modelDiffer, nameof(modelDiffer));
            Check.NotNull(model, nameof(model));
            Check.NotNull(idGenerator, nameof(idGenerator));
            Check.NotNull(sqlGenerator, nameof(sqlGenerator));
            Check.NotNull(loggerFactory, nameof(loggerFactory));
            Check.NotNull(modelFactory, nameof(modelFactory));

            _migrationAssembly = migrationAssembly;
            _historyRepository = historyRepository;
            _databaseCreator = (IRelationalDatabaseCreator)databaseCreator;
            _migrationSqlGenerator = migrationSqlGenerator;
            _executor = executor;
            _connection = connection;
            _modelDiffer = modelDiffer;
            _model = model;
            _idGenerator = idGenerator;
            _sqlGenerator = sqlGenerator;
            _logger = new LazyRef<ILogger>(loggerFactory.CreateLogger<Migrator>);
            _modelFactory = modelFactory;
        }

        protected virtual string ProductVersion =>
            typeof(Migrator).GetTypeInfo().Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;

        public virtual IReadOnlyList<Migration> GetUnappliedMigrations()
        {
            var appliedMigrations = _historyRepository.GetAppliedMigrations();

            return _migrationAssembly.Migrations.Where(
                m => !appliedMigrations.Any(
                    e => string.Equals(e.MigrationId, m.Id, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        public virtual bool HasPendingModelChanges() => _modelDiffer.HasDifferences(_migrationAssembly.LastModel, _model);

        public virtual void ApplyMigrations(string targetMigration = null)
        {
            var connection = _connection.DbConnection;
            _logger.Value.LogVerbose(Strings.UsingConnection(connection.Database, connection.DataSource));

            var migrations = _migrationAssembly.Migrations;
            var appliedMigrationEntries = _historyRepository.GetAppliedMigrations();

            var appliedMigrations = new List<Migration>();
            var unappliedMigrations = new List<Migration>();
            foreach (var migraion in migrations)
            {
                if (appliedMigrationEntries.Any(
                    e => string.Equals(e.MigrationId, migraion.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    appliedMigrations.Add(migraion);
                }
                else
                {
                    unappliedMigrations.Add(migraion);
                }
            }

            IReadOnlyList<Migration> migrationsToApply;
            IReadOnlyList<Migration> migrationsToRevert;
            if (string.IsNullOrEmpty(targetMigration))
            {
                migrationsToApply = unappliedMigrations;
                migrationsToRevert = new Migration[0];
            }
            else if (targetMigration == InitialDatabase)
            {
                migrationsToApply = new Migration[0];
                migrationsToRevert = appliedMigrations.OrderByDescending(m => m.Id).ToList();
            }
            else
            {
                targetMigration = _idGenerator.ResolveId(targetMigration, migrations);
                migrationsToApply = unappliedMigrations
                    .Where(m => string.Compare(m.Id, targetMigration, StringComparison.OrdinalIgnoreCase) <= 0)
                    .ToList();
                migrationsToRevert = appliedMigrations
                    .Where(m => string.Compare(m.Id, targetMigration, StringComparison.OrdinalIgnoreCase) > 0)
                    .OrderByDescending(m => m.Id)
                    .ToList();
            }

            bool first;
            var checkFirst = true;
            foreach (var migration in migrationsToApply)
            {
                var batches = ApplyMigration(migration).ToList();

                first = false;
                if (checkFirst)
                {
                    first = migration == migrations[0];
                    if (first && !_historyRepository.Exists())
                    {
                        // TODO: Prepend to first batch instead
                        batches.Insert(0, new RelationalCommand(_historyRepository.GetCreateScript()));
                    }

                    checkFirst = false;
                }

                _logger.Value.LogInformation(Strings.ApplyingMigration(migration.Id));

                Execute(batches, first);
            }

            for (var i = 0; i < migrationsToRevert.Count; i++)
            {
                var migration = migrationsToRevert[i];

                _logger.Value.LogInformation(Strings.RevertingMigration(migration.Id));

                Execute(RevertMigration(
                    migration,
                    i != migrationsToRevert.Count - 1
                        ? migrationsToRevert[i + 1]
                        : null));
            }
        }

        public virtual string ScriptMigrations(
            string fromMigrationName,
            string toMigrationName,
            bool idempotent = false)
        {
            var migrations = _migrationAssembly.Migrations;

            if (string.IsNullOrEmpty(fromMigrationName))
            {
                fromMigrationName = InitialDatabase;
            }
            else if (fromMigrationName != InitialDatabase)
            {
                fromMigrationName = _idGenerator.ResolveId(fromMigrationName, migrations);
            }

            if (string.IsNullOrEmpty(toMigrationName))
            {
                toMigrationName = migrations.Last().Id;
            }
            else if (toMigrationName != InitialDatabase)
            {
                toMigrationName = _idGenerator.ResolveId(toMigrationName, migrations);
            }

            var builder = new IndentedStringBuilder();

            // If going up...
            if (string.Compare(fromMigrationName, toMigrationName, StringComparison.OrdinalIgnoreCase) <= 0)
            {
                var migrationsToApply = migrations.Where(
                    m => string.Compare(m.Id, fromMigrationName, StringComparison.OrdinalIgnoreCase) > 0
                         && string.Compare(m.Id, toMigrationName, StringComparison.OrdinalIgnoreCase) <= 0);
                var checkFirst = true;
                foreach (var migration in migrationsToApply)
                {
                    if (checkFirst)
                    {
                        if (migration == migrations[0])
                        {
                            builder.AppendLine(_historyRepository.GetCreateIfNotExistsScript());
                            builder.AppendLine(_sqlGenerator.BatchSeparator);
                            builder.AppendLine();
                        }

                        checkFirst = false;
                    }

                    _logger.Value.LogVerbose(Strings.GeneratingUp(migration.Id));

                    foreach (var command in ApplyMigration(migration))
                    {
                        if (idempotent)
                        {
                            builder.AppendLine(_historyRepository.GetBeginIfNotExistsScript(migration.Id));
                            using (builder.Indent())
                            {
                                builder.AppendLines(command.CommandText);
                            }
                            builder.AppendLine(_historyRepository.GetEndIfScript());
                        }
                        else
                        {
                            builder.Append(command.CommandText);
                        }

                        builder.AppendLine(_sqlGenerator.BatchSeparator);
                        builder.AppendLine();
                    }
                }
            }
            else // If going down...
            {
                var migrationsToRevert = migrations
                    .Where(
                        m => string.Compare(m.Id, toMigrationName, StringComparison.OrdinalIgnoreCase) > 0
                             && string.Compare(m.Id, fromMigrationName, StringComparison.OrdinalIgnoreCase) <= 0)
                    .OrderByDescending(m => m.Id)
                    .ToList();
                for (var i = 0; i < migrationsToRevert.Count; i++)
                {
                    var migration = migrationsToRevert[i];
                    var previousMigration = i != migrationsToRevert.Count - 1
                        ? migrationsToRevert[i + 1]
                        : null;

                    _logger.Value.LogVerbose(Strings.GeneratingDown(migration.Id));

                    foreach (var command in RevertMigration(migration, previousMigration))
                    {
                        if (idempotent)
                        {
                            builder.AppendLine(_historyRepository.GetBeginIfExistsScript(migration.Id));
                            using (builder.Indent())
                            {
                                builder.AppendLines(command.CommandText);
                            }
                            builder.AppendLine(_historyRepository.GetEndIfScript());
                        }
                        else
                        {
                            builder.Append(command.CommandText);
                        }

                        builder.AppendLine(_sqlGenerator.BatchSeparator);
                        builder.AppendLine();
                    }
                }
            }

            return builder.ToString();
        }

        protected virtual IReadOnlyList<RelationalCommand> ApplyMigration([NotNull] Migration migration)
        {
            Check.NotNull(migration, nameof(migration));

            var migrationBuilder = new MigrationBuilder();
            migration.Up(migrationBuilder);

            var operations = migrationBuilder.Operations.ToList();
            // TODO: Append to batch instead
            operations.Add(
                new SqlOperation { Sql = _historyRepository.GetInsertScript(new HistoryRow(migration.Id, ProductVersion)) });

            var targetModel = _modelFactory.Create(migration.BuildTargetModel);

            return _migrationSqlGenerator.Generate(operations, targetModel);
        }

        protected virtual IReadOnlyList<RelationalCommand> RevertMigration(
            [NotNull] Migration migration,
            [CanBeNull] Migration previousMigration)
        {
            Check.NotNull(migration, nameof(migration));

            var migrationBuilder = new MigrationBuilder();
            migration.Down(migrationBuilder);
            var operations = migrationBuilder.Operations.ToList();

            // TODO: Append to batch instead
            operations.Add(new SqlOperation { Sql = _historyRepository.GetDeleteScript(migration.Id) });

            var targetModel = previousMigration != null
                ? _modelFactory.Create(previousMigration.BuildTargetModel)
                : null;

            return _migrationSqlGenerator.Generate(operations, targetModel);
        }

        protected virtual void Execute([NotNull] IEnumerable<RelationalCommand> relationalCommands, bool ensureDatabase = false)
        {
            Check.NotNull(relationalCommands, nameof(relationalCommands));

            if (ensureDatabase && !_databaseCreator.Exists())
            {
                _databaseCreator.Create();
            }

            using (var transaction = _connection.BeginTransaction())
            {
                _executor.ExecuteNonQuery(_connection, relationalCommands);
                transaction.Commit();
            }
        }
    }
}
