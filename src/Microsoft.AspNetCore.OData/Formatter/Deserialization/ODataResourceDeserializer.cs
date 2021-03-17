// Copyright (c) Microsoft Corporation.  All rights reserved.
// Licensed under the MIT License.  See License.txt in the project root for license information.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using Microsoft.AspNetCore.OData.Formatter.Wrapper;
using Microsoft.AspNetCore.OData.Routing;
using Microsoft.AspNetCore.OData.Formatter.Value;
using Microsoft.OData;
using Microsoft.OData.Edm;
using Microsoft.AspNetCore.OData.Edm;
using System.Threading.Tasks;
using Microsoft.AspNetCore.OData.Deltas;
using Microsoft.AspNetCore.OData.Extensions;
using Microsoft.AspNetCore.OData.Routing.Parser;
using Microsoft.OData.UriParser;

namespace Microsoft.AspNetCore.OData.Formatter.Deserialization
{
    /// <summary>
    /// Represents an <see cref="ODataDeserializer"/> for reading OData resource payloads.
    /// </summary>
    public class ODataResourceDeserializer : ODataEdmTypeDeserializer
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ODataResourceDeserializer"/> class.
        /// </summary>
        /// <param name="deserializerProvider">The deserializer provider to use to read inner objects.</param>
        public ODataResourceDeserializer(ODataDeserializerProvider deserializerProvider)
            : base(ODataPayloadKind.Resource, deserializerProvider)
        {
        }

        /// <inheritdoc />
        public override async Task<object> ReadAsync(ODataMessageReader messageReader, Type type, ODataDeserializerContext readContext)
        {
            if (messageReader == null)
            {
                throw new ArgumentNullException(nameof(messageReader));
            }

            if (readContext == null)
            {
                throw new ArgumentNullException(nameof(readContext));
            }

            IEdmTypeReference edmType = readContext.GetEdmType(type);
            Contract.Assert(edmType != null);

            if (!edmType.IsStructured())
            {
                throw Error.Argument("type", SRResources.ArgumentMustBeOfType, "Structured");
            }

            IEdmStructuredTypeReference structuredType = edmType.AsStructured();

            IEdmNavigationSource navigationSource = null;
            if (structuredType.IsEntity())
            {
                if (readContext.Path == null)
                {
                    throw Error.Argument("readContext", SRResources.ODataPathMissing);
                }

                navigationSource = readContext.Path.GetNavigationSource();
                if (navigationSource == null)
                {
                    throw new SerializationException(SRResources.NavigationSourceMissingDuringDeserialization);
                }
            }

            ODataReader odataReader = await messageReader
                .CreateODataResourceReaderAsync(navigationSource, structuredType.StructuredDefinition()).ConfigureAwait(false);
            ODataResourceWrapper topLevelResource = await odataReader.ReadResourceOrResourceSetAsync().ConfigureAwait(false)
                as ODataResourceWrapper;
            Contract.Assert(topLevelResource != null);

            return ReadInline(topLevelResource, structuredType, readContext);
        }

        /// <inheritdoc />
        public sealed override object ReadInline(object item, IEdmTypeReference edmType, ODataDeserializerContext readContext)
        {
            if (edmType == null)
            {
                throw new ArgumentNullException(nameof(edmType));
            }

            if (edmType.IsComplex() && item == null)
            {
                return null;
            }

            if (item == null)
            {
                throw new ArgumentNullException(nameof(item));
            }

            if (!edmType.IsStructured())
            {
                throw Error.Argument("edmType", SRResources.ArgumentMustBeOfType, "Entity or Complex");
            }

            ODataResourceWrapper resourceWrapper = item as ODataResourceWrapper;
            if (resourceWrapper == null)
            {
                throw Error.Argument("item", SRResources.ArgumentMustBeOfType, typeof(ODataResource).Name);
            }

            // Recursion guard to avoid stack overflows
            RuntimeHelpers.EnsureSufficientExecutionStack();

            resourceWrapper = UpdateResourceWrapper(resourceWrapper, readContext);

            return ReadResource(resourceWrapper, edmType.AsStructured(), readContext);
        }

