// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using JetBrains.Annotations;
using Microsoft.Data.Entity.Infrastructure;
using Microsoft.Data.Entity.Internal;
using Microsoft.Data.Entity.Utilities;

namespace Microsoft.Data.Entity.Metadata
{
    public class EntityType : Annotatable, IEntityType
    {
        private static readonly char[] _simpleNameChars = { '.', '+' };

        private readonly SortedDictionary<IReadOnlyList<Property>, ForeignKey> _foreignKeys
            = new SortedDictionary<IReadOnlyList<Property>, ForeignKey>(PropertyListComparer.Instance);

        private readonly SortedDictionary<string, Navigation> _navigations
            = new SortedDictionary<string, Navigation>(StringComparer.Ordinal);

        private readonly SortedDictionary<IReadOnlyList<Property>, Index> _indexes
            = new SortedDictionary<IReadOnlyList<Property>, Index>(PropertyListComparer.Instance);

        private readonly SortedDictionary<string, Property> _properties;

        private readonly SortedDictionary<IReadOnlyList<Property>, Key> _keys
            = new SortedDictionary<IReadOnlyList<Property>, Key>(PropertyListComparer.Instance);

        private readonly object _typeOrName;

        private Key _primaryKey;
        private EntityType _baseType;

        private bool _useEagerSnapshots;

        /// <summary>
        ///     Creates a new metadata object representing an entity type associated with the given .NET type.
        /// </summary>
        /// <param name="type">The .NET entity type that this metadata object represents.</param>
        /// <param name="model">The model associated with this entity type.</param>
        public EntityType([NotNull] Type type, [NotNull] Model model)
            : this((object)Check.NotNull(type, nameof(type)),
                Check.NotNull(model, nameof(model)))
        {
            Check.ValidEntityType(type, nameof(type));

            _useEagerSnapshots = !this.HasPropertyChangingNotifications();
        }

        /// <summary>
        ///     Creates a new metadata object representing an entity type that will participate in shadow-state
        ///     such that there is no underlying .NET type corresponding to this metadata object.
        /// </summary>
        /// <param name="name">The name of the shadow-state entity type.</param>
        /// <param name="model">The model associated with this entity type.</param>
        public EntityType([NotNull] string name, [NotNull] Model model)
            : this((object)Check.NotEmpty(name, nameof(name)),
                Check.NotNull(model, nameof(model)))
        {
        }

        private EntityType(object typeOrName, Model model)
        {
            _typeOrName = typeOrName;

            Model = model;

            _properties = new SortedDictionary<string, Property>(new PropertyComparer(this));
        }

        public virtual Type ClrType => _typeOrName as Type;

        public virtual Model Model { get; }

        public virtual EntityType BaseType
        {
            get { return _baseType; }
            [param: CanBeNull]
            set
            {
                if (_baseType == value)
                {
                    return;
                }

                _baseType = null;
                if (value != null)
                {
                    if (HasClrType)
                    {
                        if (!value.HasClrType)
                        {
                            throw new InvalidOperationException(Strings.NonClrBaseType(this, value));
                        }

                        if (!value.ClrType.GetTypeInfo().IsAssignableFrom(ClrType.GetTypeInfo()))
                        {
                            throw new InvalidOperationException(Strings.NotAssignableClrBaseType(this, value, ClrType.Name, value.ClrType.Name));
                        }
                    }

                    if (!HasClrType
                        && value.HasClrType)
                    {
                        throw new InvalidOperationException(Strings.NonShadowBaseType(this, value));
                    }

                    if (value.InheritsFrom(this))
                    {
                        throw new InvalidOperationException(Strings.CircularInheritance(this, value));
                    }

                    if (_keys.Any())
                    {
                        throw new InvalidOperationException(Strings.DerivedEntityCannotHaveKeys(Name));
                    }

                    var baseProperties = value.Properties.Select(p => p.Name).ToArray();
                    var propertyCollisions = FindPropertyCollisions(baseProperties);
                    // ReSharper disable once PossibleMultipleEnumeration
                    if (propertyCollisions.Any())
                    {
                        throw new InvalidOperationException(
                            Strings.DuplicatePropertiesOnBase(
                                Name,
                                value.Name,
                                string.Join(", ", propertyCollisions.Select(p => p.Name))));
                    }

                    var baseNavigations = value.Navigations.Select(p => p.Name).ToArray();
                    var navigationCollisions = FindNavigationCollisions(baseNavigations);
                    // ReSharper disable once PossibleMultipleEnumeration
                    if (navigationCollisions.Any())
                    {
                        throw new InvalidOperationException(
                            Strings.DuplicateNavigationsOnBase(
                                Name,
                                value.Name,
                                string.Join(", ", navigationCollisions.Select(p => p.Name))));
                    }

                    _baseType = value;
                }

                PropertyMetadataChanged(null);
            }
        }

