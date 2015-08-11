// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using JetBrains.Annotations;
using Microsoft.Data.Entity.Query.Expressions;
using Microsoft.Data.Entity.Utilities;
using Microsoft.Framework.DependencyInjection;

namespace Microsoft.Data.Entity.Query.Sql
{
    public class SqlServerQuerySqlGeneratorFactory : ISqlQueryGeneratorFactory
    {
        private readonly IServiceProvider _serviceProvider;

        public SqlServerQuerySqlGeneratorFactory([NotNull] IServiceProvider serviceProvider)
        {
            Check.NotNull(serviceProvider, nameof(serviceProvider));

            _serviceProvider = serviceProvider;
        }

        public virtual ISqlQueryGenerator Create(SelectExpression selectExpression)
        {
            var querySqlGenerator = _serviceProvider.GetService<SqlServerQuerySqlGenerator>();
            querySqlGenerator.SelectExpression = selectExpression;

            return querySqlGenerator;
        }
    }
}
