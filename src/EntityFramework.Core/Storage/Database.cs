// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using JetBrains.Annotations;
using Microsoft.Data.Entity.ChangeTracking.Internal;
using Microsoft.Data.Entity.Internal;
using Microsoft.Data.Entity.Metadata;
using Microsoft.Data.Entity.Query;
using Microsoft.Data.Entity.Utilities;
using Microsoft.Framework.Logging;
using Remotion.Linq;

namespace Microsoft.Data.Entity.Storage
{
    public abstract class Database : IDatabase
    {
        private readonly LazyRef<ILogger> _logger;
        private readonly IQueryCompilationContextFactory _compilationContextFactory;

        protected Database(
            [NotNull] IModel model,
            [NotNull] ILoggerFactory loggerFactory,
            [NotNull] IQueryCompilationContextFactory compilationContextFactory)
        {
            Check.NotNull(model, nameof(model));
            Check.NotNull(loggerFactory, nameof(loggerFactory));
            Check.NotNull(compilationContextFactory, nameof(compilationContextFactory));

            Model = model;

            _logger = new LazyRef<ILogger>(loggerFactory.CreateLogger<Database>);
            _compilationContextFactory = compilationContextFactory;
        }

        public virtual IModel Model { get; }

        public virtual ILogger Logger => _logger.Value;

        public abstract int SaveChanges(IReadOnlyList<InternalEntityEntry> entries);

        public abstract Task<int> SaveChangesAsync(
            IReadOnlyList<InternalEntityEntry> entries,
            CancellationToken cancellationToken = default(CancellationToken));

        public virtual Func<QueryContext, IEnumerable<TResult>> CompileQuery<TResult>(QueryModel queryModel)
            => _compilationContextFactory.Create()
                .CreateQueryModelVisitor()
                .CreateQueryExecutor<TResult>(
                    Check.NotNull(queryModel, nameof(queryModel)));

        public virtual Func<QueryContext, IAsyncEnumerable<TResult>> CompileAsyncQuery<TResult>(QueryModel queryModel)
            => _compilationContextFactory.Create()
                .CreateQueryModelVisitor()
                .CreateAsyncQueryExecutor<TResult>(
                    Check.NotNull(queryModel, nameof(queryModel)));
    }
}
