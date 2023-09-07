// Unity C# reference source
// Copyright (c) Unity Technologies. For terms of use, see
// https://unity3d.com/legal/licenses/Unity_Reference_Only_License

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Pool;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace UnityEditor.UIElements
{
    internal class UxmlSerializedAttributeDescription : UxmlAttributeDescription
    {
        static readonly Dictionary<Type, string> k_BaseTypes = new()
        {
            { typeof(short), "short" },
            { typeof(int), "int" },
            { typeof(long), "long" },
            { typeof(float), "float" },
            { typeof(double), "double" },
            { typeof(bool), "boolean" }
        };

        // Lazy loaded fields that are mostly used in the UI BUilder.
        IList<Type> m_UxmlObjectAcceptedTypes;
        object m_ObjectField;
        bool? m_IsUnityObject;
        bool? m_IsList;
        bool? m_IsUxmlObject;

        public new Type type { get; set; }

        /// <summary>
        /// The FieldInfo for the UxmlSerializedData.
        /// </summary>
        public FieldInfo serializedField { get; set; }

        /// <summary>
        /// The field/property value for the VisualElement/UxmlObject.
        /// </summary>
        public object objectField
        {
            get
            {
                if (m_ObjectField == null)
                {
                    var fieldName = serializedField.Name;

                    const BindingFlags publicFlags = BindingFlags.Instance | BindingFlags.Public;

                    // First check for a public version of the field/property.
                    m_ObjectField = elementType.GetProperty(fieldName, publicFlags) ?? (object)elementType.GetField(fieldName, publicFlags);
                    if (m_ObjectField != null)
                        return m_ObjectField;

                    // If we did not find a public version of the field/property, check for a private version.
                    // We need to walk through the type hierarchy to find the field/property as we can not get base private properties using GetProperty/GetField.
                    const BindingFlags privateFlags = BindingFlags.Instance | BindingFlags.NonPublic;

                    // We need to walk through the type hierarchy to find the field/property as we can not get base private properties using GetProperty/GetField.
                    var oType = elementType;
                    while (m_ObjectField == null && oType != null)
                    {
                        m_ObjectField = oType.GetProperty(fieldName, privateFlags) ?? (object)oType.GetField(fieldName, privateFlags);

                        if (m_ObjectField == null)
                            oType = oType.BaseType;
                    }
                }
                return m_ObjectField;
            }
        }

        public object defaultValue { get; set; }

        public object defaultValueClone => UxmlUtility.CloneObject(defaultValue);

        public override string defaultValueAsString => defaultValue?.ToString() ?? "";

        public bool isUnityObject
        {
            get
            {
                m_IsUnityObject ??= typeof(Object).IsAssignableFrom(type);
                return m_IsUnityObject.Value;
            }
        }

        public bool isUxmlObject
        {
            get
            {
                m_IsUxmlObject ??= serializedField.GetCustomAttribute<UxmlObjectReferenceAttribute>() != null;
                return m_IsUxmlObject.Value;
            }
        }

        public bool isList
        {
            get
            {
                m_IsList ??= typeof(IList).IsAssignableFrom(type) && (type.IsArray || type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>));
                return m_IsList.Value;
            }
        }

        /// <summary>
        /// The type of element or object that this attribute belongs to.
        /// </summary>
        public Type elementType { get; set; }

        /// <summary>
        /// The UxmlObject types that can be applied to this attribute.
        /// </summary>
        public IList<Type> uxmlObjectAcceptedTypes
        {
            get
            {
                if (m_UxmlObjectAcceptedTypes == null)
                {
                    // Were the types set in the UxmlObjectAttribute?
                    var attribute = serializedField.GetCustomAttribute<UxmlObjectReferenceAttribute>();
                    if (attribute.types != null)
                    {
                        m_UxmlObjectAcceptedTypes = attribute.types;
                        return m_UxmlObjectAcceptedTypes;
                    }

                    var acceptedTypes = new List<Type>();

                    void AddType(Type type)
                    {
                        if (type.IsAbstract || type.IsGenericType)
                            return;

                        // We only accept UxmlObjectAttribute types.
                        if (type.GetCustomAttribute<UxmlObjectAttribute>() == null)
                            return;

                        var uxmlSerializedDataType = type.GetNestedType(nameof(UxmlSerializedData));
                        if (uxmlSerializedDataType == null)
                            return;

                        acceptedTypes.Add(uxmlSerializedDataType);
                    }

                    // We need to query the source data as the type may be an interface which we dont store in the serialized data.
                    Type uxmlObjectType = null;
                    if (objectField is PropertyInfo propertyField)
                        uxmlObjectType = propertyField.PropertyType;
                    else if (objectField is FieldInfo fieldInfo)
                        uxmlObjectType = fieldInfo.FieldType;

                    if (isList)
                        uxmlObjectType = uxmlObjectType.GetArrayOrListElementType();

                    var foundTypes = TypeCache.GetTypesDerivedFrom(uxmlObjectType);

                    AddType(uxmlObjectType); // Add the base type
                    foreach (var t in foundTypes)
                    {
                        AddType(t);
                    }

                    m_UxmlObjectAcceptedTypes = acceptedTypes;
                }

                return m_UxmlObjectAcceptedTypes;
            }
        }

        internal virtual object GetValueFromBagAsObject(IUxmlAttributes bag, CreationContext cc)
        {
            if (!TryGetValueFromBagAsString(bag, cc, out var str))
                return defaultValueClone;

            if (UxmlAttributeConverter.TryConvertFromString(type, str, cc, out var value))
                return value;

            return defaultValueClone;
        }

        /// <summary>
        /// Copies the value from the VisualElement/UxmlObject to the UxmlSerializedData.
        /// </summary>
        /// <param name="obj">The instance of the VisualElement/UxmlObject to copy the value from.</param>
        /// <param name="uxmlSerializedData">The instance of the UxmlSerializedData to copy the value to.</param>
        public void SyncSerializedData(object obj, object uxmlSerializedData)
        {
            try
            {
                if (!TryGetValueFromObject(obj, out var value))
                    value = defaultValueClone;

                if (isUxmlObject && value != null)
                {
                    var previousValue = GetSerializedValue(uxmlSerializedData);

                    if (value is IList list)
                    {
                        // We try to use the old data so that we can preserve the uxml asset id.
                        var previousList = previousValue as IList;
                        var serializedList = Activator.CreateInstance(serializedField.FieldType) as IList;

                        for (int i = 0; i < list.Count; i++)
                        {
                            var item = list[i];
                            var previousData = previousList != null && i < previousList.Count ? previousList[i] as UxmlSerializedData : null;

                            var desc = UxmlSerializedDataRegistry.GetDescription(item.GetType().FullName);
                            var data = previousData ?? desc.CreateSerializedData();
                            desc.SyncSerializedData(item, data);
                            serializedList.Add(data);
                        }

                        value = serializedList;
                    }
                    else
                    {
                        var desc = UxmlSerializedDataRegistry.GetDescription(value.GetType().FullName);
                        var data = previousValue ?? desc.CreateSerializedData();
                        desc.SyncSerializedData(value, data);
                        value = data;
                    }
                }
                else
                {
                    value = UxmlUtility.CloneObject(value);
                }

                SetSerializedValue(uxmlSerializedData, value);
            }
            catch (Exception ex)
            {
                Debug.LogException(new Exception($"Failed to sync {name} to {serializedField.Name}", ex));
            }
        }

        public bool TryGetValueFromObject(object objInstance, out object value)
        {
            try
            {
                value = null;
                if (objInstance == null)
                    return false;

                if (objectField is PropertyInfo propertyField)
                {
                    value = propertyField.GetValue(objInstance);
                    return true;
                }
                else if (objectField is FieldInfo fieldInfo)
                {
                    value = fieldInfo.GetValue(objInstance);
                    return true;
                }

                return false;
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                value = default;
                return false;
            }
        }

        public void SetValueToObject(object objInstance, object value)
        {
            try
            {
                if (objectField is PropertyInfo propertyField)
                {
                    propertyField.SetValue(objInstance, value);
                }
                else if (objectField is FieldInfo fieldInfo)
                {
                    fieldInfo.SetValue(objInstance, value);
                }
                else
                {
                    Debug.LogError($"Could not set {name} with value {value}.");
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        public void SetSerializedValue(object uxmlSerializedData, object value)
        {
            if (serializedField.FieldType == typeof(string) && value is Type)
            {
                // Attempt to convert the field to a string
                if (UxmlAttributeConverter.TryConvertToString<Type>(value, null, out var stringValue))
                    value = stringValue;
            }

            serializedField.SetValue(uxmlSerializedData, value);
        }

        public object GetSerializedValue(object uxmlSerializedData)
        {
            try
            {
                return serializedField.GetValue(uxmlSerializedData);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                return defaultValue;
            }
        }

        public void UpdateSchemaRestriction()
        {
            if (type is { IsEnum: true })
            {
                var enumRestriction = new UxmlEnumeration();

                var values = new List<string>();
                foreach (var item in Enum.GetValues(type))
                {
                    values.Add(item.ToString());
                }

                enumRestriction.values = values;
                restriction = enumRestriction;
            }
            else if (serializedField.GetCustomAttribute<RangeAttribute>() != null)
            {
                var attribute = serializedField.GetCustomAttribute<RangeAttribute>();
                var uxmlValueBoundsRestriction = new UxmlValueBounds
                {
                    max = attribute.max.ToString(),
                    min = attribute.min.ToString()
                };

                restriction = uxmlValueBoundsRestriction;
            }
        }

        public void UpdateBaseType()
        {
            if (!k_BaseTypes.TryGetValue(type, out var baseType))
                baseType = "string";

            typeNamespace = xmlSchemaNamespace;
            ((UxmlAttributeDescription)this).type = baseType;
        }

        public override string ToString() => $"{serializedField.DeclaringType.ReflectedType.Name}.{serializedField.Name} ({serializedField.FieldType})";
    }

    internal class UxmlSerializedUxmlObjectAttributeDescription : UxmlSerializedAttributeDescription
    {
        internal static readonly string k_MultipleUxmlObjectsWarning = "Multiple UxmlObjects Found for UxmlObjectReference Field {0}. " +
            "Only the first UxmlObject will be used in the current configuration. " +
            "If you intend to use multiple UxmlObjects, it is recommended to convert the field into a list.";

        public string rootName { get; set; }

        internal override object GetValueFromBagAsObject(IUxmlAttributes bag, CreationContext cc)
        {
            if (bag is UxmlAsset ua)
            {
                var entry = cc.visualTreeAsset.GetUxmlObjectEntry(ua.id);
                if (entry.uxmlObjectAssets == null)
                    return defaultValueClone;

                // First we need to extract the element that contains the values for this field.
                // Legacy fields, such as those found in MultiColumnListView and MultiColumnTreeView, do not have a root element.
                // We expect the UXML to look like this:
                // <visual-element>
                //   <element-field-name>
                //     <field-value/>
                //     <field-value/>
                //   </element-field-name>
                // </visual-element>
                // Legacy elements, such as MultiColumnListView do not have the field name and look like this:
                // <visual-element>
                //   <field-value/>
                //   <field-value/>
                // </visual-element>
                if (!string.IsNullOrEmpty(rootName))
                {
                    foreach (var asset in entry.uxmlObjectAssets)
                    {
                        if (asset.isField && asset.fullTypeName == rootName)
                        {
                            entry = cc.visualTreeAsset.GetUxmlObjectEntry(asset.id);
                            break;
                        }
                    }
                }

                // Extract values.
                if (entry.uxmlObjectAssets != null)
                {
                    Type objectType = type;
                    using (ListPool<(UxmlObjectAsset, UxmlSerializedDataDescription)>.Get(out var foundObjects))
                    {
                        if (isList)
                        {
                            objectType = type.GetArrayOrListElementType();
                        }

                        foreach (var asset in entry.uxmlObjectAssets)
                        {
                            var assetDescription = UxmlSerializedDataRegistry.GetDescription(asset.fullTypeName);
                            if (assetDescription != null && objectType.IsAssignableFrom(assetDescription.serializedDataType))
                            {
                                foundObjects.Add((asset, assetDescription));
                            }
                        }

                        // Display a warning when uxml file contains more than one named UxmlObject of a type defined in a single instance attribute
                        if (entry.uxmlObjectAssets.Count > 1 && isUxmlObject && !isList)
                        {
                            var foundTypes = new HashSet<string>();
                            foreach (var asset in entry.uxmlObjectAssets)
                            {
                                if (foundTypes.Contains(asset.fullTypeName))
                                {
                                    Debug.LogWarning(string.Format(k_MultipleUxmlObjectsWarning, asset.fullTypeName));
                                    break;
                                }
                                foundTypes.Add(asset.fullTypeName);
                            }
                        }

                        IList list = null;
                        if (foundObjects.Count > 0)
                        {
                            if (isList)
                            {
                                list = type.IsArray ? Array.CreateInstance(objectType, foundObjects.Count) : (IList)Activator.CreateInstance(type);
                            }

                            for (int i = 0; i < foundObjects.Count; ++i)
                            {
                                (var asset, var assetDescription) = foundObjects[i];
                                var nestedData = UxmlSerializer.Serialize(assetDescription, asset, cc);
                                if (nestedData != null)
                                {
                                    if (isList)
                                    {
                                        if (type.IsArray)
                                            list[i] = nestedData;
                                        else
                                            list.Add(nestedData);
                                    }
                                    else
                                    {
                                        return nestedData;
                                    }
                                }
                            }
                        }
                        if (list != null)
                            return list;
                    }
                }
            }

            return defaultValueClone;
        }
    }

    internal static class UxmlSerializer
    {
        public static UxmlSerializedData Serialize(string fullTypeName, IUxmlAttributes bag, CreationContext cc)
        {
            var desc = UxmlSerializedDataRegistry.GetDescription(fullTypeName);
            if (desc == null)
                return null;

            return Serialize(desc, bag, cc);
        }

        public static UxmlSerializedData Serialize(UxmlSerializedDataDescription description, IUxmlAttributes bag, CreationContext cc)
        {
            var data = description.CreateSerializedData();
            if (bag is UxmlAsset uxmlAsset)
                data.uxmlAssetId = uxmlAsset.id;

            // Do we need to handle any legacy fields?
            // Previously, we needed to use multiple fields to represent a composite, eg. x,y,z fields for a Vector3,
            // we are now able to serialize composites but still need to support these legacy fields.
            // If we find a legacy field then we mark it as handled so that we dont then serialize it again and replace the value.
            var handledAttributes = HashSetPool<string>.Get();
            if (data is IUxmlSerializedDataCustomAttributeHandler legacyHandler)
            {
                legacyHandler.SerializeCustomAttributes(bag, handledAttributes);
            }

            foreach (var attribute in description.serializedAttributes)
            {
                if (handledAttributes.Contains(attribute.name))
                    continue;

                var value = attribute.GetValueFromBagAsObject(bag, cc);
                if (value is Type type)
                {
                    // Validate that type can be assigned
                    var typeRefAttribute = attribute.serializedField.GetCustomAttribute<UxmlTypeReferenceAttribute>();
                    if (typeRefAttribute != null && typeRefAttribute.baseType != null && !typeRefAttribute.baseType.IsAssignableFrom(type))
                    {
                        Debug.LogError($"Type: Invalid type \"{type}\". Type must derive from {typeRefAttribute.baseType.FullName}.");
                        continue;
                    }

                    // Type are serialized as string
                    UxmlAttributeConverter.TryConvertToString<Type>(type, cc.visualTreeAsset, out var str);
                    value = str;
                }

                try
                {
                    attribute.SetSerializedValue(data, value);
                }
                catch (Exception e)
                {
                    Debug.LogException(new Exception($"Could not set value for {attribute.serializedField.Name} with {value}", e), cc.visualTreeAsset);
                }
            }

            HashSetPool<string>.Release(handledAttributes);
            return data;
        }

        public static UxmlSerializedData SerializeObject(object target)
        {
            var desc = UxmlSerializedDataRegistry.GetDescription(target.GetType().FullName);
            if (desc == null)
                return null;

            var data = desc.CreateDefaultSerializedData();
            return data;
        }

        public static void SyncVisualTreeAssetSerializedData(CreationContext cc)
        {
            // Skip the Uxml root element
            var vta = cc.visualTreeAsset;
            var veaCount = vta.visualElementAssets?.Count ?? 0;
            for (var i = 1; i < veaCount; i++)
            {
                var vea = vta.visualElementAssets[i];
                SyncVisualTreeElementSerializedData(vea, cc);
            }

            var tplCount = vta.templateAssets?.Count ?? 0;
            for (var i = 0; i < tplCount; i++)
            {
                var rootTemplate = vta.templateAssets[i];
                rootTemplate.serializedData = Serialize(rootTemplate.fullTypeName, rootTemplate, cc);
                CreateSerializedDataOverride(rootTemplate, vta, rootTemplate, cc);
            }
        }

        public static void SyncVisualTreeElementSerializedData(VisualElementAsset vea, CreationContext cc)
        {
            var desc = UxmlSerializedDataRegistry.GetDescription(vea.fullTypeName);
            if (desc != null)
                vea.serializedData = Serialize(desc, vea, cc);
        }

        private static void CreateSerializedDataOverride(TemplateAsset rootTemplate, VisualTreeAsset vta, TemplateAsset templateAsset, CreationContext cc)
        {
            // If there's no attribute override no need to create additional serialized data.
            if (!templateAsset.hasAttributeOverride && cc.attributeOverrides == null)
                return;

            var bagOverrides = templateAsset.attributeOverrides;
            var contextOverrides = cc.attributeOverrides;

            List<CreationContext.AttributeOverrideRange> overrideRanges = null;
            if (bagOverrides != null || contextOverrides != null)
            {
                // We want to add contextOverrides first here, then bagOverrides, as we
                // want parent instances to always override child instances.
                overrideRanges = new List<CreationContext.AttributeOverrideRange>();
                if (contextOverrides != null)
                    overrideRanges.AddRange(contextOverrides);
                if (bagOverrides != null)
                    overrideRanges.Add(new CreationContext.AttributeOverrideRange(vta, bagOverrides));
            }

            var templateContext = new CreationContext(null, overrideRanges, vta, null);
            var templateVta = vta.ResolveTemplate(templateAsset.templateAlias);

            // If the template reference is invalid simply skip it.
            // On instantiation it will get replaced by a label showing that the template is unknown.
            if (templateVta == null)
                return;

            // For each element with an override create a serialized data stored in the template asset
            foreach (var vea in templateVta.visualElementAssets)
            {
                if (!vea.TryGetAttributeValue("name", out var elementName) || string.IsNullOrEmpty(elementName))
                    continue;

                bool hasOverride = false;
                for (int i = 0; i < overrideRanges.Count && !hasOverride; i++)
                {
                    for (int j = 0; j < overrideRanges[i].attributeOverrides.Count; j++)
                    {
                        if (overrideRanges[i].attributeOverrides[j].m_ElementName == elementName)
                            hasOverride = true;
                    }
                }

                if (!hasOverride)
                    continue;

                var desc = UxmlSerializedDataRegistry.GetDescription(vea.fullTypeName);
                if (desc == null)
                    continue;

                var serializedDataOverride = Serialize(desc, vea, templateContext);
                rootTemplate.serializedDataOverrides.Add(new TemplateAsset.UxmlSerializedDataOverride()
                {
                    m_ElementId = vea.id,
                    m_SerializedData = serializedDataOverride
                });
            }

            foreach (var ta in templateVta.templateAssets)
                CreateSerializedDataOverride(rootTemplate, templateVta, ta, templateContext);
        }
    }
}