        /// <summary>
        /// Deserializes the given <paramref name="resourceWrapper"/> under the given <paramref name="readContext"/>.
        /// </summary>
        /// <param name="resourceWrapper">The OData resource to deserialize.</param>
        /// <param name="structuredType">The type of the resource to deserialize.</param>
        /// <param name="readContext">The deserializer context.</param>
        /// <returns>The deserialized resource.</returns>
        public virtual object ReadResource(ODataResourceWrapper resourceWrapper, IEdmStructuredTypeReference structuredType,
            ODataDeserializerContext readContext)
        {
            if (resourceWrapper == null)
            {
                throw new ArgumentNullException(nameof(resourceWrapper));
            }

            if (readContext == null)
            {
                throw new ArgumentNullException(nameof(readContext));
            }

            if (!String.IsNullOrEmpty(resourceWrapper.Resource.TypeName) && structuredType.FullName() != resourceWrapper.Resource.TypeName)
            {
                // received a derived type in a base type deserializer. delegate it to the appropriate derived type deserializer.
                IEdmModel model = readContext.Model;

                if (model == null)
                {
                    throw Error.Argument("readContext", SRResources.ModelMissingFromReadContext);
                }

                IEdmStructuredType actualType = model.FindType(resourceWrapper.Resource.TypeName) as IEdmStructuredType;
                if (actualType == null)
                {
                    throw new ODataException(Error.Format(SRResources.ResourceTypeNotInModel, resourceWrapper.Resource.TypeName));
                }

                if (actualType.IsAbstract)
                {
                    string message = Error.Format(SRResources.CannotInstantiateAbstractResourceType, resourceWrapper.Resource.TypeName);
                    throw new ODataException(message);
                }

                IEdmTypeReference actualStructuredType;
                IEdmEntityType actualEntityType = actualType as IEdmEntityType;
                if (actualEntityType != null)
                {
                    actualStructuredType = new EdmEntityTypeReference(actualEntityType, isNullable: false);
                }
                else
                {
                    actualStructuredType = new EdmComplexTypeReference(actualType as IEdmComplexType, isNullable: false);
                }

                ODataEdmTypeDeserializer deserializer = DeserializerProvider.GetEdmTypeDeserializer(actualStructuredType);
                if (deserializer == null)
                {
                    throw new SerializationException(
                        Error.Format(SRResources.TypeCannotBeDeserialized, actualEntityType.FullName()));
                }

                object resource = deserializer.ReadInline(resourceWrapper, actualStructuredType, readContext);

                EdmStructuredObject structuredObject = resource as EdmStructuredObject;
                if (structuredObject != null)
                {
                    structuredObject.ExpectedEdmType = structuredType.StructuredDefinition();
                }

                return resource;
            }
            else
            {
                object resource = CreateResourceInstance(structuredType, readContext);
                ApplyResourceProperties(resource, resourceWrapper, structuredType, readContext);
                return resource;
            }
        }