        private bool InheritsFrom(EntityType entityType)
        {
            var et = this;

            do
            {
                if (entityType == et)
                {
                    return true;
                }
            }
            while ((et = et.BaseType) != null);

            return false;
        }

        public virtual string Name
        {
            get
            {
                if (ClrType != null)
                {
                    return ClrType.DisplayName() ?? (string)_typeOrName;
                }
                return (string)_typeOrName;
            }
        }

        public virtual string DisplayName()
        {
            if (ClrType != null)
            {
                return ClrType.DisplayName(false) ?? ParseSimpleName();
            }
            return ParseSimpleName();
        }

        private string ParseSimpleName()
        {
            var fullName = (string)_typeOrName;
            var lastDot = fullName.LastIndexOfAny(_simpleNameChars);

            return lastDot > 0 ? fullName.Substring(lastDot + 1) : fullName;
        }

        public override string ToString() => Name;

        public virtual int PropertyCount => (BaseType?.PropertyCount ?? 0) + _properties.Count;

        public virtual bool HasClrType => ClrType != null;

        public virtual bool UseEagerSnapshots
        {
            get { return _useEagerSnapshots; }
            set
            {
                if (!value
                    && !this.HasPropertyChangingNotifications())
                {
                    throw new InvalidOperationException(Strings.EagerOriginalValuesRequired(Name));
                }

                _useEagerSnapshots = value;

                PropertyMetadataChanged(null);
            }
        }

        #region Primary and Candidate Keys

        [ContractAnnotation("null => null; notnull => notnull")]
        public virtual Key SetPrimaryKey([CanBeNull] Property property)
            => SetPrimaryKey(property == null ? null : new[] { property });

        [ContractAnnotation("null => null; notnull => notnull")]
        public virtual Key SetPrimaryKey([CanBeNull] IReadOnlyList<Property> properties)
        {
            if (BaseType != null)
            {
                throw new InvalidOperationException(Strings.DerivedEntityTypeKey(Name));
            }

            if (_primaryKey != null)
            {
                foreach (var property in _primaryKey.Properties)
                {
                    _properties.Remove(property.Name);
                }

                var oldPrimaryKey = _primaryKey;
                _primaryKey = null;

                foreach (var property in oldPrimaryKey.Properties)
                {
                    _properties.Add(property.Name, property);
                }
            }

            if (properties != null
                && properties.Count != 0)
            {
                var key = GetOrAddKey(properties);

                foreach (var property in key.Properties)
                {
                    _properties.Remove(property.Name);
                }

                _primaryKey = key;

                foreach (var property in key.Properties)
                {
                    property.IsNullable = null;
                    _properties.Add(property.Name, property);
                }
            }

            PropertyMetadataChanged(null);

            return _primaryKey;
        }

        [ContractAnnotation("null => null; notnull => notnull")]
        public virtual Key GetOrSetPrimaryKey([CanBeNull] Property property)
            => GetOrSetPrimaryKey(property == null ? null : new[] { property });

        [ContractAnnotation("null => null; notnull => notnull")]
        public virtual Key GetOrSetPrimaryKey([CanBeNull] IReadOnlyList<Property> properties)
        {
            Key primaryKey;
            if (properties != null
                && (primaryKey = FindPrimaryKey(properties)) != null)
            {
                return primaryKey;
            }

            return SetPrimaryKey(properties);
        }

        public virtual Key GetPrimaryKey() => (Key)((IEntityType)this).GetPrimaryKey();

