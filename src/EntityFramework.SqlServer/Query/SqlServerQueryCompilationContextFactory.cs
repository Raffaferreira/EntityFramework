// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using JetBrains.Annotations;
using Microsoft.Data.Entity.Utilities;
using Microsoft.Framework.DependencyInjection;

namespace Microsoft.Data.Entity.Query
{
    public class SqlServerQueryCompilationContextFactory : IQueryCompilationContextFactory
    {
        private readonly IServiceProvider _serviceProvider;

        public SqlServerQueryCompilationContextFactory([NotNull] IServiceProvider serviceProvider)
        {
            Check.NotNull(serviceProvider, nameof(serviceProvider));

            _serviceProvider = serviceProvider;
        }

        public virtual QueryCompilationContext Create()
            => _serviceProvider.GetService<SqlServerQueryCompilationContext>();
    }
}