        /// <summary>
        /// Creates a new instance of the backing CLR object for the given resource type.
        /// </summary>
        /// <param name="structuredType">The EDM type of the resource to create.</param>
        /// <param name="readContext">The deserializer context.</param>
        /// <returns>The created CLR object.</returns>
        public virtual object CreateResourceInstance(IEdmStructuredTypeReference structuredType, ODataDeserializerContext readContext)
        {
            if (structuredType == null)
            {
                throw new ArgumentNullException(nameof(structuredType));
            }

            if (readContext == null)
            {
                throw new ArgumentNullException(nameof(readContext));
            }

            IEdmModel model = readContext.Model;
            if (model == null)
            {
                throw Error.Argument("readContext", SRResources.ModelMissingFromReadContext);
            }

            if (readContext.IsNoClrType)
            {
                if (structuredType.IsEntity())
                {
                    return new EdmEntityObject(structuredType.AsEntity());
                }

                return new EdmComplexObject(structuredType.AsComplex());
            }
            else
            {
                Type clrType = model.GetClrType(structuredType);
                if (clrType == null)
                {
                    throw new ODataException(
                        Error.Format(SRResources.MappingDoesNotContainResourceType, structuredType.FullName()));
                }

                if (readContext.IsDeltaOfT)
                {
                    IEnumerable<string> structuralProperties = structuredType.StructuralProperties()
                        .Select(edmProperty => model.GetClrPropertyName(edmProperty));

                    if (structuredType.IsOpen())
                    {
                        PropertyInfo dynamicDictionaryPropertyInfo = model.GetDynamicPropertyDictionary(
                            structuredType.StructuredDefinition());

                        return Activator.CreateInstance(readContext.ResourceType, clrType, structuralProperties,
                            dynamicDictionaryPropertyInfo);
                    }
                    else
                    {
                        return Activator.CreateInstance(readContext.ResourceType, clrType, structuralProperties);
                    }
                }
                else
                {
                    return Activator.CreateInstance(clrType);
                }
            }
        }

        /// <summary>
        /// Deserializes the nested properties from <paramref name="resourceWrapper"/> into <paramref name="resource"/>.
        /// </summary>
        /// <param name="resource">The object into which the nested properties should be read.</param>
        /// <param name="resourceWrapper">The resource object containing the nested properties.</param>
        /// <param name="structuredType">The type of the resource.</param>
        /// <param name="readContext">The deserializer context.</param>
        public virtual void ApplyNestedProperties(object resource, ODataResourceWrapper resourceWrapper,
            IEdmStructuredTypeReference structuredType, ODataDeserializerContext readContext)
        {
            if (resourceWrapper == null)
            {
                throw new ArgumentNullException(nameof(resourceWrapper));
            }

            foreach (ODataNestedResourceInfoWrapper nestedResourceInfo in resourceWrapper.NestedResourceInfos)
            {
                ApplyNestedProperty(resource, nestedResourceInfo, structuredType, readContext);
            }
        }