        public virtual Key FindPrimaryKey()
            => BaseType?.FindPrimaryKey() ?? FindDeclaredPrimaryKey();

        public virtual Key FindDeclaredPrimaryKey() => _primaryKey;

        public virtual Key FindPrimaryKey([CanBeNull] IReadOnlyList<Property> properties)
        {
            Check.NotNull(properties, nameof(properties));

            if (BaseType != null)
            {
                return BaseType.FindPrimaryKey(properties);
            }

            if (_primaryKey != null
                && PropertyListComparer.Instance.Compare(_primaryKey.Properties, properties) == 0)
            {
                return _primaryKey;
            }

            return null;
        }

        public virtual Key AddKey([NotNull] Property property)
            => AddKey(new[] { property });

        public virtual Key AddKey([NotNull] IReadOnlyList<Property> properties)
        {
            Check.NotEmpty(properties, nameof(properties));
            if (BaseType != null)
            {
                throw new InvalidOperationException(Strings.DerivedEntityTypeKey(Name));
            }

            foreach (var property in properties)
            {
                if (FindProperty(property.Name) != property)
                {
                    throw new ArgumentException(Strings.KeyPropertiesWrongEntity(Property.Format(properties), Name));
                }
            }

            var key = FindKey(properties);
            if (key != null)
            {
                throw new InvalidOperationException(Strings.DuplicateKey(Property.Format(properties), Name));
            }

            key = new Key(properties);
            if (key.DeclaringEntityType != this)
            {
                throw new ArgumentException(Strings.KeyPropertiesWrongEntity(Property.Format(properties), Name));
            }

            _keys.Add(properties, key);

            return key;
        }

        public virtual Key GetOrAddKey([NotNull] Property property)
            => GetOrAddKey(new[] { property });

        public virtual Key GetOrAddKey([NotNull] IReadOnlyList<Property> properties)
            => FindKey(properties)
               ?? AddKey(properties);

        public virtual Key GetKey([NotNull] IReadOnlyList<Property> properties) => (Key)((IEntityType)this).GetKey(properties);

        public virtual Key FindKey([NotNull] Property property) => FindKey(new[] { property });

        public virtual Key FindKey([NotNull] IReadOnlyList<Property> properties)
        {
            Check.NotEmpty(properties, nameof(properties));

            return FindDeclaredKey(properties) ?? BaseType?.FindKey(properties);
        }

        public virtual Key FindDeclaredKey([NotNull] IReadOnlyList<Property> properties)
        {
            Check.NotEmpty(properties, nameof(properties));

            Key key;
            return _keys.TryGetValue(properties, out key)
                ? key
                : null;
        }

        public virtual IKey FindKey(IReadOnlyList<IProperty> properties)
            => FindKey(properties.Cast<Property>().ToList());

        public virtual Key RemoveKey([NotNull] Key key)
        {
            Check.NotNull(key, nameof(key));

            Key removedKey;
            if (_keys.TryGetValue(key.Properties, out removedKey))
            {
                CheckKeyNotInUse(removedKey);

                if (_primaryKey == removedKey)
                {
                    SetPrimaryKey((IReadOnlyList<Property>)null);
                }
                _keys.Remove(key.Properties);
                return removedKey;
            }

            return null;
        }

        private void CheckKeyNotInUse(Key key)
        {
            var foreignKey = Model?.FindReferencingForeignKeys(key).FirstOrDefault();
            if (foreignKey != null)
            {
                throw new InvalidOperationException(Strings.KeyInUse(Property.Format(key.Properties), Name, foreignKey.DeclaringEntityType.Name));
            }
        }

        public virtual IEnumerable<Key> GetKeys()
            => BaseType?.GetKeys().Concat(_keys.Values) ?? _keys.Values;

        #endregion

        #region Foreign Keys

        public virtual ForeignKey AddForeignKey(
            [NotNull] Property property,
            [NotNull] Key principalKey,
            [NotNull] EntityType principalEntityType)
            => AddForeignKey(new[] { property }, principalKey, principalEntityType);

