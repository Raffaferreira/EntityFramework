// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using Microsoft.Data.Entity.Query.Expressions;

namespace Microsoft.Data.Entity.Query.Sql
{
    public interface ISqlQueryGeneratorFactory
    {
        ISqlQueryGenerator Create(SelectExpression selectExpression);
    }
}