        /// <summary>
        /// Deserializes the nested property from <paramref name="resourceInfoWrapper"/> into <paramref name="resource"/>.
        /// </summary>
        /// <param name="resource">The object into which the nested property should be read.</param>
        /// <param name="resourceInfoWrapper">The nested resource info.</param>
        /// <param name="structuredType">The type of the resource.</param>
        /// <param name="readContext">The deserializer context.</param>
        public virtual void ApplyNestedProperty(object resource, ODataNestedResourceInfoWrapper resourceInfoWrapper,
             IEdmStructuredTypeReference structuredType, ODataDeserializerContext readContext)
        {
            if (resource == null)
            {
                throw new ArgumentNullException(nameof(resource));
            }

            if (resourceInfoWrapper == null)
            {
                throw new ArgumentNullException(nameof(resourceInfoWrapper));
            }

            IEdmProperty edmProperty = structuredType.FindProperty(resourceInfoWrapper.NestedResourceInfo.Name);
            if (edmProperty == null)
            {
                if (!structuredType.IsOpen())
                {
                    throw new ODataException(
                        Error.Format(SRResources.NestedPropertyNotfound, resourceInfoWrapper.NestedResourceInfo.Name,
                            structuredType.FullName()));
                }
            }

            ODataItemWrapper itemWrapper;
            if (resourceInfoWrapper.NestedResourceSet != null)
            {
                // Let's test resource set first
                itemWrapper = resourceInfoWrapper.NestedResourceSet;
            }
            else if (resourceInfoWrapper.NestedLinks == null)
            {
                itemWrapper = resourceInfoWrapper.NestedResource;
            }
            else
            {
                // Let's support declared property
                Contract.Assert(edmProperty != null);

                if (edmProperty.Type.IsCollection())
                {
                    IEdmCollectionTypeReference edmCollectionTypeReference = edmProperty.Type.AsCollection();
                    itemWrapper = CreateResourceSetWrapper(edmCollectionTypeReference, resourceInfoWrapper.NestedLinks, readContext);
                }
                else
                {
                    itemWrapper = CreateResourceWrapper(edmProperty.Type, resourceInfoWrapper.NestedLinks[0], readContext);
                }
            }

            // It's nested resource set.
            // So far, delta resource set is not supported yet.
            ODataResourceSetWrapper resourceSetWrapper = itemWrapper as ODataResourceSetWrapper;
            if (resourceSetWrapper != null)
            {
                if (edmProperty == null)
                {
                    ApplyDynamicResourceSetInNestedProperty(resourceInfoWrapper.NestedResourceInfo.Name,
                        resource, structuredType, resourceSetWrapper, readContext);
                }
                else
                {
                    ApplyResourceSetInNestedProperty(edmProperty, resource, resourceSetWrapper, readContext);
                }

                return;
            }

            // it's a nested resource
            if (itemWrapper == null)
            {
                if (edmProperty == null)
                {
                    // for the dynamic, OData.net has a bug. see https://github.com/OData/odata.net/issues/977
                    ApplyDynamicResourceInNestedProperty(resourceInfoWrapper.NestedResourceInfo.Name, resource, structuredType, null, readContext);
                }
                else
                {
                    ApplyResourceInNestedProperty(edmProperty, resource, null, readContext);
                }
            }
            else
            {
                // It must be resource by now. deleted resource is not supported yet.
                ODataResourceWrapper resourceWrapper = itemWrapper as ODataResourceWrapper;
                if (resourceWrapper != null)
                {
                    if (edmProperty == null)
                    {
                        ApplyDynamicResourceInNestedProperty(resourceInfoWrapper.NestedResourceInfo.Name, resource,
                            structuredType, resourceWrapper, readContext);
                    }
                    else
                    {
                        ApplyResourceInNestedProperty(edmProperty, resource, resourceWrapper, readContext);
                    }
                }
            }
        }

        private ODataResourceSetWrapper CreateResourceSetWrapper(IEdmCollectionTypeReference edmPropertyType,
            IList<ODataEntityReferenceLinkWrapper> refLinks, ODataDeserializerContext readContext)
        {
            ODataResourceSet resourceSet = new ODataResourceSet
            {
                TypeName = edmPropertyType.FullName(),
            };

            IEdmTypeReference elementType = edmPropertyType.ElementType();
            ODataResourceSetWrapper resourceSetWrapper = new ODataResourceSetWrapper(resourceSet);
            foreach (ODataEntityReferenceLinkWrapper refLinkWrapper in refLinks)
            {
                ODataResourceWrapper resourceWrapper = CreateResourceWrapper(elementType, refLinkWrapper, readContext);
                resourceSetWrapper.Resources.Add(resourceWrapper);
            }

            return resourceSetWrapper;
        }

        private ODataResourceWrapper CreateResourceWrapper(IEdmTypeReference edmPropertyType, ODataEntityReferenceLinkWrapper refLink, ODataDeserializerContext readContext)
        {
            Contract.Assert(readContext != null);

            ODataResource resource = new ODataResource
            {
                TypeName = edmPropertyType.FullName(),
            };

            Uri serviceRootUri = null;
            if (refLink.EntityReferenceLink.Url.IsAbsoluteUri)
            {
                string serviceRoot = readContext.Request.CreateODataLink();
                serviceRootUri = new Uri(serviceRoot, UriKind.Absolute);
            }

            var request = readContext.Request;
            IEdmModel model = readContext.Model;
            DefaultODataPathParser pathParser = new DefaultODataPathParser();
            var path = pathParser.Parse(model, serviceRootUri, refLink.EntityReferenceLink.Url, request.GetSubServiceProvider());

            KeySegment keySegment = path.OfType<KeySegment>().LastOrDefault();
            if (keySegment == null)
            {
                return null;
            }

            resource.Properties = keySegment.Keys.Select(k => new ODataProperty
            {
                Name = k.Key,
                Value = k.Value
            });

            return new ODataResourceWrapper(resource);
        }