        public virtual ForeignKey AddForeignKey(
            [NotNull] IReadOnlyList<Property> properties,
            [NotNull] Key principalKey,
            [NotNull] EntityType principalEntityType)
        {
            Check.NotEmpty(properties, nameof(properties));
            Check.NotNull(principalKey, nameof(principalKey));
            Check.NotNull(principalEntityType, nameof(principalEntityType));

            foreach (var property in properties)
            {
                if (FindProperty(property.Name) != property)
                {
                    throw new ArgumentException(Strings.ForeignKeyPropertiesWrongEntity(Property.Format(properties), Name));
                }
            }

            if (FindForeignKeyCollisions(new[] { properties }).Any())
            {
                throw new InvalidOperationException(Strings.DuplicateForeignKey(Property.Format(properties), Name));
            }

            var foreignKey = new ForeignKey(properties, principalKey, this, principalEntityType);

            if (principalEntityType.Model != Model)
            {
                throw new ArgumentException(Strings.EntityTypeModelMismatch(this, principalEntityType));
            }

            _foreignKeys.Add(properties, foreignKey);

            PropertyMetadataChanged(null);

            return foreignKey;
        }

        public virtual ForeignKey GetOrAddForeignKey(
            [NotNull] Property property, [NotNull] Key principalKey, [NotNull] EntityType principalEntityType)
            => GetOrAddForeignKey(new[] { property }, principalKey, principalEntityType);

        // Note: this will return an existing foreign key even if it doesn't have the same referenced key
        public virtual ForeignKey GetOrAddForeignKey(
            [NotNull] IReadOnlyList<Property> properties, [NotNull] Key principalKey, [NotNull] EntityType principalEntityType)
            => FindForeignKey(properties)
               ?? AddForeignKey(properties, principalKey, principalEntityType);

        public virtual ForeignKey GetForeignKey([NotNull] IReadOnlyList<Property> properties)
            => (ForeignKey)((IEntityType)this).GetForeignKey(properties);

        public virtual ForeignKey FindForeignKey([NotNull] Property property) => FindForeignKey(new[] { property });

        public virtual ForeignKey FindForeignKey([NotNull] IReadOnlyList<Property> properties)
        {
            Check.NotEmpty(properties, nameof(properties));

            return FindDeclaredForeignKey(properties) ?? BaseType?.FindForeignKey(properties);
        }

        public virtual ForeignKey FindDeclaredForeignKey([NotNull] IReadOnlyList<Property> properties)
        {
            Check.NotEmpty(properties, nameof(properties));

            ForeignKey foreignKey;
            return _foreignKeys.TryGetValue(properties, out foreignKey)
                ? foreignKey
                : null;
        }

        public virtual IForeignKey FindForeignKey(IReadOnlyList<IProperty> properties)
            => FindForeignKey(properties.Cast<Property>().ToList());

        private IEnumerable<ForeignKey> FindForeignKeys(IEnumerable<IReadOnlyList<Property>> properties)
            => properties.Select(FindForeignKey).Where(p => p != null);

        private IEnumerable<ForeignKey> FindDerivedForeignKeys(IEnumerable<IReadOnlyList<Property>> properties)
        {
            var searchForeignKeys = new HashSet<IReadOnlyList<Property>>(properties, PropertyListComparer.Instance);

            return this.GetDerivedTypes()
                .SelectMany(et => et.GetDeclaredForeignKeys()
                    .Where(foreignKey => searchForeignKeys.Contains(foreignKey.Properties)))
                .Cast<ForeignKey>();
        }

        private IEnumerable<ForeignKey> FindForeignKeyCollisions(IReadOnlyList<Property>[] properties)
            => FindForeignKeys(properties).Concat(FindDerivedForeignKeys(properties));

        public virtual ForeignKey RemoveForeignKey([NotNull] ForeignKey foreignKey)
        {
            Check.NotNull(foreignKey, nameof(foreignKey));

            ForeignKey removedFk;
            if (_foreignKeys.TryGetValue(foreignKey.Properties, out removedFk))
            {
                CheckForeignKeyNotInUse(removedFk);

                _foreignKeys.Remove(removedFk.Properties);
                return removedFk;
            }

            return null;
        }

        public virtual IEnumerable<ForeignKey> FindReferencingForeignKeys() => Model.FindReferencingForeignKeys(this);

