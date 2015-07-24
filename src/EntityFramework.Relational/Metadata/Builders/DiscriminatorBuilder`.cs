// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using JetBrains.Annotations;
using Microsoft.Data.Entity.Utilities;

namespace Microsoft.Data.Entity.Metadata.Builders
{
    public class DiscriminatorBuilder<TDiscriminator>
    {
        public DiscriminatorBuilder([NotNull] DiscriminatorBuilder builder)
        {
            Check.NotNull(builder, nameof(builder));

            Builder = builder;
        }

        private DiscriminatorBuilder Builder { get; }

        public virtual DiscriminatorBuilder HasValue([NotNull] Type entityType, [CanBeNull] TDiscriminator value)
            => Builder.HasValue(entityType, value);

        public virtual DiscriminatorBuilder HasValue([NotNull] string entityTypeName, [CanBeNull] TDiscriminator value)
            => Builder.HasValue(entityTypeName, value);
    }
}