        private ODataResourceWrapper UpdateResourceWrapper(ODataResourceWrapper resourceWrapper, ODataDeserializerContext readContext)
        {
            Contract.Assert(readContext != null);

            if (resourceWrapper == null || resourceWrapper.Resource == null)
            {
                return resourceWrapper;
            }

            if (resourceWrapper.Resource.Id == null)
            {
                return resourceWrapper;
            }

            Uri id = resourceWrapper.Resource.Id;
            Uri serviceRootUri = null;
            if (id.IsAbsoluteUri)
            {
                string serviceRoot = readContext.Request.CreateODataLink();
                serviceRootUri = new Uri(serviceRoot, UriKind.Absolute);
            }

            var request = readContext.Request;
            IEdmModel model = readContext.Model;
            DefaultODataPathParser pathParser = new DefaultODataPathParser();
            var path = pathParser.Parse(model, serviceRootUri, id, request.GetSubServiceProvider());

            KeySegment keySegment = path.OfType<KeySegment>().LastOrDefault();
            if (keySegment == null)
            {
                return null;
            }

            resourceWrapper.Resource.Properties = resourceWrapper.Resource.Properties.Concat(
                keySegment.Keys.Select(k => new ODataProperty
                {
                    Name = k.Key,
                    Value = k.Value
                }));

            return resourceWrapper;
        }

        /// <summary>
        /// Deserializes the structural properties from <paramref name="resourceWrapper"/> into <paramref name="resource"/>.
        /// </summary>
        /// <param name="resource">The object into which the structural properties should be read.</param>
        /// <param name="resourceWrapper">The resource object containing the structural properties.</param>
        /// <param name="structuredType">The type of the resource.</param>
        /// <param name="readContext">The deserializer context.</param>
        public virtual void ApplyStructuralProperties(object resource, ODataResourceWrapper resourceWrapper,
            IEdmStructuredTypeReference structuredType, ODataDeserializerContext readContext)
        {
            if (resourceWrapper == null)
            {
                throw new ArgumentNullException(nameof(resourceWrapper));
            }

            foreach (ODataProperty property in resourceWrapper.Resource.Properties)
            {
                ApplyStructuralProperty(resource, property, structuredType, readContext);
            }
        }

        /// <summary>
        /// Deserializes the given <paramref name="structuralProperty"/> into <paramref name="resource"/>.
        /// </summary>
        /// <param name="resource">The object into which the structural property should be read.</param>
        /// <param name="structuralProperty">The structural property.</param>
        /// <param name="structuredType">The type of the resource.</param>
        /// <param name="readContext">The deserializer context.</param>
        public virtual void ApplyStructuralProperty(object resource, ODataProperty structuralProperty,
            IEdmStructuredTypeReference structuredType, ODataDeserializerContext readContext)
        {
            if (resource == null)
            {
                throw new ArgumentNullException(nameof(resource));
            }

            if (structuralProperty == null)
            {
                throw new ArgumentNullException(nameof(structuralProperty));
            }

            if (structuredType == null)
            {
                throw new ArgumentNullException(nameof(structuredType));
            }

            if (readContext == null)
            {
                throw new ArgumentNullException(nameof(readContext));
            }

            DeserializationHelpers.ApplyProperty(structuralProperty, structuredType, resource, DeserializerProvider, readContext);
        }

        private void ApplyResourceProperties(object resource, ODataResourceWrapper resourceWrapper,
            IEdmStructuredTypeReference structuredType, ODataDeserializerContext readContext)
        {
            ApplyStructuralProperties(resource, resourceWrapper, structuredType, readContext);
            ApplyNestedProperties(resource, resourceWrapper, structuredType, readContext);
        }