        private void CheckForeignKeyNotInUse(ForeignKey foreignKey)
        {
            var navigation = foreignKey.PrincipalToDependent ?? foreignKey.DependentToPrincipal;

            if (navigation != null)
            {
                throw new InvalidOperationException(
                    Strings.ForeignKeyInUse(
                        Property.Format(foreignKey.Properties),
                        Name,
                        navigation.Name,
                        navigation.DeclaringEntityType.Name));
            }
        }

        public virtual IEnumerable<ForeignKey> GetForeignKeys()
            => BaseType?.GetForeignKeys().Concat(_foreignKeys.Values) ?? _foreignKeys.Values;

        #endregion

        #region Navigations

        public virtual Navigation AddNavigation([NotNull] string name, [NotNull] ForeignKey foreignKey, bool pointsToPrincipal)
        {
            Check.NotEmpty(name, nameof(name));
            Check.NotNull(foreignKey, nameof(foreignKey));

            if (FindNavigationCollisions(new[] { name }).Any())
            {
                throw new InvalidOperationException(Strings.DuplicateNavigation(name, Name));
            }

            var otherNavigation = Navigations.FirstOrDefault(
                n => n.ForeignKey == foreignKey
                     && n.PointsToPrincipal() == pointsToPrincipal);

            if (otherNavigation != null)
            {
                throw new InvalidOperationException(Strings.MultipleNavigations(name, otherNavigation.Name, Name));
            }

            var declaringTypeFromFk = pointsToPrincipal
                ? foreignKey.DeclaringEntityType
                : foreignKey.PrincipalEntityType;

            if (declaringTypeFromFk != this)
            {
                throw new InvalidOperationException(Strings.NavigationOnWrongEntityType(name, Name, declaringTypeFromFk.Name));
            }

            var navigation = new Navigation(name, foreignKey);
            _navigations.Add(name, navigation);
            if (pointsToPrincipal)
            {
                foreignKey.DependentToPrincipal = navigation;
            }
            else
            {
                foreignKey.PrincipalToDependent = navigation;
            }

            if (!HasClrType)
            {
                throw new InvalidOperationException(Strings.NavigationOnShadowEntity(navigation.Name, Name));
            }

            var clrProperty = ClrType.GetPropertiesInHierarchy(navigation.Name).FirstOrDefault();
            if (clrProperty == null)
            {
                throw new InvalidOperationException(Strings.NoClrNavigation(navigation.Name, Name));
            }

            var targetType = navigation.GetTargetType();
            if (!targetType.HasClrType)
            {
                throw new InvalidOperationException(Strings.NavigationToShadowEntity(navigation.Name, Name, targetType.Name));
            }

            var targetClrType = targetType.ClrType;
            Debug.Assert(targetClrType != null, "targetClrType != null");
            if (navigation.IsCollection())
            {
                var elementType = clrProperty.PropertyType.TryGetSequenceType();

                if (elementType == null
                    || !elementType.GetTypeInfo().IsAssignableFrom(targetClrType.GetTypeInfo()))
                {
                    throw new InvalidOperationException(Strings.NavigationCollectionWrongClrType(
                        navigation.Name, Name, clrProperty.PropertyType.FullName, targetClrType.FullName));
                }
            }
            else if (!clrProperty.PropertyType.GetTypeInfo().IsAssignableFrom(targetClrType.GetTypeInfo()))
            {
                throw new InvalidOperationException(Strings.NavigationSingleWrongClrType(
                    navigation.Name, Name, clrProperty.PropertyType.FullName, targetClrType.FullName));
            }

            return navigation;
        }

        public virtual Navigation GetOrAddNavigation([NotNull] string name, [NotNull] ForeignKey foreignKey, bool pointsToPrincipal)
            => FindNavigation(name) ?? AddNavigation(name, foreignKey, pointsToPrincipal);

        [NotNull]
        public virtual Navigation GetNavigation([NotNull] string name)
            => (Navigation)((IEntityType)this).GetNavigation(name);

        public virtual Navigation FindNavigation([NotNull] string name)
        {
            Check.NotEmpty(name, nameof(name));

            return FindDeclaredNavigation(name) ?? BaseType?.FindNavigation(name);
        }