        private void ApplyResourceInNestedProperty(IEdmProperty nestedProperty, object resource,
            ODataResourceWrapper resourceWrapper, ODataDeserializerContext readContext)
        {
            Contract.Assert(nestedProperty != null);
            Contract.Assert(resource != null);
            Contract.Assert(readContext != null);

            if (readContext.IsDeltaOfT)
            {
                IEdmNavigationProperty navigationProperty = nestedProperty as IEdmNavigationProperty;
                if (navigationProperty != null)
                {
                    string message = Error.Format(SRResources.CannotPatchNavigationProperties, navigationProperty.Name,
                        navigationProperty.DeclaringEntityType().FullName());
                    throw new ODataException(message);
                }
            }

            object value = ReadNestedResourceInline(resourceWrapper, nestedProperty.Type, readContext);

            // First resolve Data member alias or annotation, then set the regular
            // or delta resource accordingly.
            string propertyName = readContext.Model.GetClrPropertyName(nestedProperty);

            DeserializationHelpers.SetProperty(resource, propertyName, value);
        }

        private void ApplyDynamicResourceInNestedProperty(string propertyName, object resource, IEdmStructuredTypeReference resourceStructuredType,
            ODataResourceWrapper resourceWrapper, ODataDeserializerContext readContext)
        {
            Contract.Assert(resource != null);
            Contract.Assert(readContext != null);

            object value = null;
            if (resourceWrapper != null)
            {
                IEdmSchemaType elementType = readContext.Model.FindDeclaredType(resourceWrapper.Resource.TypeName);
                IEdmTypeReference edmTypeReference = elementType.ToEdmTypeReference(true);

                value = ReadNestedResourceInline(resourceWrapper, edmTypeReference, readContext);
            }

            DeserializationHelpers.SetDynamicProperty(resource, propertyName, value,
                resourceStructuredType.StructuredDefinition(), readContext.Model);
        }

        private object ReadNestedResourceInline(ODataResourceWrapper resourceWrapper, IEdmTypeReference edmType, ODataDeserializerContext readContext)
        {
            Contract.Assert(edmType != null);
            Contract.Assert(readContext != null);

            if (resourceWrapper == null)
            {
                return null;
            }

            ODataEdmTypeDeserializer deserializer = DeserializerProvider.GetEdmTypeDeserializer(edmType);
            if (deserializer == null)
            {
                throw new SerializationException(Error.Format(SRResources.TypeCannotBeDeserialized, edmType.FullName()));
            }

            IEdmStructuredTypeReference structuredType = edmType.AsStructured();

            var nestedReadContext = new ODataDeserializerContext
            {
                Path = readContext.Path,
                Model = readContext.Model,
                Request = readContext.Request,
                TimeZone = readContext.TimeZone
            };

            Type clrType;
            if (readContext.IsNoClrType)
            {
                clrType = structuredType.IsEntity()
                    ? typeof(EdmEntityObject)
                    : typeof(EdmComplexObject);
            }
            else
            {
                clrType = readContext.Model.GetClrType(structuredType);

                if (clrType == null)
                {
                    throw new ODataException(
                        Error.Format(SRResources.MappingDoesNotContainResourceType, structuredType.FullName()));
                }
            }

            nestedReadContext.ResourceType = readContext.IsDeltaOfT
                ? typeof(Delta<>).MakeGenericType(clrType)
                : clrType;
            return deserializer.ReadInline(resourceWrapper, edmType, nestedReadContext);
        }

        private void ApplyResourceSetInNestedProperty(IEdmProperty nestedProperty, object resource,
            ODataResourceSetWrapper resourceSetWrapper, ODataDeserializerContext readContext)
        {
            Contract.Assert(nestedProperty != null);
            Contract.Assert(resource != null);
            Contract.Assert(readContext != null);

            if (readContext.IsDeltaOfT)
            {
                IEdmNavigationProperty navigationProperty = nestedProperty as IEdmNavigationProperty;
                if (navigationProperty != null)
                {
                    string message = Error.Format(SRResources.CannotPatchNavigationProperties, navigationProperty.Name,
                        navigationProperty.DeclaringEntityType().FullName());
                    throw new ODataException(message);
                }
            }

            object value = ReadNestedResourceSetInline(resourceSetWrapper, nestedProperty.Type, readContext);

            string propertyName = readContext.Model.GetClrPropertyName(nestedProperty);
            DeserializationHelpers.SetCollectionProperty(resource, nestedProperty, value, propertyName);
        }

        private void ApplyDynamicResourceSetInNestedProperty(string propertyName, object resource, IEdmStructuredTypeReference structuredType,
            ODataResourceSetWrapper resourceSetWrapper, ODataDeserializerContext readContext)
        {
            Contract.Assert(resource != null);
            Contract.Assert(readContext != null);

            if (string.IsNullOrEmpty(resourceSetWrapper.ResourceSet.TypeName))
            {
                string message = Error.Format(SRResources.DynamicResourceSetTypeNameIsRequired, propertyName);
                throw new ODataException(message);
            }

            string elementTypeName =
                DeserializationHelpers.GetCollectionElementTypeName(resourceSetWrapper.ResourceSet.TypeName,
                    isNested: false);
            IEdmSchemaType elementType = readContext.Model.FindDeclaredType(elementTypeName);

            IEdmTypeReference edmTypeReference = elementType.ToEdmTypeReference(true);
            EdmCollectionTypeReference collectionType = new EdmCollectionTypeReference(new EdmCollectionType(edmTypeReference));

            ODataEdmTypeDeserializer deserializer = DeserializerProvider.GetEdmTypeDeserializer(collectionType);
            if (deserializer == null)
            {
                throw new SerializationException(Error.Format(SRResources.TypeCannotBeDeserialized, collectionType.FullName()));
            }

            IEnumerable value = ReadNestedResourceSetInline(resourceSetWrapper, collectionType, readContext) as IEnumerable;
            object result = value;
            if (value != null)
            {
                if (readContext.IsNoClrType)
                {
                    result = value.ConvertToEdmObject(collectionType);
                }
            }

            DeserializationHelpers.SetDynamicProperty(resource, structuredType, EdmTypeKind.Collection, propertyName,
                result, collectionType, readContext.Model);
        }

        private object ReadNestedResourceSetInline(ODataResourceSetWrapper resourceSetWrapper, IEdmTypeReference edmType,
            ODataDeserializerContext readContext)
        {
            Contract.Assert(resourceSetWrapper != null);
            Contract.Assert(edmType != null);
            Contract.Assert(readContext != null);

            ODataEdmTypeDeserializer deserializer = DeserializerProvider.GetEdmTypeDeserializer(edmType);
            if (deserializer == null)
            {
                throw new SerializationException(Error.Format(SRResources.TypeCannotBeDeserialized, edmType.FullName()));
            }

            IEdmStructuredTypeReference structuredType = edmType.AsCollection().ElementType().AsStructured();
            var nestedReadContext = new ODataDeserializerContext
            {
                Path = readContext.Path,
                Model = readContext.Model,
                Request = readContext.Request,
                TimeZone = readContext.TimeZone
            };

            if (readContext.IsNoClrType)
            {
                if (structuredType.IsEntity())
                {
                    nestedReadContext.ResourceType = typeof(EdmEntityObjectCollection);
                }
                else
                {
                    nestedReadContext.ResourceType = typeof(EdmComplexObjectCollection);
                }
            }
            else
            {
                Type clrType = readContext.Model.GetClrType(structuredType);

                if (clrType == null)
                {
                    throw new ODataException(
                        Error.Format(SRResources.MappingDoesNotContainResourceType, structuredType.FullName()));
                }

                nestedReadContext.ResourceType = typeof(List<>).MakeGenericType(clrType);
            }

            return deserializer.ReadInline(resourceSetWrapper, edmType, nestedReadContext);
        }
    }
}