        public virtual Navigation FindDeclaredNavigation([NotNull] string name)
        {
            Check.NotEmpty(name, nameof(name));

            Navigation navigation;
            return _navigations.TryGetValue(name, out navigation)
                ? navigation
                : null;
        }

        private IEnumerable<Navigation> FindNavigations(IEnumerable<string> names)
            => names.Select(FindNavigation).Where(p => p != null);

        private IEnumerable<Navigation> FindDerivedNavigations(IEnumerable<string> names)
        {
            var searchNavigations = new HashSet<string>(names);

            return this.GetDerivedTypes()
                .SelectMany(et => et.GetDeclaredNavigations()
                    .Where(navigation => searchNavigations.Contains(navigation.Name)))
                .Cast<Navigation>();
        }

        private IEnumerable<Navigation> FindNavigationCollisions(string[] propertyNames)
            => FindNavigations(propertyNames).Concat(FindDerivedNavigations(propertyNames));

        public virtual Navigation RemoveNavigation([NotNull] Navigation navigation)
        {
            Check.NotNull(navigation, nameof(navigation));

            Navigation removedNavigation;
            if (_navigations.TryGetValue(navigation.Name, out removedNavigation))
            {
                _navigations.Remove(navigation.Name);

                if (removedNavigation.PointsToPrincipal())
                {
                    removedNavigation.ForeignKey.DependentToPrincipal = null;
                }
                else
                {
                    removedNavigation.ForeignKey.PrincipalToDependent = null;
                }

                return removedNavigation;
            }

            return null;
        }

        public virtual IEnumerable<Navigation> Navigations
            => BaseType?.Navigations.Concat(_navigations.Values) ?? _navigations.Values;

        #endregion

        #region Indexes

        public virtual Index AddIndex([NotNull] Property property) => AddIndex(new[] { property });

        public virtual Index AddIndex([NotNull] IReadOnlyList<Property> properties)
        {
            Check.NotEmpty(properties, nameof(properties));

            foreach (var property in properties)
            {
                if (FindProperty(property.Name) != property)
                {
                    throw new ArgumentException(Strings.IndexPropertiesWrongEntity(Property.Format(properties), Name));
                }
            }

            if (FindIndexCollisions(new[] { properties }).Any())
            {
                throw new InvalidOperationException(Strings.DuplicateIndex(Property.Format(properties), Name));
            }

            var index = new Index(properties);
            if (index.DeclaringEntityType != this)
            {
                throw new ArgumentException(Strings.IndexPropertiesWrongEntity(Property.Format(properties), Name));
            }

            _indexes.Add(properties, index);

            return index;
        }

        public virtual Index GetOrAddIndex([NotNull] Property property)
            => GetOrAddIndex(new[] { property });

        public virtual Index GetOrAddIndex([NotNull] IReadOnlyList<Property> properties)
            => FindIndex(properties) ?? AddIndex(properties);

        public virtual Index GetIndex([NotNull] IReadOnlyList<Property> properties)
            => (Index)((IEntityType)this).GetIndex(properties);

        public virtual Index FindIndex([NotNull] Property property)
            => FindIndex(new[] { property });

        public virtual Index FindIndex([NotNull] IReadOnlyList<Property> properties)
        {
            Check.NotEmpty(properties, nameof(properties));

            return FindDeclaredIndex(properties) ?? BaseType?.FindIndex(properties);
        }

        public virtual Index FindDeclaredIndex([NotNull] IReadOnlyList<Property> properties)
        {
            Check.NotEmpty(properties, nameof(properties));

            Index index;
            return _indexes.TryGetValue(properties, out index)
                ? index
                : null;
        }

        public virtual IIndex FindIndex(IReadOnlyList<IProperty> properties)
            => FindIndex(properties.Cast<Property>().ToList());

        private IEnumerable<Index> FindIndexes(IEnumerable<IReadOnlyList<Property>> properties)
            => properties.Select(FindIndex).Where(p => p != null);

        private IEnumerable<Index> FindDerivedIndexes(IEnumerable<IReadOnlyList<Property>> properties)
        {
            var searchIndexes = new HashSet<IReadOnlyList<Property>>(properties, PropertyListComparer.Instance);

            return this.GetDerivedTypes()
                .SelectMany(et => et.GetDeclaredIndexes()
                    .Where(index => searchIndexes.Contains(index.Properties)))
                .Cast<Index>();
        }

        private IEnumerable<Index> FindIndexCollisions(IReadOnlyList<Property>[] properties)
            => FindIndexes(properties).Concat(FindDerivedIndexes(properties));

        public virtual Index RemoveIndex([NotNull] Index index)
        {
            Check.NotNull(index, nameof(index));

            Index removedIndex;
            if (_indexes.TryGetValue(index.Properties, out removedIndex))
            {
                _indexes.Remove(index.Properties);
                return removedIndex;
            }

            return null;
        }

        public virtual IEnumerable<Index> Indexes => BaseType?.Indexes.Concat(_indexes.Values) ?? _indexes.Values;

        #endregion

        #region Properties

        [NotNull]
        public virtual Property AddProperty([NotNull] PropertyInfo propertyInfo)
        {
            Check.NotNull(propertyInfo, nameof(propertyInfo));

            if (HasClrType)
            {
                if (!propertyInfo.DeclaringType.GetTypeInfo().IsAssignableFrom(ClrType.GetTypeInfo()))
                {
                    throw new ArgumentException(Strings.PropertyWrongEntityClrType(propertyInfo.Name, Name, propertyInfo.DeclaringType.Name));
                }
            }
            else
            {
                throw new InvalidOperationException(Strings.ClrPropertyOnShadowEntity(propertyInfo.Name, Name));
            }

            var property = AddProperty(propertyInfo.Name, propertyInfo.PropertyType);
            property.IsShadowProperty = false;
            return property;
        }

        [NotNull]
        public virtual Property AddProperty([NotNull] string name, [NotNull] Type propertyType)
        {
            var property = AddProperty(name);
            property.ClrType = propertyType;
            return property;
        }

        [NotNull]
        public virtual Property AddProperty([NotNull] string name)
        {
            Check.NotNull(name, nameof(name));

            if (FindPropertyCollisions(new[] { name }).Any())
            {
                throw new InvalidOperationException(Strings.DuplicateProperty(name, Name));
            }

            var property = new Property(name, this);

            _properties.Add(name, property);

            PropertyMetadataChanged(property);

            return property;
        }

        public virtual Property GetOrAddProperty([NotNull] PropertyInfo propertyInfo)
        {
            Check.NotNull(propertyInfo, nameof(propertyInfo));

            var property = FindProperty(propertyInfo);
            if (property != null)
            {
                property.ClrType = propertyInfo.PropertyType;
                property.IsShadowProperty = false;
                return property;
            }

            return AddProperty(propertyInfo);
        }

        [NotNull]
        public virtual Property GetOrAddProperty([NotNull] string name)
            => FindProperty(name) ?? AddProperty(name);

        [NotNull]
        public virtual Property GetProperty([NotNull] PropertyInfo propertyInfo)
        {
            Check.NotNull(propertyInfo, nameof(propertyInfo));

            return GetProperty(propertyInfo.Name);
        }

        [NotNull]
        public virtual Property GetProperty([NotNull] string propertyName)
            => (Property)((IEntityType)this).GetProperty(propertyName);

        public virtual Property FindProperty([NotNull] PropertyInfo propertyInfo)
        {
            Check.NotNull(propertyInfo, nameof(propertyInfo));

            return FindProperty(propertyInfo.Name);
        }

        public virtual Property FindProperty([NotNull] string propertyName)
        {
            Check.NotEmpty(propertyName, nameof(propertyName));

            return FindDeclaredProperty(propertyName) ?? BaseType?.FindProperty(propertyName);
        }

        public virtual Property FindDeclaredProperty([NotNull] string propertyName)
        {
            Check.NotEmpty(propertyName, nameof(propertyName));

            Property property;
            return _properties.TryGetValue(propertyName, out property)
                ? property
                : null;
        }

        private IEnumerable<Property> FindProperties(IEnumerable<string> propertyNames)
            => propertyNames.Select(FindProperty).Where(p => p != null);

        private IEnumerable<Property> FindDerivedProperties(IEnumerable<string> propertyNames)
        {
            var searchProperties = new HashSet<string>(propertyNames);

            return this.GetDerivedTypes()
                .SelectMany(et => et.GetDeclaredProperties()
                    .Where(property => searchProperties.Contains(property.Name)))
                .Cast<Property>();
        }

        private IEnumerable<Property> FindPropertyCollisions(string[] propertyNames)
            => FindProperties(propertyNames).Concat(FindDerivedProperties(propertyNames));

        public virtual Property RemoveProperty([NotNull] Property property)
        {
            Check.NotNull(property, nameof(property));

            Property removedProperty;
            if (_properties.TryGetValue(property.Name, out removedProperty))
            {
                if (GetKeys().Any(k => k.Properties.Contains(property))
                    || GetForeignKeys().Any(k => k.Properties.Contains(property))
                    || Indexes.Any(i => i.Properties.Contains(property)))
                {
                    throw new InvalidOperationException(Strings.PropertyInUse(property.Name, Name));
                }

                _properties.Remove(property.Name);

                PropertyMetadataChanged(property);

                return removedProperty;
            }

            return null;
        }

        public virtual IEnumerable<Property> Properties
            => BaseType?.Properties.Concat(_properties.Values) ?? _properties.Values;

        public virtual void PropertyMetadataChanged([CanBeNull] Property property)
        {
            var index = BaseType?.PropertyCount ?? 0;
            var shadowIndex = BaseType?.ShadowPropertyCount() ?? 0;
            var originalValueIndex = BaseType?.OriginalValueCount() ?? 0;

            foreach (var indexedProperty in _properties.Values)
            {
                indexedProperty.Index = index++;

                if (((IProperty)indexedProperty).IsShadowProperty)
                {
                    indexedProperty.SetShadowIndex(shadowIndex++);
                }

                indexedProperty.SetOriginalValueIndex(
                    RequiresOriginalValue(indexedProperty) ? originalValueIndex++ : -1);
            }

            foreach (var derivedType in this.GetDirectlyDerivedTypes())
            {
                derivedType.PropertyMetadataChanged(property);
            }
        }

        private bool RequiresOriginalValue(Property addedOrRemovedProperty)
        {
            return _useEagerSnapshots
                   || ((IProperty)addedOrRemovedProperty).IsConcurrencyToken
                   || GetForeignKeys().SelectMany(k => k.Properties).Contains(addedOrRemovedProperty);
        }

        #endregion

        #region Explicit interface implementations

        IEntityType IEntityType.BaseType => BaseType;

        IModel IEntityType.Model => Model;

        IKey IEntityType.FindPrimaryKey() => FindPrimaryKey();

        IProperty IEntityType.FindProperty(string propertyName) => FindProperty(propertyName);

        INavigation IEntityType.FindNavigation(string name) => FindNavigation(name);

        IEnumerable<IProperty> IEntityType.GetProperties() => Properties;

        IEnumerable<IForeignKey> IEntityType.GetForeignKeys() => GetForeignKeys();

        IEnumerable<INavigation> IEntityType.GetNavigations() => Navigations;

        IEnumerable<IIndex> IEntityType.GetIndexes() => Indexes;

        IEnumerable<IKey> IEntityType.GetKeys() => GetKeys();

        #endregion

        private class PropertyComparer : IComparer<string>
        {
            private readonly EntityType _entityType;

            public PropertyComparer(EntityType entityType)
            {
                _entityType = entityType;
            }

            public int Compare(string x, string y)
            {
                var properties = _entityType.FindPrimaryKey()?.Properties.Select(p => p.Name).ToList();

                var xIndex = -1;
                var yIndex = -1;

                if (properties != null)
                {
                    xIndex = properties.IndexOf(x);
                    yIndex = properties.IndexOf(y);
                }

                // Neither property is part of the Primary Key
                // Compare the property names
                if (xIndex == -1
                    && yIndex == -1)
                {
                    return StringComparer.Ordinal.Compare(x, y);
                }

                // Both properties are part of the Primary Key
                // Compare the indices
                if (xIndex > -1
                    && yIndex > -1)
                {
                    return xIndex - yIndex;
                }

                // One property is part of the Primary Key
                // The primary key property is first
                return (xIndex > yIndex)
                    ? -1
                    : 1;
            }
        }
    }
}
